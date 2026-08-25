param(
    [string]$RepositoryRoot = ""
)

$ErrorActionPreference = "Stop"

try {
    if ([String]::IsNullOrWhiteSpace($RepositoryRoot)) {
        $RepositoryRoot = (& git rev-parse --show-toplevel 2>$null).Trim()
    }
    if ([String]::IsNullOrWhiteSpace($RepositoryRoot)) {
        throw "raiz do Git não encontrada"
    }

    $projectDirectory = [IO.Path]::GetFullPath($RepositoryRoot)
    $validator = Join-Path $projectDirectory "scripts\validate-agent-harness.ps1"
    if (-not (Test-Path -LiteralPath $validator -PathType Leaf)) {
        throw "validador central ausente"
    }

    $powerShell = Get-Command powershell.exe -ErrorAction SilentlyContinue
    if ($null -eq $powerShell) {
        $powerShell = Get-Command pwsh -ErrorAction SilentlyContinue
    }
    if ($null -eq $powerShell) {
        throw "PowerShell não encontrado"
    }

    Set-Location -LiteralPath $projectDirectory
    $previousErrorAction = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $powerShell.Source -NoProfile -NonInteractive -ExecutionPolicy Bypass `
            -File $validator `
            -Quiet `
            -EvidencePath (Join-Path $projectDirectory "build-validation\pre-commit.json") `
            2>$null | Out-Null
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }
    $exitCode = [int]$LASTEXITCODE
    if ($exitCode -ne 0) {
        [Console]::Error.WriteLine(
            ("TailMsg pre-commit falhou (exit={0}). Evidência: build-validation/pre-commit.json" -f $exitCode))
    }
    exit $exitCode
}
catch {
    $detail = $_.Exception.Message -replace '[\r\n]+', ' '
    if ($detail.Length -gt 240) {
        $detail = $detail.Substring(0, 240)
    }
    [Console]::Error.WriteLine(
        ("TailMsg pre-commit falhou: validação indisponível ({0})." -f $detail))
    exit 1
}
