FROM mcr.microsoft.com/dotnet/sdk:10.0.401-alpine3.23@sha256:84c5d09dfeb4275f6540084e69331f140223eb968b5ef48023923b0b2eda156b AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble@sha256:6a94333d37514e385650a3c81a55e5350b67253dbe136e9cf17e499c35606a8c AS runtime
# Production crashed repeatedly inside Alpine's musl loader. The supported Ubuntu/glibc
# runtime removes that native failure surface; curl provides the container healthcheck
# and libgssapi is loaded by Npgsql during streamed PostgreSQL backups.
RUN apt-get update && apt-get install -y --no-install-recommends curl libgssapi-krb5-2 && \
    rm -rf /var/lib/apt/lists/* && \
    groupadd --system proxyharbor && \
    useradd --system --gid proxyharbor --home-dir /app --no-create-home --shell /usr/sbin/nologin proxyharbor && \
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
