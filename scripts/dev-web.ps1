<#
.SYNOPSIS  Runs the Next.js frontend on http://localhost:3000.
#>
$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot "load-env.ps1")
if (-not $env:NEXT_PUBLIC_API_BASE_URL) { $env:NEXT_PUBLIC_API_BASE_URL = "http://localhost:5080" }
Push-Location (Join-Path $Root "frontend")
try {
    if (-not (Test-Path "node_modules")) { pnpm install }
    Write-Host "Web: http://localhost:3000" -ForegroundColor Green
    pnpm dev
} finally { Pop-Location }
