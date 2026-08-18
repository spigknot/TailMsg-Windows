param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^\d{8}_\d{3}$")]
    [string]$Version,

    [string]$PackagePath = ""
)

$ErrorActionPreference = "Stop"

$projectDirectory = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$releaseDirectory = Join-Path $projectDirectory "release"
$packagePathValue = $PackagePath
if ([String]::IsNullOrWhiteSpace($packagePathValue)) {
    $packagePathValue = Join-Path $releaseDirectory ("packages\" + $Version + ".zip")
}
$packagePathValue = [System.IO.Path]::GetFullPath($packagePathValue)
$generatedDirectory = Join-Path $releaseDirectory ("generated\" + $Version)
$packageDirectory = Join-Path $generatedDirectory "package"
$installerPath = Join-Path $generatedDirectory ("setup_tailmsg_" + $Version + ".exe")

if (-not (Test-Path -LiteralPath $packagePathValue -PathType Leaf)) {
    throw "Pacote full não encontrado: $packagePathValue"
}

$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object {
    Test-Path -LiteralPath $_ -PathType Leaf
} | Select-Object -First 1
if ([String]::IsNullOrWhiteSpace($iscc)) {
    throw "Inno Setup 6 não encontrado. Instale o Inno Setup para gerar o instalador offline."
}

if (Test-Path -LiteralPath $packageDirectory) {
    [System.IO.Directory]::Delete(
        [System.IO.Path]::GetFullPath($packageDirectory),
        $true)
}
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory(
    $packagePathValue,
    $packageDirectory)

$requiredFiles = @("TailMsg.exe", "TailMsgUpdater.exe", "firewall.ps1", "version.txt")
foreach ($requiredFile in $requiredFiles) {
    $requiredPath = Join-Path $packageDirectory $requiredFile
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "O pacote full não contém o arquivo necessário ao instalador: $requiredFile"
    }
}
$packageVersion = (Get-Content -Raw -LiteralPath (Join-Path $packageDirectory "version.txt")).Trim()
if ($packageVersion -ne $Version) {
    throw "A versão do pacote ($packageVersion) não corresponde a $Version."
}

if (Test-Path -LiteralPath $installerPath -PathType Leaf) {
    Remove-Item -LiteralPath $installerPath -Force
}

& $iscc ("-DAppVersion=" + $Version) (Join-Path $projectDirectory "installer\tailmsg_installer.iss")
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "A compilação do instalador offline do TailMsg falhou."
}

$installerSize = (Get-Item -LiteralPath $installerPath).Length
$installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Instalador offline criado: $installerPath"
Write-Host "Tamanho: $installerSize bytes"
Write-Host "SHA-256: $installerHash"
