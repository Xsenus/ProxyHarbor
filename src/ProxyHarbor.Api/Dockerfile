FROM mcr.microsoft.com/dotnet/sdk:10.0.401-alpine3.23@sha256:3f9c03432d664163a90d20e3ed0a3784d0aa82c1b9cbd1a7dd4609fede95669e AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble@sha256:2d584d8147faddb0d678c5748d47953e5b8e18621ed4fb7049a91381d9d7746f AS runtime
# Production crashed repeatedly inside Alpine's musl loader. The supported Ubuntu/glibc
# runtime removes that native failure surface; curl provides the container healthcheck
# and libgssapi is loaded by Npgsql during streamed PostgreSQL backups.
RUN apt-get update && apt-get install -y --no-install-recommends curl libgssapi-krb5-2 && \
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
