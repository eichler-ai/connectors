#!/usr/bin/env bash
# Build and deploy the Connectors Hub to Cloud Run.
#
#   hub/deploy/deploy.sh staging
#   hub/deploy/deploy.sh prod
#
# Idempotent: safe to re-run. Builds the image with Cloud Build from the repo
# root (hub/Dockerfile needs hub/, excel/ and internal/ as siblings, so a
# plain `gcloud run deploy --source` — which only looks for a Dockerfile at
# the root of the source directory it uploads — can't point at
# hub/Dockerfile; `gcloud builds submit --config hub/deploy/cloudbuild.yaml`
# from the repo root gives that context and full control over the build-arg
# that stamps the version), tags the image with the git revision, then
# deploys to Cloud Run with the settings the excel-bridge POC used (single
# instance, session affinity, 60-minute request timeout, WebSockets).
#
# Uses the custom domain (prod) or the deterministic Cloud Run URL (staging)
# as the public URL, and pulls the environment's secrets from
# Secret Manager: HUB_DEV_TOKEN (the bridge hello token until pane sign-in
# lands) and the JWT signing key, both created with fresh random values the
# first time this runs for an environment, plus the Entra client secret,
# which is created by hand (it comes from the Entra portal) and only checked
# here. No secret value is ever printed.
#
# Firestore (PRD §11): prod uses the project's (default) database; staging
# gets its own database so test users and tokens never share collections
# with production. TTL policies on expires_at keep auth_codes, login_states
# and refresh_tokens from accumulating.
set -euo pipefail

usage() {
  echo "usage: $0 staging|prod" >&2
  exit 1
}

[ $# -eq 1 ] || usage
ENV="$1"

case "$ENV" in
  staging)
    SERVICE=hub-staging
    SECRET=hub-staging-dev-token
    JWT_SECRET=hub-staging-jwt-signing-key
    FIRESTORE_DB=hub-staging
    FIXED_PUBLIC_URL=""   # the deterministic run.app URL, computed below
    ;;
  prod)
    SERVICE=hub
    SECRET=hub-dev-token
    JWT_SECRET=hub-jwt-signing-key
    FIRESTORE_DB="(default)"
    FIXED_PUBLIC_URL="https://connectors.eichler.ai"
    ;;
  *)
    usage
    ;;
esac

PROJECT=eichler-ai
REGION=us-central1
REPO=cloud-run-source-deploy   # existing Artifact Registry repo (POC also pushes here)
ENTRA_SECRET=entra-client-secret
# The Entra application (client) id of "Eichler Connectors"; override with
# HUB_MS_CLIENT_ID in the environment to deploy against another registration.
MS_CLIENT_ID="${HUB_MS_CLIENT_ID:-4fce14f7-6415-4d92-a8b0-c98f7f0a1763}"

# --- Safety: this script must only ever touch the one GCP project. ---------
ACTIVE_PROJECT="$(gcloud config get-value project 2>/dev/null)"
if [ "$ACTIVE_PROJECT" != "$PROJECT" ]; then
  echo "refusing to deploy: active gcloud project is '$ACTIVE_PROJECT', expected '$PROJECT'" >&2
  echo "run: gcloud config set project $PROJECT" >&2
  exit 1
fi

# --- Root of the repo is the Cloud Build context. ---------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
cd "$REPO_ROOT"

REV="$(git rev-parse --short HEAD)"
if [ -n "$(git status --porcelain)" ]; then
  REV="${REV}-dirty"
fi
IMAGE="us-central1-docker.pkg.dev/${PROJECT}/${REPO}/hub:${REV}"

echo "==> [$ENV] building ${IMAGE} (revision ${REV}) with Cloud Build"
gcloud builds submit \
  --project="$PROJECT" \
  --region="$REGION" \
  --config="hub/deploy/cloudbuild.yaml" \
  --substitutions="_IMAGE=${IMAGE},_VERSION=${REV}" \
  "$REPO_ROOT"

# --- Secret: create if missing, never overwrite an existing value. ---------
if ! gcloud secrets describe "$SECRET" --project="$PROJECT" >/dev/null 2>&1; then
  echo "==> [$ENV] creating secret ${SECRET}"
  gcloud secrets create "$SECRET" --project="$PROJECT" --replication-policy=automatic >/dev/null
fi
if [ -z "$(gcloud secrets versions list "$SECRET" --project="$PROJECT" --format='value(name)' 2>/dev/null)" ]; then
  echo "==> [$ENV] seeding ${SECRET} with a fresh random value (this only happens once)"
  # tr -d '\n': --data-file=- takes the pipe's bytes verbatim, and openssl's
  # output ends in a newline, which would become part of the token and never
  # match a bearer header again.
  openssl rand -hex 24 | tr -d '\n' | gcloud secrets versions add "$SECRET" --project="$PROJECT" --data-file=- >/dev/null
fi

# --- JWT signing key: one P-256 key per environment, created once. --------
# The secret holds PEM; the hub reads it from the HUB_JWT_SIGNING_KEY env var
# (multi-line values are fine there, unlike the token's trailing newline).
# Rotation: prepend a new key block to a new secret version — the first key
# signs, every key verifies — then drop the old block a deploy later.
if ! gcloud secrets describe "$JWT_SECRET" --project="$PROJECT" >/dev/null 2>&1; then
  echo "==> [$ENV] creating secret ${JWT_SECRET}"
  gcloud secrets create "$JWT_SECRET" --project="$PROJECT" --replication-policy=automatic >/dev/null
fi
if [ -z "$(gcloud secrets versions list "$JWT_SECRET" --project="$PROJECT" --format='value(name)' 2>/dev/null)" ]; then
  echo "==> [$ENV] generating the JWT signing key into ${JWT_SECRET} (this only happens once)"
  openssl ecparam -genkey -name prime256v1 -noout | gcloud secrets versions add "$JWT_SECRET" --project="$PROJECT" --data-file=- >/dev/null
fi

# --- Entra client secret: created by hand from the portal, checked here. ---
if [ -z "$(gcloud secrets versions list "$ENTRA_SECRET" --project="$PROJECT" --filter='state=enabled' --format='value(name)' 2>/dev/null)" ]; then
  echo "refusing to deploy: secret ${ENTRA_SECRET} has no enabled version; add the Entra app's client secret:" >&2
  echo "  printf '%s' '<secret>' | gcloud secrets versions add ${ENTRA_SECRET} --project=${PROJECT} --data-file=-" >&2
  exit 1
fi

# --- Runtime service account needs access to the secrets. ------------------
RUNTIME_SA="$(gcloud iam service-accounts list --project="$PROJECT" \
  --filter="email ~ ^[0-9]+-compute@developer.gserviceaccount.com\$" \
  --format='value(email)')"
for s in "$SECRET" "$JWT_SECRET" "$ENTRA_SECRET"; do
  gcloud secrets add-iam-policy-binding "$s" \
    --project="$PROJECT" \
    --member="serviceAccount:${RUNTIME_SA}" \
    --role="roles/secretmanager.secretAccessor" \
    >/dev/null
done

# --- Firestore: the environment's database and its TTL policies. -----------
if [ "$FIRESTORE_DB" != "(default)" ] && ! gcloud firestore databases describe --database="$FIRESTORE_DB" --project="$PROJECT" >/dev/null 2>&1; then
  echo "==> [$ENV] creating Firestore database ${FIRESTORE_DB}"
  gcloud firestore databases create --database="$FIRESTORE_DB" --location="$REGION" --type=firestore-native --project="$PROJECT" >/dev/null
fi
for col in auth_codes login_states refresh_tokens; do
  # Idempotent: re-enabling an enabled TTL is a no-op. Runs async on
  # Google's side; --async keeps the deploy moving.
  gcloud firestore fields ttls update expires_at --collection-group="$col" --database="$FIRESTORE_DB" \
    --enable-ttl --project="$PROJECT" --async >/dev/null 2>&1 || true
done

# --- HUB_PUBLIC_URL: the custom domain for prod, the deterministic Cloud Run
# URL for staging. A service answers on two run.app URLs: a random-suffix one
# (status.url) and https://<service>-<project number>.<region>.run.app, which
# is stable across deleting and recreating the service. The public URL is
# the OAuth issuer and the Entra redirect URI (exact match), so it must be
# the stable one — and being computable up front, a fresh environment needs
# only one deploy.
if [ -n "$FIXED_PUBLIC_URL" ]; then
  PUBLIC_URL="$FIXED_PUBLIC_URL"
else
  PROJECT_NUMBER="$(gcloud projects describe "$PROJECT" --format='value(projectNumber)')"
  PUBLIC_URL="https://${SERVICE}-${PROJECT_NUMBER}.${REGION}.run.app"
fi

deploy_revision() {
  local -a args=(
    "$SERVICE"
    --project="$PROJECT"
    --region="$REGION"
    --image="$IMAGE"
    --platform=managed
    --allow-unauthenticated
    --min-instances=1
    --max-instances=1
    --session-affinity
    --timeout=3600
    --set-secrets="HUB_DEV_TOKEN=${SECRET}:latest,HUB_JWT_SIGNING_KEY=${JWT_SECRET}:latest,HUB_MS_CLIENT_SECRET=${ENTRA_SECRET}:latest"
    --quiet
  )
  # HUB_ENV distinguishes this deployment's add-in Id/DisplayName from the
  # other environment's (see hub.Options.Environment) so Excel for the web,
  # which keys a sideloaded add-in by manifest Id, never conflates them.
  # "^@^" makes @ the list delimiter for gcloud, since the default comma is
  # a legal character in some of these values.
  args+=(--set-env-vars="^@^HUB_ENV=${ENV}@HUB_PUBLIC_URL=${PUBLIC_URL}@HUB_FIRESTORE_PROJECT=${PROJECT}@HUB_FIRESTORE_DATABASE=${FIRESTORE_DB}@HUB_MS_CLIENT_ID=${MS_CLIENT_ID}")
  gcloud run deploy "${args[@]}"
}

echo "==> [$ENV] deploying ${SERVICE}"
deploy_revision

# --- Verify: liveness, discovery documents, the 401 challenge. -------------
# This is the check an operator would run by hand; catches a broken deploy
# (or a mangled secret) here instead of at the next real client connection.
# A real token needs a real sign-in, so the MCP call itself is the
# orchestrator's live test (hub/README.md "Auth").
echo "==> [$ENV] verifying ${PUBLIC_URL}"
GOT_VERSION="$(go run ./hub/deploy/verify "${PUBLIC_URL}" excel)"
if [ "$GOT_VERSION" != "$REV" ]; then
  echo "verify: /health Hub-Version=${GOT_VERSION}, want the deployed revision ${REV}" >&2
  exit 1
fi

echo "==> [$ENV] done: ${SERVICE} at ${PUBLIC_URL} (image ${IMAGE}, hub_version ${GOT_VERSION})"
echo "    add in Claude Code:  claude mcp add --transport http excel ${PUBLIC_URL}/excel/mcp"
echo "    read the bridge token:  gcloud secrets versions access latest --secret=${SECRET} --project=${PROJECT}"
