FROM mcr.microsoft.com/dotnet/sdk:10.0.400-alpine3.23@sha256:b36516b249f0cccf9e5017082f51d4bda2d61469f205a7167fbf3b8498ecdd59 AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11-alpine3.23@sha256:4d5339ac9814f1a033a09e664bcf159e9fb386c89b6e7917b3dca7254e656027
WORKDIR /app
RUN addgroup -S checker && adduser -S checker -G checker
COPY --from=build --chown=checker:checker /app/publish ./
USER checker
ENV DOTNET_EnableDiagnostics=0 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true
ENTRYPOINT ["dotnet", "ProxyHarbor.CheckerAgent.dll"]
