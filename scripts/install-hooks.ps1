param(
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location -LiteralPath $projectDirectory

$gitRoot = (& git rev-parse --show-toplevel 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or [String]::IsNullOrWhiteSpace($gitRoot)) {
    throw "Este diretório não está dentro de um checkout Git."
}

$hooksPath = Join-Path $projectDirectory ".githooks"
if (-not (Test-Path -LiteralPath $hooksPath -PathType Container)) {
    throw "Diretório de hooks não encontrado: $hooksPath"
}
if (-not (Test-Path -LiteralPath (Join-Path $hooksPath "pre-commit") -PathType Leaf)) {
    throw "Dispatcher pre-commit não encontrado em $hooksPath."
}

& git config --local core.hooksPath .githooks
if ($LASTEXITCODE -ne 0) {
    throw "Não foi possível ativar core.hooksPath."
}

$configuredPath = (& git config --local --get core.hooksPath 2>$null).Trim()
if ($configuredPath -ne ".githooks") {
    throw "core.hooksPath não foi confirmado como .githooks."
}

if (-not $Quiet) {
    Write-Output "Hooks do TailMsg ativados em .githooks."
}
