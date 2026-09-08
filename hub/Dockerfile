# Connectors Hub for Cloud Run: one static Go binary in a distroless image.
#
# Build context is the REPO ROOT: the module lives there and links hub/ and
# every connector directory (excel/).
#
#   docker build -f hub/Dockerfile -t hub .
FROM golang:1.25 AS build
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY hub ./hub
COPY excel ./excel
COPY internal ./internal
# -trimpath drops build-machine paths; VCS stamping is unavailable in a bare
# copy anyway (no .git), so the deploy pipeline (hub/deploy/deploy.sh) passes
# the git revision in as VERSION and it is linked into main.buildVersion,
# which version() prefers over the (absent) VCS stamp.
ARG VERSION=unknown
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w -X main.buildVersion=${VERSION}" -o /hub ./hub/cmd/hub

FROM gcr.io/distroless/static-debian12:nonroot
COPY --from=build /hub /hub
# Cloud Run sets $PORT; everything else (HUB_PUBLIC_URL, the Firestore and
# Entra settings, the mounted secrets) comes from the service configuration
# that hub/deploy/deploy.sh writes.
ENTRYPOINT ["/hub"]
