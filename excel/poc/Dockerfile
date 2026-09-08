# Excel bridge POC for Cloud Run: static Go binary in a distroless image.
FROM golang:1.25 AS build
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /excel-bridge ./cmd/excel-bridge

FROM gcr.io/distroless/static-debian12:nonroot
COPY --from=build /excel-bridge /excel-bridge
# Cloud Run sets $PORT; the entrypoint reads BRIDGE_PREFIX / BRIDGE_PUBLIC_URL / BRIDGE_TOKEN from the env.
ENTRYPOINT ["/excel-bridge", "serve"]
