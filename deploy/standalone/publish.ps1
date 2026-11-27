<#
.SYNOPSIS
  单体部署打包：构建管理后台 + 发布后端到一个目录，一个进程承担全部角色（Gateway + Admin + Worker）。

.EXAMPLE
  pwsh deploy/standalone/publish.ps1                       # 依赖目标机已安装 .NET 10 运行时
  pwsh deploy/standalone/publish.ps1 -Runtime linux-x64    # 自包含发布，目标机无需安装运行时
#>
param(
    [string]$Output = "artifacts/payops",
    [string]$Runtime = "",
    [switch]$SkipWeb
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "../..")
$out = Join-Path $root $Output

if (-not $SkipWeb) {
    Push-Location (Join-Path $root "web/admin")
    try {
        npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm ci 失败" }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "前端构建失败" }
    } finally { Pop-Location }
}

$publishArgs = @("publish", (Join-Path $root "src/PayOps.Server/PayOps.Server.csproj"), "-c", "Release", "-o", $out, "--nologo")
if ($Runtime) { $publishArgs += @("-r", $Runtime, "--self-contained", "true") }
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

$dist = Join-Path $root "web/admin/dist"
if (Test-Path $dist) {
    $wwwroot = Join-Path $out "wwwroot"
    if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }
    Copy-Item $dist $wwwroot -Recurse
}

Remove-Item (Join-Path $out "appsettings.Development.json") -ErrorAction SilentlyContinue
Copy-Item (Join-Path $PSScriptRoot "appsettings.Production.example.json") (Join-Path $out "appsettings.Production.example.json") -Force

Write-Host ""
Write-Host "发布完成：$out"
Write-Host "下一步：把 appsettings.Production.example.json 复制为 appsettings.Production.json 并填写连接信息，然后启动："
Write-Host "  ASPNETCORE_ENVIRONMENT=Production dotnet PayOps.Server.dll"
