# Один образ: сервис (с панелью в wwwroot), Migrator и симулятор источника.

# Панель (React)
FROM node:24.22.1-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/index.html web/tsconfig.json web/vite.config.ts ./
COPY web/public ./public
COPY web/src ./src
RUN npm run build

# Сервис и Migrator
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS builder
ARG SERVICE_NAME=Aeza.Risk
WORKDIR /app
COPY global.json nuget.config ./
COPY src/ src/
RUN dotnet restore src/${SERVICE_NAME}.Migrator --locked-mode \
 && dotnet publish src/${SERVICE_NAME}.Migrator -c Release -o /app/publish/migrator --no-restore /p:UseAppHost=false
RUN dotnet restore src/${SERVICE_NAME}.Web --locked-mode \
 && dotnet publish src/${SERVICE_NAME}.Web -c Release -o /app/publish --no-restore /p:UseAppHost=false
# ONNX Runtime кладёт нативные библиотеки всех платформ: оставляем только Linux целевой архитектуры
ARG TARGETARCH=amd64
RUN keep=$([ "$TARGETARCH" = "arm64" ] && echo linux-arm64 || echo linux-x64) \
 && for dir in /app/publish/runtimes /app/publish/migrator/runtimes; do \
      [ -d "$dir" ] && find "$dir" -mindepth 1 -maxdepth 1 -type d ! -name "$keep" -exec rm -rf {} +; true; \
    done
# Симулятор источника: /app/simulator/Aeza.Risk.SourceSimulator.dll
COPY tools/ tools/
RUN dotnet restore tools/${SERVICE_NAME}.SourceSimulator --locked-mode \
 && dotnet publish tools/${SERVICE_NAME}.SourceSimulator -c Release -o /app/publish/simulator --no-restore /p:UseAppHost=false  && mkdir -p /simstate
COPY --from=web /web/dist /app/publish/wwwroot
# В chiseled-образе нет shell, поэтому ссылки на точки входа создаются здесь
RUN ln -s ${SERVICE_NAME}.Web.dll /app/publish/service.runtime.dll \
 && ln -s migrator/${SERVICE_NAME}.Migrator.dll /app/publish/service.migrator.dll

# Образ выполнения: chiseled, без root
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS base
WORKDIR /app
COPY --from=builder /app/publish/ .
# Каталог состояния симулятора с правами пользователя приложения
COPY --from=builder --chown=1654:1654 /simstate /state

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:5000 \
    DOTNET_gcServer=0
EXPOSE 5000

# Версия сборки (видна в OpenAPI info.version)
ARG BUILD_VERSION=Undefined
ENV BUILD_VERSION=${BUILD_VERSION}
ARG BUILD_DATE=Undefined
ENV BUILD_DATE=${BUILD_DATE}
LABEL org.opencontainers.image.title="aeza-risk-analyzer" org.opencontainers.image.version="${BUILD_VERSION}"

USER $APP_UID
HEALTHCHECK --interval=10s --timeout=5s --start-period=20s --retries=6 CMD ["dotnet", "/app/service.runtime.dll", "healthcheck"]

ENTRYPOINT ["dotnet", "/app/service.runtime.dll"]
