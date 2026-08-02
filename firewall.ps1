param([string]$ProgramPath)

$ErrorActionPreference = "Stop"

if ([String]::IsNullOrEmpty($ProgramPath)) {
    $installedPath = Join-Path $env:LOCALAPPDATA "TailMsg\TailMsg.exe"
    if (Test-Path -LiteralPath $installedPath) {
        $ProgramPath = $installedPath
    } else {
        $ProgramPath = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "dist\TailMsg.exe"
    }
}

$programPath = $ProgramPath
$programPath = [System.IO.Path]::GetFullPath($programPath)

if (-not (Test-Path -LiteralPath $programPath)) {
    throw "Executável não encontrado: $programPath"
}

$tcpRule = "TailMsg - mensagens TCP"
$udpRule = "TailMsg - descoberta UDP"

netsh advfirewall firewall delete rule name="$tcpRule" | Out-Null
netsh advfirewall firewall delete rule name="$udpRule" | Out-Null

netsh advfirewall firewall add rule name="$tcpRule" dir=in action=allow protocol=TCP localport=38257 program="$programPath" profile=any remoteip=10.0.0.0/8,100.64.0.0/10 | Out-Null
netsh advfirewall firewall add rule name="$udpRule" dir=in action=allow protocol=UDP localport=38258 program="$programPath" profile=any remoteip=10.0.0.0/8,100.64.0.0/10 | Out-Null

Write-Host "Regras de firewall do TailMsg instaladas para $programPath"
