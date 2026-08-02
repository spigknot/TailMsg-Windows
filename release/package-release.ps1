param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^\d{8}_\d{3}$")]
    [string]$Version
)

$ErrorActionPreference = "Stop"

$projectDirectory = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$releaseDirectory = Join-Path $projectDirectory "release"
$packagesDirectory = Join-Path $releaseDirectory "packages"
$packagePath = Join-Path $packagesDirectory ($Version + ".zip")
$configPath = Join-Path $projectDirectory "UpdateConfig.cs"

if (Test-Path -LiteralPath $packagePath) {
    throw "O pacote $packagePath já existe. Versões antigas não são sobrescritas."
}

$configText = Get-Content -Raw -LiteralPath $configPath
if ($configText -notmatch ('CurrentVersion\s*=\s*"' + [regex]::Escape($Version) + '"')) {
    throw "UpdateConfig.CurrentVersion não corresponde a $Version."
}

& (Join-Path $projectDirectory "build.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "A compilação falhou."
}

if (-not (Test-Path -LiteralPath $packagesDirectory)) {
    New-Item -ItemType Directory -Path $packagesDirectory | Out-Null
}

$versionFile = Join-Path $releaseDirectory "version.txt"
[System.IO.File]::WriteAllText(
    $versionFile,
    $Version + [Environment]::NewLine,
    (New-Object System.Text.UTF8Encoding($false)))

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$files = @(
    @{ Source = (Join-Path $projectDirectory "dist\TailMsg.exe"); Entry = "TailMsg.exe" },
    @{ Source = (Join-Path $projectDirectory "dist\TailMsgUpdater.exe"); Entry = "TailMsgUpdater.exe" },
    @{ Source = (Join-Path $projectDirectory "install.ps1"); Entry = "install.ps1" },
    @{ Source = (Join-Path $projectDirectory "firewall.ps1"); Entry = "firewall.ps1" },
    @{ Source = (Join-Path $projectDirectory "README.md"); Entry = "README.md" },
    @{ Source = $versionFile; Entry = "version.txt" }
)

$archive = [System.IO.Compression.ZipFile]::Open(
    $packagePath,
    [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        if (-not (Test-Path -LiteralPath $file.Source)) {
            throw "Arquivo do pacote não encontrado: $($file.Source)"
        }
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive,
            $file.Source,
            $file.Entry,
            [System.IO.Compression.CompressionLevel]::Fastest)
    }
} finally {
    $archive.Dispose()
}

$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $packagePath).Length

Write-Host "Pacote criado: $packagePath"
Write-Host "Versão: $Version"
Write-Host "Tamanho: $size bytes"
Write-Host "SHA-256: $hash"
