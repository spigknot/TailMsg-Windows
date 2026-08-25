[CmdletBinding()]
param(
    [string]$ProjectRoot = "",
    [switch]$Quiet,
    [switch]$Json
)

$ErrorActionPreference = "Stop"

function Get-RepositoryRoot {
    param([string]$RequestedRoot)

    if (-not [String]::IsNullOrWhiteSpace($RequestedRoot)) {
        return [IO.Path]::GetFullPath($RequestedRoot)
    }

    $root = (& git rev-parse --show-toplevel 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or [String]::IsNullOrWhiteSpace($root)) {
        throw "Não foi possível localizar a raiz do repositório Git."
    }
    return [IO.Path]::GetFullPath($root)
}

function Normalize-RepositoryPath([string]$Path) {
    $normalized = $Path.Replace('\', '/')
    while ($normalized.StartsWith('./', [StringComparison]::Ordinal)) {
        $normalized = $normalized.Substring(2)
    }
    return $normalized
}

function Test-ValidationRelevantPath([string]$Path) {
    $normalized = Normalize-RepositoryPath $Path
    return $normalized -match '^(AGENTS\.md|README\.md$|\.agents/|\.githooks/|\.github/|docs/agents/|scripts/|tests/|build\.ps1$|install\.ps1$|firewall\.ps1$|.*\.cs$|installer/|assets/|release/)'
}

function Get-GitLines([string[]]$Arguments) {
    $previousErrorAction = $ErrorActionPreference
    try {
        # O Git pode emitir avisos de normalização em stderr mesmo com exit 0.
        # Eles não podem virar exceções do Windows PowerShell sob -ErrorAction Stop.
        $ErrorActionPreference = "Continue"
        $lines = @(& git @Arguments 2>$null)
        $exitCode = [int]$LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }
    if ($exitCode -ne 0) {
        throw "Falha ao executar git $($Arguments -join ' ')."
    }
    return @($lines)
}

$repositoryRoot = Get-RepositoryRoot $ProjectRoot
if (-not (Test-Path -LiteralPath $repositoryRoot -PathType Container)) {
    throw "Raiz do repositório não encontrada: $repositoryRoot"
}
Set-Location -LiteralPath $repositoryRoot

$unstagedTracked = @(Get-GitLines @("diff", "--name-only", "--diff-filter=ACDMRTUXB"))
$untracked = @(Get-GitLines @("ls-files", "--others", "--exclude-standard"))

$affected = @(
    @($unstagedTracked + $untracked) |
        Where-Object { -not [String]::IsNullOrWhiteSpace([string]$_) } |
        ForEach-Object { Normalize-RepositoryPath ([string]$_) } |
        Where-Object { Test-ValidationRelevantPath $_ } |
        Sort-Object -Unique
)

$status = if ($affected.Count -eq 0) { "PASS" } else { "BLOCKED" }
$result = [ordered]@{
    schemaVersion = 1
    check = "staged-snapshot"
    status = $status
    affectedPathCount = $affected.Count
    affectedPaths = @($affected | Select-Object -First 20)
}

if ($Json) {
    Write-Output ($result | ConvertTo-Json -Depth 5 -Compress)
}
elseif (-not $Quiet -and $status -eq "PASS") {
    Write-Output "PASS: snapshot staged sem divergência relevante."
}

if ($status -ne "PASS") {
    $preview = @($affected | Select-Object -First 5) -join ", "
    if ($affected.Count -gt 5) {
        $preview += ", ..."
    }
    [Console]::Error.WriteLine(
        "FAIL: alterações relevantes fora do índice impedem validar o snapshot staged. Afetados: " + $preview)
    exit 1
}

exit 0
