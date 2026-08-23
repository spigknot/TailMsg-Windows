param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^\d{8}_\d{3}$")]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[A-Za-z0-9_.-]{1,200}$")]
    [string]$FileId,

    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"

$releaseDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$packagePath = Join-Path $releaseDirectory ("packages\" + $Version + ".zip")
$privateKeyPath = Join-Path $releaseDirectory "update-private-key.xml"
$manifestPath = $OutputPath
if ([String]::IsNullOrWhiteSpace($manifestPath)) {
    $manifestPath = Join-Path $releaseDirectory "tailmsg-update.json"
} else {
    $manifestPath = [System.IO.Path]::GetFullPath($manifestPath)
}

if (-not (Test-Path -LiteralPath $packagePath)) {
    throw "Pacote não encontrado: $packagePath"
}
if (-not (Test-Path -LiteralPath $privateKeyPath)) {
    throw "Chave privada não encontrada: $privateKeyPath"
}

$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToUpperInvariant()
$size = (Get-Item -LiteralPath $packagePath).Length
$payload = $Version + "`n" + $FileId + "`n" + $hash + "`n" + $size

$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
try {
    $rsa.PersistKeyInCsp = $false
    $rsa.FromXmlString((Get-Content -Raw -LiteralPath $privateKeyPath))
    $signatureBytes = $rsa.SignData(
        [System.Text.Encoding]::UTF8.GetBytes($payload),
        [System.Security.Cryptography.CryptoConfig]::MapNameToOID("SHA256"))
    $signature = [Convert]::ToBase64String($signatureBytes)
} finally {
    $rsa.Clear()
}

$json = @"
{
  "version": "$Version",
  "fileId": "$FileId",
  "sha256": "$($hash.ToLowerInvariant())",
  "size": $size,
  "signature": "$signature"
}
"@

[System.IO.File]::WriteAllText(
    $manifestPath,
    $json + [Environment]::NewLine,
    (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Manifesto assinado: $manifestPath"
Write-Host "Versão: $Version"
Write-Host "Arquivo no R2: $FileId"
