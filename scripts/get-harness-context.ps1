param(
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location -LiteralPath $projectDirectory

$tempManifest = Join-Path ([IO.Path]::GetTempPath()) ("TailMsgHarnessPrefix-" + [Guid]::NewGuid().ToString("N") + ".json")

try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $projectDirectory "scripts\validate-harness-prefix.ps1") `
        -Quiet -OutputPath $tempManifest
    if ($LASTEXITCODE -ne 0) {
        throw "Não foi possível calcular o prefixo estável."
    }

    $prefix = Get-Content -Raw -LiteralPath $tempManifest | ConvertFrom-Json
    $config = Get-Content -Raw -LiteralPath (Join-Path $projectDirectory "UpdateConfig.cs")
    $versionMatch = [regex]::Match($config, 'CurrentVersion\s*=\s*"([0-9]{8}_[0-9]{3})"')
    $branch = ((& git branch --show-current 2>$null | Select-Object -First 1) -as [string]).Trim()
    $statusLines = @(& git status --short 2>$null)

    $context = [ordered]@{
        schemaVersion = 1
        staticPrefixHash = $prefix.staticPrefixHash
        volatile = [ordered]@{
            localDate = (Get-Date -Format "yyyy-MM-dd")
            timezone = [TimeZoneInfo]::Local.Id
            productVersion = if ($versionMatch.Success) { $versionMatch.Groups[1].Value } else { "unknown" }
            branch = if ([String]::IsNullOrWhiteSpace($branch)) { "unknown" } else { $branch }
            changedFileCount = $statusLines.Count
            wineAvailable = $null -ne (Get-Command wine -ErrorAction SilentlyContinue)
            tailscaleAvailable = $null -ne (Get-Command tailscale,tailscale.exe -ErrorAction SilentlyContinue)
            osVersion = [Environment]::OSVersion.VersionString
        }
    }

    $json = $context | ConvertTo-Json -Depth 8
    if ([String]::IsNullOrWhiteSpace($OutputPath)) {
        Write-Output $json
    }
    else {
        $resolvedOutput = if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $projectDirectory $OutputPath }
        $parent = Split-Path -Parent $resolvedOutput
        if (-not [String]::IsNullOrWhiteSpace($parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        $json | Set-Content -LiteralPath $resolvedOutput -Encoding UTF8
    }
    exit 0
}
catch {
    [Console]::Error.WriteLine("FAIL: " + $_.Exception.Message)
    exit 1
}
finally {
    Remove-Item -LiteralPath $tempManifest -Force -ErrorAction SilentlyContinue
}
