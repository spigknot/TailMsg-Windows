param(
    [switch]$Quiet
)

# Vacina contra a classe de bug que derrubou o popup de áudio: campo declarado,
# usado no código e NUNCA atribuído → NullReferenceException em tempo de uso.
# Um campo privado sem escrita é sempre suspeito: ou é lixo, ou o caminho que
# deveria preenchê-lo não existe (era o caso de bodyPanel).
$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location -LiteralPath $projectDirectory

function Get-RelativePath([string]$Path) {
    $resolvedProject = [IO.Path]::GetFullPath($projectDirectory).TrimEnd('\') + '\'
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    return $resolvedPath.Substring($resolvedProject.Length).Replace('\', '/')
}

function Test-Excluded([string]$RelativePath) {
    return $RelativePath -match '^(dist|build|release/packages|release/generated|release/staging[^/]*)/'
}

try {
    $fieldPattern = [regex]'(?m)^[ \t]*private[ \t]+(?:static[ \t]+|readonly[ \t]+)*[\w\.<>\[\],\s]+\s+(\w+)[ \t]*;[ \t]*$'
    $sourceFiles = @(Get-ChildItem -LiteralPath $projectDirectory -Filter *.cs -File -Recurse |
        ForEach-Object {
            $relative = Get-RelativePath $_.FullName
            if (-not (Test-Excluded $relative)) { $_ }
        })

    $failures = @()
    foreach ($file in $sourceFiles) {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($match in $fieldPattern.Matches($text)) {
            $name = $match.Groups[1].Value
            $line = ($text.Substring(0, $match.Index) -split "`n").Count
            $declarationLine = ($text -split "`n")[$line - 1]
            if ($declarationLine -match '//\s*lint:allow-unassigned') { continue }

            $escaped = [regex]::Escape($name)
            # escrita direta (campo = valor, +=, ++, --) e passagem por ref/out
            $writePattern = '(?:this\.)?' + $escaped + '\s*(?:\+\+|--|[+\-*/|&^]?=(?!=))|(?:ref|out)\s+' + $escaped + '\b'
            if (-not [regex]::IsMatch($text, $writePattern)) {
                $failures += ("{0}:{1}: campo '{2}' nunca recebe atribuição" -f
                    (Get-RelativePath $file.FullName), $line, $name)
            }
        }
    }

    if ($failures.Count -gt 0) {
        throw ("Campo(s) privado(s) sem atribuição (provável NullReferenceException):`n" + ($failures -join "`n"))
    }

    if (-not $Quiet) {
        Write-Output ("PASS: campos privados atribuídos ({0} arquivos .cs)" -f $sourceFiles.Count)
    }
    exit 0
}
catch {
    [Console]::Error.WriteLine("FAIL: campos-sem-atribuicao - " + $_.Exception.Message)
    exit 1
}
