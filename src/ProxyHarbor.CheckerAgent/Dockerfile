FROM mcr.microsoft.com/dotnet/sdk:10.0.302-alpine3.23@sha256:d8ee39817ca03a3757288e83c37ed73cc969a286c603b827c7cbe33add1c2d1c AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10-alpine3.23@sha256:27b6b84beeede74fd16886177d360799c8e4299ceadfbd64eef57bafead7878a
WORKDIR /app
RUN addgroup -S checker && adduser -S checker -G checker
COPY --from=build --chown=checker:checker /app/publish ./
USER checker
ENV DOTNET_EnableDiagnostics=0 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true
ENTRYPOINT ["dotnet", "ProxyHarbor.CheckerAgent.dll"]
