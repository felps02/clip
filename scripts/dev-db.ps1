<#
.SYNOPSIS  Controls the portable PostgreSQL used for local development.
.EXAMPLE   .\scripts\dev-db.ps1 start | stop | status | restart | psql [db]
#>
param(
    [Parameter(Position = 0)][ValidateSet("start", "stop", "status", "restart", "psql")][string]$Action = "status",
    [Parameter(Position = 1)][string]$Database = "clipforge",
    [string]$ToolsDir = $(if ($env:CLIPFORGE_TOOLS_DIR) { $env:CLIPFORGE_TOOLS_DIR } else { Join-Path $env:LOCALAPPDATA "ClipForge\tools" }),
    [int]$Port = 5432
)
$ErrorActionPreference = "Stop"
$PgBin = Join-Path $ToolsDir "pgsql\bin"
$PgData = Join-Path $env:LOCALAPPDATA "ClipForge\pgdata"
$Log = Join-Path $env:LOCALAPPDATA "ClipForge\logs\postgres.log"
$pgctl = Join-Path $PgBin "pg_ctl.exe"
if (-not (Test-Path $pgctl)) { throw "pg_ctl not found in $PgBin. Run scripts\dev-setup.ps1 first." }
New-Item -ItemType Directory -Force -Path (Split-Path $Log -Parent) | Out-Null

switch ($Action) {
    "start" {
        & $pgctl -D $PgData status *> $null
        if ($LASTEXITCODE -eq 0) { Write-Host "PostgreSQL already running on port $Port."; break }
        # Start detached so the server does not inherit this console's handles.
        Start-Process -FilePath $pgctl -ArgumentList @("-D", "`"$PgData`"", "-l", "`"$Log`"", "-o", "`"-p $Port`"", "start") -WindowStyle Hidden -Wait
        $deadline = (Get-Date).AddSeconds(30)
        do {
            Start-Sleep -Milliseconds 500
            & (Join-Path $PgBin "pg_isready.exe") -h localhost -p $Port *> $null
        } until ($LASTEXITCODE -eq 0 -or (Get-Date) -gt $deadline)
        if ($LASTEXITCODE -eq 0) { Write-Host "PostgreSQL started on port $Port (log: $Log)." -ForegroundColor Green }
        else { throw "PostgreSQL did not become ready. See $Log" }
    }
    "stop" { & $pgctl -D $PgData stop -m fast; Write-Host "PostgreSQL stopped." }
    "restart" { & $pgctl -D $PgData restart -m fast -l $Log -o "-p $Port" }
    "status" { & $pgctl -D $PgData status }
    "psql" {
        $env:PGPASSWORD = "clipforge"
        & (Join-Path $PgBin "psql.exe") -U clipforge -h localhost -p $Port -d $Database
        Remove-Item Env:PGPASSWORD
    }
}
