FROM node:22-alpine@sha256:b6f26b36c8ff49624cfdac716b8ea1138d606df02586a77d364bb5536a634f85 AS build
WORKDIR /app
COPY src/proxyharbor-web/package*.json ./
# CI and release workflows audit this exact lockfile before publishing images.
RUN npm ci --no-audit
COPY src/proxyharbor-web/ .
ARG VERSION=0.0.0-local
ARG SOURCE_REVISION=unknown
ENV VITE_APP_VERSION=$VERSION VITE_SOURCE_REVISION=$SOURCE_REVISION
RUN npm run build

FROM nginx:1.31-alpine@sha256:62ff2089abf5a9ed33bd232895bef5e22f7bb4b200675cec49a5ebc48e3d4ac8
RUN apk upgrade --no-cache libexpat libcrypto3 libssl3 libuuid
COPY --chown=nginx:nginx src/proxyharbor-web/nginx.conf /etc/nginx/nginx.conf
COPY --from=build --chown=nginx:nginx /app/dist /usr/share/nginx/html
USER nginx
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --retries=3 CMD wget -q --spider http://127.0.0.1:8080/healthz || exit 1
