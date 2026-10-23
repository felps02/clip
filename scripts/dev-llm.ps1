<#
.SYNOPSIS
  Runs the product's own local LLM (llama.cpp llama-server) used by the assistant chat and by the
  worker's optional titles / re-ranking. No API key and no per-call cost.

.DESCRIPTION
  Serves an OpenAI-compatible API on http://127.0.0.1:<Port>/v1 (default 8090) with the GGUF model
  downloaded by scripts\dev-setup.ps1 (default: Qwen3-1.7B Q4_K_M, Apache-2.0, ~1.1 GB).
  Each parallel slot keeps its own prompt cache, so after the first request the assistant's fixed
  system prompt is not re-processed (measured 5-9 s per reply on a 4-core desktop CPU).

.EXAMPLE
  .\scripts\dev-llm.ps1
  .\scripts\dev-llm.ps1 -Parallel 1 -Threads 4
#>
param(
    [int]$Port = 8090,
    [int]$Parallel = 2,
    [int]$ContextPerSlot = 4096,
    [int]$Threads = 0,
    [string]$Model
)

$ErrorActionPreference = "Stop"
$Base = Join-Path $env:LOCALAPPDATA "ClipForge"
$Tools = if ($env:CLIPFORGE_TOOLS_DIR) { $env:CLIPFORGE_TOOLS_DIR } else { Join-Path $Base "tools" }
$Server = Join-Path $Tools "llama\llama-server.exe"
if (-not $Model) {
    $Model = if ($env:LLM_MODEL_PATH) { $env:LLM_MODEL_PATH } else { Join-Path $Base "models\llm\Qwen3-1.7B-Q4_K_M.gguf" }
}
if (-not (Test-Path $Server)) { throw "llama-server not found at $Server. Run .\scripts\dev-setup.ps1 (without -SkipLlm)." }
if (-not (Test-Path $Model)) { throw "Model not found at $Model. Run .\scripts\dev-setup.ps1 (without -SkipLlm)." }
if ($Threads -le 0) { $Threads = [Math]::Max(1, [Environment]::ProcessorCount) }

Write-Host "LLM: http://127.0.0.1:$Port/v1  (model $(Split-Path $Model -Leaf), $Parallel slots x $ContextPerSlot tokens, $Threads threads)" -ForegroundColor Green
& $Server -m $Model --host 127.0.0.1 --port $Port -c ($ContextPerSlot * $Parallel) -np $Parallel -t $Threads --jinja --no-webui
