FROM mcr.microsoft.com/dotnet/sdk:10.0.302-alpine3.23 AS build
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
RUN dotnet publish "src/ProxyHarbor.Restore/ProxyHarbor.Restore.csproj" -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10-alpine3.23 AS runtime
# Restore работает без root. Отдельная точка монтирования получает временный
# расшифрованный ZIP и удаляется вместе с одноразовым контейнером.
RUN addgroup -S proxyharbor && adduser -S proxyharbor -G proxyharbor && \
    mkdir -p /app/backups /restore-temp && chown -R proxyharbor:proxyharbor /app /restore-temp
WORKDIR /app
COPY --from=build --chown=proxyharbor:proxyharbor /app .
USER proxyharbor
ENV DOTNET_EnableDiagnostics=0 TMPDIR=/restore-temp
ENTRYPOINT ["dotnet", "ProxyHarbor.Restore.dll"]
# Без явно переданной команды контейнер только печатает справку и не меняет БД.
CMD ["--help"]
