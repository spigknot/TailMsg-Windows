param(
    [ValidateSet("x86", "x64", "AnyCPU")]
    [string]$Architecture = "x86",

    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"

$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDirectory = $OutputDirectory
if ([String]::IsNullOrWhiteSpace($outputDirectory)) {
    $outputDirectory = Join-Path $projectDirectory "dist"
}
$outputDirectory = [System.IO.Path]::GetFullPath($outputDirectory)
$publicKey = Join-Path $projectDirectory "release\update-public-key.xml"

if ($Architecture -eq "x64") {
    $compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
} else {
    $compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

if (-not (Test-Path -LiteralPath $compiler)) {
    $fallback = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    if (Test-Path -LiteralPath $fallback) {
        $compiler = $fallback
    } else {
        throw "Compilador C# do .NET Framework 4 não encontrado."
    }
}

if (-not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

if (-not (Test-Path -LiteralPath $publicKey)) {
    throw "Chave pública de atualização não encontrada: $publicKey"
}

& $compiler `
    /nologo `
    /target:winexe `
    /optimize+ `
    /platform:$Architecture `
    /win32icon:"$projectDirectory\assets\ninja.ico" `
    "/resource:$publicKey,TailMsg.UpdatePublicKey" `
    "/resource:$projectDirectory\assets\appwin.png,TailMsg.AppWinImage" `
    /out:"$outputDirectory\TailMsg.exe" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    "$projectDirectory\TailMsg.cs" `
    "$projectDirectory\TailMsgUpdate.cs" `
    "$projectDirectory\UpdateConfig.cs"

if ($LASTEXITCODE -ne 0) {
    throw "A compilação do TailMsg falhou."
}

& $compiler `
    /nologo `
    /target:winexe `
    /optimize+ `
    /platform:$Architecture `
    /win32icon:"$projectDirectory\assets\ninja.ico" `
    /out:"$outputDirectory\TailMsgUpdater.exe" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.IO.Compression.dll `
    /reference:System.IO.Compression.FileSystem.dll `
    /reference:System.Windows.Forms.dll `
    "$projectDirectory\TailMsgUpdater.cs"

if ($LASTEXITCODE -ne 0) {
    throw "A compilação do atualizador do TailMsg falhou."
}

Write-Host "TailMsg compilado em: $outputDirectory\TailMsg.exe ($Architecture)"
Write-Host "Atualizador compilado em: $outputDirectory\TailMsgUpdater.exe ($Architecture)"
