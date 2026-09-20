FROM mcr.microsoft.com/dotnet/sdk:10.0.401-alpine3.23@sha256:84c5d09dfeb4275f6540084e69331f140223eb968b5ef48023923b0b2eda156b AS build
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["NuGet.Config", "global.json", "./"]
COPY ["src/ProxyHarbor.Domain/ProxyHarbor.Domain.csproj", "src/ProxyHarbor.Domain/"]
COPY ["src/ProxyHarbor.Domain/packages.lock.json", "src/ProxyHarbor.Domain/"]
COPY ["src/ProxyHarbor.Infrastructure/ProxyHarbor.Infrastructure.csproj", "src/ProxyHarbor.Infrastructure/"]
COPY ["src/ProxyHarbor.Infrastructure/packages.lock.json", "src/ProxyHarbor.Infrastructure/"]
COPY ["src/ProxyHarbor.Restore/ProxyHarbor.Restore.csproj", "src/ProxyHarbor.Restore/"]
COPY ["src/ProxyHarbor.Restore/packages.lock.json", "src/ProxyHarbor.Restore/"]
RUN dotnet restore "src/ProxyHarbor.Restore/ProxyHarbor.Restore.csproj" --locked-mode
COPY . .
ARG VERSION=0.0.0-local
ARG SOURCE_REVISION=unknown
RUN dotnet publish "src/ProxyHarbor.Restore/ProxyHarbor.Restore.csproj" -c Release -o /app --no-restore \
    /p:UseAppHost=false /p:Version="$VERSION" /p:SourceRevisionId="$SOURCE_REVISION" /p:ContinuousIntegrationBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-alpine3.23@sha256:23c1e50c547546cb1192b84369ecfc6fa227cb447b17fee57456bd178962c213 AS runtime
RUN apk upgrade --no-cache libcrypto3 libssl3
# Restore работает без root. Отдельная точка монтирования получает временный
# расшифрованный ZIP и удаляется вместе с одноразовым контейнером.
RUN apk add --no-cache krb5-libs && \
    addgroup -S proxyharbor && adduser -S proxyharbor -G proxyharbor && \
    mkdir -p /app/backups /restore-temp && chown -R proxyharbor:proxyharbor /app /restore-temp
WORKDIR /app
COPY --from=build --chown=proxyharbor:proxyharbor /app .
USER proxyharbor
ENV DOTNET_EnableDiagnostics=0 TMPDIR=/restore-temp
ENTRYPOINT ["dotnet", "ProxyHarbor.Restore.dll"]
# Без явно переданной команды контейнер только печатает справку и не меняет БД.
CMD ["--help"]
