[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [switch]$IncludeHistory
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
}
$root = [System.IO.Path]::GetFullPath($RepositoryRoot)
$git = Get-Command git -ErrorAction Stop

function Get-RelativeRepositoryPath {
    param([string]$Path)
    return $Path.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
}

$forbiddenNames = @(
    'release-settings.local.json',
    '.env',
    'id_rsa',
    'id_ed25519',
    'secrets.json'
)
$forbiddenExtensions = @('.pfx', '.p12', '.pem', '.key', '.snk', '.db', '.sqlite', '.sqlite3')

$patterns = @(
    [pscustomobject]@{ Name = 'chave privada PEM'; Regex = '-----BEGIN (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----' },
    [pscustomobject]@{ Name = 'token do GitHub'; Regex = '\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{20,})\b' },
    [pscustomobject]@{ Name = 'webhook do Discord'; Regex = 'https://discord(?:app)?\.com/api/webhooks/[0-9]{17,20}/[A-Za-z0-9._-]{30,}' },
    [pscustomobject]@{ Name = 'possível token do Discord'; Regex = '\b[MN][A-Za-z0-9_-]{23,}\.[A-Za-z0-9_-]{6}\.[A-Za-z0-9_-]{27,}\b' },
    [pscustomobject]@{ Name = 'token do Slack'; Regex = '\bxox[baprs]-[A-Za-z0-9-]{10,}\b' },
    [pscustomobject]@{ Name = 'chave de API do Google'; Regex = '\bAIza[0-9A-Za-z_-]{35}\b' },
    [pscustomobject]@{ Name = 'chave de acesso da AWS'; Regex = '\bAKIA[0-9A-Z]{16}\b' },
    [pscustomobject]@{ Name = 'caminho absoluto de usuário'; Regex = '[A-Za-z]:\\Users\\[^\\\s"'']+' }
)

$textExtensions = @(
    '.cs', '.xaml', '.js', '.mjs', '.cjs', '.html', '.htm', '.css', '.json', '.md', '.xml',
    '.txt', '.csproj', '.slnx', '.sln', '.props', '.targets', '.ps1', '.psm1', '.bat', '.cmd',
    '.yml', '.yaml', '.config', '.manifest', '.resw', '.resx', '.sh', '.editorconfig', '.template',
    '.gitignore', '.gitattributes'
)

$findings = [System.Collections.Generic.List[string]]::new()
$tracked = @(& git -C $root ls-files)
if ($LASTEXITCODE -ne 0) {
    throw 'git ls-files falhou.'
}
$untracked = @(& git -C $root ls-files --others --exclude-standard)
if ($LASTEXITCODE -ne 0) {
    throw 'git ls-files --others falhou.'
}
$filesToScan = @(($tracked + $untracked) | Sort-Object -Unique)

foreach ($relativePath in $filesToScan) {
    if ([string]::IsNullOrWhiteSpace($relativePath)) { continue }
    $portablePath = $relativePath.Replace('\', '/')
    $leaf = [System.IO.Path]::GetFileName($portablePath)
    $extension = [System.IO.Path]::GetExtension($leaf).ToLowerInvariant()

    if ($leaf -in $forbiddenNames -or $extension -in $forbiddenExtensions) {
        $findings.Add("arquivo proibido presente: $portablePath")
        continue
    }

    if ($extension -notin $textExtensions -and $leaf -notin @('.gitignore', '.gitattributes')) {
        continue
    }

    $path = Join-Path $root $relativePath.Replace('/', '\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    $info = Get-Item -LiteralPath $path
    if ($info.Length -gt 12MB) {
        Write-Warning "Arquivo grande ignorado pela varredura de segredos: $portablePath"
        continue
    }

    $bytes = [System.IO.File]::ReadAllBytes($path)
    if ($bytes.Length -gt 8192 -and ($bytes[0..8191] -contains 0)) {
        continue
    }

    $content = [System.Text.Encoding]::UTF8.GetString($bytes)
    foreach ($pattern in $patterns) {
        foreach ($match in [regex]::Matches($content, $pattern.Regex)) {
            $line = ($content.Substring(0, $match.Index) -split "`n").Count
            $findings.Add(("{0}:{1}: {2}" -f $portablePath, $line, $pattern.Name))
        }
    }
}

if ($IncludeHistory) {
    Write-Host 'Varrendo o histórico Git (pode demorar)...'
    $historyPattern = ($patterns | ForEach-Object { $_.Regex }) -join '|'
    $historyLines = @(& git -C $root log --all -p --no-color --no-ext-diff -- . ':(exclude)Organessonium/Organessonium/media/THIRD-PARTY-NOTICES.txt' 2>$null |
        Select-String -Pattern $historyPattern)
    if ($historyLines.Count -gt 0) {
        $findings.Add("histórico Git: $($historyLines.Count) linha(s) suspeita(s). Execute a varredura manualmente e avalie antes de publicar.")
    }
}

if ($findings.Count -gt 0) {
    Write-Host "`nPossíveis segredos ou dados locais encontrados:" -ForegroundColor Red
    $findings | Select-Object -Unique | ForEach-Object { Write-Host " - $_" -ForegroundColor Red }
    Write-Host "`nNenhum valor secreto foi impresso. Revogue qualquer credencial exposta antes de publicar." -ForegroundColor Yellow
    exit 1
}

Write-Host "Nenhum segredo ou caminho local encontrado nos $($filesToScan.Count) arquivos do repositório." -ForegroundColor Green
if (-not $IncludeHistory) {
    Write-Host 'O histórico Git não foi varrido nesta execução; use -IncludeHistory para uma verificação completa.'
}
exit 0
