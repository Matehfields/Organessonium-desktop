[CmdletBinding()]
param(
    [string]$WorkDirectory = (Join-Path $PSScriptRoot '..\..\..\..\artifacts\media-build'),
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$work = [IO.Path]::GetFullPath($WorkDirectory)
$media = Split-Path -Parent $PSScriptRoot
$inventory = Get-Content -LiteralPath (Join-Path $work 'source-inventory.json') -Raw | ConvertFrom-Json
$review = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'source-review.json') -Raw | ConvertFrom-Json
$staged = Join-Path $work 'staged-media'
$null = New-Item -ItemType Directory -Force -Path $staged, (Join-Path $staged 'sources')
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Json([string]$Path, $Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false)) }
function Add-File($Zip, [string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "Missing source input: $Destination" }
    $entry = $Zip.CreateEntry($Destination.Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal)
    $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $inputStream = [IO.File]::OpenRead($Source)
    $outputStream = $entry.Open()
    try { $inputStream.CopyTo($outputStream) }
    finally { $outputStream.Dispose(); $inputStream.Dispose() }
}

# Review is bound to the signed archive, not a blanket flag accepting future
# versions with different build-time fetches.
foreach ($source in $inventory.sourceArchives) {
    if ((Hash (Join-Path $work $source.archive)) -ne $source.sha256) { throw 'Source archive changed after signature verification.' }
    if ($source.requiresReview) {
        $approval = @($review.packages | Where-Object { $_.archive -eq $source.archive -and $_.sha256 -eq $source.sha256 })
        if ($approval.Count -ne 1) { throw "Build-time source review missing: $($source.archive)" }
        foreach ($extra in @($approval[0].additionalSources)) {
            if (-not (Test-Path -LiteralPath (Join-Path $work $extra))) { throw "Additional source missing: $extra" }
        }
    }
}
$files = [Collections.Generic.List[object]]::new()
foreach ($file in $inventory.runtimeFiles) {
    if ((Hash $file.buildPath) -ne $file.sha256) { throw "Runtime changed after inspection: $($file.path)" }
    Copy-Item -LiteralPath $file.buildPath -Destination (Join-Path $staged $file.path) -Force
    $files.Add([ordered]@{ path = $file.path; sha256 = $file.sha256; sourcePackage = $file.sourcePackage })
}

$notices = [Text.StringBuilder]::new()
$null = $notices.AppendLine('Organessonium media runtime: third-party copyright and license notices.').AppendLine()
function Add-Notices([string]$Root, [string]$Label) {
    foreach ($license in Get-ChildItem -LiteralPath $Root -File -Recurse -Force |
        Where-Object { $_.Name -match '^(COPYING|COPYRIGHT|LICENSE|LICENCE|NOTICE|PATENTS)([-._].*)?$' -and $_.FullName -notmatch '\\.git\\' }) {
        $relative = $license.FullName.Substring($Root.Length + 1).Replace('\', '/')
        $null = $notices.AppendLine("--- $Label/$relative ---").AppendLine([IO.File]::ReadAllText($license.FullName)).AppendLine()
    }
}
foreach ($license in Get-ChildItem -LiteralPath (Join-Path $work 'sources\FFmpeg-b16b5f2a01f3c4f8c9a7769d7a35e8b193946d3e') -Filter 'COPYING.*' -File) {
    $null = $notices.AppendLine($license.Name).AppendLine([IO.File]::ReadAllText($license.FullName)).AppendLine()
}
Add-Notices (Join-Path $work 'source-audit\vendor\rav1e') 'rav1e-vendor'
Add-Notices (Join-Path $work 'source-audit\vendor\libdovi') 'libdovi-vendor'
if (Test-Path -LiteralPath (Join-Path $work 'msys64\ucrt64\share\licenses\rust')) {
    Add-Notices (Join-Path $work 'msys64\ucrt64\share\licenses\rust') 'rust-system-runtime'
}
foreach ($package in $inventory.packages) {
    $null = $notices.AppendLine("===== $($package.name) $($package.version) =====").AppendLine($package.license)
    $database = Get-ChildItem -LiteralPath (Join-Path $work 'msys64\var\lib\pacman\local') -Directory |
        Where-Object { $_.Name -eq "$($package.name)-$($package.version)" } | Select-Object -First 1
    if ($null -eq $database) { throw "Installed license inventory missing: $($package.name)" }
    foreach ($relative in Get-Content -LiteralPath (Join-Path $database.FullName 'files')) {
        if ($relative -notmatch '(^|/)share/licenses/' -or $relative.EndsWith('/')) { continue }
        $license = Join-Path (Join-Path $work 'msys64') $relative
        if (Test-Path -LiteralPath $license -PathType Leaf) {
            $null = $notices.AppendLine("--- $relative ---").AppendLine([IO.File]::ReadAllText($license)).AppendLine()
        }
    }
}
$native = @([regex]::Matches([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'build-runtime.sh')),
    '(?m)^checkout_source (\S+) (https://\S+) ([0-9a-f]{40})$') | ForEach-Object {
        [ordered]@{ name = $_.Groups[1].Value; url = $_.Groups[2].Value; commit = $_.Groups[3].Value }
    })
foreach ($component in $native) {
    $sourceRoot = Join-Path $work "sources\$($component.name)"
    $commit = & git -C $sourceRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $commit -ne $component.commit) { throw 'Native component source identity changed.' }
    $null = $notices.AppendLine("===== $($component.name) $($component.commit) =====")
    Add-Notices $sourceRoot $component.name
}
# Supplemental build headers have no installed DLL/license directory here.
foreach ($source in $inventory.sourceArchives | Where-Object { (Split-Path -Leaf $_.archive) -notin @($inventory.packages.archive) }) {
    $extra = Join-Path $work ('source-audit\extra-licenses\' + (Split-Path -Leaf $source.archive))
    $null = New-Item -ItemType Directory -Force -Path $extra
    & tar.exe -xf (Join-Path $work $source.archive) -C $extra
    if ($LASTEXITCODE -ne 0) { throw 'Cannot extract supplemental source notices.' }
    foreach ($nested in Get-ChildItem -LiteralPath $extra -File -Recurse |
        Where-Object { $_.Name -match '\.(tar\.(gz|xz|zst|bz2)|tgz|zip)$' }) {
        $entries = @(& tar.exe -tf $nested.FullName)
        if ($LASTEXITCODE -ne 0 -or @($entries | Where-Object { $_ -match '(^/|(^|/)\.\.(/|$)|^[a-zA-Z]:)' }).Count) { throw 'Unsafe supplemental source archive.' }
        & tar.exe -xf $nested.FullName -C $extra
        if ($LASTEXITCODE -ne 0) { throw 'Cannot extract supplemental source notices.' }
    }
    Add-Notices $extra (Split-Path -Leaf $source.archive)
}
[IO.File]::WriteAllText((Join-Path $staged 'THIRD-PARTY-NOTICES.txt'), $notices.ToString(), [Text.UTF8Encoding]::new($false))
foreach ($notice in @('GPL-3.0.txt', 'BtbN-LICENSE.txt', 'NOTICE.md', 'THIRD-PARTY-NOTICES.txt')) {
    if ($notice -ne 'THIRD-PARTY-NOTICES.txt') { Copy-Item -LiteralPath (Join-Path $media $notice) -Destination $staged -Force }
    $files.Add([ordered]@{ path = $notice; sha256 = Hash (Join-Path $staged $notice); sourcePackage = 'notices' })
}

$sourcePath = Join-Path $staged 'sources\ffmpeg-corresponding-source.zip'
foreach ($input in @(
    @('ffmpeg-source.zip', '4127c816b3f969d4b3422968ecf56c4bf4be77b8262b34c18b46b0df2c617ffc'),
    @('build-recipes.zip', '0a6f3f2f4e9b8d4d4eb7040f1d53d3aa43963866b641fd15e7f87831abdde5e8'))) {
    if ((Hash (Join-Path $media ('sources\' + $input[0]))) -ne $input[1]) { throw 'Core/recipe source archive identity changed.' }
}
$publicInventory = [ordered]@{ version = 1; runtimeFiles = $files.ToArray(); packages = $inventory.packages;
    sourceArchives = $inventory.sourceArchives; nativeSources = $native }
$publicPath = Join-Path $work 'public-source-inventory.json'
Json $publicPath $publicInventory
if (Test-Path -LiteralPath ($sourcePath + '.partial')) { Remove-Item -LiteralPath ($sourcePath + '.partial') -Force }
$zip = [IO.Compression.ZipFile]::Open($sourcePath + '.partial', [IO.Compression.ZipArchiveMode]::Create)
try {
    Add-File $zip $publicPath 'source-inventory.json'
    Add-File $zip (Join-Path $media 'sources\ffmpeg-source.zip') 'ffmpeg-source.zip'
    Add-File $zip (Join-Path $media 'sources\build-recipes.zip') 'supplier-recipes.zip'
    foreach ($source in $inventory.sourceArchives) {
        Add-File $zip (Join-Path $work $source.archive) $source.archive
        Add-File $zip (Join-Path $work ($source.archive + '.sig')) ($source.archive + '.sig')
        Add-File $zip (Join-Path $work $source.recipe) $source.recipe
    }
    Add-File $zip (Join-Path $media 'Verify-Sources.ps1') 'Verify-Sources.ps1'
    foreach ($component in $native) {
        $sourceRoot = Join-Path $work "sources\$($component.name)"
        # Keep tracked working files (including the recorded adaptations),
        # submodule sources and Git history used by upstream version scripts.
        $tracked = @(& git -C $sourceRoot ls-files --recurse-submodules)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate native component sources.' }
        foreach ($relative in $tracked) { Add-File $zip (Join-Path $sourceRoot $relative) "sources/$($component.name)/$relative" }
        foreach ($gitFile in Get-ChildItem -LiteralPath (Join-Path $sourceRoot '.git') -File -Recurse -Force |
            Where-Object { $_.FullName -notmatch '\\(logs|hooks)\\' }) {
            $relative = $gitFile.FullName.Substring($sourceRoot.Length + 1)
            Add-File $zip $gitFile.FullName "sources/$($component.name)/$relative"
        }
    }
    foreach ($vendor in @('rav1e', 'libdovi')) {
        $vendorRoot = Join-Path $work "source-audit\vendor\$vendor"
        foreach ($file in Get-ChildItem -LiteralPath $vendorRoot -File -Recurse -Force) {
            Add-File $zip $file.FullName ("vendor/$vendor/" + $file.FullName.Substring($vendorRoot.Length + 1))
        }
    }
    foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -File) { Add-File $zip $script.FullName "build/$($script.Name)" }
    foreach ($notice in @('GPL-3.0.txt', 'BtbN-LICENSE.txt', 'NOTICE.md', 'THIRD-PARTY-NOTICES.txt')) {
        Add-File $zip (Join-Path $staged $notice) $notice
    }
}
finally { $zip.Dispose() }
Move-Item -LiteralPath ($sourcePath + '.partial') -Destination $sourcePath -Force

$runtimePath = Join-Path $staged 'runtime-package.zip'
if (Test-Path -LiteralPath ($runtimePath + '.partial')) { Remove-Item -LiteralPath ($runtimePath + '.partial') -Force }
$zip = [IO.Compression.ZipFile]::Open($runtimePath + '.partial', [IO.Compression.ZipArchiveMode]::Create)
try { foreach ($file in $files) { Add-File $zip (Join-Path $staged $file.path) $file.path } }
finally { $zip.Dispose() }
Move-Item -LiteralPath ($runtimePath + '.partial') -Destination $runtimePath -Force
$manifest = [ordered]@{
    runtime = 'N-126168-gb16b5f2a01-organessonium-b16b5f2a01'; architecture = 'win-x64'; license = 'GPL-3.0-or-later';
    sha256 = Hash (Join-Path $staged 'ffmpeg.exe'); redistributionReady = $true;
    runtimeArchive = [ordered]@{ path = 'runtime-package.zip'; sha256 = Hash $runtimePath };
    correspondingSourceArchive = [ordered]@{ path = 'sources/ffmpeg-corresponding-source.zip'; sha256 = Hash $sourcePath };
    runtimeFiles = $files.ToArray(); sourceInventory = 'source-inventory.json'
}
Json (Join-Path $staged 'source-manifest.json') $manifest
& (Join-Path $media 'Verify-Sources.ps1') -ForRedistribution -MediaDirectory $staged
if ($Install) {
    # Preserve the previous external executable until the candidate has passed
    # the focused media/deployment regressions and the operator installs it.
    Copy-Item -LiteralPath (Join-Path $staged 'runtime-package.zip') -Destination $media -Force
    Copy-Item -LiteralPath (Join-Path $staged 'source-manifest.json') -Destination $media -Force
    Copy-Item -LiteralPath (Join-Path $staged 'THIRD-PARTY-NOTICES.txt') -Destination $media -Force
    Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $media 'sources\ffmpeg-corresponding-source.zip') -Force
    Copy-Item -LiteralPath (Join-Path $staged 'ffmpeg.exe') -Destination (Join-Path $media 'ffmpeg.exe') -Force
}
Write-Output "Verified media package: $staged"
