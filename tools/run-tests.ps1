[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$SkipMedia,
    [switch]$IncludeUi,
    [switch]$NoRestore,
    [switch]$NoBuild,
    [string[]]$Filter = @(),
    [string]$FfmpegDirectory,
    [string]$FfmpegExecutable,
    [string]$AppExecutable
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$testProject = Join-Path $repoRoot 'tests\Organessonium.NetworkTests\Organessonium.NetworkTests.csproj'
$mediaRoot = Join-Path $repoRoot 'Organessonium\Organessonium\media'

if (-not (Test-Path -LiteralPath $testProject -PathType Leaf)) {
    throw "Projeto de testes não encontrado: $testProject"
}

if (-not $NoRestore) {
    & dotnet restore $testProject
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore falhou.' }
}

if (-not $NoBuild) {
    $buildArguments = @('build', $testProject, '-c', $Configuration, '--no-restore')
    & dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build falhou.' }
}

$configurationRoot = Join-Path $repoRoot ("tests\Organessonium.NetworkTests\bin\{0}" -f $Configuration)
$testExe = Get-ChildItem -LiteralPath $configurationRoot -Filter 'Organessonium.NetworkTests.exe' -File -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $testExe) {
    throw "Executável de testes não encontrado em $configurationRoot. Compile o projeto primeiro."
}
$testOutput = $testExe.DirectoryName

if ($SkipMedia) {
    Remove-Item Env:ORGANESSONIUM_MEDIA_TEST_EXE -ErrorAction SilentlyContinue
    Remove-Item Env:ORGANESSONIUM_FFMPEG_TEST_EXE -ErrorAction SilentlyContinue
}
else {
    $ffmpegPath = $FfmpegExecutable
    if ([string]::IsNullOrWhiteSpace($ffmpegPath) -and -not [string]::IsNullOrWhiteSpace($FfmpegDirectory)) {
        $ffmpegPath = Join-Path $FfmpegDirectory 'ffmpeg.exe'
    }
    if ([string]::IsNullOrWhiteSpace($ffmpegPath)) {
        $ffmpegPath = Join-Path $mediaRoot 'ffmpeg.exe'
    }
    if (-not (Test-Path -LiteralPath $ffmpegPath -PathType Leaf)) {
        $runtimeArchive = Join-Path $mediaRoot 'runtime-package.zip'
        if (Test-Path -LiteralPath $runtimeArchive -PathType Leaf) {
            $extractRoot = Join-Path $repoRoot 'artifacts\test-media\runtime'
            if (Test-Path -LiteralPath $extractRoot) {
                Remove-Item -LiteralPath $extractRoot -Recurse -Force
            }
            $null = New-Item -ItemType Directory -Force -Path $extractRoot
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            [System.IO.Compression.ZipFile]::ExtractToDirectory($runtimeArchive, $extractRoot)
            $ffmpegPath = Join-Path $extractRoot 'ffmpeg.exe'
        }
    }
    if (-not (Test-Path -LiteralPath $ffmpegPath -PathType Leaf)) {
        throw 'FFmpeg não encontrado. Informe -FfmpegExecutable ou prepare media/runtime-package.zip. Use -SkipMedia para a suíte rápida.'
    }

    $appPath = $AppExecutable
    if ([string]::IsNullOrWhiteSpace($appPath)) {
        $appPath = Join-Path $testOutput 'Organessonium.exe'
    }
    if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) {
        throw "Executável do aplicativo não encontrado em $appPath. Informe -AppExecutable."
    }

    # AppearanceTests procura media\ffmpeg.exe ao lado do assembly de teste.
    $testMedia = Join-Path $testOutput 'media'
    $null = New-Item -ItemType Directory -Force -Path $testMedia
    $ffmpegSourceDirectory = Split-Path -Parent $ffmpegPath
    Get-ChildItem -LiteralPath $ffmpegSourceDirectory -File |
        Where-Object { $_.Extension -in @('.exe', '.dll') -or $_.Name -in @('NOTICE.md', 'BtbN-LICENSE.txt', 'THIRD-PARTY-NOTICES.txt') } |
        Copy-Item -Destination $testMedia -Force
    if (-not (Test-Path -LiteralPath (Join-Path $testMedia 'ffprobe.exe'))) {
        Write-Warning 'ffprobe.exe não está disponível ao lado do FFmpeg. Testes que sondam mídia com ffprobe falharão; use -SkipMedia ou informe -FfmpegDirectory com a distribuição completa do FFmpeg.'
    }

    $env:ORGANESSONIUM_FFMPEG_TEST_EXE = [System.IO.Path]::GetFullPath($ffmpegPath)
    $env:ORGANESSONIUM_MEDIA_TEST_EXE = [System.IO.Path]::GetFullPath($appPath)
}

$testArguments = @()
if ($IncludeUi) { $testArguments += '--include-ui' }
if ($SkipMedia) { $testArguments += '--skip-media' }
foreach ($name in $Filter) {
    if (-not [string]::IsNullOrWhiteSpace($name)) { $testArguments += $name }
}

Write-Host ("Executando {0} ({1})..." -f $testExe.FullName, $Configuration) -ForegroundColor Cyan
& $testExe.FullName @testArguments
exit $LASTEXITCODE
