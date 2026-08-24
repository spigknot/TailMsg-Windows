$ErrorActionPreference = "Stop"

$projectDirectory = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location -LiteralPath $projectDirectory

git diff --cached --check *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Error "O commit contém whitespace inválido. Execute: git diff --cached --check"
    exit 1
}

$forbidden = @(
    "release/update-private-key.xml",
    "release/r2_config.json"
)
$staged = @(git diff --cached --name-only)
foreach ($path in $staged) {
    $normalized = $path.Replace('\', '/')
    if ($forbidden -contains $normalized -or
        $normalized -match '^(dist|release/packages|release/generated|build)/') {
        Write-Error "Arquivo de build/segredo não pode ser commitado: $path"
        exit 1
    }
}

$config = Get-Content -Raw -LiteralPath (Join-Path $projectDirectory "UpdateConfig.cs")
$productVersion = [regex]::Match($config, 'CurrentVersion\s*=\s*"([0-9]{8}_[0-9]{3})"').Groups[1].Value
$updater = Get-Content -Raw -LiteralPath (Join-Path $projectDirectory "TailMsgUpdater.cs")
if ($updater -notmatch 'TailMsg\.UpdateConfig\.CurrentVersion') {
    Write-Error "TailMsgUpdater.cs não está usando UpdateConfig.CurrentVersion."
    exit 1
}
if ([string]::IsNullOrWhiteSpace($productVersion)) {
    Write-Error "UpdateConfig.CurrentVersion inválido."
    exit 1
}

exit 0
