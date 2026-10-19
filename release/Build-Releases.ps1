[CmdletBinding()]
param(
    [string]$Version,
    [switch]$KeepStaging,
    [switch]$RunWack
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$projectPath = Join-Path $repoRoot 'Organessonium\Organessonium\Organessonium.csproj'
$solutionPath = Join-Path $repoRoot 'Organessonium\Organessonium.slnx'
$iconPath = Join-Path $repoRoot 'Organessonium\Organessonium\Assets\app.ico'
$settingsPath = Join-Path $PSScriptRoot 'release-settings.json'
$localSettingsPath = Join-Path $PSScriptRoot 'release-settings.local.json'
$mediaRoot = Join-Path (Split-Path -Parent $projectPath) 'media'
# Check before creating/replacing any release artifact.
& (Join-Path $mediaRoot 'Verify-Sources.ps1') -ForRedistribution

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

function Get-PeMachine {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw "Arquivo sem cabeçalho MZ válido: $Path"
        }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "Arquivo sem cabeçalho PE válido: $Path"
        }
        return $reader.ReadUInt16()
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
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

function Read-JsonObjectUtf8 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $utf8 = [System.Text.UTF8Encoding]::new($false, $true)
    $json = [System.IO.File]::ReadAllText($Path, $utf8)
    return $json | ConvertFrom-Json
}

function Release-ComObject {
    param([object]$Value)
    if ($null -ne $Value -and [System.Runtime.InteropServices.Marshal]::IsComObject($Value)) {
        [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($Value)
    }
}

function Get-RelativePathCompat {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $baseFullPath = [System.IO.Path]::GetFullPath($BasePath).TrimEnd('\', '/')
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ($fullPath.Equals($baseFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        return '.'
    }
    $prefix = $baseFullPath + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "O caminho $fullPath não pertence ao diretório $baseFullPath."
    }
    return $fullPath.Substring($prefix.Length)
}

function Get-MsiScalar {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Query
    )

    $installer = $null
    $database = $null
    $view = $null
    $record = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($Path, 0)
        $view = $database.OpenView($Query)
        [void]$view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) {
            return $null
        }
        return $record.StringData(1).Trim()
    }
    finally {
        if ($null -ne $view) {
            try { [void]$view.Close() } catch {}
        }
        Release-ComObject $record
        Release-ComObject $view
        Release-ComObject $database
        Release-ComObject $installer
    }
}

function Test-MsiArtifact {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        [Parameter(Mandatory = $true)][string]$ExpectedUpgradeCode,
        [Parameter(Mandatory = $true)][ValidateSet('x86', 'x64', 'arm64')][string]$Architecture
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -le 0) {
        throw 'O MSI não existe ou está vazio.'
    }

    $expectedProductName = 'Organess' + [char]0x00F4 + 'nium'
    $expectedLaunchDescription = 'O ' + $expectedProductName + ' requer Windows 10 ou posterior.'
    $productVersion = Get-MsiScalar -Path $Path -Query "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductVersion'"
    $productName = Get-MsiScalar -Path $Path -Query "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductName'"
    $allUsers = Get-MsiScalar -Path $Path -Query "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ALLUSERS'"
    $installFolderParent = Get-MsiScalar -Path $Path -Query "SELECT ``Directory_Parent`` FROM ``Directory`` WHERE ``Directory``='INSTALLFOLDER'"
    $startMenuShortcut = Get-MsiScalar -Path $Path -Query "SELECT ``Directory_`` FROM ``Shortcut`` WHERE ``Shortcut``='StartMenuShortcut'"
    $desktopShortcut = Get-MsiScalar -Path $Path -Query "SELECT ``Directory_`` FROM ``Shortcut`` WHERE ``Shortcut``='DesktopShortcut'"
    $upgradeCode = Get-MsiScalar -Path $Path -Query "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='UpgradeCode'"
    $componentAttributes = Get-MsiScalar -Path $Path -Query "SELECT ``Attributes`` FROM ``Component`` WHERE ``Component``='ApplicationFiles'"
    $fileName = Get-MsiScalar -Path $Path -Query "SELECT ``FileName`` FROM ``File`` WHERE ``File``='AppExe'"
    $cabinetStream = Get-MsiScalar -Path $Path -Query "SELECT ``Name`` FROM ``_Streams`` WHERE ``Name``='app.cab'"
    $launchCondition = Get-MsiScalar -Path $Path -Query "SELECT ``Condition`` FROM ``LaunchCondition`` WHERE ``Description``='$expectedLaunchDescription'"
    $expectedMsiVersion = ($ExpectedVersion.Split('.')[0..2] -join '.')
    $expectedTemplate = @{ x86 = 'Intel;1046'; x64 = 'x64;1046'; arm64 = 'Arm64;1046' }[$Architecture]
    $expectedSchema = @{ x86 = 200; x64 = 200; arm64 = 500 }[$Architecture]
    $expected64BitComponent = $Architecture -ne 'x86'
    $installer = $null
    $database = $null
    $summary = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($Path, 0)
        $summary = $database.SummaryInformation(0)
        $summaryTemplate = $summary.Property(7)
        $summarySchema = [int]$summary.Property(14)
        $summaryWords = [int]$summary.Property(15)
    }
    finally {
        Release-ComObject $summary
        Release-ComObject $database
        Release-ComObject $installer
    }

    if ($productVersion -ne $expectedMsiVersion) {
        throw "Versão incorreta no MSI. Esperado: $expectedMsiVersion. Encontrado: $productVersion"
    }
    if ($productName -ne $expectedProductName) {
        throw "Nome incorreto ou corrompido no MSI $Architecture`: $productName"
    }
    if ($null -ne $allUsers -or $installFolderParent -ne 'AppDataFolder' -or ($summaryWords -band 8) -eq 0) {
        throw "O MSI $Architecture não está configurado como instalação por usuário no AppData."
    }
    if ($startMenuShortcut -ne 'ProgramMenuFolder' -or $desktopShortcut -ne 'DesktopFolder') {
        throw "O MSI $Architecture não contém os atalhos esperados no Menu Iniciar e na Área de Trabalho."
    }
    if ($upgradeCode -ne $ExpectedUpgradeCode) {
        throw "UpgradeCode incorreto no MSI. Esperado: $ExpectedUpgradeCode. Encontrado: $upgradeCode"
    }
    $is64BitComponent = (([int]$componentAttributes -band 256) -ne 0)
    if ($is64BitComponent -ne $expected64BitComponent) {
        throw "O componente principal do MSI $Architecture tem atributos de arquitetura incorretos."
    }
    if ($summaryTemplate -ne $expectedTemplate -or $summarySchema -ne $expectedSchema) {
        throw "O resumo do MSI $Architecture está incorreto: template $summaryTemplate, schema $summarySchema."
    }
    if ($fileName -notmatch 'Organessonium\.exe$') {
        throw 'O MSI não referencia o executável Organessonium.exe.'
    }
    if ($cabinetStream -ne 'app.cab') {
        throw "O MSI $Architecture não contém o cabinet interno esperado."
    }
    if ($launchCondition -ne 'VersionNT >= 603') {
        throw "O MSI $Architecture não contém a condição correta para Windows 10 ou posterior."
    }
}

function Test-PublicSourceFiles {
    param(
        [Parameter(Mandatory = $true)][string[]]$RelativePaths,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $blocked = [System.Collections.Generic.List[string]]::new()
    $textExtensions = @('.cs', '.xaml', '.js', '.mjs', '.html', '.css', '.json', '.md', '.xml', '.txt', '.csproj', '.slnx', '.props', '.targets')
    foreach ($relativePath in $RelativePaths) {
        $portablePath = $relativePath.Replace('\', '/')
        $leaf = [System.IO.Path]::GetFileName($portablePath)
        $extension = [System.IO.Path]::GetExtension($leaf).ToLowerInvariant()
        if ($leaf -ieq '.env' -or $leaf -ieq 'release-settings.local.json' -or
            $extension -in @('.pfx', '.p12', '.pem', '.key', '.db', '.log')) {
            $blocked.Add("arquivo proibido: $portablePath")
            continue
        }

        if ($extension -notin $textExtensions) {
            continue
        }

        $path = Join-Path $Root ($relativePath.Replace('/', '\'))
        $content = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
        if ($content -match 'https://(?:discord(?:app)?\.com)/api/webhooks/[0-9]{17,20}/[A-Za-z0-9._-]{20,}') {
            $blocked.Add("webhook do Discord: $portablePath")
        }
        if ($content -match '-----BEGIN [A-Z ]*PRIVATE KEY-----') {
            $blocked.Add("chave privada: $portablePath")
        }
        if ($content -match '(?i)[A-Z]:\\Users\\[^\s\\/"'']+') {
            $blocked.Add("caminho de usuário local: $portablePath")
        }
        if ($content -match '(?m)\b[MN][A-Za-z0-9_-]{22,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{20,}\b') {
            $blocked.Add("possível token do Discord: $portablePath")
        }
    }

    if ($blocked.Count -gt 0) {
        throw "O ZIP público foi bloqueado pela verificação de dados locais/segredos:`n - $($blocked -join "`n - ")"
    }
}

function New-PortableSourceZip {
    param(
        [Parameter(Mandatory = $true)][string[]]$RelativePaths,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stream = [System.IO.File]::Open($Destination, [System.IO.FileMode]::Create)
    $archive = $null
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $false)
        foreach ($relativePath in $RelativePaths) {
            $sourcePath = Join-Path $Root ($relativePath.Replace('/', '\'))
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
                throw "Arquivo rastreado não encontrado ao gerar o ZIP: $relativePath"
            }
            $entryPath = if ($relativePath.StartsWith('Organessonium/')) {
                $relativePath.Substring('Organessonium/'.Length)
            } else {
                $relativePath
            }
            $entryPath = $entryPath.Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $sourcePath,
                $entryPath,
                [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        if ($null -ne $archive) { $archive.Dispose() }
        $stream.Dispose()
    }
}

function New-FfmpegSourcePackage {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Directory
    )

    $manifest = Read-JsonObjectUtf8 -Path (Join-Path $Root 'source-manifest.json')
    if ($manifest.redistributionReady -eq $true) {
        $destination = Join-Path $Directory 'ffmpeg-corresponding-source.zip'
        Copy-Item -LiteralPath (Join-Path $Root $manifest.correspondingSourceArchive.path) -Destination $destination
        return $destination
    }

    $rootPrefix = [System.IO.Path]::GetFullPath($Root) + [System.IO.Path]::DirectorySeparatorChar
    $files = @('GPL-3.0.txt', 'BtbN-LICENSE.txt', 'NOTICE.md', 'source-manifest.json', 'Verify-Sources.ps1')
    foreach ($source in $manifest.sources) {
        $archivePath = [System.IO.Path]::GetFullPath((Join-Path $Root $source.archive))
        if (-not $archivePath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
            [System.IO.Path]::GetExtension($archivePath) -ne '.zip') {
            throw 'Um arquivo de fontes está fora do diretório de mídia ou não é ZIP.'
        }
        if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
            $uri = [uri]$source.url
            if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'codeload.github.com') {
                throw 'Origem de fontes não suportada; use os snapshots HTTPS identificados no manifesto.'
            }
            $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $archivePath)
            Invoke-WebRequest -Uri $uri -OutFile $archivePath -UseBasicParsing
        }
        if ((Get-Sha256Hex -Path $archivePath) -ne $source.sha256) {
            throw "O SHA-256 das fontes de $($source.name) não corresponde ao manifesto."
        }
        $files += [string]$source.archive
    }
    $destination = Join-Path $Directory 'ffmpeg-source-reference.zip'
    New-PortableSourceZip -RelativePaths $files -Root $Root -Destination $destination
    return $destination
}

function Test-SourceZip {
    param([Parameter(Mandatory = $true)][string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName })
        foreach ($required in @('Organessonium.slnx', 'Organessonium/Organessonium.csproj', 'README.md', 'implementation.md', 'implementation2.md', 'implementation old.md', 'implementation old 2.md', 'migration.md', 'checksums.md',
            'Organessonium/media/GPL-3.0.txt', 'Organessonium/media/BtbN-LICENSE.txt', 'Organessonium/media/NOTICE.md', 'Organessonium/media/source-manifest.json')) {
            if ($required -notin $entries) {
                throw "O ZIP de código-fonte não contém $required."
            }
        }
        if (-not ($entries | Where-Object { $_ -like 'Organessonium/*.cs' }) -or
            -not ($entries | Where-Object { $_ -like 'Organessonium/*.xaml' }) -or
            -not ($entries | Where-Object { $_ -like 'Organessonium/web/*' })) {
            throw 'O ZIP de código-fonte não contém todo o código C#, XAML ou web esperado.'
        }
        $invalid = @($entries | Where-Object {
            $_ -match '\\' -or $_ -match '(^|/)(bin|obj|artifacts|release|Logs)(/|$)' -or
            $_ -match '(?i)(release-settings\.local\.json|\.(pfx|p12|pem|key|db|log)$)'
        })
        if ($invalid.Count -gt 0) {
            throw "O ZIP de código-fonte contém entradas proibidas: $($invalid -join ', ')"
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Write-MsixManifest {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Architecture,
        [Parameter(Mandatory = $true)][string]$PackageVersion,
        [Parameter(Mandatory = $true)][hashtable]$ManifestSettings
    )

    $identityName = Escape-Xml $ManifestSettings.storeIdentityName
    $publisher = Escape-Xml $ManifestSettings.storePublisher
    $publisherDisplayName = Escape-Xml $ManifestSettings.storePublisherDisplayName
    $displayName = Escape-Xml $ManifestSettings.storeDisplayName
    $description = Escape-Xml $ManifestSettings.storeDescription
    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
  xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap desktop uap10 rescap">
  <Identity Name="$identityName" Publisher="$publisher" Version="$PackageVersion" ProcessorArchitecture="$Architecture" />
  <Properties>
    <DisplayName>$displayName</DisplayName>
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
        DisplayName="$displayName"
        Description="$description"
        BackgroundColor="transparent"
        Square150x150Logo="Assets\Square150x150Logo.png"
        Square44x44Logo="Assets\Square44x44Logo.png" />
      <Extensions>
        <desktop:Extension Category="windows.startupTask" Executable="Organessonium.exe" EntryPoint="Windows.FullTrustApplication" uap10:Parameters="--background">
          <desktop:StartupTask TaskId="OrganessoniumSync" Enabled="false" DisplayName="$displayName" />
        </desktop:Extension>
      </Extensions>
    </Application>
  </Applications>
  <Capabilities>
    <Capability Name="internetClient" />
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
"@
    [System.IO.File]::WriteAllText($Path, $manifest, [System.Text.UTF8Encoding]::new($false))
}

function Test-MsixArtifact {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$UnpackDirectory,
        [Parameter(Mandatory = $true)][string]$Architecture,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        [Parameter(Mandatory = $true)][hashtable]$ExpectedSettings,
        [Parameter(Mandatory = $true)][string]$MakeAppx
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -le 0) {
        throw "O MSIX $Architecture não existe ou está vazio."
    }
    $null = New-Item -ItemType Directory -Force -Path $UnpackDirectory
    Invoke-Native -FilePath $MakeAppx -Arguments @('unpack', '/p', $Path, '/d', $UnpackDirectory, '/o')
    $manifestPath = Join-Path $UnpackDirectory 'AppxManifest.xml'
    if (-not (Test-Path -LiteralPath $manifestPath) -or
        -not (Test-Path -LiteralPath (Join-Path $UnpackDirectory 'Organessonium.exe'))) {
        throw "O MSIX $Architecture não contém o manifesto e o executável esperados."
    }
    $expectedPeMachine = @{ x86 = 0x014C; x64 = 0x8664; arm64 = 0xAA64 }[$Architecture]
    $actualPeMachine = Get-PeMachine -Path (Join-Path $UnpackDirectory 'Organessonium.exe')
    if ($actualPeMachine -ne $expectedPeMachine) {
        throw ('O executável do MSIX {0} tem arquitetura PE incorreta: 0x{1:X4}.' -f $Architecture, $actualPeMachine)
    }

    $rawManifest = [System.IO.File]::ReadAllText($manifestPath, [System.Text.UTF8Encoding]::new($false, $true))
    if ($rawManifest.Contains('Ã')) {
        throw "O manifesto $Architecture contém texto com encoding corrompido."
    }
    $xml = [System.Xml.XmlDocument]::new()
    $xml.LoadXml($rawManifest)
    $namespaces = [System.Xml.XmlNamespaceManager]::new($xml.NameTable)
    $namespaces.AddNamespace('a', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $namespaces.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
    $namespaces.AddNamespace('rescap', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')
    $identity = $xml.SelectSingleNode('/a:Package/a:Identity', $namespaces)
    $description = $xml.SelectSingleNode('/a:Package/a:Properties/a:Description', $namespaces)
    $displayName = $xml.SelectSingleNode('/a:Package/a:Properties/a:DisplayName', $namespaces)
    $visualElements = $xml.SelectSingleNode('/a:Package/a:Applications/a:Application/uap:VisualElements', $namespaces)
    $publisherDisplayName = $xml.SelectSingleNode('/a:Package/a:Properties/a:PublisherDisplayName', $namespaces)
    $runFullTrust = $xml.SelectSingleNode('/a:Package/a:Capabilities/rescap:Capability[@Name="runFullTrust"]', $namespaces)
    if ($null -eq $identity -or $identity.GetAttribute('ProcessorArchitecture') -ne $Architecture -or
        $identity.GetAttribute('Version') -ne $ExpectedVersion -or
        $identity.GetAttribute('Name') -ne $ExpectedSettings.storeIdentityName -or
        $identity.GetAttribute('Publisher') -ne $ExpectedSettings.storePublisher) {
        throw "O manifesto $Architecture tem identidade, arquitetura ou versão incorreta."
    }
    if ($null -eq $description -or $description.InnerText -ne $ExpectedSettings.storeDescription -or
        $null -eq $displayName -or $displayName.InnerText -ne $ExpectedSettings.storeDisplayName -or
        $null -eq $visualElements -or $visualElements.GetAttribute('DisplayName') -ne $ExpectedSettings.storeDisplayName -or
        $null -eq $publisherDisplayName -or $publisherDisplayName.InnerText -ne $ExpectedSettings.storePublisherDisplayName) {
        throw "O manifesto $Architecture não preservou os textos UTF-8 configurados."
    }
    if ($null -eq $runFullTrust) {
        throw "O manifesto $Architecture não contém runFullTrust."
    }
}

function Test-MsixBundleArtifact {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$UnbundleDirectory,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        [Parameter(Mandatory = $true)][hashtable]$ExpectedSettings,
        [Parameter(Mandatory = $true)][string]$MakeAppx
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -le 0) {
        throw 'O MSIXBundle não existe ou está vazio.'
    }
    $null = New-Item -ItemType Directory -Force -Path $UnbundleDirectory
    Invoke-Native -FilePath $MakeAppx -Arguments @('unbundle', '/p', $Path, '/d', $UnbundleDirectory, '/o')
    $packages = @(Get-ChildItem -LiteralPath $UnbundleDirectory -Filter '*.msix' -File)
    if ($packages.Count -ne 3) {
        throw "O MSIXBundle deveria conter 3 pacotes, mas contém $($packages.Count)."
    }

    $bundleManifestPath = Join-Path $UnbundleDirectory 'AppxMetadata\AppxBundleManifest.xml'
    $xml = [System.Xml.XmlDocument]::new()
    $xml.Load($bundleManifestPath)
    $namespaces = [System.Xml.XmlNamespaceManager]::new($xml.NameTable)
    $namespaces.AddNamespace('b', 'http://schemas.microsoft.com/appx/2013/bundle')
    $identity = $xml.SelectSingleNode('/b:Bundle/b:Identity', $namespaces)
    $packageNodes = @($xml.SelectNodes('/b:Bundle/b:Packages/b:Package', $namespaces))
    $architectures = @($packageNodes | ForEach-Object { $_.GetAttribute('Architecture') } | Sort-Object)
    if ($null -eq $identity -or $identity.GetAttribute('Version') -ne $ExpectedVersion -or
        $identity.GetAttribute('Name') -ne $ExpectedSettings.storeIdentityName -or
        $identity.GetAttribute('Publisher') -ne $ExpectedSettings.storePublisher) {
        throw 'O MSIXBundle tem identidade ou versão incorreta.'
    }
    if (($architectures -join ',') -ne 'arm64,x64,x86') {
        throw "Arquiteturas incorretas no MSIXBundle: $($architectures -join ', ')"
    }
    foreach ($packageNode in $packageNodes) {
        if ($packageNode.GetAttribute('Version') -ne $ExpectedVersion) {
            throw 'Um pacote interno do MSIXBundle tem versão diferente da release.'
        }
    }
}

if (-not (Test-Path -LiteralPath $projectPath) -or -not (Test-Path -LiteralPath $solutionPath)) {
    throw 'Execute o script dentro do checkout completo do Organessonium.'
}

$settings = @{}
foreach ($property in (Read-JsonObjectUtf8 -Path $settingsPath).PSObject.Properties) {
    $settings[$property.Name] = [string]$property.Value
}
if (Test-Path -LiteralPath $localSettingsPath) {
    foreach ($property in (Read-JsonObjectUtf8 -Path $localSettingsPath).PSObject.Properties) {
        $settings[$property.Name] = [string]$property.Value
    }
}
foreach ($requiredSetting in @('version', 'storeIdentityName', 'storePublisher', 'storePublisherDisplayName', 'storeDisplayName', 'storeDescription')) {
    if ([string]::IsNullOrWhiteSpace($settings[$requiredSetting])) {
        throw "A configuração de release '$requiredSetting' está ausente ou vazia."
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

$publishRoot = Join-Path $workRoot 'publish'
$msiPublishRoot = Join-Path $workRoot 'msi-publish'
$msiWorkRoot = Join-Path $workRoot 'msi'
$msixStageRoot = Join-Path $workRoot 'msix-stage'
$verifyDirectory = Join-Path $workRoot 'verify'
$msixOutputDirectory = Join-Path $outputDirectory 'msix'
$null = New-Item -ItemType Directory -Force -Path $publishRoot
$null = New-Item -ItemType Directory -Force -Path $msiPublishRoot
$null = New-Item -ItemType Directory -Force -Path $msixOutputDirectory

$architectures = @(
    [pscustomobject]@{ Rid = 'win-x86'; Name = 'x86'; ProcessorArchitecture = 'x86'; PeMachine = 0x014C; UpgradeCode = '{86D2DF89-568A-4720-B3BE-C0DFC4F1C658}' },
    [pscustomobject]@{ Rid = 'win-x64'; Name = 'x64'; ProcessorArchitecture = 'x64'; PeMachine = 0x8664; UpgradeCode = '{6CA5E42D-31EF-49FC-9C65-C784DB28C1D5}' },
    [pscustomobject]@{ Rid = 'win-arm64'; Name = 'arm64'; ProcessorArchitecture = 'arm64'; PeMachine = 0xAA64; UpgradeCode = '{51F35ACC-553A-4F2B-A54F-18C7F0898D8B}' }
)
$publishedExecutables = @{}
foreach ($architecture in $architectures) {
    $publishDirectory = Join-Path $publishRoot $architecture.Name
    Write-Step "Compilando o aplicativo $Version para $($architecture.Rid)"
    Invoke-Native -FilePath 'dotnet.exe' -Arguments @(
        'publish', $projectPath,
        '-c', 'Release',
        '-r', $architecture.Rid,
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
    if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf) -or (Get-Item -LiteralPath $publishedExe).Length -le 0) {
        throw "A publicação $($architecture.Rid) não produziu Organessonium.exe."
    }
    $publishedExecutables[$architecture.Name] = $publishedExe
    if ($architecture.Name -eq [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()) {
        Write-Step 'Verificando extracao e execucao da midia embutida, sem interface'
        Invoke-Native -FilePath $publishedExe -Arguments @('--verify-media-runtime')
    }
}

$msiPublishedDirectories = @{}
$msiPublishedExecutables = @{}
foreach ($architecture in $architectures) {
    $publishDirectory = Join-Path $msiPublishRoot $architecture.Name
    Write-Step "Compilando o aplicativo MSI framework-dependent para $($architecture.Rid)"
    Invoke-Native -FilePath 'dotnet.exe' -Arguments @(
        'publish', $projectPath,
        '-c', 'Release',
        '-r', $architecture.Rid,
        '--self-contained', 'false',
        '-o', $publishDirectory,
        '-p:PublishSingleFile=false',
        '-p:IncludeNativeLibrariesForSelfExtract=false',
        '-p:EnableCompressionInSingleFile=false',
        '-p:WindowsAppSDKSelfContained=false',
        '-p:WebView2EnableCsWinRTProjection=false',
        "-p:Version=$($versionNumbers[0]).$($versionNumbers[1]).$($versionNumbers[2])",
        "-p:FileVersion=$Version",
        "-p:AssemblyVersion=$Version"
    )
    $publishedExe = Join-Path $publishDirectory 'Organessonium.exe'
    foreach ($requiredFile in @($publishedExe, (Join-Path $publishDirectory 'Organessonium.dll'), (Join-Path $publishDirectory 'Organessonium.runtimeconfig.json'))) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf) -or (Get-Item -LiteralPath $requiredFile).Length -le 0) {
            throw "A publicação MSI $($architecture.Rid) não produziu o arquivo framework-dependent esperado: $requiredFile"
        }
    }
    foreach ($selfContainedFile in @('coreclr.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll')) {
        if (Test-Path -LiteralPath (Join-Path $publishDirectory $selfContainedFile)) {
            throw "A publicação MSI $($architecture.Rid) incluiu $selfContainedFile e não é framework-dependent."
        }
    }
    if ((Get-PeMachine -Path $publishedExe) -ne $architecture.PeMachine) {
        throw "A publicação MSI $($architecture.Rid) produziu um executável de arquitetura incorreta."
    }
    $msiPublishedDirectories[$architecture.Name] = $publishDirectory
    $msiPublishedExecutables[$architecture.Name] = $publishedExe
}

Write-Step 'Verificando e gerando o código-fonte público'
$trackedFiles = @(& git -C $repoRoot ls-files -- 'Organessonium/Organessonium.slnx' 'Organessonium/Organessonium/**' 'README.md' 'implementation.md' 'implementation2.md' 'implementation old.md' 'implementation old 2.md' 'migration.md' 'checksums.md')
if ($LASTEXITCODE -ne 0 -or $trackedFiles.Count -eq 0) {
    throw 'Git não conseguiu enumerar os arquivos necessários do código-fonte.'
}
Test-PublicSourceFiles -RelativePaths $trackedFiles -Root $repoRoot
$mediaManifest = Read-JsonObjectUtf8 -Path (Join-Path $mediaRoot 'source-manifest.json')
# A clean source extraction can build/publish without looking for external media
# binaries or sources. These generated inputs are verified before this step.
$mediaInputs = @([string]$mediaManifest.runtimeArchive.path, [string]$mediaManifest.correspondingSourceArchive.path)
foreach ($mediaInput in $mediaInputs) {
    $trackedFiles += 'Organessonium/Organessonium/media/' + $mediaInput.Replace('\', '/')
}
$trackedFiles = @($trackedFiles | Sort-Object -Unique)
$sourceZip = Join-Path $outputDirectory 'Organessonium-windows-source.zip'
New-PortableSourceZip -RelativePaths $trackedFiles -Root $repoRoot -Destination $sourceZip
Test-SourceZip -Path $sourceZip
$ffmpegSourceZip = New-FfmpegSourcePackage -Root $mediaRoot -Directory $outputDirectory

Write-Step 'Copiando os executaveis portateis com midia e licencas embutidas'
$portablePaths = @()
foreach ($architecture in $architectures) {
    $portableExe = Join-Path $outputDirectory ("Organessonium-{0}.exe" -f $architecture.Name)
    Copy-Item -LiteralPath $publishedExecutables[$architecture.Name] -Destination $portableExe
    $portablePaths += $portableExe
}

$sourceVerifyDirectory = Join-Path $verifyDirectory 'source'
$null = New-Item -ItemType Directory -Force -Path $sourceVerifyDirectory
[System.IO.Compression.ZipFile]::ExtractToDirectory($sourceZip, $sourceVerifyDirectory)
Write-Step 'Compilando o projeto extraído do ZIP de código-fonte'
Invoke-Native -FilePath 'dotnet.exe' -Arguments @(
    'build', (Join-Path $sourceVerifyDirectory 'Organessonium.slnx'),
    '-c', 'Release'
)

Write-Step 'Gerando os instaladores MSI x86, x64 e ARM64 sem compressão'
$msiPaths = @{}
foreach ($architecture in $architectures) {
    $msiPath = Join-Path $outputDirectory ("Organessonium-installer-{0}.msi" -f $architecture.Name)
    $architectureWorkDirectory = Join-Path $msiWorkRoot $architecture.Name
    & (Join-Path $PSScriptRoot 'Create-Msi.ps1') `
        -ApplicationExe $msiPublishedExecutables[$architecture.Name] `
        -ApplicationIcon $iconPath `
        -OutputPath $msiPath `
        -Version $Version `
        -WorkingDirectory $architectureWorkDirectory `
        -Architecture $architecture.Name
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $msiPath)) {
        throw "A geração do MSI $($architecture.Name) falhou."
    }
    Test-MsiArtifact `
        -Path $msiPath `
        -ExpectedVersion $Version `
        -ExpectedUpgradeCode $architecture.UpgradeCode `
        -Architecture $architecture.Name

    Write-Step "Validando a extração administrativa do MSI $($architecture.Name)"
    $msiVerifyDirectory = Join-Path $verifyDirectory ('msi-' + $architecture.Name)
    $msiLog = Join-Path $verifyDirectory ("msi-{0}-validation.log" -f $architecture.Name)
    $null = New-Item -ItemType Directory -Force -Path $msiVerifyDirectory
    $msiProcess = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -Wait -PassThru -ArgumentList @(
        '/a', ('"' + $msiPath + '"'), ('TARGETDIR="' + $msiVerifyDirectory + '"'), '/qn', '/norestart', '/l*v', ('"' + $msiLog + '"')
    )
    $extractedMsiExe = Get-ChildItem -LiteralPath $msiVerifyDirectory -Filter 'Organessonium.exe' -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($msiProcess.ExitCode -ne 0 -or $null -eq $extractedMsiExe) {
        throw "O MSI $($architecture.Name) não passou pela extração administrativa. Código: $($msiProcess.ExitCode). Log: $msiLog"
    }
    $extractedApplicationDirectory = $extractedMsiExe.DirectoryName
    $publishedDirectory = $msiPublishedDirectories[$architecture.Name]
    foreach ($publishedFile in (Get-ChildItem -LiteralPath $publishedDirectory -File -Recurse)) {
        $relativePath = Get-RelativePathCompat -BasePath $publishedDirectory -Path $publishedFile.FullName
        $extractedFile = Join-Path $extractedApplicationDirectory $relativePath
        if (-not (Test-Path -LiteralPath $extractedFile -PathType Leaf) -or
            (Get-Sha256Hex -Path $extractedFile) -ne (Get-Sha256Hex -Path $publishedFile.FullName)) {
            throw "O arquivo $relativePath extraído do MSI $($architecture.Name) não corresponde à publicação."
        }
    }
    if ((Get-PeMachine -Path $extractedMsiExe.FullName) -ne $architecture.PeMachine) {
        throw "O executável extraído do MSI $($architecture.Name) tem arquitetura incorreta."
    }
    $msiPaths[$architecture.Name] = $msiPath
}

Write-Step 'Gerando os pacotes MSIX x86, x64 e ARM64'
$makeAppx = Resolve-WindowsSdkTool -Name 'makeappx.exe'
$msixPaths = @{}
$pfxPath = $env:ORGANESSONIUM_PFX_PATH
$signTool = $null
$signArguments = $null
if (-not [string]::IsNullOrWhiteSpace($pfxPath)) {
    $signTool = Resolve-WindowsSdkTool -Name 'signtool.exe'
    $signArguments = @('sign', '/fd', 'SHA256', '/f', $pfxPath)
    if (-not [string]::IsNullOrWhiteSpace($env:ORGANESSONIUM_PFX_PASSWORD)) {
        $signArguments += @('/p', $env:ORGANESSONIUM_PFX_PASSWORD)
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ORGANESSONIUM_TIMESTAMP_URL)) {
        $signArguments += @('/tr', $env:ORGANESSONIUM_TIMESTAMP_URL, '/td', 'SHA256')
    }
}

foreach ($architecture in $architectures) {
    $msixStageDirectory = Join-Path $msixStageRoot $architecture.Name
    $assetsDirectory = Join-Path $msixStageDirectory 'Assets'
    $null = New-Item -ItemType Directory -Force -Path $assetsDirectory
    Copy-Item -LiteralPath $publishedExecutables[$architecture.Name] -Destination (Join-Path $msixStageDirectory 'Organessonium.exe') -Force
    $storeMedia = Join-Path $msixStageDirectory 'media'
    $null = New-Item -ItemType Directory -Force -Path $storeMedia
    foreach ($notice in @('GPL-3.0.txt', 'BtbN-LICENSE.txt', 'NOTICE.md', 'THIRD-PARTY-NOTICES.txt', 'source-manifest.json')) {
        Copy-Item -LiteralPath (Join-Path $mediaRoot $notice) -Destination $storeMedia
    }
    # Store users receive the exact corresponding source with the binary. No
    # mutable download URL, login or later manual source request is necessary.
    $storeSources = Join-Path $msixStageDirectory 'media\sources'
    $null = New-Item -ItemType Directory -Force -Path $storeSources
    Copy-Item -LiteralPath $ffmpegSourceZip -Destination (Join-Path $storeSources 'ffmpeg-corresponding-source.zip')
    Convert-IconToPng -Source $iconPath -Destination (Join-Path $assetsDirectory 'StoreLogo.png') -Width 50 -Height 50 -LogoSize 44
    Convert-IconToPng -Source $iconPath -Destination (Join-Path $assetsDirectory 'Square44x44Logo.png') -Width 44 -Height 44 -LogoSize 38
    Convert-IconToPng -Source $iconPath -Destination (Join-Path $assetsDirectory 'Square150x150Logo.png') -Width 150 -Height 150 -LogoSize 128
    Write-MsixManifest `
        -Path (Join-Path $msixStageDirectory 'AppxManifest.xml') `
        -Architecture $architecture.ProcessorArchitecture `
        -PackageVersion $Version `
        -ManifestSettings $settings

    $msixPath = Join-Path $msixOutputDirectory ("Organessonium_{0}_{1}.msix" -f $Version, $architecture.Name)
    Invoke-Native -FilePath $makeAppx -Arguments @('pack', '/d', $msixStageDirectory, '/p', $msixPath, '/o')
    if ($null -ne $signTool) {
        Invoke-Native -FilePath $signTool -Arguments ($signArguments + @($msixPath))
    }
    Test-MsixArtifact `
        -Path $msixPath `
        -UnpackDirectory (Join-Path $verifyDirectory ('msix-' + $architecture.Name)) `
        -Architecture $architecture.ProcessorArchitecture `
        -ExpectedVersion $Version `
        -ExpectedSettings $settings `
        -MakeAppx $makeAppx
    $msixPaths[$architecture.Name] = $msixPath
}

Write-Step 'Gerando o MSIXBundle único da Microsoft Store'
$bundlePath = Join-Path $outputDirectory ("Organessonium_{0}.msixbundle" -f $Version)
Invoke-Native -FilePath $makeAppx -Arguments @(
    'bundle', '/d', $msixOutputDirectory, '/p', $bundlePath, '/bv', $Version, '/o'
)
if ($null -ne $signTool) {
    Write-Step 'Assinando MSIs e MSIXBundle com SignTool'
    foreach ($msiPath in $msiPaths.Values) {
        Invoke-Native -FilePath $signTool -Arguments ($signArguments + @($msiPath))
    }
    Invoke-Native -FilePath $signTool -Arguments ($signArguments + @($bundlePath))
}
Test-MsixBundleArtifact `
    -Path $bundlePath `
    -UnbundleDirectory (Join-Path $verifyDirectory 'bundle') `
    -ExpectedVersion $Version `
    -ExpectedSettings $settings `
    -MakeAppx $makeAppx

$wackPath = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\App Certification Kit\appcert.exe'
$wackStatus = if (Test-Path -LiteralPath $wackPath) { 'não executado (use -RunWack)' } else { 'não instalado' }
if ($RunWack) {
    if (-not (Test-Path -LiteralPath $wackPath)) {
        Write-Warning 'O Windows App Certification Kit não está instalado. Os demais artefatos foram mantidos.'
    }
    else {
        Write-Step 'Executando o Windows App Certification Kit'
        $wackReport = Join-Path $outputDirectory 'WackReport.xml'
        try {
            $wackProcess = Start-Process -FilePath $wackPath -Wait -PassThru -ArgumentList @(
                'test', '-appxpackagepath', ('"' + $bundlePath + '"'), '-reportoutputpath', ('"' + $wackReport + '"')
            )
            if ($wackProcess.ExitCode -eq 0) {
                $wackStatus = 'aprovado'
            }
            else {
                $wackStatus = "falhou (código $($wackProcess.ExitCode), relatório: $wackReport)"
                Write-Warning "O WACK falhou com código $($wackProcess.ExitCode). Os demais artefatos foram mantidos."
            }
        }
        catch {
            $wackStatus = 'falhou ao iniciar; execute o script em um terminal elevado'
            Write-Warning "O WACK não pôde ser iniciado: $($_.Exception.Message)"
        }
    }
}

Write-Step 'Gerando hashes SHA-256'
$releaseFiles = @($msiPaths.Values) + @($sourceZip, $ffmpegSourceZip, $bundlePath) + @($msixPaths.Values) + $portablePaths
$hashLines = foreach ($file in $releaseFiles) {
    $relativeOutputPath = [System.IO.Path]::GetFullPath($file).Substring([System.IO.Path]::GetFullPath($outputDirectory).Length).TrimStart('\').Replace('\', '/')
    '{0}  {1}' -f (Get-Sha256Hex -Path $file), $relativeOutputPath
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

$storeIdentityStatus = if ($settings.storeIdentityName -eq 'Organessonium.Desktop' -or $settings.storePublisher -eq 'CN=Organessonium') {
    'utilizando placeholder'
} else {
    'configurada'
}

Write-Host "`nRelease Organessonium $Version concluída." -ForegroundColor Green
Write-Host "`nMSI framework-dependent e sem compressão:`n- x86: OK`n- x64: OK`n- ARM64: OK"
Write-Host "`nMSIX:`n- x86: OK`n- x64: OK`n- ARM64: OK"
Write-Host "`nMSIXBundle:`n- x86 + x64 + ARM64: OK`n- versão: $Version"
Write-Host "`nSource ZIP:`n- OK"
Write-Host "`nMicrosoft Store identity:`n- $storeIdentityStatus"
Write-Host "`nWACK:`n- $wackStatus"
Write-Host "`nArquivos gerados em: $outputDirectory"
Get-ChildItem -LiteralPath $outputDirectory -File -Recurse |
    Sort-Object FullName |
    Select-Object @{ Name = 'Arquivo'; Expression = { $_.FullName.Substring($outputDirectory.Length).TrimStart('\').Replace('\', '/') } }, Length |
    Format-Table -AutoSize
if ($storeIdentityStatus -eq 'utilizando placeholder') {
    Write-Warning 'O MSIXBundle usa a identidade local padrão. Antes de enviar à Store, copie release-settings.local.example.json para release-settings.local.json e use os valores exatos do Partner Center.'
}
