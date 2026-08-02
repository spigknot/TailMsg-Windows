$ErrorActionPreference = "Stop"

$releaseDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$privateKeyPath = Join-Path $releaseDirectory "update-private-key.xml"
$publicKeyPath = Join-Path $releaseDirectory "update-public-key.xml"

if ((Test-Path -LiteralPath $privateKeyPath) -or
    (Test-Path -LiteralPath $publicKeyPath)) {
    throw "As chaves de atualização já existem. Nenhum arquivo foi alterado."
}

$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider(2048)
try {
    [System.IO.File]::WriteAllText(
        $privateKeyPath,
        $rsa.ToXmlString($true),
        (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::WriteAllText(
        $publicKeyPath,
        $rsa.ToXmlString($false),
        (New-Object System.Text.UTF8Encoding($false)))
} finally {
    $rsa.PersistKeyInCsp = $false
    $rsa.Clear()
}

Write-Host "Chave privada criada em: $privateKeyPath"
Write-Host "Chave pública criada em: $publicKeyPath"
Write-Host "Proteja a chave privada; ela não pode ser distribuída com o aplicativo."
