FROM node:22-alpine@sha256:c610fcdfb1d5b4740dd70c284ed3cb16bb857e0f7166196e36a5501df7a3aa32 AS build
WORKDIR /app
COPY src/proxyharbor-web/package*.json ./
RUN npm ci
COPY src/proxyharbor-web/ .
ARG VERSION=0.0.0-local
ARG SOURCE_REVISION=unknown
ENV VITE_APP_VERSION=$VERSION VITE_SOURCE_REVISION=$SOURCE_REVISION
RUN npm run build

FROM nginx:1.29-alpine@sha256:5616878291a2eed594aee8db4dade5878cf7edcb475e59193904b198d9b830de
COPY --chown=nginx:nginx src/proxyharbor-web/nginx.conf /etc/nginx/nginx.conf
COPY --from=build --chown=nginx:nginx /app/dist /usr/share/nginx/html
USER nginx
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --retries=3 CMD wget -q --spider http://127.0.0.1:8080/healthz || exit 1
