<#
.SYNOPSIS
  One-time developer setup for Windows machines without Docker.

.DESCRIPTION
  Downloads FFmpeg (gyan.dev essentials build) and a portable PostgreSQL 18 (EDB binaries,
  no installer, no admin rights) into $ToolsDir (default %LOCALAPPDATA%\ClipForge\tools),
  initialises a database cluster in %LOCALAPPDATA%\ClipForge\pgdata, creates the
  'clipforge' role and the 'clipforge' / 'clipforge_test' databases, creates the Python venv
  for processing/, installs frontend packages with pnpm and restores the .NET solution.
  Safe to re-run: every step is skipped when already done.

.PARAMETER ToolsDir
  Where binaries are stored. Override with the CLIPFORGE_TOOLS_DIR environment variable.
.PARAMETER SkipDownloads  Skip FFmpeg/PostgreSQL/LLM downloads (use existing binaries).
.PARAMETER SkipPackages   Skip pip/pnpm/dotnet restores.
.PARAMETER SkipLlm        Skip the local LLM (llama.cpp + ~1.1 GB model) used by the assistant chat.
#>
param(
    [string]$ToolsDir = $(if ($env:CLIPFORGE_TOOLS_DIR) { $env:CLIPFORGE_TOOLS_DIR } else { Join-Path $env:LOCALAPPDATA "ClipForge\tools" }),
    [string]$PgVersion = "18.1-1",
    [string]$LlamaBuild = "b11836",
    [string]$LlmModelUrl = "https://huggingface.co/unsloth/Qwen3-1.7B-GGUF/resolve/main/Qwen3-1.7B-Q4_K_M.gguf",
    [switch]$SkipDownloads,
    [switch]$SkipPackages,
    [switch]$SkipLlm
)

$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent
$AppData = Join-Path $env:LOCALAPPDATA "ClipForge"
$PgData = Join-Path $AppData "pgdata"
$Logs = Join-Path $AppData "logs"
New-Item -ItemType Directory -Force -Path $ToolsDir, (Join-Path $ToolsDir "downloads"), $Logs, (Join-Path $AppData "storage"), (Join-Path $AppData "work"), (Join-Path $AppData "models") | Out-Null

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

# ---------------------------------------------------------------- FFmpeg --
$FfmpegBin = Join-Path $ToolsDir "ffmpeg\bin\ffmpeg.exe"
if (-not $SkipDownloads -and -not (Test-Path $FfmpegBin)) {
    Step "Downloading FFmpeg (essentials build)"
    $zip = Join-Path $ToolsDir "downloads\ffmpeg-release-essentials.zip"
    Invoke-WebRequest -Uri "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" -OutFile $zip
    $tmp = Join-Path $ToolsDir "ffmpeg-tmp"
    if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    $inner = Get-ChildItem $tmp -Directory | Select-Object -First 1
    Move-Item $inner.FullName (Join-Path $ToolsDir "ffmpeg")
    Remove-Item -Recurse -Force $tmp
}
if (Test-Path $FfmpegBin) { Write-Host "FFmpeg: $FfmpegBin" } else { Write-Warning "FFmpeg not found at $FfmpegBin" }

# ------------------------------------------------------------- Local LLM --
# The product's own AI for the assistant chat: llama.cpp (CPU build) + an open model (Apache-2.0).
$LlamaServer = Join-Path $ToolsDir "llama\llama-server.exe"
$LlmModel = Join-Path $AppData "models\llm\$(Split-Path $LlmModelUrl -Leaf)"
if (-not $SkipDownloads -and -not $SkipLlm) {
    if (-not (Test-Path $LlamaServer)) {
        Step "Downloading llama.cpp $LlamaBuild (CPU build)"
        $zip = Join-Path $ToolsDir "downloads\llama-$LlamaBuild-bin-win-cpu-x64.zip"
        Invoke-WebRequest -Uri "https://github.com/ggml-org/llama.cpp/releases/download/$LlamaBuild/llama-$LlamaBuild-bin-win-cpu-x64.zip" -OutFile $zip
        Expand-Archive -Path $zip -DestinationPath (Join-Path $ToolsDir "llama") -Force
        Remove-Item $zip -Force
    }
    if (-not (Test-Path $LlmModel)) {
        Step "Downloading the local LLM model (~1.1 GB)"
        New-Item -ItemType Directory -Force -Path (Split-Path $LlmModel -Parent) | Out-Null
        Invoke-WebRequest -Uri $LlmModelUrl -OutFile "$LlmModel.part"
        Move-Item "$LlmModel.part" $LlmModel -Force
    }
}
if (Test-Path $LlamaServer) { Write-Host "llama-server: $LlamaServer" } elseif (-not $SkipLlm) { Write-Warning "llama-server not found; the assistant chat will report 'indisponível'" }

# ------------------------------------------------------------ PostgreSQL --
$PgBin = Join-Path $ToolsDir "pgsql\bin"
if (-not $SkipDownloads -and -not (Test-Path (Join-Path $PgBin "pg_ctl.exe"))) {
    Step "Downloading portable PostgreSQL $PgVersion"
    $zip = Join-Path $ToolsDir "downloads\postgresql-$PgVersion-windows-x64-binaries.zip"
    Invoke-WebRequest -Uri "https://get.enterprisedb.com/postgresql/postgresql-$PgVersion-windows-x64-binaries.zip" -OutFile $zip
    Expand-Archive -Path $zip -DestinationPath $ToolsDir -Force
    foreach ($d in "pgAdmin 4", "doc", "symbols", "include", "StackBuilder") {
        $p = Join-Path $ToolsDir "pgsql\$d"; if (Test-Path $p) { Remove-Item -Recurse -Force $p }
    }
}
if (-not (Test-Path (Join-Path $PgBin "pg_ctl.exe"))) { throw "PostgreSQL binaries not found in $PgBin" }

if (-not (Test-Path (Join-Path $PgData "PG_VERSION"))) {
    Step "Initialising PostgreSQL cluster in $PgData"
    $pw = Join-Path $Logs "pwfile.tmp"
    Set-Content -Path $pw -Value "postgres" -Encoding ascii -NoNewline
    & (Join-Path $PgBin "initdb.exe") -D $PgData -U postgres --auth=scram-sha-256 --pwfile=$pw -E UTF8 --locale=C | Out-Null
    Remove-Item $pw -Force
}

Step "Starting PostgreSQL"
& (Join-Path $PSScriptRoot "dev-db.ps1") start

$env:PGPASSWORD = "postgres"
$psql = Join-Path $PgBin "psql.exe"
$roleExists = & $psql -U postgres -h localhost -p 5432 -tAc "SELECT 1 FROM pg_roles WHERE rolname='clipforge'"
if ($roleExists -ne "1") { & $psql -U postgres -h localhost -p 5432 -c "CREATE ROLE clipforge LOGIN PASSWORD 'clipforge' CREATEDB;" | Out-Null }
foreach ($db in "clipforge", "clipforge_test") {
    $exists = & $psql -U postgres -h localhost -p 5432 -tAc "SELECT 1 FROM pg_database WHERE datname='$db'"
    if ($exists -ne "1") { & $psql -U postgres -h localhost -p 5432 -c "CREATE DATABASE $db OWNER clipforge;" | Out-Null }
}
Remove-Item Env:PGPASSWORD
Write-Host "Databases ready: clipforge, clipforge_test (user clipforge / clipforge)"

# ---------------------------------------------------------------- .env ----
$envFile = Join-Path $Root ".env"
if (-not (Test-Path $envFile)) {
    Step "Creating .env from .env.example"
    Copy-Item (Join-Path $Root ".env.example") $envFile
    Write-Host "Review $envFile and replace the change-me secrets before exposing the API." -ForegroundColor Yellow
}

if (-not $SkipPackages) {
    # ---------------------------------------------------------- Python ----
    $venvPy = Join-Path $Root "processing\.venv\Scripts\python.exe"
    if (-not (Test-Path $venvPy)) {
        Step "Creating Python virtual environment"
        python -m venv (Join-Path $Root "processing\.venv")
    }
    Step "Installing processing dependencies"
    & $venvPy -m pip install --upgrade pip -q
    & $venvPy -m pip install -q -r (Join-Path $Root "processing\requirements.txt")

    # -------------------------------------------------------- Frontend ----
    if (Get-Command pnpm -ErrorAction SilentlyContinue) {
        Step "Installing frontend packages"
        Push-Location (Join-Path $Root "frontend"); pnpm install; Pop-Location
    } else { Write-Warning "pnpm not found: install with 'npm i -g pnpm' and run 'pnpm install' in frontend/." }

    # --------------------------------------------------------- Backend ----
    Step "Restoring .NET solution"
    dotnet restore (Join-Path $Root "backend\ClipForge.sln")
}

Write-Host "`nSetup complete. Next: .\scripts\dev-api.ps1, .\scripts\dev-worker.ps1, .\scripts\dev-web.ps1" -ForegroundColor Green
