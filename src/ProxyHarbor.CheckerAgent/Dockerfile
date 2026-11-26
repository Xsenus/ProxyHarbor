FROM mcr.microsoft.com/dotnet/sdk:10.0.404-alpine3.24@sha256:0294f285d062afacc8b5f6d7febb0cd4ee879320e5585fa15f2f4ebf1feb9ba7 AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-alpine3.24@sha256:21c3f9798416cba4c0576a947c286231cb71ea2884896cb968253f5c9668f930
WORKDIR /app
RUN apk upgrade --no-cache && \
    addgroup -S checker && adduser -S checker -G checker
COPY --from=build --chown=checker:checker /app/publish ./
USER checker
ENV DOTNET_EnableDiagnostics=0 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true
ENTRYPOINT ["dotnet", "ProxyHarbor.CheckerAgent.dll"]
