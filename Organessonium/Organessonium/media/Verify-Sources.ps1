[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
function Get-MediaSha256 {
    param([string]$Path)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        return ([System.BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
    }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}

$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'source-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.redistributionReady -ne $true) {
    Write-Warning 'A correspondência de todas as fontes transitivas do FFmpeg ainda requer revisão antes de redistribuição pública. Licenças e referências estão em media/NOTICE.md e source-manifest.json.'
}

$runtime = Join-Path $PSScriptRoot 'ffmpeg.exe'
if (-not (Test-Path -LiteralPath $runtime -PathType Leaf) -or
    (Get-MediaSha256 -Path $runtime) -ne $manifest.sha256) {
    throw 'O executável FFmpeg não corresponde à identidade registrada no manifesto.'
}
foreach ($license in @('GPL-3.0.txt', 'BtbN-LICENSE.txt', 'NOTICE.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $license) -PathType Leaf)) {
        throw "Um aviso ou licença do componente FFmpeg está ausente: $license."
    }
}

if ($manifest.redistributionReady -eq $true -and ([string]::IsNullOrWhiteSpace($manifest.correspondingSourceArchive.path) -or
    [string]::IsNullOrWhiteSpace($manifest.correspondingSourceArchive.sha256))) {
    throw 'O manifesto não identifica o pacote completo de fontes para redistribuição.'
}
if ($manifest.redistributionReady -eq $true) {
    $archive = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $manifest.correspondingSourceArchive.path))
    $mediaRoot = [System.IO.Path]::GetFullPath($PSScriptRoot) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $archive.StartsWith($mediaRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $archive -PathType Leaf) -or
        (Get-MediaSha256 -Path $archive) -ne $manifest.correspondingSourceArchive.sha256) {
        throw 'O pacote completo de fontes está ausente ou seu SHA-256 não corresponde ao manifesto.'
    }
}
Write-Output 'FFmpeg: identidade e licença incluída conferidas.'
