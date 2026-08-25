param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[A-Za-z0-9_.:-]{1,80}$")]
    [string]$Name,

    [Parameter(Mandatory = $true)]
    [string]$FilePath,

    [string]$Arguments = "",

    [string]$WorkingDirectory = "",

    [Parameter(Mandatory = $true)]
    [string]$LogPath,

    [string]$ResultPath = "",

    [switch]$Quiet,

    [int]$ExpectedExitCode = 0,

    [ValidateRange(0, 200)]
    [int]$TailLines = 40
)

$ErrorActionPreference = "Stop"
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectDirectory = Split-Path -Parent $scriptDirectory

function Resolve-PathValue([string]$Value, [string]$BaseDirectory) {
    if ([IO.Path]::IsPathRooted($Value)) {
        return [IO.Path]::GetFullPath($Value)
    }
    return [IO.Path]::GetFullPath((Join-Path $BaseDirectory $Value))
}

function Write-Utf8Lines([string]$Path, [string[]]$Lines) {
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllLines($Path, $Lines, $encoding)
}

function Mask-SensitiveText([string]$Text) {
    if ($null -eq $Text) {
        return ""
    }

    $masked = $Text
    $masked = [regex]::Replace(
        $masked,
        '(?i)(\b(?:authorization)\s*:\s*(?:bearer|basic)\s+)[^\s\r\n]+',
        '$1<REDACTED>')
    $masked = [regex]::Replace(
        $masked,
        '(?i)(\b(?:authorization)\s+(?:bearer|basic)\s+)[^\s\r\n]+',
        '$1<REDACTED>')
    $masked = [regex]::Replace(
        $masked,
        '(?i)(\b(?:api[_-]?key|access[_-]?key(?:[_-]?id)?|secret(?:[_-]?access)?[_-]?key|token|password)\b\s*[:=]\s*["'']?)[^"''\s,;]+',
        '$1<REDACTED>')
    $masked = [regex]::Replace(
        $masked,
        '(?i)([?&](?:api[_-]?key|access[_-]?key|secret|token|signature|password)=)[^&\s]+',
        '$1<REDACTED>')
    $masked = [regex]::Replace(
        $masked,
        '(?i)\b(?:cfat|sk|ghp|github_pat)_[A-Za-z0-9_-]+\b',
        '<REDACTED>')
    return $masked
}

function Read-LogLines([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return @()
    }
    return @(Get-Content -LiteralPath $Path -ErrorAction SilentlyContinue)
}

$resolvedLogPath = Resolve-PathValue $LogPath $projectDirectory
$resolvedWorkingDirectory = $WorkingDirectory
if ([String]::IsNullOrWhiteSpace($resolvedWorkingDirectory)) {
    $resolvedWorkingDirectory = $projectDirectory
}
else {
    $resolvedWorkingDirectory = Resolve-PathValue $resolvedWorkingDirectory $projectDirectory
}

if ([String]::IsNullOrWhiteSpace($ResultPath)) {
    $resolvedResultPath = $resolvedLogPath + ".result.json"
}
else {
    $resolvedResultPath = Resolve-PathValue $ResultPath $projectDirectory
}

$logParent = Split-Path -Parent $resolvedLogPath
$resultParent = Split-Path -Parent $resolvedResultPath
if (-not [String]::IsNullOrWhiteSpace($logParent)) {
    New-Item -ItemType Directory -Path $logParent -Force | Out-Null
}
if (-not [String]::IsNullOrWhiteSpace($resultParent)) {
    New-Item -ItemType Directory -Path $resultParent -Force | Out-Null
}

$stdoutPath = $resolvedLogPath + ".stdout"
$stderrPath = $resolvedLogPath + ".stderr"
Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue

$startedAt = [DateTime]::UtcNow
$exitCode = 1
$startError = $null
$process = $null

try {
    if (-not (Test-Path -LiteralPath $resolvedWorkingDirectory -PathType Container)) {
        throw "Diretório de trabalho não encontrado: $resolvedWorkingDirectory"
    }

    $process = Start-Process -FilePath $FilePath `
        -ArgumentList $Arguments `
        -WorkingDirectory $resolvedWorkingDirectory `
        -Wait -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath
    $exitCode = $process.ExitCode
}
catch {
    $startError = $_.Exception.Message
}

$stdoutLines = Read-LogLines $stdoutPath
$stderrLines = Read-LogLines $stderrPath
$stdoutBytes = if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) {
    (Get-Item -LiteralPath $stdoutPath).Length
} else { 0 }
$stderrBytes = if (Test-Path -LiteralPath $stderrPath -PathType Leaf) {
    (Get-Item -LiteralPath $stderrPath).Length
} else { 0 }

$combinedLines = @("[stdout]") + $stdoutLines + @("[stderr]") + $stderrLines
if (-not [String]::IsNullOrWhiteSpace($startError)) {
    $combinedLines += "[launcher-error]"
    $combinedLines += $startError
    $exitCode = 1
}
$maskedLines = @(
    $combinedLines | ForEach-Object { Mask-SensitiveText ([string]$_) }
)
Write-Utf8Lines $resolvedLogPath ([string[]]$maskedLines)
Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue

$durationMs = [int]([DateTime]::UtcNow - $startedAt).TotalMilliseconds
$status = if ($exitCode -eq $ExpectedExitCode) {
    "PASS"
} elseif ($exitCode -eq 2) {
    "UNVERIFIED"
} else {
    "FAIL"
}
$logBytes = (Get-Item -LiteralPath $resolvedLogPath).Length
$result = [ordered]@{
    schemaVersion = 1
    name = $Name
    status = $status
    exitCode = $exitCode
    expectedExitCode = $ExpectedExitCode
    durationMs = $durationMs
    stdoutBytes = $stdoutBytes
    stderrBytes = $stderrBytes
    logBytes = $logBytes
    logPath = $resolvedLogPath
}
Write-Utf8Lines $resolvedResultPath @($result | ConvertTo-Json -Depth 5)

if ($exitCode -ne $ExpectedExitCode) {
    $prefix = if ($status -eq "UNVERIFIED") { "UNVERIFIED" } else { "FAIL" }
    [Console]::Error.WriteLine(
        ("{0}: [{1}] exit={2}, esperado={3}, log={4}" -f
            $prefix, $Name, $exitCode, $ExpectedExitCode, $resolvedLogPath))
    if ($TailLines -gt 0) {
        $tail = @(Get-Content -LiteralPath $resolvedLogPath |
            Select-Object -Last $TailLines)
        foreach ($line in $tail) {
            if (-not [String]::IsNullOrWhiteSpace([string]$line)) {
                [Console]::Error.WriteLine(("  " + $line))
            }
        }
    }
}
elseif (-not $Quiet) {
    Write-Output ("PASS: [{0}] ({1} ms)" -f $Name, $durationMs)
}

exit $exitCode
