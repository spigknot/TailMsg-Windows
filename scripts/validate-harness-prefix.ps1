param(
    [switch]$Quiet,
    [string]$OutputPath,
    [switch]$RequireTracked
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location -LiteralPath $projectDirectory

# This is the only runtime prefix asset. Do not derive it from filesystem
# enumeration or modification time.
$stableAssets = @(
    "AGENTS.md"
)

$requiredRoutes = @(
    "AGENTS.md",
    "docs/agents/harness-prefix.md",
    ".agents/skills/tailmsg-validation/SKILL.md",
    "docs/agents/validation.md",
    "scripts/check-staged-snapshot.ps1",
    "scripts/validate-agent-harness.ps1",
    "scripts/validate-harness-prefix.ps1",
    "scripts/get-harness-context.ps1",
    "scripts/lint.ps1",
    "scripts/invoke-validated-command.ps1",
    "scripts/validate-release.ps1",
    "scripts/install-hooks.ps1",
    "tests/test-validation-harness.ps1",
    ".githooks/pre-commit",
    ".github/workflows/validate.yml",
    ".githooks/pre-commit.ps1"
)

function Normalize-Text([string]$Text) {
    return $Text.Replace("`r`n", "`n").Replace("`r", "`n")
}

function Get-Sha256Text([string]$Text) {
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $bytes = $utf8.GetBytes($Text)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Fail([string]$Message) {
    throw $Message
}

try {
    $manifestAssets = @()
    $hashParts = @()
    $volatility = @()

    foreach ($relativePath in $requiredRoutes) {
        $routePath = Join-Path $projectDirectory ($relativePath.Replace("/", "\"))
        if (-not (Test-Path -LiteralPath $routePath -PathType Leaf)) {
            Fail "Rota canônica ausente: $relativePath"
        }
        if ($RequireTracked) {
            $previousErrorAction = $ErrorActionPreference
            $ErrorActionPreference = "Continue"
            git ls-files --error-unmatch -- $relativePath 2>$null | Out-Null
            $trackedExitCode = $LASTEXITCODE
            $ErrorActionPreference = $previousErrorAction
            if ($trackedExitCode -ne 0) {
                Fail "Rota canônica não rastreada pelo Git: $relativePath"
            }
        }
    }

    foreach ($relativePath in $stableAssets) {
        $fullPath = Join-Path $projectDirectory ($relativePath.Replace("/", "\"))
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            Fail "Asset estável ausente: $relativePath"
        }

        $normalized = Normalize-Text ([IO.File]::ReadAllText($fullPath))
        $assetVolatility = @()

        if ($normalized -match '(?<![A-Za-z0-9])20[0-9]{6}_[0-9]{3}(?![A-Za-z0-9])') {
            $assetVolatility += "release-version"
        }
        if ($normalized -match '(?i)([A-Z]:\\|\\\\|/Users/|/home/)') {
            $assetVolatility += "absolute-path"
        }
        if ($normalized -match '(?i)\b(Get-Date|DateTime\.Now|DateTime\.UtcNow)\b') {
            $assetVolatility += "runtime-metadata"
        }

        if ($assetVolatility.Count -gt 0) {
            $volatility += [ordered]@{
                path = $relativePath
                reasons = @($assetVolatility)
            }
        }

        $assetHash = Get-Sha256Text $normalized
        $manifestAssets += [ordered]@{
            path = $relativePath
            sha256 = $assetHash
            bytes = ([Text.Encoding]::UTF8.GetByteCount($normalized))
            lines = (($normalized -split "`n").Count)
        }
        $hashParts += "PATH:$relativePath`n$normalized`n---`n"
    }

    if ($volatility.Count -gt 0) {
        $paths = (($volatility | ForEach-Object { $_.path + ":" + ($_.reasons -join ",") }) -join "; ")
        Fail "Conteúdo volátil encontrado nos assets estáveis: $paths"
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        assets = @($manifestAssets)
        routes = [ordered]@{
            count = $requiredRoutes.Count
            requireTracked = [bool]$RequireTracked
        }
        staticPrefixHash = Get-Sha256Text ($hashParts -join "")
        volatilityChecks = [ordered]@{
            releaseVersion = "pass"
            absolutePath = "pass"
            runtimeMetadata = "pass"
        }
    }

    if (-not [String]::IsNullOrWhiteSpace($OutputPath)) {
        $resolvedOutput = if ([IO.Path]::IsPathRooted($OutputPath)) {
            $OutputPath
        }
        else {
            Join-Path $projectDirectory $OutputPath
        }
        $parent = Split-Path -Parent $resolvedOutput
        if (-not [String]::IsNullOrWhiteSpace($parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resolvedOutput -Encoding UTF8
    }

    if (-not $Quiet) {
        Write-Output ("PASS: prefixo estável " + $manifest.staticPrefixHash)
        Write-Output ("Assets verificados: " + $stableAssets.Count)
        Write-Output ("Rotas verificadas: " + $requiredRoutes.Count)
    }
    exit 0
}
catch {
    [Console]::Error.WriteLine("FAIL: " + $_.Exception.Message)
    exit 1
}
