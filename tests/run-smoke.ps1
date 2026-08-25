param(
    [ValidateSet("Network", "Update", "Wine", "All")]
    [string]$Scenario = "All",

    [switch]$Quiet,
    [switch]$SkipBuild,
    [switch]$SkipWine
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$distDirectory = Join-Path $projectDirectory "dist"
$tailMsgPath = Join-Path $distDirectory "TailMsg.exe"
$updaterPath = Join-Path $distDirectory "TailMsgUpdater.exe"
$runId = [Guid]::NewGuid().ToString("N")
$logPath = Join-Path ([IO.Path]::GetTempPath()) ("TailMsgSmoke-" + $runId + ".log")
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("TailMsgSmoke-" + $runId)
$processLogDirectory = Join-Path ([IO.Path]::GetTempPath()) ("TailMsgSmoke-" + $runId + "-process")
$buildLockPath = Join-Path ([IO.Path]::GetTempPath()) "TailMsg-build.lock"
$script:processSequence = 0
$script:validationSucceeded = $false
$script:unverifiedChecks = @()

function Resolve-PowerShellExecutable {
    foreach ($candidate in @("powershell.exe", "pwsh")) {
        $command = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            return $command.Source
        }
    }
    throw "Não foi possível localizar powershell.exe nem pwsh para executar o smoke test."
}

$script:PowerShellExecutable = Resolve-PowerShellExecutable

function Write-Status([string]$Message) {
    if (-not $Quiet) { Write-Output $Message }
}

function Mark-Unverified([string]$Message) {
    $script:unverifiedChecks += $Message
    Write-Status ("UNVERIFIED: " + $Message)
}

function Invoke-Build {
    $buildLog = $logPath + ".build"
    $lockStream = $null
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        while ([DateTime]::UtcNow -lt $deadline) {
            try {
                $lockStream = [IO.File]::Open(
                    $buildLockPath,
                    [IO.FileMode]::OpenOrCreate,
                    [IO.FileAccess]::ReadWrite,
                    [IO.FileShare]::None)
                break
            }
            catch [IO.IOException] {
                Start-Sleep -Milliseconds 250
            }
        }
        if ($null -eq $lockStream) {
            throw "Não foi possível obter o bloqueio de build após 90 segundos."
        }

        & $script:PowerShellExecutable -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $projectDirectory "build.ps1") `
            -OutputDirectory $distDirectory `
            -Quiet *> $buildLog
        $buildExitCode = $LASTEXITCODE
        if ($buildExitCode -ne 0) {
            throw "O build falhou. Log: $buildLog"
        }
        Remove-Item -LiteralPath $buildLog -Force -ErrorAction SilentlyContinue
    }
    finally {
        if ($null -ne $lockStream) {
            $lockStream.Dispose()
        }
    }
}

function Invoke-Executable(
    [string]$Path,
    [string]$Arguments,
    [int]$ExpectedExitCode = 0) {
    if (-not (Test-Path -LiteralPath $processLogDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $processLogDirectory -Force | Out-Null
    }
    $script:processSequence++
    $logBase = Join-Path $processLogDirectory ("process-{0:D3}" -f $script:processSequence)
    $stdoutPath = $logBase + ".stdout.log"
    $stderrPath = $logBase + ".stderr.log"

    try {
        $process = Start-Process -FilePath $Path `
            -ArgumentList $Arguments `
            -WorkingDirectory $projectDirectory `
            -Wait -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath
    }
    catch {
        throw ("Não foi possível iniciar o comando: {0} {1}. stdout={2}; stderr={3}; erro={4}" -f
            $Path, $Arguments, $stdoutPath, $stderrPath, $_.Exception.Message)
    }

    if ($process.ExitCode -ne $ExpectedExitCode) {
        throw ("Comando falhou ({0}, esperado {1}): {2} {3}. stdout={4}; stderr={5}" -f
            $process.ExitCode, $ExpectedExitCode, $Path, $Arguments, $stdoutPath, $stderrPath)
    }

    Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    return $process.ExitCode
}

function Quote-Argument([string]$Value) {
    return '"' + ($Value.Replace('"', '\"')) + '"'
}

function Read-ProductVersion {
    $config = Get-Content -Raw -LiteralPath (Join-Path $projectDirectory "UpdateConfig.cs")
    $match = [regex]::Match($config, 'CurrentVersion\s*=\s*"([0-9]{8}_[0-9]{3})"')
    if (-not $match.Success) { throw "UpdateConfig.CurrentVersion inválido." }
    return $match.Groups[1].Value
}

function Run-NetworkScenario {
    Write-Status "[Network] integração UDP/TCP/ACK"
    $operationId = [Guid]::NewGuid().ToString("N")
    Invoke-Executable $tailMsgPath `
        ("--integration-self-test " + (Quote-Argument $operationId)) | Out-Null

    $eventPath = Join-Path $env:LOCALAPPDATA "TailMsg\tailmsg-events.log"
    if (-not (Test-Path -LiteralPath $eventPath)) {
        throw "O teste integrado não produziu o log de eventos: $eventPath"
    }
    $runEvents = @(Get-Content -LiteralPath $eventPath | Where-Object {
        $_ -match (";operation=" + [regex]::Escape($operationId) + ";")
    })
    if ($runEvents.Count -eq 0) {
        throw "O teste integrado não produziu eventos para a operação atual."
    }
    foreach ($stage in @(
        "discovery_request_sent",
        "payload_sent",
        "received",
        "ack_sent",
        "ack_received",
        "completed")) {
        if (-not ($runEvents -match ("stage=" + [regex]::Escape($stage)))) {
            throw "O log integrado não contém o estágio esperado: $stage"
        }
    }
    Write-Status "[Network] PASS"
}

function Run-WineScenario {
    Write-Status "[Wine] diagnóstico Tailscale/Wine"
    Invoke-Executable $tailMsgPath "--diagnose" | Out-Null
    $diagnosePath = Join-Path $env:LOCALAPPDATA "TailMsg\tailmsg-diagnose.txt"
    if (-not (Test-Path -LiteralPath $diagnosePath)) {
        throw "O diagnóstico Wine não produziu o relatório: $diagnosePath"
    }
    $diagnoseText = Get-Content -Raw -LiteralPath $diagnosePath
    if ($diagnoseText -notmatch "Wine detectado:\s+SIM") {
        if ($Scenario -eq "All") {
            Write-Status "[Wine] NOT_APPLICABLE: host nativo Windows"
            return
        }
        Mark-Unverified "o host atual não está executando sob Wine"
        return
    }

    $peerMatch = [regex]::Match(
        $diagnoseText,
        "Peers Tailscale via 'tailscale status':\s*(\d+)")
    if (-not $peerMatch.Success) {
        throw "O diagnóstico Wine não informou a quantidade de peers Tailscale. Log: $diagnosePath"
    }
    $peerCount = [int]$peerMatch.Groups[1].Value
    if ($peerCount -le 0) {
        Mark-Unverified "o diagnóstico Wine não encontrou peers Tailscale. Log: $diagnosePath"
        return
    }
    if ($diagnoseText -notmatch "Tentativa Wine:") {
        Mark-Unverified "o diagnóstico Wine não informou a rota de helper usada. Log: $diagnosePath"
        return
    }
    Write-Status "[Wine] PASS"
}

function New-SmokePackage([string]$PackagePath, [string]$Version) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $versionPath = Join-Path $testRoot "version.txt"
    [IO.File]::WriteAllText($versionPath, $Version + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
    $entries = @(
        @{ Source = (Join-Path $distDirectory "TailMsg.exe"); Entry = "TailMsg.exe" },
        @{ Source = (Join-Path $distDirectory "TailMsgUpdater.exe"); Entry = "TailMsgUpdater.exe" },
        @{ Source = (Join-Path $projectDirectory "install.ps1"); Entry = "install.ps1" },
        @{ Source = (Join-Path $projectDirectory "firewall.ps1"); Entry = "firewall.ps1" },
        @{ Source = (Join-Path $projectDirectory "README.md"); Entry = "README.md" },
        @{ Source = $versionPath; Entry = "version.txt" }
    )
    $archive = [IO.Compression.ZipFile]::Open(
        $PackagePath,
        [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $entries) {
            if (-not (Test-Path -LiteralPath $item.Source)) {
                throw "Arquivo de fixture ausente: $($item.Source)"
            }
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $item.Source,
                $item.Entry,
                [IO.Compression.CompressionLevel]::Fastest)
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-JournalPath([string]$OperationId) {
    return Join-Path $env:LOCALAPPDATA ("TailMsg\updates\operations\" + $OperationId + ".log")
}

function Invoke-Updater(
    [string]$PackagePath,
    [string]$TargetPath,
    [string]$OperationId,
    [string]$Version,
    [switch]$TestOnly,
    [switch]$Isolated,
    [switch]$SimulateFailure,
    [switch]$SimulateServiceFailure,
    [int]$ExpectedExitCode = 0) {
    $arguments = "--standalone-install true" +
        " --zip " + (Quote-Argument $PackagePath) +
        " --target " + (Quote-Argument $TargetPath) +
        " --operation-id " + (Quote-Argument $OperationId) +
        " --expected-version " + (Quote-Argument $Version) +
        " --quiet true"
    if ($TestOnly) { $arguments += " --test-only true" }
    if ($Isolated) { $arguments += " --isolated true" }
    if ($SimulateFailure) { $arguments += " --simulate-failure-after-apply true" }
    if ($SimulateServiceFailure) { $arguments += " --simulate-service-failure true" }
    Invoke-Executable $updaterPath $arguments $ExpectedExitCode | Out-Null
}

function Stop-TestApplication([string]$TargetPath) {
    $applicationPath = [IO.Path]::GetFullPath((Join-Path $TargetPath "TailMsg.exe"))
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    $sawTargetProcess = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        $found = $false
        foreach ($process in @(Get-Process -Name TailMsg -ErrorAction SilentlyContinue)) {
            try {
                $processPath = $process.MainModule.FileName
                if ([string]::Equals(
                    $processPath,
                    $applicationPath,
                    [StringComparison]::OrdinalIgnoreCase)) {
                    $sawTargetProcess = $true
                    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                    $found = $true
                }
            }
            catch { }
        }
        if (-not $found) { return }
        Start-Sleep -Milliseconds 150
    }
    if ($sawTargetProcess) {
        throw "O processo TailMsg do destino não encerrou após 10 segundos."
    }
}

function Assert-JournalState([string]$JournalPath, [string]$State) {
    if (-not (Test-Path -LiteralPath $JournalPath)) {
        throw "Journal não encontrado: $JournalPath"
    }
    $text = Get-Content -Raw -LiteralPath $JournalPath
    if ($text -notmatch ("state=" + [regex]::Escape($State))) {
        throw "Journal não contém state=${State}: $JournalPath"
    }
}

function Assert-JournalOrder(
    [string]$JournalPath,
    [string]$EarlierState,
    [string]$LaterState) {
    if (-not (Test-Path -LiteralPath $JournalPath)) {
        throw "Journal não encontrado: $JournalPath"
    }
    $states = @(Get-Content -LiteralPath $JournalPath | ForEach-Object {
        $match = [regex]::Match($_, '(^|;)state=([^;]*)')
        if ($match.Success) { $match.Groups[2].Value }
    })
    $earlierIndex = [Array]::IndexOf($states, $EarlierState)
    if ($earlierIndex -lt 0) {
        throw "Journal não contém o estado inicial esperado: $EarlierState"
    }
    for ($index = $earlierIndex + 1; $index -lt $states.Count; $index++) {
        if ($states[$index] -eq $LaterState) {
            return
        }
    }
    throw "Journal não preserva a ordem esperada: $EarlierState -> $LaterState"
}

function Run-UpdateScenario {
    Write-Status "[Update] pacote full, confirmação e rollback"
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $version = Read-ProductVersion
    $packagePath = Join-Path $testRoot "smoke.zip"
    New-SmokePackage $packagePath $version

    $testOnlyTarget = Join-Path $testRoot "test-only"
    $testOnlyOperation = [Guid]::NewGuid().ToString("N")
    New-Item -ItemType Directory -Path $testOnlyTarget -Force | Out-Null
    Invoke-Updater $packagePath $testOnlyTarget $testOnlyOperation $version -TestOnly
    $testOnlyJournal = Get-JournalPath $testOnlyOperation
    Assert-JournalState $testOnlyJournal "package-applied"
    Assert-JournalState $testOnlyJournal "test-only-completed"
    if (-not (Test-Path -LiteralPath (Join-Path $testOnlyTarget "TailMsg.exe"))) {
        throw "O modo test-only não instalou TailMsg.exe."
    }

    $rollbackTarget = Join-Path $testRoot "rollback"
    $rollbackOperation = [Guid]::NewGuid().ToString("N")
    New-Item -ItemType Directory -Path $rollbackTarget -Force | Out-Null
    Invoke-Updater $packagePath $rollbackTarget $rollbackOperation $version `
        -SimulateFailure -Isolated -ExpectedExitCode 1
    $rollbackJournal = Get-JournalPath $rollbackOperation
    Assert-JournalState $rollbackJournal "package-applied"
    Assert-JournalState $rollbackJournal "rollback"
    if (Test-Path -LiteralPath (Join-Path $rollbackTarget "TailMsg.exe")) {
        throw "O rollback deixou TailMsg.exe no destino vazio."
    }

    $serviceFailureTarget = Join-Path $testRoot "service-failure"
    $serviceFailureOperation = [Guid]::NewGuid().ToString("N")
    New-Item -ItemType Directory -Path $serviceFailureTarget -Force | Out-Null
    Invoke-Updater $packagePath $serviceFailureTarget $serviceFailureOperation $version `
        -Isolated -SimulateServiceFailure -ExpectedExitCode 1
    $serviceFailureJournal = Get-JournalPath $serviceFailureOperation
    Assert-JournalState $serviceFailureJournal "app-service-failed"
    Assert-JournalState $serviceFailureJournal "rollback"
    if (Test-Path -LiteralPath (Join-Path $serviceFailureTarget "TailMsg.exe")) {
        throw "O rollback de falha de serviço deixou TailMsg.exe no destino."
    }

    $successTarget = Join-Path $testRoot "success"
    $successOperation = [Guid]::NewGuid().ToString("N")
    New-Item -ItemType Directory -Path $successTarget -Force | Out-Null
    try {
        Invoke-Updater $packagePath $successTarget $successOperation $version -Isolated
        $successJournal = Get-JournalPath $successOperation
        Assert-JournalState $successJournal "app-started"
        Assert-JournalState $successJournal "service-ready"
        Assert-JournalState $successJournal "app-confirmed"
        Assert-JournalState $successJournal "completed"
        Assert-JournalOrder $successJournal "app-started" "service-ready"
        Assert-JournalOrder $successJournal "service-ready" "app-confirmed"
        Assert-JournalOrder $successJournal "app-confirmed" "completed"
    }
    finally {
        Stop-TestApplication $successTarget
    }

    $legacyOperation = [Guid]::NewGuid().ToString("N")
    $legacyArgs = "--background" +
        " --update-operation-id " + (Quote-Argument $legacyOperation) +
        " --update-expected-version " + (Quote-Argument $version) +
        " --test-instance " + (Quote-Argument $legacyOperation) +
        " --test-no-network --test-exit-after-confirm"
    Invoke-Executable (Join-Path $testOnlyTarget "TailMsg.exe") $legacyArgs | Out-Null
    $legacyJournal = Get-JournalPath $legacyOperation
    Assert-JournalState $legacyJournal "app-started"
    Assert-JournalState $legacyJournal "service-ready"
    Assert-JournalState $legacyJournal "app-confirmed"
    Assert-JournalOrder $legacyJournal "service-ready" "app-confirmed"

    Write-Status "[Update] PASS"
}

try {
    if ($SkipWine -and $Scenario -eq "Wine") {
        throw "-SkipWine não pode ser usado com -Scenario Wine."
    }
    if (-not $SkipBuild) {
        Write-Status "[Build] compilando"
        Invoke-Build
    }
    if (-not (Test-Path -LiteralPath $tailMsgPath) -or
        -not (Test-Path -LiteralPath $updaterPath)) {
        throw "Executáveis ausentes em dist; execute o build."
    }
    Invoke-Executable $tailMsgPath "--self-test" | Out-Null
    if ($Scenario -eq "Network" -or $Scenario -eq "All") {
        Run-NetworkScenario
    }
    if ($Scenario -eq "Update" -or $Scenario -eq "All") {
        Run-UpdateScenario
    }
    if ($Scenario -eq "Wine" -or ($Scenario -eq "All" -and -not $SkipWine)) {
        Run-WineScenario
    }
    if ($script:unverifiedChecks.Count -gt 0) {
        throw ("UNVERIFIED: " + ($script:unverifiedChecks -join " | "))
    }
    $script:validationSucceeded = $true
    Write-Status "PASS: smoke test $Scenario"
    exit 0
}
catch {
    $_ | Out-File -LiteralPath $logPath -Encoding UTF8
    if ($_.Exception.Message.StartsWith("UNVERIFIED:", [StringComparison]::OrdinalIgnoreCase)) {
        [Console]::Error.WriteLine(
            "UNVERIFIED: " + $_.Exception.Message.Substring(12).Trim() +
            " | log=" + $logPath)
        exit 2
    }
    [Console]::Error.WriteLine(
        "FAIL: " + $_.Exception.Message + " | log=" + $logPath)
    exit 1
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
        if ($resolvedTestRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    if ($script:validationSucceeded -and
        (Test-Path -LiteralPath $processLogDirectory)) {
        Remove-Item -LiteralPath $processLogDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
