param(
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location -LiteralPath $projectDirectory

function Write-Status([string]$Message) {
    if (-not $Quiet) {
        Write-Output $Message
    }
}

function Get-RelativePath([string]$Path) {
    $resolvedProject = [IO.Path]::GetFullPath($projectDirectory).TrimEnd('\') + '\'
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    return $resolvedPath.Substring($resolvedProject.Length).Replace('\', '/')
}

function Test-Excluded([string]$RelativePath) {
    return $RelativePath -match '^(dist|build|release/packages|release/generated|release/staging[^/]*)/'
}

try {
    # O Git pode emitir avisos de normalização de fim de linha em stderr mesmo
    # com exit code zero. Mantenha o erro de sintaxe do lint sob Stop, mas não
    # transforme esse aviso ambiental em uma exceção do Windows PowerShell.
    $previousErrorAction = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    git diff --check 2>$null
    $gitExitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorAction
    if ($gitExitCode -ne 0) {
        throw "git diff --check encontrou whitespace inválido."
    }

    $parserType = [System.Management.Automation.Language.Parser]
    $scriptFiles = @(Get-ChildItem -LiteralPath $projectDirectory -Filter *.ps1 -File -Recurse |
        ForEach-Object {
            $relative = Get-RelativePath $_.FullName
            if (-not (Test-Excluded $relative)) {
                $_
            }
        })
    $syntaxErrors = @()
    foreach ($scriptFile in $scriptFiles) {
        $tokens = $null
        $errors = $null
        [void]$parserType::ParseFile($scriptFile.FullName, [ref]$tokens, [ref]$errors)
        foreach ($errorRecord in @($errors)) {
            $syntaxErrors += ("{0}:{1}:{2}: {3}" -f
                (Get-RelativePath $scriptFile.FullName),
                $errorRecord.Extent.StartLineNumber,
                $errorRecord.Extent.StartColumnNumber,
                $errorRecord.Message)
        }
    }
    if ($syntaxErrors.Count -gt 0) {
        throw ("Erro(s) de sintaxe PowerShell:`n" + ($syntaxErrors -join "`n"))
    }

    Write-Status ("PASS: lint PowerShell/whitespace ({0} scripts)" -f $scriptFiles.Count)
    exit 0
}
catch {
    [Console]::Error.WriteLine("FAIL: lint - " + $_.Exception.Message)
    exit 1
}
