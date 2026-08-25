[CmdletBinding()]
param(
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$snapshotScript = Join-Path $projectDirectory "scripts\check-staged-snapshot.ps1"
$wrapperScript = Join-Path $projectDirectory "scripts\invoke-validated-command.ps1"
$installerScript = Join-Path $projectDirectory "scripts\install-hooks.ps1"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("TailMsgHarnessTest-" + [Guid]::NewGuid().ToString("N"))

function Write-Utf8Text([string]$Path, [string]$Text) {
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, $Text, $encoding)
}

function Quote-Argument([string]$Value) {
    return '"' + ($Value.Replace('"', '\"')) + '"'
}

function Invoke-Git([string]$Repository, [string[]]$Arguments) {
    & git -C $Repository @Arguments *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "comando Git de teste falhou"
    }
}

function Invoke-CapturedPowerShell([string]$Arguments, [string]$WorkingDirectory) {
    $stdoutPath = Join-Path $testRoot ("stdout-" + [Guid]::NewGuid().ToString("N") + ".txt")
    $stderrPath = Join-Path $testRoot ("stderr-" + [Guid]::NewGuid().ToString("N") + ".txt")
    $process = Start-Process -FilePath "powershell.exe" `
        -ArgumentList $Arguments `
        -WorkingDirectory $WorkingDirectory `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    [pscustomobject]@{
        ExitCode = [int]$process.ExitCode
        Stdout = if (Test-Path -LiteralPath $stdoutPath) { Get-Content -Raw -LiteralPath $stdoutPath } else { "" }
        Stderr = if (Test-Path -LiteralPath $stderrPath) { Get-Content -Raw -LiteralPath $stderrPath } else { "" }
    }
}

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}

function Copy-HookFixture([string]$Destination) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $excludedTopLevel = @(
        ".git",
        ".codex",
        "dist",
        "build",
        "build-validation",
        "downloads",
        "linux",
        "release"
    )
    Get-ChildItem -LiteralPath $projectDirectory -Force |
        Where-Object { $excludedTopLevel -notcontains $_.Name } |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
        }

    $releaseFixture = Join-Path $Destination "release"
    New-Item -ItemType Directory -Path $releaseFixture -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectDirectory "release\update-public-key.xml") `
        -Destination (Join-Path $releaseFixture "update-public-key.xml") -Force
}

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

    $sourcePath = Join-Path $testRoot "TailMsg.cs"
    Write-Utf8Text $sourcePath "class TailMsgFixture { static void Main() {} }`n"
    Invoke-Git $testRoot @("init", "--quiet")
    Invoke-Git $testRoot @("config", "core.autocrlf", "false")
    Invoke-Git $testRoot @("config", "user.email", "tailmsg-harness@example.invalid")
    Invoke-Git $testRoot @("config", "user.name", "TailMsg Harness")
    Invoke-Git $testRoot @("add", "TailMsg.cs")
    Invoke-Git $testRoot @("commit", "--quiet", "-m", "fixture")

    Write-Utf8Text $sourcePath "class TailMsgFixture { static void Main() { } }`n"
    Invoke-Git $testRoot @("add", "TailMsg.cs")

    $silentSuccess = Invoke-CapturedPowerShell (
        "-NoProfile -ExecutionPolicy Bypass -File " + (Quote-Argument $snapshotScript) +
        " -ProjectRoot " + (Quote-Argument $testRoot) + " -Quiet") $projectDirectory
    Assert-Condition ($silentSuccess.ExitCode -eq 0) "snapshot staged deveria passar"
    Assert-Condition ([String]::IsNullOrWhiteSpace($silentSuccess.Stdout)) "sucesso não está silencioso"
    Assert-Condition ([String]::IsNullOrWhiteSpace($silentSuccess.Stderr)) "sucesso gerou diagnóstico"

    $jsonSuccess = Invoke-CapturedPowerShell (
        "-NoProfile -ExecutionPolicy Bypass -File " + (Quote-Argument $snapshotScript) +
        " -ProjectRoot " + (Quote-Argument $testRoot) + " -Quiet -Json") $projectDirectory
    Assert-Condition ($jsonSuccess.ExitCode -eq 0) "JSON de sucesso deveria passar"
    $jsonSuccessValue = $jsonSuccess.Stdout | ConvertFrom-Json
    Assert-Condition ($jsonSuccessValue.status -eq "PASS") "JSON de sucesso inválido"

    Write-Utf8Text $sourcePath "class TailMsgFixture { static void Main() { } } // unstaged`n"
    $jsonFailure = Invoke-CapturedPowerShell (
        "-NoProfile -ExecutionPolicy Bypass -File " + (Quote-Argument $snapshotScript) +
        " -ProjectRoot " + (Quote-Argument $testRoot) + " -Quiet -Json") $projectDirectory
    Assert-Condition ($jsonFailure.ExitCode -ne 0) "divergência staged deveria bloquear"
    $jsonFailureValue = $jsonFailure.Stdout | ConvertFrom-Json
    Assert-Condition ($jsonFailureValue.status -eq "BLOCKED") "JSON de falha staged inválido"
    Assert-Condition ($jsonFailure.Stderr.Length -lt 1000) "diagnóstico staged excessivo"
    Assert-Condition ($jsonFailure.Stderr -notmatch "unstaged") "diagnóstico expôs conteúdo da fonte"

    $hiddenFixtureDirectory = Join-Path $testRoot ".github\workflows"
    $hiddenFixturePath = Join-Path $hiddenFixtureDirectory "validate.yml"
    New-Item -ItemType Directory -Path $hiddenFixtureDirectory -Force | Out-Null
    Write-Utf8Text $hiddenFixturePath "name: fixture`n"
    Invoke-Git $testRoot @("add", ".github/workflows/validate.yml")
    Invoke-Git $testRoot @("commit", "--quiet", "-m", "hidden path fixture")
    Write-Utf8Text $hiddenFixturePath "name: unstaged fixture change`n"
    $jsonHiddenFailure = Invoke-CapturedPowerShell (
        "-NoProfile -ExecutionPolicy Bypass -File " + (Quote-Argument $snapshotScript) +
        " -ProjectRoot " + (Quote-Argument $testRoot) + " -Quiet -Json") $projectDirectory
    Assert-Condition ($jsonHiddenFailure.ExitCode -ne 0) "divergência em diretório oculto deveria bloquear"
    $jsonHiddenFailureValue = $jsonHiddenFailure.Stdout | ConvertFrom-Json
    Assert-Condition ($jsonHiddenFailureValue.status -eq "BLOCKED") "JSON de diretório oculto inválido"
    Assert-Condition (@($jsonHiddenFailureValue.affectedPaths) -contains ".github/workflows/validate.yml") (
        "divergência em .github não foi detectada")

    $releaseFixtureDirectory = Join-Path $testRoot "release"
    $releaseFixturePath = Join-Path $releaseFixtureDirectory "package-release.ps1"
    New-Item -ItemType Directory -Path $releaseFixtureDirectory -Force | Out-Null
    Write-Utf8Text $releaseFixturePath "# release fixture`n"
    Invoke-Git $testRoot @("add", "release/package-release.ps1")
    Invoke-Git $testRoot @("commit", "--quiet", "-m", "release fixture")
    Remove-Item -LiteralPath $releaseFixturePath -Force
    $jsonDeletion = Invoke-CapturedPowerShell (
        "-NoProfile -ExecutionPolicy Bypass -File " + (Quote-Argument $snapshotScript) +
        " -ProjectRoot " + (Quote-Argument $testRoot) + " -Quiet -Json") $projectDirectory
    Assert-Condition ($jsonDeletion.ExitCode -ne 0) "deleção unstaged deveria bloquear"
    $jsonDeletionValue = $jsonDeletion.Stdout | ConvertFrom-Json
    Assert-Condition ($jsonDeletionValue.status -eq "BLOCKED") "JSON de deleção staged inválido"
    Assert-Condition (@($jsonDeletionValue.affectedPaths) -contains "release/package-release.ps1") (
        "deleção de artefato de release não foi detectada")

    $probeScript = Join-Path $testRoot "emit-sensitive.ps1"
    Write-Utf8Text $probeScript @"
Write-Output 'api_key=TOP_SECRET'
Write-Error 'Authorization: Bearer TOP_TOKEN'
exit 7
"@
    $logPath = Join-Path $testRoot "masked.log"
    $resultPath = Join-Path $testRoot "masked.result.json"
    $wrapperArguments = (
        "-NoProfile -ExecutionPolicy Bypass -File " + (Quote-Argument $wrapperScript) +
        " -Name mask-test -FilePath powershell.exe -Arguments " +
        (Quote-Argument ("-NoProfile -ExecutionPolicy Bypass -File " + (Quote-Argument $probeScript))) +
        " -WorkingDirectory " + (Quote-Argument $projectDirectory) +
        " -LogPath " + (Quote-Argument $logPath) +
        " -ResultPath " + (Quote-Argument $resultPath) +
        " -TailLines 5")
    $maskedRun = Invoke-CapturedPowerShell $wrapperArguments $projectDirectory
    Assert-Condition ($maskedRun.ExitCode -eq 7) "wrapper não preservou exit code real"
    $maskedLog = Get-Content -Raw -LiteralPath $logPath
    Assert-Condition ($maskedLog -notmatch "TOP_SECRET|TOP_TOKEN") "log reteve segredo"
    Assert-Condition ($maskedLog -match "<REDACTED>") "log não foi mascarado"
    $maskedResult = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    Assert-Condition ($maskedResult.exitCode -eq 7) "JSON do wrapper inválido"

    $installerRoot = Join-Path $testRoot "installer-fixture"
    New-Item -ItemType Directory -Path (Join-Path $installerRoot "scripts"), (Join-Path $installerRoot ".githooks") -Force | Out-Null
    Write-Utf8Text (Join-Path $installerRoot ".githooks\pre-commit") "#!/bin/sh`n"
    Copy-Item -LiteralPath $installerScript -Destination (Join-Path $installerRoot "scripts\install-hooks.ps1") -Force
    Invoke-Git $installerRoot @("init", "--quiet")
    $installerArguments = "-NoProfile -ExecutionPolicy Bypass -File " +
        (Quote-Argument (Join-Path $installerRoot "scripts\install-hooks.ps1")) + " -Quiet"
    $installFirst = Invoke-CapturedPowerShell $installerArguments $installerRoot
    $installSecond = Invoke-CapturedPowerShell $installerArguments $installerRoot
    $installerError = (($installFirst.Stderr + " " + $installSecond.Stderr).Trim() -replace '[\r\n]+', ' ')
    if ($installerError.Length -gt 400) {
        $installerError = $installerError.Substring(0, 400)
    }
    Assert-Condition ($installFirst.ExitCode -eq 0 -and $installSecond.ExitCode -eq 0) (
        "instalador não é idempotente (exit1={0}, exit2={1}, erro={2})" -f
        $installFirst.ExitCode, $installSecond.ExitCode, $installerError)
    Assert-Condition ([String]::IsNullOrWhiteSpace($installFirst.Stdout) -and [String]::IsNullOrWhiteSpace($installSecond.Stdout)) "instalador quiet gerou saída"
    $fixtureHookPath = (& git -C $installerRoot config --local --get core.hooksPath 2>$null).Trim()
    Assert-Condition ($fixtureHookPath -eq ".githooks") "instalador não configurou core.hooksPath"

    $hookClone = Join-Path $testRoot "git-hook-clone"
    Copy-HookFixture $hookClone
    Invoke-Git $hookClone @("init", "--quiet")
    Invoke-Git $hookClone @("config", "core.autocrlf", "false")
    Invoke-Git $hookClone @("config", "user.email", "tailmsg-hook@example.invalid")
    Invoke-Git $hookClone @("config", "user.name", "TailMsg Hook Test")
    Invoke-Git $hookClone @("add", "-A")
    Invoke-Git $hookClone @("commit", "--quiet", "-m", "fixture")
    $hookInstaller = Join-Path $hookClone "scripts\install-hooks.ps1"
    $hookInstall = Invoke-CapturedPowerShell (
        "-NoProfile -ExecutionPolicy Bypass -File " + (Quote-Argument $hookInstaller) + " -Quiet") $hookClone
    Assert-Condition ($hookInstall.ExitCode -eq 0) "instalador do clone de hook falhou"
    Invoke-Git $hookClone @("hook", "run", "pre-commit")

    $currentHookPath = (& git -C $hookClone config --local --get core.hooksPath 2>$null).Trim()
    Assert-Condition ($currentHookPath -eq ".githooks") "hook não está ativo no clone atual"
    Assert-Condition (Test-Path -LiteralPath (Join-Path $hookClone ".githooks\pre-commit") -PathType Leaf) "launcher do hook ausente no clone"

    if (-not $Quiet) {
        Write-Output "PASS: testes do mecanismo de validação concluídos."
    }
    exit 0
}
catch {
    [Console]::Error.WriteLine(
        ("FAIL: testes do mecanismo de validação falharam: " + $_.Exception.Message))
    exit 1
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
