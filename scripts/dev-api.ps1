<#
.SYNOPSIS  Runs the ASP.NET Core API on http://localhost:5080 with the .env configuration.
#>
param([switch]$NoBuild)
$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot "load-env.ps1")
if (-not $env:ASPNETCORE_ENVIRONMENT) { $env:ASPNETCORE_ENVIRONMENT = "Development" }
if (-not $env:Storage__Local__RootPath) { $env:Storage__Local__RootPath = Join-Path $env:LOCALAPPDATA "ClipForge\storage" }
$args = @("run", "--project", (Join-Path $Root "backend\src\ClipForge.Api\ClipForge.Api.csproj"))
if ($NoBuild) { $args += "--no-build" }
Write-Host "API: http://localhost:5080  (OpenAPI UI: http://localhost:5080/scalar)" -ForegroundColor Green
& dotnet @args
