#!/usr/bin/env bash
# 单体部署打包：构建管理后台 + 发布后端到一个目录，一个进程承担全部角色（Gateway + Admin + Worker）。
#   deploy/standalone/publish.sh                 # 依赖目标机已安装 .NET 10 运行时
#   RUNTIME=linux-x64 deploy/standalone/publish.sh   # 自包含发布
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT="${OUTPUT:-$ROOT/artifacts/payops}"
RUNTIME="${RUNTIME:-}"

if [ "${SKIP_WEB:-0}" != "1" ]; then
  (cd "$ROOT/web/admin" && npm ci --no-audit --no-fund && npm run build)
fi

ARGS=(publish "$ROOT/src/PayOps.Server/PayOps.Server.csproj" -c Release -o "$OUT" --nologo)
if [ -n "$RUNTIME" ]; then
  ARGS+=(-r "$RUNTIME" --self-contained true)
fi
dotnet "${ARGS[@]}"

if [ -d "$ROOT/web/admin/dist" ]; then
  rm -rf "$OUT/wwwroot"
  cp -r "$ROOT/web/admin/dist" "$OUT/wwwroot"
fi

rm -f "$OUT/appsettings.Development.json"
cp "$ROOT/deploy/standalone/appsettings.Production.example.json" "$OUT/"

echo
echo "发布完成：$OUT"
echo "下一步：把 appsettings.Production.example.json 复制为 appsettings.Production.json 并填写连接信息，然后启动："
echo "  ASPNETCORE_ENVIRONMENT=Production dotnet PayOps.Server.dll"
