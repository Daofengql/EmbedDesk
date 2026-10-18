# SPDX-License-Identifier: GPL-3.0-only
FROM node:24-alpine AS console
WORKDIR /src/www
COPY www/package.json www/package-lock.json ./
RUN npm ci
COPY www/ ./
RUN npm run build

FROM golang:1.26.9-bookworm AS build
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
COPY --from=console /src/www/dist ./www/dist
RUN CGO_ENABLED=0 go build -trimpath -ldflags='-s -w' -o /out/embeddesk-server . && mkdir /out/data

FROM gcr.io/distroless/static-debian12:nonroot
WORKDIR /app
COPY --from=build --chown=65532:65532 /out/embeddesk-server /app/embeddesk-server
COPY --from=build --chown=65532:65532 /out/data /data
ENV EMBEDDESK_LISTEN=0.0.0.0:7080 EMBEDDESK_DATABASE=/data/relay.sqlite
EXPOSE 7080
ENTRYPOINT ["/app/embeddesk-server"]
