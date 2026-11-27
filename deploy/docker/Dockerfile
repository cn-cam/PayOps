# syntax=docker/dockerfile:1
# 构建上下文为仓库根目录：
#   docker build -f deploy/docker/Dockerfile -t payops:1.1.0 .
# 同一个镜像通过 PayOps__Roles 环境变量决定角色（All / Gateway / Admin / Worker 任意组合）。

ARG NODE_IMAGE=node:24-alpine
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0

# ---------- 管理后台（Vue3） ----------
FROM ${NODE_IMAGE} AS web
ARG NPM_REGISTRY=https://registry.npmjs.org
WORKDIR /web
COPY web/admin/package.json web/admin/package-lock.json ./
RUN npm ci --registry=${NPM_REGISTRY} --no-audit --no-fund
COPY web/admin/ ./
RUN npm run build

# ---------- 后端（.NET 10） ----------
FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/PayOps.Core/PayOps.Core.csproj src/PayOps.Core/
COPY src/PayOps.Providers/PayOps.Providers.csproj src/PayOps.Providers/
COPY src/PayOps.Service/PayOps.Service.csproj src/PayOps.Service/
COPY src/PayOps.Server/PayOps.Server.csproj src/PayOps.Server/
RUN dotnet restore src/PayOps.Server/PayOps.Server.csproj
COPY src/ src/
RUN dotnet publish src/PayOps.Server/PayOps.Server.csproj -c Release -o /app --no-restore -p:UseAppHost=false

# ---------- 运行时 ----------
FROM ${RUNTIME_IMAGE}
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0
COPY --from=build /app ./
COPY --from=web /web/dist ./wwwroot
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=5s --start-period=40s --retries=3 \
  CMD ["dotnet", "PayOps.Server.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "PayOps.Server.dll"]
