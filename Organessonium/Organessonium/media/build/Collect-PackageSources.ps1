[CmdletBinding()]
param([string]$WorkDirectory = (Join-Path $PSScriptRoot '..\..\..\..\artifacts\media-build'))

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$work = [IO.Path]::GetFullPath($WorkDirectory)
$environmentRoot = Join-Path $work 'msys64'
$ucrtRoot = Join-Path $environmentRoot 'ucrt64'
$customRoot = Join-Path $work 'prefix'
$pacman = Join-Path $environmentRoot 'usr\bin\pacman.exe'
$objdump = Join-Path $ucrtRoot 'bin\objdump.exe'
$runtime = Join-Path $work 'compiled\ffmpeg\ffmpeg.exe'
$map = "$runtime.map"
if (-not (Test-Path -LiteralPath $map)) { $map = Join-Path $work 'compiled\ffmpeg\ffmpeg_g.exe.map' }
if (-not (Test-Path -LiteralPath $runtime) -or -not (Test-Path -LiteralPath $map)) {
    throw 'Compile the controlled FFmpeg and its link map before collecting sources.'
}
$runtimeFiles = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
$packageNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$queue = [Collections.Generic.Queue[string]]::new()
$queue.Enqueue($runtime)
$owners = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($directory in Get-ChildItem -LiteralPath (Join-Path $environmentRoot 'var\lib\pacman\local') -Directory) {
    $description = Get-Content -LiteralPath (Join-Path $directory.FullName 'desc') -Raw
    $packageName = [regex]::Match($description, '(?m)^%NAME%\r?\n([^\r\n]+)').Groups[1].Value
    foreach ($entry in [IO.File]::ReadLines((Join-Path $directory.FullName 'files'))) {
        if ($entry.StartsWith('ucrt64/') -and -not $entry.EndsWith('/')) { $owners[$entry] = $packageName }
    }
}

function Get-SourceHash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function ConvertTo-MsysPath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path).Replace('\', '/')
    return '/' + $absolute.Substring(0, 1).ToLowerInvariant() + $absolute.Substring(2)
}
function Add-Owner([string]$Path) {
    $packagePath = 'ucrt64/' + [IO.Path]::GetFullPath($Path).Substring($ucrtRoot.Length + 1).Replace('\', '/')
    if (-not $owners.ContainsKey($packagePath)) { throw "No package provenance for $([IO.Path]::GetFileName($Path))." }
    $owner = $owners[$packagePath]
    $null = $packageNames.Add([string]$owner)
    return [string]$owner
}

while ($queue.Count -gt 0) {
    $file = $queue.Dequeue()
    $name = [IO.Path]::GetFileName($file)
    if ($runtimeFiles.ContainsKey($name)) { continue }
    if ($name -ieq 'msys-2.0.dll') { throw 'The media runtime must not depend on MSYS2.' }
    $owner = if ($file.StartsWith($ucrtRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { Add-Owner $file } else { 'controlled-source' }
    $runtimeFiles[$name] = [ordered]@{ path = $name; sha256 = Get-SourceHash $file; sourcePackage = $owner; buildPath = $file }
    $imports = & $objdump -p $file | Select-String 'DLL Name:\s*(\S+)'
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect imports for $name." }
    foreach ($import in $imports) {
        $dll = $import.Matches[0].Groups[1].Value
        if ($dll -notmatch '^[a-zA-Z0-9_.+-]+\.dll$') { throw 'Invalid DLL import path.' }
        $custom = Join-Path $customRoot "bin\$dll"
        $package = Join-Path $ucrtRoot "bin\$dll"
        if (Test-Path -LiteralPath $custom -PathType Leaf) { $queue.Enqueue($custom) }
        elseif (Test-Path -LiteralPath $package -PathType Leaf) { $queue.Enqueue($package) }
        elseif ($dll -notmatch '^(api-ms-win-|ext-ms-win-)' -and
            -not (Test-Path -LiteralPath (Join-Path ([Environment]::SystemDirectory) $dll) -PathType Leaf)) {
            throw "A runtime DLL is missing: $dll."
        }
    }
}

# Link maps also identify statically linked dependencies that PE imports cannot see.
foreach ($line in Get-Content -LiteralPath $map) {
    if ($line -notmatch '^LOAD\s+(.+\.a)\s*$') { continue }
    $library = $Matches[1].Trim().Replace('/', '\')
    if ($library.StartsWith('\ucrt64\', [StringComparison]::OrdinalIgnoreCase)) {
        $library = Join-Path $ucrtRoot $library.Substring(8)
    }
    $library = [IO.Path]::GetFullPath($library)
    if ($library.StartsWith($ucrtRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { $null = Add-Owner $library }
}
foreach ($headers in @('amf-headers', 'ffnvcodec-headers', 'vulkan-headers', 'opencl-headers', 'headers', 'crt', 'lv2')) {
    $null = $packageNames.Add("mingw-w64-ucrt-x86_64-$headers")
}

function Get-Field([string]$Text, [string]$Name) {
    $match = [regex]::Match($Text, '(?ms)^%' + [regex]::Escape($Name) + '%\r?\n(.*?)(?:\r?\n\r?\n|\z)')
    if (-not $match.Success) { return '' }
    return $match.Groups[1].Value.Trim()
}
$installed = @{}
foreach ($directory in Get-ChildItem -LiteralPath (Join-Path $environmentRoot 'var\lib\pacman\local') -Directory) {
    $desc = Join-Path $directory.FullName 'desc'
    if (Test-Path -LiteralPath $desc) {
        $text = Get-Content -LiteralPath $desc -Raw
        $installed[(Get-Field $text 'NAME')] = $text
    }
}
# A DLL can contain static libraries internally. Follow its package dependencies
# as well as PE imports, instead of overlooking those corresponding sources.
$providers = @{}
foreach ($name in $installed.Keys) {
    foreach ($provided in ((Get-Field $installed[$name] 'PROVIDES') -split '\r?\n')) {
        $provided = $provided -replace '[<>=].*$', ''
        if ($provided -and -not $providers.ContainsKey($provided)) { $providers[$provided] = $name }
    }
}
$dependencies = [Collections.Generic.Queue[string]]::new()
foreach ($name in $packageNames) { $dependencies.Enqueue($name) }
while ($dependencies.Count -gt 0) {
    $name = $dependencies.Dequeue()
    foreach ($dependency in ((Get-Field $installed[$name] 'DEPENDS') -split '\r?\n')) {
        $dependency = $dependency -replace '[<>=].*$', ''
        if (-not $dependency.StartsWith('mingw-w64-ucrt-x86_64-')) { continue }
        if (-not $installed.ContainsKey($dependency) -and $providers.ContainsKey($dependency)) { $dependency = $providers[$dependency] }
        if (-not $installed.ContainsKey($dependency)) { throw "Unresolved native package dependency: $dependency" }
        if ($packageNames.Add($dependency)) { $dependencies.Enqueue($dependency) }
    }
}
$cache = Join-Path $work 'package-sources'
$audit = Join-Path $work 'source-audit\packages'
$null = New-Item -ItemType Directory -Force -Path $cache, $audit
$packages = [Collections.Generic.List[object]]::new()
$archives = @{}
function Get-VerifiedSource([string]$Base, [string]$Version) {
    $sourceName = "$Base-$($Version -replace '^\d+:','').src.tar.zst"
    if ($archives.ContainsKey($sourceName)) { return $sourceName }
    $source = Join-Path $cache $sourceName
    $url = "https://repo.msys2.org/mingw/sources/$sourceName"
    Write-Output "Collecting corresponding source: $sourceName" | Out-Host
    if (-not (Test-Path -LiteralPath $source)) {
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile "$source.download" -TimeoutSec 120
        Move-Item -LiteralPath "$source.download" -Destination $source
    }
    if (-not (Test-Path -LiteralPath "$source.sig")) { Invoke-WebRequest -UseBasicParsing -Uri "$url.sig" -OutFile "$source.sig" -TimeoutSec 30 }
    $signature = & (Join-Path $environmentRoot 'usr\bin\gpg.exe') --homedir /etc/pacman.d/gnupg --verify (ConvertTo-MsysPath "$source.sig") (ConvertTo-MsysPath $source) 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Source signature verification failed: $sourceName. $signature" }
    $contents = @(& tar.exe -tf $source)
    if ($LASTEXITCODE -ne 0 -or @($contents | Where-Object { $_ -match '(^/|(^|/)\.\.(/|$)|^[a-zA-Z]:)' }).Count -gt 0 -or
        "$Base/PKGBUILD" -notin $contents -or $contents.Count -lt 4) { throw "Incomplete or invalid source archive: $sourceName." }
    $recipe = (& tar.exe -xOf $source "$Base/PKGBUILD") -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect source recipe: $sourceName." }
    $recipePath = "source-audit/packages/$sourceName.PKGBUILD"
    [IO.File]::WriteAllText((Join-Path $work $recipePath), $recipe)
    $archives[$sourceName] = [ordered]@{ archive = "package-sources/$sourceName"; url = $url; sha256 = Get-SourceHash $source;
        recipe = $recipePath; requiresReview = [bool]($recipe -match 'cargo|git submodule|git clone|FetchContent|curl |wget |npm |pip ') }
    return $sourceName
}
foreach ($name in ($packageNames | Sort-Object)) {
    if (-not $installed.ContainsKey($name)) { throw "Installed package metadata missing: $name." }
    $desc = $installed[$name]
    $version = Get-Field $desc 'VERSION'
    $base = Get-Field $desc 'BASE'
    $sourceName = Get-VerifiedSource $base $version
    $packages.Add([ordered]@{ name = $name; version = $version; source = $base; license = Get-Field $desc 'LICENSE'; archive = $sourceName })
}
# Header-only/static build inputs are not PE imports. Read their exact revisions
# from the actual binary package's .BUILDINFO, not today's repository versions.
$buildHeaders = @{
    'libplacebo' = @('fast_float', 'xxhash', 'python-glad')
    'dav1d' = @('xxhash')
    'shaderc' = @('spirv-headers')
    'spirv-cross' = @('spirv-headers')
    'spirv-tools' = @('spirv-headers')
}
foreach ($component in $buildHeaders.Keys) {
    $name = "mingw-w64-ucrt-x86_64-$component"
    if (-not $packageNames.Contains($name)) { continue }
    $version = Get-Field $installed[$name] 'VERSION'
    $binary = Join-Path $environmentRoot ("var/cache/pacman/pkg/$name-$version-any.pkg.tar.zst")
    if (-not (Test-Path -LiteralPath $binary)) { throw "Binary build metadata missing: $name" }
    $buildInfo = (& tar.exe -xOf $binary '.BUILDINFO') -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect build metadata: $name" }
    foreach ($header in $buildHeaders[$component]) {
        $match = [regex]::Match($buildInfo, '(?m)^installed = mingw-w64-ucrt-x86_64-' + [regex]::Escape($header) + '-(.+)-any$')
        if (-not $match.Success) { throw "Build input identity missing: $component/$header" }
        $null = Get-VerifiedSource "mingw-w64-$header" $match.Groups[1].Value
    }
}
$inventory = [ordered]@{ version = 1; runtimeFiles = @($runtimeFiles.Values | Sort-Object path);
    packages = $packages.ToArray(); sourceArchives = @($archives.Values | Sort-Object archive) }
[IO.File]::WriteAllText((Join-Path $work 'source-inventory.json'), ($inventory | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
Write-Output "Collected $($packages.Count) package identities and $($archives.Count) signed source archives."
