param(
    [ValidateSet("Network", "Update", "All")]
    [string]$Scenario = "All",

    [switch]$Quiet,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$distDirectory = Join-Path $projectDirectory "dist"
$tailMsgPath = Join-Path $distDirectory "TailMsg.exe"
$updaterPath = Join-Path $distDirectory "TailMsgUpdater.exe"
$runId = [Guid]::NewGuid().ToString("N")
$logPath = Join-Path ([IO.Path]::GetTempPath()) ("TailMsgSmoke-" + $runId + ".log")
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("TailMsgSmoke-" + $runId)

function Write-Status([string]$Message) {
    if (-not $Quiet) { Write-Output $Message }
}

function Invoke-Build {
    $buildLog = $logPath + ".build"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $projectDirectory "build.ps1") `
        -OutputDirectory $distDirectory *> $buildLog
    if ($LASTEXITCODE -ne 0) {
        throw "O build falhou. Log: $buildLog"
    }
    Remove-Item -LiteralPath $buildLog -Force -ErrorAction SilentlyContinue
}

function Invoke-Executable(
    [string]$Path,
    [string]$Arguments,
    [int]$ExpectedExitCode = 0) {
    $process = Start-Process -FilePath $Path `
        -ArgumentList $Arguments `
        -WorkingDirectory $projectDirectory `
        -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne $ExpectedExitCode) {
        throw "Comando falhou ($($process.ExitCode), esperado $ExpectedExitCode): $Path $Arguments"
    }
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
    Invoke-Executable $tailMsgPath "--integration-self-test" | Out-Null

    $eventPath = Join-Path $env:LOCALAPPDATA "TailMsg\tailmsg-events.log"
    if (-not (Test-Path -LiteralPath $eventPath)) {
        throw "O teste integrado não produziu o log de eventos: $eventPath"
    }
    $events = Get-Content -Raw -LiteralPath $eventPath
    foreach ($stage in @("discovery_request_sent", "payload_sent", "ack_received", "completed")) {
        if ($events -notmatch ("stage=" + $stage)) {
            throw "O log integrado não contém o estágio esperado: $stage"
        }
    }
    Write-Status "[Network] PASS"
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
    Invoke-Executable $updaterPath $arguments $ExpectedExitCode | Out-Null
}

function Stop-TestApplication([string]$TargetPath) {
    $applicationPath = [IO.Path]::GetFullPath((Join-Path $TargetPath "TailMsg.exe"))
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $deadline) {
        $found = $false
        foreach ($process in @(Get-Process -Name TailMsg -ErrorAction SilentlyContinue)) {
            try {
                $processPath = $process.MainModule.FileName
                if ([string]::Equals(
                    $processPath,
                    $applicationPath,
                    [StringComparison]::OrdinalIgnoreCase)) {
                    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                    $found = $true
                }
            }
            catch { }
        }
        if ($found) { return }
        Start-Sleep -Milliseconds 150
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

    $successTarget = Join-Path $testRoot "success"
    $successOperation = [Guid]::NewGuid().ToString("N")
    New-Item -ItemType Directory -Path $successTarget -Force | Out-Null
    try {
        Invoke-Updater $packagePath $successTarget $successOperation $version -Isolated
        $successJournal = Get-JournalPath $successOperation
        Assert-JournalState $successJournal "app-confirmed"
        Assert-JournalState $successJournal "completed"
    }
    finally {
        Stop-TestApplication $successTarget
    }
    Write-Status "[Update] PASS"
}

try {
    if (-not $SkipBuild) {
        Write-Status "[Build] compilando"
        Invoke-Build
    }
    if (-not (Test-Path -LiteralPath $tailMsgPath) -or
        -not (Test-Path -LiteralPath $updaterPath)) {
        throw "Executáveis ausentes em dist; execute o build."
    }
    if ($Scenario -eq "Network" -or $Scenario -eq "All") {
        Run-NetworkScenario
    }
    if ($Scenario -eq "Update" -or $Scenario -eq "All") {
        Run-UpdateScenario
    }
    Write-Status "PASS: smoke test $Scenario"
    exit 0
}
catch {
    $_ | Out-File -LiteralPath $logPath -Encoding UTF8
    Write-Error ("FAIL: " + $_.Exception.Message + " | log=" + $logPath)
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
}
