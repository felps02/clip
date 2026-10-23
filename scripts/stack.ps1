<#
.SYNOPSIS
  Packages and runs frozen copies of the whole stack (API + worker + web) on Windows.

.DESCRIPTION
  Two isolated profiles can run side by side:
    demo  ports 5080 / 8008 / 3000, database 'clipforge',     storage %LOCALAPPDATA%\ClipForge\storage
    e2e   ports 5180 / 8108 / 3100, database 'clipforge_e2e', storage %LOCALAPPDATA%\ClipForge\storage-e2e
  'package' publishes the API, copies the worker sources and builds the web app into
  %LOCALAPPDATA%\ClipForge\run\<profile>, so the running stack is independent of the source
  tree (keep editing and rebuilding while it runs). 'start' launches the three processes hidden
  (logs in %LOCALAPPDATA%\ClipForge\logs) and waits for their health checks. 'stop' stops only
  processes started from that profile's run folder.

  -Lan exposes the profile on the local network: API and web bind all interfaces, CORS and the
  e-mail links use the machine's LAN address (or -PublicHost). The web resolves the API host
  from the address the browser used, so both http://localhost:<web> and http://<lan-ip>:<web>
  keep the auth cookies first-party. Opening the ports may require a Windows Firewall rule.

.EXAMPLE
  .\scripts\stack.ps1 package -Name demo -Lan
  .\scripts\stack.ps1 start -Name demo -Lan
  .\scripts\stack.ps1 package -Name e2e ; .\scripts\stack.ps1 start -Name e2e
  .\scripts\stack.ps1 status -Name e2e
  .\scripts\stack.ps1 stop -Name e2e
#>
param(
    [Parameter(Position = 0)][ValidateSet("package", "start", "stop", "status", "restart")][string]$Action = "status",
    [ValidateSet("demo", "e2e")][string]$Name = "demo",
    [switch]$Lan,
    [string]$PublicHost,
    [switch]$SkipWebBuild,
    [switch]$StopLlm,
    # package: only these parts (default: all three)
    [ValidateSet("api", "worker", "web")][string[]]$Only,
    # package: copy the worker from this folder (a tested snapshot) instead of processing\
    [string]$WorkerSource
)

$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent
$Base = Join-Path $env:LOCALAPPDATA "ClipForge"
$Logs = Join-Path $Base "logs"
$Profiles = @{
    demo = @{ Api = 5080; Worker = 8008; Web = 3000; Db = "clipforge"; Storage = "storage"; Work = "work" }
    e2e  = @{ Api = 5180; Worker = 8108; Web = 3100; Db = "clipforge_e2e"; Storage = "storage-e2e"; Work = "work-e2e" }
}
$P = $Profiles[$Name]
$Run = Join-Path $Base "run\$Name"

function Get-LanAddress {
    if ($PublicHost) { return $PublicHost }
    $candidates = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*" -and $_.PrefixOrigin -in @("Dhcp", "Manual") -and $_.InterfaceAlias -notlike "vEthernet*" }
    $first = $candidates | Select-Object -First 1
    if (-not $first) { throw "Could not detect the LAN address; pass -PublicHost <ip>" }
    return $first.IPAddress
}

$PublicName = if ($Lan) { Get-LanAddress } else { "localhost" }
# The local LLM (llama.cpp) is shared by every profile: one server on 8090.
$LlmPort = 8090
$LlamaServer = Join-Path $Base "tools\llama\llama-server.exe"
$LlmModel = if ($env:LLM_MODEL_PATH) { $env:LLM_MODEL_PATH } else { Join-Path $Base "models\llm\Qwen3-1.7B-Q4_K_M.gguf" }
$HasLlm = (Test-Path $LlamaServer) -and (Test-Path $LlmModel)
$Services = [ordered]@{}
if ($HasLlm) { $Services["llm"] = @{ Port = $LlmPort; Health = "http://127.0.0.1:$LlmPort/health" } }
$Services["api"] = @{ Port = $P.Api; Health = "http://localhost:$($P.Api)/health" }
$Services["worker"] = @{ Port = $P.Worker; Health = "http://127.0.0.1:$($P.Worker)/health" }
$Services["web"] = @{ Port = $P.Web; Health = "http://localhost:$($P.Web)/" }

function Test-SourceSupports([string]$RelativePath, [string]$Pattern) {
    # Feature switches only make sense once the code that reads them exists.
    $file = Join-Path $Root $RelativePath
    if (-not (Test-Path $file)) { return $false }
    return [bool](Select-String -Path $file -Pattern $Pattern -SimpleMatch -CaseSensitive -List)
}

function Get-ListenerPid([int]$Port) {
    $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($conn) { return [int]$conn.OwningProcess }
    return $null
}

function Test-Health([string]$Url) {
    try { return (Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200 } catch { return $false }
}

function Copy-Tree([string]$Source, [string]$Destination) {
    # robocopy follows junctions by default, so pnpm links become real folders.
    & robocopy $Source $Destination /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE): $Source -> $Destination" }
}

function Repair-StandaloneLinks([string]$Standalone, [string]$Target) {
    # Next.js writes the top-level node_modules entries of a pnpm standalone build as links into
    # node_modules\.pnpm. On Windows they are absolute junctions, so a copied build would still
    # point at the source tree (or, once dereferenced, lose the sibling packages they resolve).
    # Re-create each top-level link inside the copy, pointing at the copied .pnpm folder.
    $sourceModules = Join-Path $Standalone "node_modules"
    $entries = @(Get-ChildItem -LiteralPath $sourceModules -Force | Where-Object { $_.Name -ne ".pnpm" })
    $scoped = @($entries | Where-Object { $_.Name.StartsWith("@") -and -not $_.LinkType } | ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Force })
    foreach ($item in @($entries + $scoped)) {
        if (-not $item.LinkType) { continue }
        $linkTarget = @($item.Target)[0]
        if (-not [System.IO.Path]::IsPathRooted($linkTarget)) {
            $linkTarget = [System.IO.Path]::GetFullPath((Join-Path (Split-Path $item.FullName -Parent) $linkTarget))
        }
        $relativeTarget = $linkTarget.Substring($Standalone.Length).TrimStart("\", "/")
        $relativeLink = $item.FullName.Substring($Standalone.Length).TrimStart("\", "/")
        $newLink = Join-Path $Target $relativeLink
        $newTarget = Join-Path $Target $relativeTarget
        if (-not (Test-Path -LiteralPath $newTarget)) { throw "link target missing in the copy: $newTarget" }
        if (Test-Path -LiteralPath $newLink) { Remove-Item -LiteralPath $newLink -Recurse -Force }
        New-Item -ItemType Junction -Path $newLink -Target $newTarget | Out-Null
    }
}

function Get-WebApiBaseUrl {
    # The web client replaces '{host}' with the hostname the browser used (see frontend/src/config).
    # Older frontends without that support get a fixed URL instead.
    $client = Get-ChildItem -Path (Join-Path $Root "frontend\src") -Recurse -Include *.ts, *.tsx -ErrorAction SilentlyContinue |
        Select-String -Pattern "{host}" -SimpleMatch -List | Select-Object -First 1
    if ($client) { return "http://{host}:$($P.Api)" }
    return "http://$($PublicName):$($P.Api)"
}

function Invoke-Package {
    New-Item -ItemType Directory -Force -Path $Run | Out-Null
    $doApi = -not $Only -or $Only -contains "api"
    $doWorker = -not $Only -or $Only -contains "worker"
    $doWeb = -not $Only -or $Only -contains "web"

    if ($doApi) {
        Write-Host "==> API (dotnet publish)" -ForegroundColor Cyan
        # Publish to a staging folder first so a failed build never leaves the profile without an API.
        $apiStage = Join-Path $Run "api.new"
        Remove-Tree $apiStage
        & dotnet publish (Join-Path $Root "backend\src\ClipForge.Api\ClipForge.Api.csproj") -c Release -o $apiStage -v q -nologo
        if ($LASTEXITCODE -ne 0) { Remove-Tree $apiStage; throw "dotnet publish failed" }
    }

    if ($doWorker) {
        $workerSrc = if ($WorkerSource) { $WorkerSource } else { Join-Path $Root "processing" }
        Write-Host "==> Worker (copy sources from $workerSrc)" -ForegroundColor Cyan
        $workerOut = Join-Path $Run "processing.new"
        Remove-Tree $workerOut
        New-Item -ItemType Directory -Force -Path $workerOut | Out-Null
        foreach ($dir in "app", "assets") { Copy-Tree (Join-Path $workerSrc $dir) (Join-Path $workerOut $dir) }
        Copy-Item (Join-Path $workerSrc "pyproject.toml") $workerOut
        Get-ChildItem $workerOut -Recurse -Directory -Filter "__pycache__" | Remove-Item -Recurse -Force
    }

    if (-not $doWeb) { Write-Host "Packaged '$Name' ($($Only -join ', ')) into $Run" -ForegroundColor Green; return }
    Write-Host "==> Web (next build, standalone)" -ForegroundColor Cyan
    $frontend = Join-Path $Root "frontend"
    if (-not $SkipWebBuild) {
        $env:NEXT_PUBLIC_API_BASE_URL = Get-WebApiBaseUrl
        Write-Host "    NEXT_PUBLIC_API_BASE_URL=$($env:NEXT_PUBLIC_API_BASE_URL)"
        Push-Location $frontend
        try { pnpm build; if ($LASTEXITCODE -ne 0) { throw "pnpm build failed" } } finally { Pop-Location }
    }
    $standalone = Join-Path $frontend ".next\standalone"
    if (-not (Test-Path $standalone)) { throw "no standalone output in $standalone (run without -SkipWebBuild)" }
    $webOut = Join-Path $Run "web.new"
    Remove-Tree $webOut
    Copy-Tree $standalone $webOut
    Copy-Tree (Join-Path $frontend ".next\static") (Join-Path $webOut ".next\static")
    if (Test-Path (Join-Path $frontend "public")) { Copy-Tree (Join-Path $frontend "public") (Join-Path $webOut "public") }
    Repair-StandaloneLinks -Standalone (Resolve-Path $standalone).Path -Target $webOut
    Write-Host "Packaged '$Name' into $Run (new parts go live on the next start/restart)" -ForegroundColor Green
}

function Remove-Tree([string]$Path) {
    # robocopy handles paths longer than 260 characters (deep pnpm folders); Remove-Item on
    # Windows PowerShell 5.1 does not and can leave a half-deleted folder behind.
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $empty = Join-Path $env:TEMP ("cf-empty-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $empty | Out-Null
    & robocopy $empty $Path /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    Remove-Item -LiteralPath $empty -Force
    Remove-Item -LiteralPath $Path -Recurse -Force
}

function Repair-RenamedLinks([string]$Folder, [string]$OldName, [string]$NewName) {
    # Junctions store absolute targets: after renaming web.new to web they still point into web.new.
    $modules = Join-Path $Folder "node_modules"
    $entries = @(Get-ChildItem -LiteralPath $modules -Force | Where-Object { $_.Name -ne ".pnpm" })
    $scoped = @($entries | Where-Object { $_.Name.StartsWith("@") -and -not $_.LinkType } | ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Force })
    foreach ($item in @($entries + $scoped)) {
        if (-not $item.LinkType) { continue }
        $target = @($item.Target)[0]
        $fixed = $target -replace [regex]::Escape("\$OldName\"), "\$NewName\"
        if ($fixed -eq $target) { continue }
        $path = $item.FullName
        cmd /c rmdir "$path" | Out-Null
        New-Item -ItemType Junction -Path $path -Target $fixed | Out-Null
    }
}

function Publish-NewParts {
    # Packaged parts wait in <part>.new and replace the live folder only while that service is stopped.
    foreach ($pair in @(@("api", $P.Api), @("processing", $P.Worker), @("web", $P.Web))) {
        $part = $pair[0]; $port = $pair[1]
        $staged = Join-Path $Run "$part.new"
        if (-not (Test-Path $staged)) { continue }
        if (Get-ListenerPid $port) { Write-Warning "$part is running; its new version goes live after a restart"; continue }
        $live = Join-Path $Run $part
        Remove-Tree $live
        Rename-Item -LiteralPath $staged -NewName $part
        if ($part -eq "web") { Repair-RenamedLinks -Folder $live -OldName "web.new" -NewName "web" }
        Write-Host "$part updated to the newly packaged version" -ForegroundColor Green
    }
}

function Initialize-Database {
    if ($P.Db -eq "clipforge") { return }
    $tools = if ($env:CLIPFORGE_TOOLS_DIR) { $env:CLIPFORGE_TOOLS_DIR } else { Join-Path $Base "tools" }
    $psql = Join-Path $tools "pgsql\bin\psql.exe"
    if (-not (Test-Path $psql)) { Write-Warning "psql not found; create database $($P.Db) manually"; return }
    $env:PGPASSWORD = "clipforge"
    $exists = & $psql -U clipforge -h localhost -p 5432 -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='$($P.Db)'"
    if ($exists -ne "1") { & $psql -U clipforge -h localhost -p 5432 -d postgres -c "CREATE DATABASE $($P.Db);" | Out-Null; Write-Host "created database $($P.Db)" }
    Remove-Item Env:PGPASSWORD
}

# Variables the services read; copied into each detached launcher.
$ServiceEnvNames = @(
    "ASPNETCORE_ENVIRONMENT", "Kestrel__Endpoints__Http__Url", "ConnectionStrings__Default", "Storage__Local__RootPath",
    "Api__PublicBaseUrl", "Api__WorkerBaseUrl", "Frontend__BaseUrl", "Cors__AllowedOrigins__0", "Cors__AllowedOrigins__1",
    "Assistant__Provider", "Assistant__Local__BaseUrl", "CLIPFORGE_API_BASE_URL", "WORKER_ID", "WORK_DIR", "SERVICE_PORT",
    "LLM_PROVIDER", "LLM_BASE_URL", "PORT", "HOSTNAME")

function Start-Detached([string]$Exe, [string]$Arguments, [string]$WorkDir, [string]$Out, [string]$Err, [string]$Tag) {
    # Start-Process children stay inside the caller's Windows job object, so they die with the
    # terminal (or agent session) that started them. A process created through WMI belongs to the
    # WMI host instead and keeps running after this script exits.
    $launcher = Join-Path $Logs "launch-$Tag.cmd"
    $lines = @("@echo off")
    foreach ($name in $ServiceEnvNames + @(Get-ChildItem Env: | Where-Object { $_.Name -match "__" } | ForEach-Object { $_.Name })) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value) { $lines += "set `"$name=$value`"" }
    }
    $lines += "cd /d `"$WorkDir`""
    $lines += "`"$Exe`" $Arguments > `"$Out`" 2> `"$Err`""
    Set-Content -LiteralPath $launcher -Value ($lines | Select-Object -Unique) -Encoding ASCII
    $startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0 }
    $result = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = "cmd.exe /c `"$launcher`""; CurrentDirectory = $WorkDir; ProcessStartupInformation = $startup }
    if ($result.ReturnValue -ne 0) { throw "could not start $Tag (Win32_Process.Create returned $($result.ReturnValue))" }
}

function Start-Stack {
    Publish-NewParts
    if (-not (Test-Path (Join-Path $Run "api\ClipForge.Api.dll"))) { throw "profile '$Name' is not packaged; run: .\scripts\stack.ps1 package -Name $Name" }
    New-Item -ItemType Directory -Force -Path $Logs | Out-Null
    . (Join-Path $PSScriptRoot "load-env.ps1")
    Initialize-Database
    $bind = if ($Lan) { "*" } else { "localhost" }
    $webOrigin = "http://$($PublicName):$($P.Web)"

    # API
    $env:ASPNETCORE_ENVIRONMENT = if ($env:ASPNETCORE_ENVIRONMENT) { $env:ASPNETCORE_ENVIRONMENT } else { "Development" }
    $env:Kestrel__Endpoints__Http__Url = "http://$($bind):$($P.Api)"
    $env:ConnectionStrings__Default = "Host=localhost;Port=5432;Database=$($P.Db);Username=clipforge;Password=clipforge"
    $env:Storage__Local__RootPath = Join-Path $Base $P.Storage
    $env:Api__PublicBaseUrl = "http://$($PublicName):$($P.Api)"
    $env:Api__WorkerBaseUrl = "http://localhost:$($P.Api)"
    $env:Frontend__BaseUrl = $webOrigin
    $env:Cors__AllowedOrigins__0 = "http://localhost:$($P.Web)"
    if ($Lan) { $env:Cors__AllowedOrigins__1 = $webOrigin } else { Remove-Item Env:Cors__AllowedOrigins__1 -ErrorAction SilentlyContinue }
    # Worker
    $env:CLIPFORGE_API_BASE_URL = "http://localhost:$($P.Api)"
    $env:WORKER_ID = "worker-$Name"
    $env:WORK_DIR = Join-Path $Base $P.Work
    $env:SERVICE_PORT = "$($P.Worker)"
    # Web
    $env:PORT = "$($P.Web)"
    $env:HOSTNAME = if ($Lan) { "0.0.0.0" } else { "127.0.0.1" }
    # Local LLM (the product's own AI): wired only when the code that reads these settings exists.
    if ($HasLlm) {
        $llmUrl = "http://127.0.0.1:$LlmPort"
        if (Test-SourceSupports "backend\src\ClipForge.Api\appsettings.json" '"Assistant"') {
            $env:Assistant__Provider = "local"
            $env:Assistant__Local__BaseUrl = $llmUrl
        }
        if (Test-SourceSupports "processing\app\config.py" 'llm_base_url') {
            $env:LLM_PROVIDER = "local"
            $env:LLM_BASE_URL = $llmUrl
        }
    } else {
        Write-Warning "Local LLM not installed (run scripts\dev-setup.ps1): the assistant chat will report 'indisponível'."
    }

    foreach ($svc in $Services.Keys) {
        $port = $Services[$svc].Port
        if (Get-ListenerPid $port) { Write-Host "$svc already listening on $port"; continue }
        $out = Join-Path $Logs "$Name-$svc.log"; $err = Join-Path $Logs "$Name-$svc.err.log"
        if ($svc -eq "llm") { $out = Join-Path $Logs "llm.log"; $err = Join-Path $Logs "llm.err.log" }
        switch ($svc) {
            "llm" {
                $threads = [Math]::Max(1, [Environment]::ProcessorCount)
                $llmArgs = "-m `"$LlmModel`" --host 127.0.0.1 --port $LlmPort -c 8192 -np 2 -t $threads --jinja --no-webui"
                Start-Detached -Exe $LlamaServer -Arguments $llmArgs -WorkDir (Split-Path $LlamaServer -Parent) -Out $out -Err $err -Tag "llm"
            }
            "api" {
                Start-Detached -Exe "dotnet" -Arguments "ClipForge.Api.dll" -WorkDir (Join-Path $Run "api") -Out $out -Err $err -Tag "$Name-api"
            }
            "worker" {
                $python = Join-Path $Root "processing\.venv\Scripts\python.exe"
                Start-Detached -Exe $python -Arguments "-m uvicorn app.main:app --host 127.0.0.1 --port $port" -WorkDir (Join-Path $Run "processing") -Out $out -Err $err -Tag "$Name-worker"
            }
            "web" {
                Start-Detached -Exe "node" -Arguments "server.js" -WorkDir (Join-Path $Run "web") -Out $out -Err $err -Tag "$Name-web"
            }
        }
        $deadline = (Get-Date).AddSeconds(120)
        while (-not (Test-Health $Services[$svc].Health)) {
            if ((Get-Date) -gt $deadline) { throw "$svc did not become healthy; see $out and $err" }
            Start-Sleep -Milliseconds 500
        }
        Write-Host "$svc ready on port $port" -ForegroundColor Green
    }
    Write-Host "Open $webOrigin" -ForegroundColor Green
}

function Stop-Stack {
    foreach ($svc in $Services.Keys) {
        # The LLM server is shared by every profile; stop it only when asked.
        if ($svc -eq "llm" -and -not $StopLlm) { Write-Host "llm left running (shared; use -StopLlm to stop it)"; continue }
        $port = $Services[$svc].Port
        $listener = Get-ListenerPid $port
        if (-not $listener) { Write-Host "$svc not running"; continue }
        $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$listener"
        $owned = $proc.CommandLine -match "ClipForge\.Api\.dll|app\.main:app|server\.js|llama-server"
        if (-not $owned) { Write-Warning "$svc port $port is used by another program (pid $listener); left untouched"; continue }
        # Kill the launcher parent too (the venv python.exe spawns the real interpreter).
        $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($proc.ParentProcessId)" -ErrorAction SilentlyContinue
        Stop-Process -Id $listener -Force
        if ($parent -and $parent.CommandLine -match "app\.main:app") { Stop-Process -Id $parent.ProcessId -Force -ErrorAction SilentlyContinue }
        Write-Host "$svc stopped (pid $listener)"
    }
}

function Show-Status {
    foreach ($svc in $Services.Keys) {
        $port = $Services[$svc].Port
        $listener = Get-ListenerPid $port
        $healthy = if ($listener) { Test-Health $Services[$svc].Health } else { $false }
        "{0,-5} {1,-7} port {2,-5} pid {3,-7} healthy {4}" -f $Name, $svc, $port, ($(if ($listener) { $listener } else { "-" })), $healthy
    }
}

switch ($Action) {
    "package" { Invoke-Package }
    "start" { Start-Stack }
    "stop" { Stop-Stack }
    "restart" { Stop-Stack; Start-Stack }
    "status" { Show-Status }
}
# robocopy reports 1 for "files copied"; failures throw above, so a run that gets here succeeded.
exit 0
