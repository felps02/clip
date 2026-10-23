# Loads KEY=VALUE pairs from a .env file into the current process environment.
# Usage: . .\scripts\load-env.ps1 [-Path .env]
param(
    [string]$Path = (Join-Path (Split-Path $PSScriptRoot -Parent) ".env")
)

if (-not (Test-Path $Path)) {
    Write-Host "No .env at $Path (using built-in defaults)." -ForegroundColor DarkGray
    return
}

Get-Content $Path | ForEach-Object {
    $line = $_.Trim()
    if ($line -eq "" -or $line.StartsWith("#")) { return }
    $idx = $line.IndexOf("=")
    if ($idx -lt 1) { return }
    $key = $line.Substring(0, $idx).Trim()
    $value = $line.Substring($idx + 1).Trim()
    if (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'"))) {
        $value = $value.Substring(1, $value.Length - 2)
    }
    $value = [Environment]::ExpandEnvironmentVariables($value)
    if (-not [string]::IsNullOrEmpty($value)) {
        Set-Item -Path "Env:$key" -Value $value
    }
}
Write-Host "Loaded environment from $Path" -ForegroundColor DarkGray
