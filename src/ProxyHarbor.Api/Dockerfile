FROM mcr.microsoft.com/dotnet/sdk:10.0.302-alpine3.23@sha256:d8ee39817ca03a3757288e83c37ed73cc969a286c603b827c7cbe33add1c2d1c AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10-alpine3.23@sha256:27b6b84beeede74fd16886177d360799c8e4299ceadfbd64eef57bafead7878a AS runtime
# ICU нужен RequestLocalization и корректному форматированию ru/en/de/fr/zh в Alpine.
RUN apk add --no-cache icu-libs && addgroup -S proxyharbor && adduser -S proxyharbor -G proxyharbor && mkdir -p /app/backups /app/data-protection /app/geo-data && chown -R proxyharbor:proxyharbor /app
WORKDIR /app
COPY --from=build --chown=proxyharbor:proxyharbor /app .
USER proxyharbor
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics=0 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD wget -q --spider http://127.0.0.1:8080/health/ready || exit 1
ENTRYPOINT ["dotnet", "ProxyHarbor.Api.dll"]
