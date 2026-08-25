param(
    [ValidateSet("x86", "x64", "AnyCPU")]
    [string]$Architecture = "x86",

    [string]$OutputDirectory = "",

    [switch]$Quiet,

    [string]$LogPath = ""
)

$ErrorActionPreference = "Stop"

$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDirectory = $OutputDirectory
if ([String]::IsNullOrWhiteSpace($outputDirectory)) {
    $outputDirectory = Join-Path $projectDirectory "dist"
}
$outputDirectory = [System.IO.Path]::GetFullPath($outputDirectory)
$publicKey = Join-Path $projectDirectory "release\update-public-key.xml"
$resolvedLogPath = $LogPath
if (-not [String]::IsNullOrWhiteSpace($resolvedLogPath)) {
    if (-not [System.IO.Path]::IsPathRooted($resolvedLogPath)) {
        $resolvedLogPath = Join-Path $projectDirectory $resolvedLogPath
    }
    $resolvedLogPath = [System.IO.Path]::GetFullPath($resolvedLogPath)
    $logParent = Split-Path -Parent $resolvedLogPath
    if (-not [String]::IsNullOrWhiteSpace($logParent) -and
        -not (Test-Path -LiteralPath $logParent)) {
        New-Item -ItemType Directory -Path $logParent -Force | Out-Null
    }
    if (Test-Path -LiteralPath $resolvedLogPath) {
        Remove-Item -LiteralPath $resolvedLogPath -Force
    }
}

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

function Invoke-Compiler([string[]]$Arguments, [string]$Label, [string]$FailureMessage) {
    $fragmentPath = $null
    if (-not [String]::IsNullOrWhiteSpace($resolvedLogPath)) {
        $fragmentPath = $resolvedLogPath + "." + $Label
        if (Test-Path -LiteralPath $fragmentPath) {
            Remove-Item -LiteralPath $fragmentPath -Force
        }
        & $compiler @Arguments *> $fragmentPath
        $exitCode = $LASTEXITCODE
        if (Test-Path -LiteralPath $fragmentPath) {
            Get-Content -Raw -LiteralPath $fragmentPath |
                Add-Content -LiteralPath $resolvedLogPath -Encoding UTF8
            Remove-Item -LiteralPath $fragmentPath -Force
        }
    }
    else {
        & $compiler @Arguments
        $exitCode = $LASTEXITCODE
    }

    if ($exitCode -ne 0) {
        if ([String]::IsNullOrWhiteSpace($resolvedLogPath)) {
            throw $FailureMessage
        }
        throw ($FailureMessage + " Log: " + $resolvedLogPath)
    }
}

Invoke-Compiler @(
    "/nologo",
    "/target:winexe",
    "/optimize+",
    "/platform:$Architecture",
    "/win32icon:$projectDirectory\assets\ninja.ico",
    "/resource:$publicKey,TailMsg.UpdatePublicKey",
    "/resource:$projectDirectory\assets\appwin.png,TailMsg.AppWinImage",
    "/out:$outputDirectory\TailMsg.exe",
    "/reference:System.dll",
    "/reference:System.Core.dll",
    "/reference:System.IO.Compression.dll",
    "/reference:System.IO.Compression.FileSystem.dll",
    "/reference:System.Drawing.dll",
    "/reference:System.Windows.Forms.dll",
    "$projectDirectory\TailMsg.cs",
    "$projectDirectory\TailMsgUpdate.cs",
    "$projectDirectory\UpdateConfig.cs",
    "$projectDirectory\TailMsgDiagnostics.cs",
    "$projectDirectory\UpdateJournal.cs"
) "tailmsg" "A compilação do TailMsg falhou."

Invoke-Compiler @(
    "/nologo",
    "/target:winexe",
    "/optimize+",
    "/platform:$Architecture",
    "/win32icon:$projectDirectory\assets\ninja.ico",
    "/resource:$publicKey,TailMsg.UpdatePublicKey",
    "/out:$outputDirectory\TailMsgUpdater.exe",
    "/reference:System.dll",
    "/reference:System.Core.dll",
    "/reference:System.IO.Compression.dll",
    "/reference:System.IO.Compression.FileSystem.dll",
    "/reference:System.Drawing.dll",
    "/reference:System.Windows.Forms.dll",
    "$projectDirectory\TailMsgUpdater.cs",
    "$projectDirectory\UpdateConfig.cs",
    "$projectDirectory\UpdateJournal.cs"
) "updater" "A compilação do atualizador do TailMsg falhou."

if (-not $Quiet) {
    Write-Host "TailMsg compilado em: $outputDirectory\TailMsg.exe ($Architecture)"
    Write-Host "Atualizador compilado em: $outputDirectory\TailMsgUpdater.exe ($Architecture)"
}
