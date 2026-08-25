param(
    [switch]$Quiet,

    [switch]$SkipWine,

    [string]$LedgerPath = "",

    [string]$DiagnosticsPath = ""
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location -LiteralPath $projectDirectory

$runId = [Guid]::NewGuid().ToString("N")
$runDirectory = Join-Path ([IO.Path]::GetTempPath()) ("TailMsgRelease-" + $runId)
$script:startedAt = [DateTime]::UtcNow
$script:records = @()
$script:failureExitCode = 1
$script:validationSucceeded = $false
$wrapperPath = Join-Path $projectDirectory "scripts\invoke-validated-command.ps1"

New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null

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

$resolvedDiagnosticsPath = Resolve-ArtifactDirectory $DiagnosticsPath
$script:PowerShellExecutable = Resolve-PowerShellExecutable

if ([String]::IsNullOrWhiteSpace($LedgerPath)) {
    if ([String]::IsNullOrWhiteSpace($resolvedDiagnosticsPath)) {
        $resolvedLedgerPath = Join-Path $runDirectory "validation-ledger.json"
    }
    else {
        $resolvedLedgerPath = Join-Path $resolvedDiagnosticsPath "validation-ledger.json"
    }
}
elseif ([IO.Path]::IsPathRooted($LedgerPath)) {
    $resolvedLedgerPath = [IO.Path]::GetFullPath($LedgerPath)
}
else {
    $resolvedLedgerPath = [IO.Path]::GetFullPath((Join-Path $projectDirectory $LedgerPath))
}

function Write-Status([string]$Message) {
    if (-not $Quiet) {
        Write-Output $Message
    }
}

function Quote-Argument([string]$Value) {
    return '"' + ($Value.Replace('"', '\"')) + '"'
}

function Write-Ledger([string]$Status, [string]$FailureMessage = "") {
    $parent = Split-Path -Parent $resolvedLedgerPath
    if (-not [String]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $failureValue = $null
    if (-not [String]::IsNullOrWhiteSpace($FailureMessage)) {
        $failureValue = $FailureMessage
    }
    $ledger = [ordered]@{
        schemaVersion = 1
        status = $Status
        startedUtc = $script:startedAt.ToString("o")
        finishedUtc = [DateTime]::UtcNow.ToString("o")
        project = "TailMsg"
        steps = @($script:records)
        failure = $failureValue
    }
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText(
        $resolvedLedgerPath,
        ($ledger | ConvertTo-Json -Depth 8),
        $encoding)
}

function Invoke-Step(
    [string]$Name,
    [string]$FilePath,
    [string]$Arguments,
    [int]$ExpectedExitCode = 0) {
    $safeName = $Name -replace '[^A-Za-z0-9_.-]', '_'
    $stepLogPath = Join-Path $runDirectory ($safeName + ".log")
    $stepResultPath = Join-Path $runDirectory ($safeName + ".result.json")
    Write-Status ("[" + $Name + "]")

    $stepStarted = [DateTime]::UtcNow
    $previousErrorAction = $ErrorActionPreference
    try {
        # O wrapper preserva stdout/stderr no log; aqui descarte ambos para que
        # somente o resultado compacto da etapa chegue ao processo pai.
        $ErrorActionPreference = "Continue"
        & $script:PowerShellExecutable -NoProfile -ExecutionPolicy Bypass `
            -File $wrapperPath `
            -Name $Name `
            -FilePath $FilePath `
            -Arguments $Arguments `
            -WorkingDirectory $projectDirectory `
            -LogPath $stepLogPath `
            -ResultPath $stepResultPath `
            -ExpectedExitCode $ExpectedExitCode `
            -Quiet 2>$null | Out-Null
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }
    $durationMs = [int]([DateTime]::UtcNow - $stepStarted).TotalMilliseconds

    $result = $null
    if (Test-Path -LiteralPath $stepResultPath -PathType Leaf) {
        try {
            $result = Get-Content -Raw -LiteralPath $stepResultPath | ConvertFrom-Json
        }
        catch {
            $result = $null
        }
    }
    $record = [ordered]@{
        step = $Name
        status = if ($exitCode -eq $ExpectedExitCode) { "PASS" } elseif ($exitCode -eq 2) { "UNVERIFIED" } else { "FAIL" }
        exitCode = $exitCode
        expectedExitCode = $ExpectedExitCode
        durationMs = if ($null -ne $result -and $null -ne $result.durationMs) { [int]$result.durationMs } else { $durationMs }
        stdoutBytes = if ($null -ne $result -and $null -ne $result.stdoutBytes) { [int64]$result.stdoutBytes } else { 0 }
        stderrBytes = if ($null -ne $result -and $null -ne $result.stderrBytes) { [int64]$result.stderrBytes } else { 0 }
        logBytes = if ($null -ne $result -and $null -ne $result.logBytes) { [int64]$result.logBytes } elseif (Test-Path -LiteralPath $stepLogPath) { [int64](Get-Item -LiteralPath $stepLogPath).Length } else { 0 }
        logPath = $stepLogPath
    }
    $script:records += $record

    if ($exitCode -ne $ExpectedExitCode) {
        if ($exitCode -eq 2) {
            $script:failureExitCode = 2
            throw ("UNVERIFIED: a etapa {0} retornou {1}. Log: {2}" -f $Name, $exitCode, $stepLogPath)
        }
        throw ("A etapa {0} falhou com código {1}. Log: {2}" -f $Name, $exitCode, $stepLogPath)
    }
}

try {
    if (-not (Test-Path -LiteralPath $wrapperPath -PathType Leaf)) {
        throw "Wrapper de validação ausente: $wrapperPath"
    }

    $prefixScript = Join-Path $projectDirectory "scripts\validate-harness-prefix.ps1"
    Invoke-Step "prefix" $script:PowerShellExecutable (
        "-NoProfile -ExecutionPolicy Bypass -File " + (Quote-Argument $prefixScript) + " -Quiet")

    $lintScript = Join-Path $projectDirectory "scripts\lint.ps1"
    Invoke-Step "lint" $script:PowerShellExecutable (
        "-NoProfile -ExecutionPolicy Bypass -File " + (Quote-Argument $lintScript) + " -Quiet")

    $journalTest = Join-Path $projectDirectory "tests\test-update-journal.ps1"
    Invoke-Step "journal-redaction" $script:PowerShellExecutable (
        "-NoProfile -ExecutionPolicy Bypass -File " + (Quote-Argument $journalTest) + " -Quiet")

    $harnessTest = Join-Path $projectDirectory "tests\test-validation-harness.ps1"
    Invoke-Step "harness-self-test" $script:PowerShellExecutable (
        "-NoProfile -ExecutionPolicy Bypass -File " + (Quote-Argument $harnessTest) + " -Quiet")

    $buildScript = Join-Path $projectDirectory "build.ps1"
    $distDirectory = Join-Path $projectDirectory "dist"
    Invoke-Step "build" $script:PowerShellExecutable (
        "-NoProfile -ExecutionPolicy Bypass -File " + (Quote-Argument $buildScript) +
        " -OutputDirectory " + (Quote-Argument $distDirectory) + " -Quiet")

    $tailMsgPath = Join-Path $distDirectory "TailMsg.exe"
    $integrationOperation = [Guid]::NewGuid().ToString("N")
    Invoke-Step "self-test" $tailMsgPath "--self-test"
    Invoke-Step "integration-self-test" $tailMsgPath (
        "--integration-self-test " + (Quote-Argument $integrationOperation))

    $smokeScript = Join-Path $projectDirectory "tests\run-smoke.ps1"
    $smokeWineArgument = if ($SkipWine) { " -SkipWine" } else { "" }
    Invoke-Step "smoke-all" $script:PowerShellExecutable (
        "-NoProfile -ExecutionPolicy Bypass -File " + (Quote-Argument $smokeScript) +
        " -Scenario All -Quiet -SkipBuild" + $smokeWineArgument)

    $gitCommand = Get-Command git.exe -ErrorAction SilentlyContinue
    if ($null -eq $gitCommand) {
        throw "git.exe não foi encontrado para a verificação de whitespace."
    }
    Invoke-Step "git-diff-check" $gitCommand.Source "diff --check"

    $script:validationSucceeded = $true
    Write-Ledger "PASS"
    Write-Status "PASS: gate de release do TailMsg concluído."
    exit 0
}
catch {
    $message = $_.Exception.Message
    if ($message.StartsWith("UNVERIFIED:", [StringComparison]::OrdinalIgnoreCase)) {
        $script:failureExitCode = 2
    }
    try {
        $ledgerStatus = if ($script:failureExitCode -eq 2) { "UNVERIFIED" } else { "FAIL" }
        Write-Ledger $ledgerStatus $message
    }
    catch {
        [Console]::Error.WriteLine("WARN: não foi possível gravar o ledger: " + $_.Exception.Message)
    }
    $errorPrefix = if ($script:failureExitCode -eq 2) { "UNVERIFIED: " } else { "FAIL: " }
    [Console]::Error.WriteLine($errorPrefix + $message + " | artefatos=" + $runDirectory)
    exit $script:failureExitCode
}
finally {
    try {
        Preserve-Diagnostics
    }
    catch {
        if (-not $Quiet) {
            [Console]::Error.WriteLine("WARN: não foi possível preservar diagnósticos: " + $_.Exception.Message)
        }
    }
    if ($script:validationSucceeded -and (Test-Path -LiteralPath $runDirectory)) {
        Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
