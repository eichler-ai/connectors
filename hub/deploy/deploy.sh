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
# Reads the Cloud Run runtime URL from the service itself where the caller
# hasn't fixed one (staging), and pulls HUB_DEV_TOKEN from Secret Manager,
# creating the secret with a fresh random value the first time this runs for
# an environment. The token value is never printed; see the "read a token"
# line this script prints at the end.
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
    FIXED_PUBLIC_URL=""   # discovered from the service's own run.app URL below
    ;;
  prod)
    SERVICE=hub
    SECRET=hub-dev-token
    FIXED_PUBLIC_URL="https://connectors.eichler.ai"
    ;;
  *)
    usage
    ;;
esac

PROJECT=eichler-ai
REGION=us-central1
REPO=cloud-run-source-deploy   # existing Artifact Registry repo (POC also pushes here)

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

# --- Runtime service account needs access to the secret. -------------------
RUNTIME_SA="$(gcloud iam service-accounts list --project="$PROJECT" \
  --filter="email ~ ^[0-9]+-compute@developer.gserviceaccount.com\$" \
  --format='value(email)')"
gcloud secrets add-iam-policy-binding "$SECRET" \
  --project="$PROJECT" \
  --member="serviceAccount:${RUNTIME_SA}" \
  --role="roles/secretmanager.secretAccessor" \
  >/dev/null

# --- HUB_PUBLIC_URL: fixed for prod, discovered for staging. ---------------
# A brand-new staging service doesn't have a URL until it exists, so the
# first deploy for a fresh environment goes out with the DevPublicURL
# fallback (the hub still serves fine; only the manifest URL is briefly
# wrong), then a second deploy corrects it once the run.app URL is known.
# On every later run the service already exists, so this happens once.
if [ -n "$FIXED_PUBLIC_URL" ]; then
  PUBLIC_URL="$FIXED_PUBLIC_URL"
else
  PUBLIC_URL="$(gcloud run services describe "$SERVICE" --project="$PROJECT" --region="$REGION" \
    --format='value(status.url)' 2>/dev/null || true)"
fi

deploy_revision() {
  local url="$1"
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
    --set-secrets="HUB_DEV_TOKEN=${SECRET}:latest"
    --quiet
  )
  if [ -n "$url" ]; then
    args+=(--set-env-vars="HUB_PUBLIC_URL=${url}")
  fi
  gcloud run deploy "${args[@]}"
}

echo "==> [$ENV] deploying ${SERVICE}"
deploy_revision "$PUBLIC_URL"

if [ -z "$PUBLIC_URL" ]; then
  PUBLIC_URL="$(gcloud run services describe "$SERVICE" --project="$PROJECT" --region="$REGION" \
    --format='value(status.url)')"
  echo "==> [$ENV] discovered ${SERVICE}'s URL (${PUBLIC_URL}); redeploying with HUB_PUBLIC_URL set"
  deploy_revision "$PUBLIC_URL"
fi

# --- Verify: liveness, then a real MCP call through the deployed token. ----
# This is the check an operator would run by hand; catches a broken deploy
# (or a mangled secret, see the tr -d '\n' above) here instead of at the next
# real client connection.
echo "==> [$ENV] verifying /health"
HEALTH_CODE="$(curl -s -o /dev/null -w '%{http_code}' "${PUBLIC_URL}/health")"
if [ "$HEALTH_CODE" != "200" ]; then
  echo "verify: GET ${PUBLIC_URL}/health -> ${HEALTH_CODE}, want 200" >&2
  exit 1
fi

echo "==> [$ENV] verifying get_skills over the deployed MCP endpoint"
TOKEN="$(gcloud secrets versions access latest --secret="$SECRET" --project="$PROJECT")"
GOT_VERSION="$(HUB_DEV_TOKEN="$TOKEN" go run ./hub/deploy/verify "${PUBLIC_URL}/excel/mcp")"
if [ "$GOT_VERSION" != "$REV" ]; then
  echo "verify: get_skills hub_version=${GOT_VERSION}, want the deployed revision ${REV}" >&2
  exit 1
fi

echo "==> [$ENV] done: ${SERVICE} at ${PUBLIC_URL} (image ${IMAGE}, hub_version ${GOT_VERSION})"
echo "    read the dev token:  gcloud secrets versions access latest --secret=${SECRET} --project=${PROJECT}"
