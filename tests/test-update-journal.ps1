param(
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$journalSource = Get-Content -Raw -LiteralPath (Join-Path $projectDirectory "UpdateJournal.cs")
$probeSource = @"
public static class TailMsgJournalProbe
{
    public static string Sanitize(string value)
    {
        return UpdateJournal.SanitizeForJournal(value);
    }
}
"@

try {
    Add-Type -TypeDefinition ($journalSource + "`r`n" + $probeSource)
    $cases = @(
        @{ Value = "Authorization: Bearer TOP_SECRET"; Forbidden = "TOP_SECRET" },
        @{ Value = "api_key=TOP_KEY"; Forbidden = "TOP_KEY" },
        @{ Value = "https://example.invalid/status?token=TOP_QUERY&x=1"; Forbidden = "TOP_QUERY" },
        @{ Value = "cfat_TOP_TOKEN"; Forbidden = "cfat_TOP_TOKEN" }
    )

    foreach ($case in $cases) {
        $sanitized = [TailMsgJournalProbe]::Sanitize($case.Value)
        if ($sanitized.Contains($case.Forbidden)) {
            throw "o journal reteve um valor sensível de teste"
        }
        if (-not $sanitized.Contains("<REDACTED>")) {
            throw "o journal não aplicou a marca de redação"
        }
        if ($sanitized -match "[\r\n;]") {
            throw "o journal permitiu quebra de registro"
        }
    }

    if (-not $Quiet) {
        Write-Output "PASS: redação segura do journal validada."
    }
    exit 0
}
catch {
    [Console]::Error.WriteLine("FAIL: redação do journal - " + $_.Exception.Message)
    exit 1
}
