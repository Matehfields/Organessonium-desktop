[CmdletBinding()]
param(
    [switch]$ForRedistribution,
    [string]$MediaDirectory
)

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

if ([string]::IsNullOrWhiteSpace($MediaDirectory)) { $MediaDirectory = $PSScriptRoot }
$mediaDirectory = [System.IO.Path]::GetFullPath($MediaDirectory)
$manifest = Get-Content -LiteralPath (Join-Path $mediaDirectory 'source-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.redistributionReady -ne $true) {
    if ($ForRedistribution) {
        throw 'Release público bloqueado: o FFmpeg ainda não possui fontes correspondentes completas e conferidas. Consulte media/NOTICE.md.'
    }
    Write-Warning 'A correspondência de todas as fontes transitivas do FFmpeg ainda requer revisão antes de redistribuição pública. Licenças e referências estão em media/NOTICE.md e source-manifest.json.'
}

$runtime = Join-Path $mediaDirectory 'ffmpeg.exe'
if ($null -eq $manifest.runtimeArchive -and (-not (Test-Path -LiteralPath $runtime -PathType Leaf) -or
    (Get-MediaSha256 -Path $runtime) -ne $manifest.sha256)) {
    throw 'O executável FFmpeg não corresponde à identidade registrada no manifesto.'
}
foreach ($license in @('GPL-3.0.txt', 'BtbN-LICENSE.txt', 'NOTICE.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $mediaDirectory $license) -PathType Leaf)) {
        throw "Um aviso ou licença do componente FFmpeg está ausente: $license."
    }
}

if ($manifest.redistributionReady -eq $true -and ([string]::IsNullOrWhiteSpace($manifest.correspondingSourceArchive.path) -or
    [string]::IsNullOrWhiteSpace($manifest.correspondingSourceArchive.sha256))) {
    throw 'O manifesto não identifica o pacote completo de fontes para redistribuição.'
}
if ($manifest.redistributionReady -eq $true) {
    $archive = [System.IO.Path]::GetFullPath((Join-Path $mediaDirectory $manifest.correspondingSourceArchive.path))
    $mediaRoot = $mediaDirectory + [System.IO.Path]::DirectorySeparatorChar
    if (-not $archive.StartsWith($mediaRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $archive -PathType Leaf) -or
        (Get-MediaSha256 -Path $archive) -ne $manifest.correspondingSourceArchive.sha256) {
        throw 'O pacote completo de fontes está ausente ou seu SHA-256 não corresponde ao manifesto.'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $sourceZip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entry = $sourceZip.GetEntry('source-inventory.json')
        if ($null -eq $entry) { throw 'Inventário das fontes correspondentes ausente.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $sourceInventory = $reader.ReadToEnd() | ConvertFrom-Json }
        finally { $reader.Dispose() }
        foreach ($component in @($manifest.runtimeFiles)) {
            $matched = @($sourceInventory.runtimeFiles | Where-Object { $_.path -eq $component.path -and $_.sha256 -eq $component.sha256 })
            if ($matched.Count -ne 1) { throw 'As fontes não identificam o mesmo runtime da distribuição.' }
        }
    }
    finally { $sourceZip.Dispose() }
}
$runtimeArchive = $null
if ($null -ne $manifest.runtimeArchive) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $runtimeArchivePath = [IO.Path]::GetFullPath((Join-Path $mediaDirectory $manifest.runtimeArchive.path))
    if (-not $runtimeArchivePath.StartsWith($mediaDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $runtimeArchivePath -PathType Leaf) -or
        (Get-MediaSha256 $runtimeArchivePath) -ne $manifest.runtimeArchive.sha256) {
        throw 'O pacote embutido do motor de mídia está ausente ou corrompido.'
    }
    $runtimeArchive = [IO.Compression.ZipFile]::OpenRead($runtimeArchivePath)
    if (@($manifest.runtimeFiles | Where-Object { $_.path -eq 'ffmpeg.exe' -and $_.sha256 -eq $manifest.sha256 }).Count -ne 1) {
        $runtimeArchive.Dispose()
        throw 'A identidade principal do FFmpeg não corresponde ao pacote embutido.'
    }
}
try {
foreach ($file in @($manifest.runtimeFiles)) {
    if ($null -eq $file) { continue }
    if ($null -ne $runtimeArchive) {
        $entry = $runtimeArchive.GetEntry($file.path)
        if ($null -eq $entry -or [IO.Path]::GetFileName($file.path) -ne $file.path) { throw 'Componente embutido inválido ou ausente.' }
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $actual = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose(); $sha.Dispose() }
        if ($actual -ne $file.sha256) { throw 'SHA-256 de componente embutido divergente.' }
        continue
    }
    $runtimeFile = [System.IO.Path]::GetFullPath((Join-Path $mediaDirectory $file.path))
    $runtimeRoot = $mediaDirectory + [System.IO.Path]::DirectorySeparatorChar
    if (-not $runtimeFile.StartsWith($runtimeRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $runtimeFile -PathType Leaf) -or
        (Get-MediaSha256 -Path $runtimeFile) -ne $file.sha256) {
        throw 'Um arquivo do runtime FFmpeg está ausente, fora da pasta de mídia ou com SHA-256 divergente.'
    }
}
if ($null -ne $runtimeArchive -and $runtimeArchive.Entries.Count -ne @($manifest.runtimeFiles).Count) {
    throw 'O pacote embutido contém componentes não inventariados.'
}
}
finally { if ($null -ne $runtimeArchive) { $runtimeArchive.Dispose() } }
Write-Output 'FFmpeg: identidade e licença incluída conferidas.'
