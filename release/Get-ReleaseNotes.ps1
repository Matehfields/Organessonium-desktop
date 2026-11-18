[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$ChangelogPath,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ChangelogPath)) {
    $ChangelogPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'CHANGELOG.md'
}
if (-not (Test-Path -LiteralPath $ChangelogPath -PathType Leaf)) {
    throw "CHANGELOG.md não encontrado: $ChangelogPath"
}

$content = [System.IO.File]::ReadAllText($ChangelogPath)
$content = [regex]::Replace($content, '(?s)<!--.*?-->', '')
$escaped = [regex]::Escape($Version)
$pattern = "(?ms)^##\s+\[$escaped\](?:[^\r\n]*)\r?\n(.*?)(?=^##\s+\[|\z)"
$match = [regex]::Match($content, $pattern)
if (-not $match.Success) {
    throw "CHANGELOG.md não contém a seção '## [$Version]'. Adicione as notas revisadas antes de publicar."
}

$notes = $match.Groups[1].Value.Trim()
if ([string]::IsNullOrWhiteSpace($notes)) {
    throw "A seção '## [$Version]' do CHANGELOG.md está vazia."
}
if ($notes -match '(?i)Não publicado|A definir') {
    throw "A seção '## [$Version]' ainda contém marcador de versão não publicada. Substitua a data e revise as notas antes de publicar."
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Write-Output $notes
}
else {
    $parent = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        $null = New-Item -ItemType Directory -Force -Path $parent
    }
    [System.IO.File]::WriteAllText($OutputPath, ($notes + "`n"), [System.Text.UTF8Encoding]::new($false))
    Write-Host "Notas da versão gravadas em $OutputPath" -ForegroundColor Green
}
