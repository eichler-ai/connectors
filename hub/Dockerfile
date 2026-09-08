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
# copy anyway, so get_skills reports "unknown" for an image built this way
# until the deploy pipeline passes the revision through (next PR).
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /hub ./hub/cmd/hub

FROM gcr.io/distroless/static-debian12:nonroot
COPY --from=build /hub /hub
# Cloud Run sets $PORT; HUB_PUBLIC_URL, HUB_DEV_TOKEN and HUB_ALLOWED_ORIGINS come
# from the service configuration.
ENTRYPOINT ["/hub"]
