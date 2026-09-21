[CmdletBinding()]
param(
    [string]$Version,
    [switch]$KeepStaging
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$projectPath = Join-Path $repoRoot 'Organessonium\Organessonium\Organessonium.csproj'
$solutionPath = Join-Path $repoRoot 'Organessonium\Organessonium.slnx'
$iconPath = Join-Path $repoRoot 'Organessonium\Organessonium\Assets\app.ico'
$settingsPath = Join-Path $PSScriptRoot 'release-settings.json'
$localSettingsPath = Join-Path $PSScriptRoot 'release-settings.local.json'

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Resolve-WindowsSdkTool {
    param([Parameter(Mandatory = $true)][string]$Name)

    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $tool = Get-ChildItem -LiteralPath $sdkRoot -Filter $Name -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object { try { [version]$_.Directory.Parent.Name } catch { [version]'0.0' } } -Descending |
        Select-Object -First 1
    if ($null -eq $tool) {
        throw "$Name não foi encontrado. Instale o Windows 10/11 SDK pelo Visual Studio Installer."
    }
    return $tool.FullName
}

function Convert-IconToPng {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][int]$Width,
        [Parameter(Mandatory = $true)][int]$Height,
        [Parameter(Mandatory = $true)][int]$LogoSize
    )

    Add-Type -AssemblyName System.Drawing
    $icon = [System.Drawing.Icon]::new($Source)
    $sourceBitmap = $icon.ToBitmap()
    $canvas = [System.Drawing.Bitmap]::new($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $x = [int](($Width - $LogoSize) / 2)
        $y = [int](($Height - $LogoSize) / 2)
        $graphics.DrawImage($sourceBitmap, $x, $y, $LogoSize, $LogoSize)
        $canvas.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $canvas.Dispose()
        $sourceBitmap.Dispose()
        $icon.Dispose()
    }
}

function Escape-Xml {
    param([string]$Value)
    return [System.Security.SecurityElement]::Escape($Value)
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $hash = $algorithm.ComputeHash($stream)
        return ([System.BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $stream.Dispose()
        $algorithm.Dispose()
    }
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Falha ao executar $FilePath. Código: $LASTEXITCODE"
    }
}

if (-not (Test-Path -LiteralPath $projectPath) -or -not (Test-Path -LiteralPath $solutionPath)) {
    throw 'Execute o script dentro do checkout completo do Organessonium.'
}

$settings = @{}
foreach ($property in (Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).PSObject.Properties) {
    $settings[$property.Name] = [string]$property.Value
}
if (Test-Path -LiteralPath $localSettingsPath) {
    foreach ($property in (Get-Content -LiteralPath $localSettingsPath -Raw | ConvertFrom-Json).PSObject.Properties) {
        $settings[$property.Name] = [string]$property.Value
    }
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $settings.version
}
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw 'Informe a versão no formato A.B.C.D, por exemplo: build-releases.bat 1.2.0.0'
}
$versionNumbers = $Version.Split('.') | ForEach-Object { [int]$_ }
if ($versionNumbers.Where({ $_ -lt 0 -or $_ -gt 65535 }).Count -gt 0) {
    throw 'Cada parte da versão deve estar entre 0 e 65535.'
}
if ($versionNumbers[0] -gt 255 -or $versionNumbers[1] -gt 255 -or $versionNumbers[3] -ne 0) {
    throw 'Para manter upgrades MSI válidos, use A e B entre 0 e 255 e a versão no formato A.B.C.0.'
}

$artifactsRoot = Join-Path $repoRoot 'artifacts\releases'
$outputDirectory = Join-Path $artifactsRoot $Version
$workRoot = Join-Path $repoRoot ('artifacts\release-work\' + $Version)
$resolvedArtifactsRoot = [System.IO.Path]::GetFullPath($artifactsRoot) + [System.IO.Path]::DirectorySeparatorChar
foreach ($path in @($outputDirectory, $workRoot)) {
    $fullPath = [System.IO.Path]::GetFullPath($path)
    if (-not $fullPath.StartsWith($resolvedArtifactsRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $fullPath.StartsWith(([System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\release-work')) + [System.IO.Path]::DirectorySeparatorChar), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Caminho de saída inválido: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}
$null = New-Item -ItemType Directory -Force -Path $outputDirectory
$null = New-Item -ItemType Directory -Force -Path $workRoot

$publishDirectory = Join-Path $workRoot 'publish'
$sourceDirectory = Join-Path $workRoot 'source'
$msiWorkDirectory = Join-Path $workRoot 'msi'
$msixStageDirectory = Join-Path $workRoot 'msix-stage'
$bundleInputDirectory = Join-Path $workRoot 'bundle-input'
$verifyDirectory = Join-Path $workRoot 'verify'
$null = New-Item -ItemType Directory -Force -Path $publishDirectory

Write-Step "Compilando o aplicativo $Version para staging"
Invoke-Native -FilePath 'dotnet.exe' -Arguments @(
    'publish', $projectPath,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-o', $publishDirectory,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    "-p:Version=$($versionNumbers[0]).$($versionNumbers[1]).$($versionNumbers[2])",
    "-p:FileVersion=$Version",
    "-p:AssemblyVersion=$Version"
)
$publishedExe = Join-Path $publishDirectory 'Organessonium.exe'
if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw 'A publicação não produziu Organessonium.exe.'
}

Write-Step 'Gerando o código-fonte mínimo'
$null = New-Item -ItemType Directory -Force -Path $sourceDirectory
$trackedFiles = & git -C $repoRoot ls-files -- 'Organessonium/Organessonium.slnx' 'Organessonium/Organessonium/**' 'README.md' 'Information.md'
if ($LASTEXITCODE -ne 0 -or $trackedFiles.Count -eq 0) {
    throw 'Git não conseguiu enumerar os arquivos necessários do código-fonte.'
}
foreach ($relativePath in $trackedFiles) {
    $sourcePath = Join-Path $repoRoot ($relativePath.Replace('/', '\'))
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        continue
    }
    $zipRelativePath = if ($relativePath.StartsWith('Organessonium/')) {
        $relativePath.Substring('Organessonium/'.Length)
    } else {
        $relativePath
    }
    $destination = Join-Path $sourceDirectory ($zipRelativePath.Replace('/', '\'))
    $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination)
    Copy-Item -LiteralPath $sourcePath -Destination $destination -Force
}
$sourceZip = Join-Path $outputDirectory 'Organessonium-windows-source.zip'
Compress-Archive -Path (Join-Path $sourceDirectory '*') -DestinationPath $sourceZip -CompressionLevel Optimal

Write-Step 'Gerando Organessonium-installer.msi com Windows Installer e MakeCab'
$msiPath = Join-Path $outputDirectory 'Organessonium-installer.msi'
& (Join-Path $PSScriptRoot 'Create-Msi.ps1') `
    -ApplicationExe $publishedExe `
    -ApplicationIcon $iconPath `
    -OutputPath $msiPath `
    -Version $Version `
    -WorkingDirectory $msiWorkDirectory
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $msiPath)) {
    throw 'A geração do MSI falhou.'
}

Write-Step 'Validando a extração administrativa do MSI'
$msiVerifyDirectory = Join-Path $verifyDirectory 'msi'
$msiLog = Join-Path $verifyDirectory 'msi-validation.log'
$null = New-Item -ItemType Directory -Force -Path $msiVerifyDirectory
$msiProcess = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -Wait -PassThru -ArgumentList @(
    '/a', ('"' + $msiPath + '"'), ('TARGETDIR="' + $msiVerifyDirectory + '"'), '/qn', '/norestart', '/l*v', ('"' + $msiLog + '"')
)
$extractedMsiExe = Get-ChildItem -LiteralPath $msiVerifyDirectory -Filter 'Organessonium.exe' -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
if ($msiProcess.ExitCode -ne 0 -or $null -eq $extractedMsiExe) {
    throw "O MSI não passou pela extração administrativa. Código: $($msiProcess.ExitCode). Log: $msiLog"
}
if ((Get-Sha256Hex -Path $extractedMsiExe.FullName) -ne (Get-Sha256Hex -Path $publishedExe)) {
    throw 'O executável extraído do MSI não corresponde ao executável publicado.'
}

Write-Step 'Gerando o pacote MSIX x64 e o MSIXBundle da Microsoft Store'
$makeAppx = Resolve-WindowsSdkTool -Name 'makeappx.exe'
$null = New-Item -ItemType Directory -Force -Path (Join-Path $msixStageDirectory 'Assets')
$null = New-Item -ItemType Directory -Force -Path $bundleInputDirectory
Copy-Item -LiteralPath $publishedExe -Destination (Join-Path $msixStageDirectory 'Organessonium.exe') -Force
Convert-IconToPng -Source $iconPath -Destination (Join-Path $msixStageDirectory 'Assets\StoreLogo.png') -Width 50 -Height 50 -LogoSize 44
Convert-IconToPng -Source $iconPath -Destination (Join-Path $msixStageDirectory 'Assets\Square44x44Logo.png') -Width 44 -Height 44 -LogoSize 38
Convert-IconToPng -Source $iconPath -Destination (Join-Path $msixStageDirectory 'Assets\Square150x150Logo.png') -Width 150 -Height 150 -LogoSize 128

$identityName = Escape-Xml $settings.storeIdentityName
$publisher = Escape-Xml $settings.storePublisher
$publisherDisplayName = Escape-Xml $settings.storePublisherDisplayName
$description = Escape-Xml $settings.storeDescription
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap rescap">
  <Identity Name="$identityName" Publisher="$publisher" Version="$Version" ProcessorArchitecture="x64" />
  <Properties>
    <DisplayName>Organessonium</DisplayName>
    <PublisherDisplayName>$publisherDisplayName</PublisherDisplayName>
    <Description>$description</Description>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Resources>
    <Resource Language="pt-BR" />
  </Resources>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Applications>
    <Application Id="Organessonium" Executable="Organessonium.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements
        DisplayName="Organessonium"
        Description="$description"
        BackgroundColor="transparent"
        Square150x150Logo="Assets\Square150x150Logo.png"
        Square44x44Logo="Assets\Square44x44Logo.png" />
    </Application>
  </Applications>
  <Capabilities>
    <Capability Name="internetClient" />
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
"@
[System.IO.File]::WriteAllText(
    (Join-Path $msixStageDirectory 'AppxManifest.xml'),
    $manifest,
    [System.Text.UTF8Encoding]::new($false))

$innerMsix = Join-Path $bundleInputDirectory ("Organessonium_{0}_x64.msix" -f $Version)
Invoke-Native -FilePath $makeAppx -Arguments @('pack', '/d', $msixStageDirectory, '/p', $innerMsix, '/o')
$bundlePath = Join-Path $outputDirectory ("Organessonium_{0}_x64.msixbundle" -f $Version)
Invoke-Native -FilePath $makeAppx -Arguments @('bundle', '/d', $bundleInputDirectory, '/p', $bundlePath, '/o')

$bundleVerifyDirectory = Join-Path $verifyDirectory 'bundle'
$null = New-Item -ItemType Directory -Force -Path $bundleVerifyDirectory
Invoke-Native -FilePath $makeAppx -Arguments @('unbundle', '/p', $bundlePath, '/d', $bundleVerifyDirectory, '/o')
$verifiedMsix = Get-ChildItem -LiteralPath $bundleVerifyDirectory -Filter '*.msix' -File -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $verifiedMsix) {
    throw 'O MSIXBundle foi criado, mas não pôde ser validado pelo MakeAppx.'
}
$packageVerifyDirectory = Join-Path $verifyDirectory 'package'
$null = New-Item -ItemType Directory -Force -Path $packageVerifyDirectory
Invoke-Native -FilePath $makeAppx -Arguments @('unpack', '/p', $verifiedMsix.FullName, '/d', $packageVerifyDirectory, '/o')
if (-not (Test-Path -LiteralPath (Join-Path $packageVerifyDirectory 'AppxManifest.xml')) -or
    -not (Test-Path -LiteralPath (Join-Path $packageVerifyDirectory 'Organessonium.exe'))) {
    throw 'O pacote interno do MSIXBundle não contém o manifesto e o executável esperados.'
}

$pfxPath = $env:ORGANESSONIUM_PFX_PATH
if (-not [string]::IsNullOrWhiteSpace($pfxPath)) {
    Write-Step 'Assinando MSI e MSIXBundle com SignTool'
    $signTool = Resolve-WindowsSdkTool -Name 'signtool.exe'
    $signArguments = @('sign', '/fd', 'SHA256', '/f', $pfxPath)
    if (-not [string]::IsNullOrWhiteSpace($env:ORGANESSONIUM_PFX_PASSWORD)) {
        $signArguments += @('/p', $env:ORGANESSONIUM_PFX_PASSWORD)
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ORGANESSONIUM_TIMESTAMP_URL)) {
        $signArguments += @('/tr', $env:ORGANESSONIUM_TIMESTAMP_URL, '/td', 'SHA256')
    }
    Invoke-Native -FilePath $signTool -Arguments ($signArguments + @($msiPath))
    Invoke-Native -FilePath $signTool -Arguments ($signArguments + @($bundlePath))
}

Write-Step 'Gerando hashes SHA-256'
$releaseFiles = @($msiPath, $sourceZip, $bundlePath)
$hashLines = foreach ($file in $releaseFiles) {
    '{0}  {1}' -f (Get-Sha256Hex -Path $file), (Split-Path -Leaf $file)
}
[System.IO.File]::WriteAllLines(
    (Join-Path $outputDirectory 'SHA256SUMS.txt'),
    $hashLines,
    [System.Text.UTF8Encoding]::new($false))

if (-not $KeepStaging) {
    $resolvedWorkRoot = [System.IO.Path]::GetFullPath($workRoot)
    $expectedWorkRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\release-work')) + [System.IO.Path]::DirectorySeparatorChar
    if ($resolvedWorkRoot.StartsWith($expectedWorkRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force
    }
}

Write-Host "`nArtefatos gerados em: $outputDirectory" -ForegroundColor Green
Get-ChildItem -LiteralPath $outputDirectory -File | Sort-Object Name | Select-Object Name, Length | Format-Table -AutoSize
if ($settings.storeIdentityName -eq 'Organessonium.Desktop' -or $settings.storePublisher -eq 'CN=Organessonium') {
    Write-Warning 'O MSIXBundle usa a identidade local padrão. Antes de enviar à Store, copie release-settings.local.example.json para release-settings.local.json e use os valores exatos do Partner Center.'
}
