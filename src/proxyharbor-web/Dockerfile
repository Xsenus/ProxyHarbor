FROM node:22-alpine AS build
WORKDIR /app
COPY src/proxyharbor-web/package*.json ./
RUN npm ci
COPY src/proxyharbor-web/ .
ARG VERSION=0.0.0-local
ARG SOURCE_REVISION=unknown
ENV VITE_APP_VERSION=$VERSION VITE_SOURCE_REVISION=$SOURCE_REVISION
RUN npm run build

FROM nginx:1.29-alpine
COPY --chown=nginx:nginx src/proxyharbor-web/nginx.conf /etc/nginx/nginx.conf
COPY --from=build --chown=nginx:nginx /app/dist /usr/share/nginx/html
USER nginx
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --retries=3 CMD wget -q --spider http://127.0.0.1:8080/healthz || exit 1
