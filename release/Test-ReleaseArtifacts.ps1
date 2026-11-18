[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
}
$root = [System.IO.Path]::GetFullPath($RepositoryRoot)
$directory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
Import-Module (Join-Path $root 'tools\Versioning.psm1') -Force
$info = Get-OrganessoniumVersionInfo -RepositoryRoot $root

if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
    throw "Diretório de artefatos não encontrado: $directory"
}

$expected = @(
    'Organessonium-installer-x86.msi',
    'Organessonium-installer-x64.msi',
    'Organessonium-installer-arm64.msi',
    'Organessonium-x86.exe',
    'Organessonium-x64.exe',
    'Organessonium-arm64.exe',
    'Organessonium-windows-source.zip',
    'ffmpeg-corresponding-source.zip',
    'build-info.json',
    'SHA256SUMS.txt',
    ('msix/Organessonium_{0}_x86.msix' -f $info.PackageVersion),
    ('msix/Organessonium_{0}_x64.msix' -f $info.PackageVersion),
    ('msix/Organessonium_{0}_arm64.msix' -f $info.PackageVersion),
    ('Organessonium_{0}.msixbundle' -f $info.PackageVersion)
)

$failures = [System.Collections.Generic.List[string]]::new()
foreach ($relativePath in $expected) {
    $path = Join-Path $directory $relativePath.Replace('/', '\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $failures.Add("ausente: $relativePath")
    }
    elseif ((Get-Item -LiteralPath $path).Length -le 0) {
        $failures.Add("vazio: $relativePath")
    }
}

if ($failures.Count -eq 0) {
    $buildInfo = Get-Content -LiteralPath (Join-Path $directory 'build-info.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]$buildInfo.version -ne $info.Version -or [int]$buildInfo.build -ne $info.Build) {
        $failures.Add("build-info.json não corresponde a Version.props ($($info.Version) build $($info.Build)).")
    }
    if ([string]$buildInfo.packageVersion -ne $info.PackageVersion) {
        $failures.Add("build-info.json packageVersion não corresponde a $($info.PackageVersion).")
    }

    $sumsPath = Join-Path $directory 'SHA256SUMS.txt'
    $sums = @{}
    foreach ($line in [System.IO.File]::ReadAllLines($sumsPath)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $match = [regex]::Match($line, '^([0-9A-Fa-f]{64})  (.+)$')
        if (-not $match.Success) {
            $failures.Add("linha inválida em SHA256SUMS.txt: $line")
            continue
        }
        $relative = $match.Groups[2].Value.Replace('\', '/')
        if ($relative -match '(^|/)\.\.(/|$)' -or [System.IO.Path]::IsPathRooted($relative)) {
            $failures.Add("caminho inválido em SHA256SUMS.txt: $relative")
            continue
        }
        if ($sums.ContainsKey($relative)) {
            $failures.Add("entrada duplicada em SHA256SUMS.txt: $relative")
            continue
        }
        $sums[$relative] = $match.Groups[1].Value.ToLowerInvariant()
    }

    foreach ($relativePath in $expected) {
        if ($relativePath -eq 'SHA256SUMS.txt') { continue }
        $portable = $relativePath.Replace('\', '/')
        if (-not $sums.ContainsKey($portable)) {
            $failures.Add("sem hash em SHA256SUMS.txt: $portable")
            continue
        }
        $path = Join-Path $directory $portable.Replace('/', '\')
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -ne $sums[$portable]) {
                $failures.Add("SHA-256 divergente: $portable")
            }
        }
    }

    foreach ($exeRelative in @('Organessonium-x86.exe', 'Organessonium-x64.exe', 'Organessonium-arm64.exe')) {
        $exePath = Join-Path $directory $exeRelative
        if (Test-Path -LiteralPath $exePath -PathType Leaf) {
            $stream = [System.IO.File]::OpenRead($exePath)
            try {
                $header = New-Object byte[] 2
                $read = $stream.Read($header, 0, 2)
                if ($read -ne 2 -or $header[0] -ne 0x4D -or $header[1] -ne 0x5A) {
                    $failures.Add("não parece um executável PE: $exeRelative")
                }
            }
            finally {
                $stream.Dispose()
            }
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host "`nValidação dos artefatos falhou:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host " - $_" -ForegroundColor Red }
    exit 1
}

Write-Host ("Artefatos validados: {0} arquivo(s), versão {1} build {2}." -f $expected.Count, $info.Version, $info.Build) -ForegroundColor Green
exit 0
