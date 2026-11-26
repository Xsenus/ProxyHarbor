FROM mcr.microsoft.com/dotnet/sdk:10.0.404-alpine3.24@sha256:0294f285d062afacc8b5f6d7febb0cd4ee879320e5585fa15f2f4ebf1feb9ba7 AS build
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["NuGet.Config", "global.json", "./"]
COPY ["src/ProxyHarbor.Domain/ProxyHarbor.Domain.csproj", "src/ProxyHarbor.Domain/"]
COPY ["src/ProxyHarbor.Domain/packages.lock.json", "src/ProxyHarbor.Domain/"]
COPY ["src/ProxyHarbor.Infrastructure/ProxyHarbor.Infrastructure.csproj", "src/ProxyHarbor.Infrastructure/"]
COPY ["src/ProxyHarbor.Infrastructure/packages.lock.json", "src/ProxyHarbor.Infrastructure/"]
COPY ["src/ProxyHarbor.Api/ProxyHarbor.Api.csproj", "src/ProxyHarbor.Api/"]
COPY ["src/ProxyHarbor.Api/packages.lock.json", "src/ProxyHarbor.Api/"]
RUN dotnet restore "src/ProxyHarbor.Api/ProxyHarbor.Api.csproj" --locked-mode
COPY . .
ARG VERSION=0.0.0-local
ARG SOURCE_REVISION=unknown
RUN dotnet publish "src/ProxyHarbor.Api/ProxyHarbor.Api.csproj" -c Release -o /app --no-restore \
    /p:UseAppHost=false /p:Version="$VERSION" /p:SourceRevisionId="$SOURCE_REVISION" /p:ContinuousIntegrationBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble@sha256:aac50ceef10391e78a899e3d2ccd00bbafa2dcb56ccf4586cbccf00edb84fc21 AS runtime
# Production crashed repeatedly inside Alpine's musl loader. The supported Ubuntu/glibc
# runtime removes that native failure surface; curl provides the container healthcheck
# and libgssapi is loaded by Npgsql during streamed PostgreSQL backups.
# Use HTTPS while installing the patched APT packages and Ubuntu security builds.
RUN sed -i 's|^URIs: http://|URIs: https://|' /etc/apt/sources.list.d/ubuntu.sources && \
    apt-get update && \
    apt-get install -y --no-install-recommends apt libapt-pkg6.0t64 curl libgssapi-krb5-2 libssl3t64 openssl && \
    dpkg --compare-versions "$(dpkg-query -W -f='${Version}' apt)" ge 2.8.4ubuntu0.1 && \
    dpkg --compare-versions "$(dpkg-query -W -f='${Version}' libapt-pkg6.0t64)" ge 2.8.4ubuntu0.1 && \
    rm -rf /var/lib/apt/lists/* && \
    groupadd --system --gid 101 proxyharbor && \
    useradd --system --uid 100 --gid 101 --home-dir /app --no-create-home --shell /usr/sbin/nologin proxyharbor && \
    mkdir -p /app/backups /app/data-protection /app/geo-data && \
    chown -R proxyharbor:proxyharbor /app
WORKDIR /app
COPY --from=build --chown=proxyharbor:proxyharbor /app .
USER proxyharbor
EXPOSE 8080
# The .NET 10 ASP.NET runtime image already defines ASPNETCORE_HTTP_PORTS=8080.
# Setting ASPNETCORE_URLS as well makes the host override HTTP_PORTS and emits a
# misleading warning on every clean start, hiding actionable startup warnings.
ENV DOTNET_EnableDiagnostics=0 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD curl --fail --silent --show-error --output /dev/null http://127.0.0.1:8080/health/ready || exit 1
ENTRYPOINT ["dotnet", "ProxyHarbor.Api.dll"]
