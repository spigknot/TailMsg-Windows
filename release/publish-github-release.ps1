param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^\d{8}_\d{3}$")]
    [string]$Version,

    [string]$Repository = "spigknot/TailMsg-Windows"
)

$ErrorActionPreference = "Stop"

$projectDirectory = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$releaseDirectory = Join-Path $projectDirectory "release"
$packagePath = Join-Path $releaseDirectory ("packages\" + $Version + ".zip")
$installerPath = Join-Path `
    $releaseDirectory `
    ("generated\" + $Version + "\setup_tailmsg_" + $Version + ".exe")
$githubManifestDirectory = Join-Path `
    ([System.IO.Path]::GetTempPath()) `
    ("TailMsg-" + $Version)
if (-not (Test-Path -LiteralPath $githubManifestDirectory)) {
    New-Item -ItemType Directory -Path $githubManifestDirectory | Out-Null
}
$githubManifestPath = Join-Path $githubManifestDirectory "tailmsg-update.json"

if (-not (Test-Path -LiteralPath $packagePath)) {
    throw "Pacote não encontrado: $packagePath"
}
if (-not (Test-Path -LiteralPath $installerPath)) {
    & (Join-Path $releaseDirectory "build-installer.ps1") -Version $Version -PackagePath $packagePath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $installerPath)) {
        throw "Instalador offline não encontrado nem pôde ser gerado: $installerPath"
    }
}

$configText = Get-Content -Raw -LiteralPath (Join-Path $projectDirectory "UpdateConfig.cs")
if ($configText -notmatch ('CurrentVersion\s*=\s*"' + [regex]::Escape($Version) + '"')) {
    throw "UpdateConfig.CurrentVersion não corresponde a $Version."
}

$releaseExists = $false
try {
    & gh release view $Version --repo $Repository *> $null
    $releaseExists = ($LASTEXITCODE -eq 0)
} catch {
    # Para uma versão nova, o gh retorna código diferente de zero ao informar
    # corretamente que a release ainda não existe.
    $releaseExists = $false
}
if ($releaseExists) {
    throw "A release $Version já existe em $Repository. Releases antigas não são sobrescritas."
}

& (Join-Path $releaseDirectory "sign-manifest.ps1") `
    -Version $Version `
    -FileId ($Version + ".zip") `
    -OutputPath $githubManifestPath

$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $packagePath).Length
$notes = @"
Pacote completo do TailMsg $Version.

Instalador offline: setup_tailmsg_$Version.exe

SHA-256: $hash
Tamanho: $size bytes

O manifesto tailmsg-update.json é assinado e usado pelo updater para validar o ZIP antes da instalação.
"@

& gh release create $Version `
    $packagePath `
    $installerPath `
    $githubManifestPath `
    --repo $Repository `
    --title ("TailMsg " + $Version) `
    --notes $notes
if ($LASTEXITCODE -ne 0) {
    throw "A publicação da release do GitHub falhou."
}

Write-Host "Release GitHub publicada: $Repository@$Version"
Write-Host "ZIP full: $packagePath"
Write-Host "Manifesto assinado: $githubManifestPath"
