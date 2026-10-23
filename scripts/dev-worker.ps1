<#
.SYNOPSIS  Runs the processing service (FastAPI + job worker) on http://127.0.0.1:8008.
#>
param([switch]$NoWorker, [switch]$DevEndpoints)
$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot "load-env.ps1")
$Tools = if ($env:CLIPFORGE_TOOLS_DIR) { $env:CLIPFORGE_TOOLS_DIR } else { Join-Path $env:LOCALAPPDATA "ClipForge\tools" }
if (-not $env:FFMPEG_PATH) { $env:FFMPEG_PATH = Join-Path $Tools "ffmpeg\bin\ffmpeg.exe" }
if (-not $env:FFPROBE_PATH) { $env:FFPROBE_PATH = Join-Path $Tools "ffmpeg\bin\ffprobe.exe" }
if (-not $env:WORK_DIR) { $env:WORK_DIR = Join-Path $env:LOCALAPPDATA "ClipForge\work" }
if (-not $env:MODELS_DIR) { $env:MODELS_DIR = Join-Path $env:LOCALAPPDATA "ClipForge\models" }
if (-not $env:CLIPFORGE_API_BASE_URL) { $env:CLIPFORGE_API_BASE_URL = "http://localhost:5080" }
if ($NoWorker) { $env:WORKER_ENABLED = "false" }
if ($DevEndpoints) { $env:DEV_ENDPOINTS = "true" }
$py = Join-Path $Root "processing\.venv\Scripts\python.exe"
if (-not (Test-Path $py)) { throw "Python venv missing. Run scripts\dev-setup.ps1 first." }
Push-Location (Join-Path $Root "processing")
try {
    Write-Host "Processing: http://127.0.0.1:8008/health  (worker enabled: $($env:WORKER_ENABLED -ne 'false'))" -ForegroundColor Green
    & $py -m uvicorn app.main:app --host 127.0.0.1 --port 8008
} finally { Pop-Location }
