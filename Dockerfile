# syntax=docker/dockerfile:1.4

# -------- Stage 1: Build frontend --------
FROM --platform=$BUILDPLATFORM node:24-alpine3.23@sha256:595398b0081eacda8e1c4c5b97b76cd1020e4d58a8ebcb4843b9bca1e79e7436 AS frontend-build

WORKDIR /frontend
COPY ./frontend ./

# URL_BASE bakes the React Router basename, Vite asset base, and a global
# `__URL_BASE__` constant into the client bundle. Must match the URL_BASE env
# var at runtime — they configure the same setting from opposite ends.
ARG URL_BASE=""
ENV URL_BASE=${URL_BASE}

RUN npm ci
RUN npm run build
RUN npm run build:server
RUN npm prune --omit=dev

# -------- Stage 2: Build backend --------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:940f919ae84dd92ccd4aab7686fa5b777870b006c9360351039e16bcaad73d89 AS backend-build

WORKDIR /backend
COPY ./backend ./

# Accept build-time architecture as ARG (e.g., x64 or arm64)
ARG TARGETARCH
RUN dotnet restore
RUN dotnet publish -c Release -r linux-musl-${TARGETARCH} -o ./publish

# The frontend build runs on BUILDPLATFORM, but the copied runtime must match
# TARGETPLATFORM for multi-architecture images.
FROM node:24-alpine3.23@sha256:595398b0081eacda8e1c4c5b97b76cd1020e4d58a8ebcb4843b9bca1e79e7436 AS node-runtime

# -------- Stage 3: Combined runtime image --------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine@sha256:57bd717ac18ff6c8a39cc0ee4a76c1f15adc46df50434c73eff0c3f1df4c88f0

# Label the image
ARG REPO_URL
LABEL org.opencontainers.image.source=${REPO_URL}

# Prepare environment
WORKDIR /app
RUN mkdir /config /data \
    && apk add --no-cache fuse libc6-compat shadow su-exec bash curl tzdata

# Keep Node/npm on the same reviewed Alpine 3.23 base as the .NET runtime and
# avoid drifting to whatever version happens to be current in apk repositories.
COPY --from=node-runtime /usr/local/ /usr/local/

# Copy frontend
COPY --from=frontend-build /frontend/node_modules ./frontend/node_modules
COPY --from=frontend-build /frontend/package.json ./frontend/package.json
COPY --from=frontend-build /frontend/dist-node/server.js ./frontend/dist-node/server.js
COPY --from=frontend-build /frontend/build ./frontend/build

# Copy backend
COPY --from=backend-build /backend/publish ./backend

# Entry and runtime setup
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# Set env variables
# Port 3000: frontend  Port 8080: backend (WebDAV / API)
EXPOSE 3000 8080
ARG NZBDAV_VERSION
ENV NZBDAV_VERSION=${NZBDAV_VERSION}
ARG URL_BASE=""
ENV URL_BASE=${URL_BASE}
ENV NODE_ENV=production
ENV LOG_LEVEL=warning
# LISTEN_ADDRESS controls the network interface both the frontend and backend bind to.
# Default is 0.0.0.0 (all interfaces). Set to a specific IP to restrict binding.
ENV LISTEN_ADDRESS=0.0.0.0

ENTRYPOINT ["/entrypoint.sh"]
CMD []
