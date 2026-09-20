FROM mcr.microsoft.com/dotnet/sdk:10.0.401-alpine3.23@sha256:3f9c03432d664163a90d20e3ed0a3784d0aa82c1b9cbd1a7dd4609fede95669e AS build
WORKDIR /src
COPY Directory.Build.props NuGet.Config global.json ./
COPY src/ProxyHarbor.Domain/ProxyHarbor.Domain.csproj src/ProxyHarbor.Domain/packages.lock.json src/ProxyHarbor.Domain/
COPY src/ProxyHarbor.Infrastructure/ProxyHarbor.Infrastructure.csproj src/ProxyHarbor.Infrastructure/packages.lock.json src/ProxyHarbor.Infrastructure/
COPY src/ProxyHarbor.CheckerAgent/ProxyHarbor.CheckerAgent.csproj src/ProxyHarbor.CheckerAgent/packages.lock.json src/ProxyHarbor.CheckerAgent/
RUN dotnet restore src/ProxyHarbor.CheckerAgent/ProxyHarbor.CheckerAgent.csproj --locked-mode
COPY src/ProxyHarbor.Domain/ src/ProxyHarbor.Domain/
COPY src/ProxyHarbor.Infrastructure/ src/ProxyHarbor.Infrastructure/
COPY src/ProxyHarbor.CheckerAgent/ src/ProxyHarbor.CheckerAgent/
ARG VERSION=0.0.0-local
ARG SOURCE_REVISION=unknown
RUN dotnet publish src/ProxyHarbor.CheckerAgent/ProxyHarbor.CheckerAgent.csproj -c Release --no-restore -o /app/publish \
    -p:Version=$VERSION -p:InformationalVersion=$VERSION+$SOURCE_REVISION -p:ContinuousIntegrationBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-alpine3.23@sha256:68eeb5922e4737a0f4657e3a631e566c38186f910c7155fb7c377414ce58a64d
WORKDIR /app
RUN apk upgrade --no-cache libcrypto3 libssl3 && \
    addgroup -S checker && adduser -S checker -G checker
COPY --from=build --chown=checker:checker /app/publish ./
USER checker
ENV DOTNET_EnableDiagnostics=0 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true
ENTRYPOINT ["dotnet", "ProxyHarbor.CheckerAgent.dll"]
