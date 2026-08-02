$ErrorActionPreference = "Stop"

$scriptPath = $MyInvocation.MyCommand.Path
$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())
$isAdministrator = $principal.IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdministrator) {
    Start-Process powershell.exe `
        -Verb RunAs `
        -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""
    exit
}

$sourcePath = Join-Path (Split-Path -Parent $scriptPath) "dist\TailMsg.exe"
$updaterSourcePath = Join-Path (Split-Path -Parent $scriptPath) "dist\TailMsgUpdater.exe"
if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "Execute .\build.ps1 antes da instalação."
}
if (-not (Test-Path -LiteralPath $updaterSourcePath)) {
    throw "TailMsgUpdater.exe não foi compilado. Execute .\build.ps1 novamente."
}

$installDirectory = Join-Path $env:LOCALAPPDATA "TailMsg"
$installedPath = Join-Path $installDirectory "TailMsg.exe"
$installedUpdaterPath = Join-Path $installDirectory "TailMsgUpdater.exe"

if (-not (Test-Path -LiteralPath $installDirectory)) {
    New-Item -ItemType Directory -Path $installDirectory | Out-Null
}

Get-Process -Name "TailMsg" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

Copy-Item -LiteralPath $sourcePath -Destination $installedPath -Force
Copy-Item -LiteralPath $updaterSourcePath -Destination $installedUpdaterPath -Force

$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
$pathEntries = @($userPath -split ";" | Where-Object { $_ })
if ($pathEntries -notcontains $installDirectory) {
    $newUserPath = (($pathEntries + $installDirectory) -join ";")
    [Environment]::SetEnvironmentVariable("Path", $newUserPath, "User")
}

$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if (-not (Test-Path -LiteralPath $runKey)) {
    New-Item -Path $runKey -Force | Out-Null
}
Set-ItemProperty `
    -LiteralPath $runKey `
    -Name "TailMsg" `
    -Value "`"$installedPath`" --background"

$tcpRule = "TailMsg - mensagens TCP"
$udpRule = "TailMsg - descoberta UDP"
netsh advfirewall firewall delete rule name="$tcpRule" | Out-Null
netsh advfirewall firewall delete rule name="$udpRule" | Out-Null
netsh advfirewall firewall add rule name="$tcpRule" dir=in action=allow protocol=TCP localport=38257 program="$installedPath" profile=any remoteip=10.0.0.0/8,100.64.0.0/10 | Out-Null
netsh advfirewall firewall add rule name="$udpRule" dir=in action=allow protocol=UDP localport=38258 program="$installedPath" profile=any remoteip=10.0.0.0/8,100.64.0.0/10 | Out-Null

Start-Process -FilePath $installedPath -ArgumentList "--background"

Write-Host ""
Write-Host "TailMsg instalado em: $installedPath"
Write-Host "Inicialização automática e regras de firewall configuradas."
Write-Host "Abra um novo Prompt de Comando para usar: tailmsg COMPUTADOR `"Mensagem`""
