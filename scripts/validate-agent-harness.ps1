[CmdletBinding()]
param(
    [switch]$Quiet,
    [switch]$Json,
    [switch]$Full,
    [switch]$SkipStagedSnapshot,
    [switch]$SkipWine,
    [string]$EvidencePath = "",
    [string]$DiagnosticsPath = ""
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location -LiteralPath $projectDirectory

$runId = [Guid]::NewGuid().ToString("N")
$runDirectory = Join-Path ([IO.Path]::GetTempPath()) ("TailMsgCommit-" + $runId)
$wrapperPath = Join-Path $projectDirectory "scripts\invoke-validated-command.ps1"
$script:records = @()
$script:firstFailure = $null
$script:firstFailureExitCode = 0
$script:completed = $false

New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null

function Quote-Argument([string]$Value) {
    return '"' + ($Value.Replace('"', '\"')) + '"'
}

function Write-Utf8Json([string]$Path, [object]$Value) {
    $parent = Split-Path -Parent $Path
    if (-not [String]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 8), $encoding)
}

function Resolve-EvidencePath([string]$RequestedPath) {
    if ([String]::IsNullOrWhiteSpace($RequestedPath)) {
        return Join-Path $runDirectory "validation.json"
    }

    if ([IO.Path]::IsPathRooted($RequestedPath)) {
        $resolved = [IO.Path]::GetFullPath($RequestedPath)
    }
    else {
        $resolved = [IO.Path]::GetFullPath((Join-Path $projectDirectory $RequestedPath))
    }

    $rootWithSeparator = $projectDirectory.TrimEnd('\') + '\'
    if ($resolved.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
        $relative = $resolved.Substring($rootWithSeparator.Length).Replace('\', '/')
        if ($relative -notmatch '^build-validation(?:-manual)?/') {
            throw "EvidencePath dentro do repositório deve ficar em build-validation/."
        }
        & git check-ignore --no-index -q -- $relative 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw "EvidencePath não está em um diretório ignorado pelo Git."
        }
    }
    return $resolved
}

function Resolve-ArtifactDirectory([string]$RequestedPath) {
    if ([String]::IsNullOrWhiteSpace($RequestedPath)) {
        return ""
    }

    if ([IO.Path]::IsPathRooted($RequestedPath)) {
        $resolved = [IO.Path]::GetFullPath($RequestedPath)
    }
    else {
        $resolved = [IO.Path]::GetFullPath((Join-Path $projectDirectory $RequestedPath))
    }

    $rootWithSeparator = $projectDirectory.TrimEnd('\') + '\'
    if ($resolved.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
        $relative = $resolved.Substring($rootWithSeparator.Length).Replace('\', '/')
        if ($relative -notmatch '^build-validation(?:-manual)?(?:/|$)') {
            throw "DiagnosticsPath dentro do repositório deve ficar em build-validation/."
        }
        & git check-ignore --no-index -q -- $relative 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw "DiagnosticsPath não está em um diretório ignorado pelo Git."
        }
    }
    return $resolved
}

function Resolve-PowerShellExecutable {
    foreach ($candidate in @("powershell.exe", "pwsh")) {
        $command = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            return $command.Source
        }
    }
    throw "Não foi possível localizar powershell.exe nem pwsh para executar o gate."
}

function Preserve-Diagnostics {
    if ([String]::IsNullOrWhiteSpace($resolvedDiagnosticsPath)) {
        return
    }
    New-Item -ItemType Directory -Path $resolvedDiagnosticsPath -Force | Out-Null
    Get-ChildItem -LiteralPath $runDirectory -File |
        Where-Object { $_.Name -match '\.(?:log|result\.json)$' } |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName `
                -Destination (Join-Path $resolvedDiagnosticsPath $_.Name) -Force
        }
}

$script:PowerShellExecutable = Resolve-PowerShellExecutable
$resolvedDiagnosticsPath = Resolve-ArtifactDirectory $DiagnosticsPath

function Add-StageRecord(
    [string]$Name,
    [string]$Status,
    [int]$ExitCode,
    [object]$ChildResult,
    [string]$LogName) {
    $duration = 0
    $stdoutBytes = 0
    $stderrBytes = 0
    $logBytes = 0
    if ($null -ne $ChildResult) {
        if ($null -ne $ChildResult.durationMs) { $duration = [int]$ChildResult.durationMs }
        if ($null -ne $ChildResult.stdoutBytes) { $stdoutBytes = [int64]$ChildResult.stdoutBytes }
        if ($null -ne $ChildResult.stderrBytes) { $stderrBytes = [int64]$ChildResult.stderrBytes }
        if ($null -ne $ChildResult.logBytes) { $logBytes = [int64]$ChildResult.logBytes }
    }
    $script:records += [ordered]@{
        stage = $Name
        status = $Status
        exitCode = $ExitCode
        durationMs = $duration
        stdoutBytes = $stdoutBytes
        stderrBytes = $stderrBytes
        logBytes = $logBytes
        logFile = $LogName
    }
}

function Invoke-Stage(
    [string]$Name,
    [string]$FilePath,
    [string]$Arguments,
    [int]$ExpectedExitCode = 0) {
    $safeName = $Name -replace '[^A-Za-z0-9_.-]', '_'
    $logName = $safeName + ".log"
    $logPath = Join-Path $runDirectory $logName
    $resultPath = Join-Path $runDirectory ($safeName + ".result.json")

    $previousErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        & $script:PowerShellExecutable -NoProfile -ExecutionPolicy Bypass `
            -File $wrapperPath `
            -Name $Name `
            -FilePath $FilePath `
            -Arguments $Arguments `
            -WorkingDirectory $projectDirectory `
            -LogPath $logPath `
            -ResultPath $resultPath `
            -ExpectedExitCode $ExpectedExitCode `
            -TailLines 20 `
            -Quiet 2>$null | Out-Null
        $launcherExitCode = [int]$LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }

    $childResult = $null
    if (Test-Path -LiteralPath $resultPath -PathType Leaf) {
        try {
            $childResult = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
        }
        catch {
            $childResult = $null
        }
    }

    $actualExitCode = $launcherExitCode
    if ($null -ne $childResult -and $null -ne $childResult.exitCode) {
        $actualExitCode = [int]$childResult.exitCode
    }
    $status = if ($actualExitCode -eq $ExpectedExitCode) {
        "PASS"
    }
    elseif ($actualExitCode -eq 2) {
        "UNVERIFIED"
    }
    else {
        "FAIL"
    }
    Add-StageRecord $Name $status $actualExitCode $childResult $logName

    if ($actualExitCode -ne $ExpectedExitCode) {
        $script:firstFailure = $Name
        $script:firstFailureExitCode = $actualExitCode
        throw ("stage={0}; exit={1}" -f $Name, $actualExitCode)
    }
}

function Write-Summary([string]$Status, [int]$ExitCode, [string]$Failure) {
    $evidencePath = Resolve-EvidencePath $EvidencePath
    $evidenceIsExternal = [String]::IsNullOrWhiteSpace($EvidencePath)
    $summary = [ordered]@{
        schemaVersion = 1
        validator = "validate-agent-harness"
        mode = if ($Full) { "full" } else { "commit" }
        status = $Status
        exitCode = $ExitCode
        firstFailure = $Failure
        stages = @($script:records)
        evidencePath = if ($evidenceIsExternal) { $null } else { $evidencePath }
    }
    Write-Utf8Json $evidencePath $summary
    return $summary
}

$status = "PASS"
$exitCode = 0
$failure = $null
$summary = $null

try {
    if (-not (Test-Path -LiteralPath $wrapperPath -PathType Leaf)) {
        throw "Wrapper de validação ausente."
    }

    if (-not $Full) {
        if (-not $SkipStagedSnapshot) {
            $snapshotScript = Join-Path $projectDirectory "scripts\check-staged-snapshot.ps1"
            Invoke-Stage "staged-snapshot" $script:PowerShellExecutable (
                "-NoProfile -ExecutionPolicy Bypass -File " + (Quote-Argument $snapshotScript) +
                " -Quiet")
        }

        $prefixScript = Join-Path $projectDirectory "scripts\validate-harness-prefix.ps1"
        Invoke-Stage "prefix" $script:PowerShellExecutable (
            "-NoProfile -ExecutionPolicy Bypass -File " + (Quote-Argument $prefixScript) +
            " -Quiet -RequireTracked")

        $lintScript = Join-Path $projectDirectory "scripts\lint.ps1"
        Invoke-Stage "lint" $script:PowerShellExecutable (
            "-NoProfile -ExecutionPolicy Bypass -File " + (Quote-Argument $lintScript) + " -Quiet")

        $journalTest = Join-Path $projectDirectory "tests\test-update-journal.ps1"
        Invoke-Stage "journal-redaction" $script:PowerShellExecutable (
            "-NoProfile -ExecutionPolicy Bypass -File " + (Quote-Argument $journalTest) + " -Quiet")

        $buildOutput = Join-Path $runDirectory "dist"
        $buildScript = Join-Path $projectDirectory "build.ps1"
        Invoke-Stage "build" $script:PowerShellExecutable (
            "-NoProfile -ExecutionPolicy Bypass -File " + (Quote-Argument $buildScript) +
            " -OutputDirectory " + (Quote-Argument $buildOutput) + " -Quiet")

        $tailMsgPath = Join-Path $buildOutput "TailMsg.exe"
        Invoke-Stage "self-test" $tailMsgPath "--self-test"
    }
    else {
        $releaseScript = Join-Path $projectDirectory "scripts\validate-release.ps1"
        $releaseDiagnostics = ""
        $releaseLedger = Join-Path $runDirectory "release-ledger.json"
        if (-not [String]::IsNullOrWhiteSpace($resolvedDiagnosticsPath)) {
            $releaseDiagnostics = Join-Path $resolvedDiagnosticsPath "release"
            $releaseLedger = Join-Path $resolvedDiagnosticsPath "release-ledger.json"
        }
        $releaseArguments = (
            "-NoProfile -ExecutionPolicy Bypass -File " + (Quote-Argument $releaseScript) +
            " -Quiet -LedgerPath " + (Quote-Argument $releaseLedger))
        if ($SkipWine) { $releaseArguments += " -SkipWine" }
        if (-not [String]::IsNullOrWhiteSpace($releaseDiagnostics)) {
            $releaseArguments += " -DiagnosticsPath " + (Quote-Argument $releaseDiagnostics)
        }
        Invoke-Stage "full-release-gate" $script:PowerShellExecutable $releaseArguments
    }

    $script:completed = $true
}
catch {
    $status = if ($script:firstFailureExitCode -eq 2) { "UNVERIFIED" } else { "FAIL" }
    $exitCode = if ($script:firstFailureExitCode -ne 0) { $script:firstFailureExitCode } else { 1 }
    $failure = if (-not [String]::IsNullOrWhiteSpace($script:firstFailure)) {
        "stage=" + $script:firstFailure
    }
    else {
        "stage=orchestration"
    }
}

if (-not [String]::IsNullOrWhiteSpace($resolvedDiagnosticsPath)) {
    try {
        Preserve-Diagnostics
    }
    catch {
        $diagnosticMessage = $_.Exception.Message
        if ($status -eq "PASS") {
            $status = "FAIL"
            $exitCode = 1
            $failure = "stage=diagnostics"
            $script:completed = $false
        }
        [Console]::Error.WriteLine("FAIL: não foi possível preservar diagnósticos: " + $diagnosticMessage)
    }
}

try {
    $summary = Write-Summary $status $exitCode $failure
}
catch {
    $status = "FAIL"
    $exitCode = 1
    $failure = "stage=evidence"
    [Console]::Error.WriteLine("FAIL: não foi possível gravar a evidência estruturada.")
}

if ($Json -and $null -ne $summary) {
    Write-Output ($summary | ConvertTo-Json -Depth 8 -Compress)
}
elseif (-not $Quiet -and $status -eq "PASS") {
    Write-Output ("PASS: validador {0} concluído." -f $summary.mode)
}

if ($status -ne "PASS") {
    [Console]::Error.WriteLine(
        ("{0}: {1}; consulte a evidência estruturada." -f $status, $failure))
}

if ($script:completed -and (Test-Path -LiteralPath $runDirectory)) {
    Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

exit $exitCode
