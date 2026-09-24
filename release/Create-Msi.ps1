[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ApplicationExe,

    [Parameter(Mandatory = $true)]
    [string]$ApplicationIcon,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$WorkingDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateSet('x86', 'x64', 'arm64')]
    [string]$Architecture
)

$ErrorActionPreference = 'Stop'

function New-DeterministicGuid {
    param([Parameter(Mandatory = $true)][string]$Value)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    $algorithm = [System.Security.Cryptography.MD5]::Create()
    try {
        $hash = $algorithm.ComputeHash($bytes)
    }
    finally {
        $algorithm.Dispose()
    }
    return '{' + ([Guid]::new($hash)).ToString().ToUpperInvariant() + '}'
}

function New-StableId {
    param(
        [Parameter(Mandatory = $true)][string]$Prefix,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $algorithm.ComputeHash($bytes)
    }
    finally {
        $algorithm.Dispose()
    }
    $hex = [System.BitConverter]::ToString($hash).Replace('-', '')
    return $Prefix + $hex.Substring(0, 24)
}

function Write-Idt {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$Columns,
        [Parameter(Mandatory = $true)][string[]]$Types,
        [Parameter(Mandatory = $true)][string[]]$PrimaryKeys,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[][]]$Rows,
        [Parameter(Mandatory = $true)][string]$Directory
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add(($Columns -join "`t"))
    $lines.Add(($Types -join "`t"))
    $lines.Add((@($Name) + $PrimaryKeys -join "`t"))
    $normalizedRows = $Rows
    if ($Rows.Count -eq $Columns.Count -and @($Rows | Where-Object { $_.Count -ne 1 }).Count -eq 0) {
        $singleRow = @($Rows | ForEach-Object { $_[0] })
        $normalizedRows = @(, $singleRow)
    }
    foreach ($row in $normalizedRows) {
        $values = foreach ($value in $row) {
            if ($null -eq $value) { '' } else { ([string]$value).Replace("`r", ' ').Replace("`n", ' ').Replace("`t", ' ') }
        }
        $lines.Add(($values -join "`t"))
    }

    [System.IO.File]::WriteAllLines(
        (Join-Path $Directory ($Name + '.idt')),
        $lines,
        [System.Text.Encoding]::GetEncoding(1252))
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

$architectureSettings = @{
    x86 = @{
        Template = 'Intel;1046'
        ComponentAttributes = '0'
        Schema = 200
        UpgradeCode = '{86D2DF89-568A-4720-B3BE-C0DFC4F1C658}'
    }
    x64 = @{
        Template = 'x64;1046'
        ComponentAttributes = '256'
        Schema = 200
        UpgradeCode = '{6CA5E42D-31EF-49FC-9C65-C784DB28C1D5}'
    }
    arm64 = @{
        Template = 'Arm64;1046'
        ComponentAttributes = '256'
        Schema = 500
        UpgradeCode = '{51F35ACC-553A-4F2B-A54F-18C7F0898D8B}'
    }
}[$Architecture]
$productDisplayName = 'Organess' + [char]0x00F4 + 'nium'
$newerVersionMessage = 'Uma vers' + [char]0x00E3 + 'o mais recente do ' + $productDisplayName + ' j' + [char]0x00E1 + ' est' + [char]0x00E1 + ' instalada.'

$applicationExe = (Resolve-Path -LiteralPath $ApplicationExe).Path
$applicationDirectory = Split-Path -Parent $applicationExe
$applicationIcon = (Resolve-Path -LiteralPath $ApplicationIcon).Path
$outputPath = [System.IO.Path]::GetFullPath($OutputPath)
$workingDirectory = [System.IO.Path]::GetFullPath($WorkingDirectory)
$null = New-Item -ItemType Directory -Force -Path $workingDirectory
$null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outputPath)

$versionParts = $Version.Split('.')
if ($versionParts.Count -ne 4 -or $versionParts.Where({ $_ -notmatch '^\d+$' }).Count -gt 0) {
    throw 'A versão do MSI deve chegar no formato numérico A.B.C.D.'
}
if ([int]$versionParts[0] -gt 255 -or [int]$versionParts[1] -gt 255 -or [int]$versionParts[3] -ne 0) {
    throw 'A versão do MSI deve usar A.B.C.0, com A e B entre 0 e 255.'
}
$msiVersion = '{0}.{1}.{2}' -f $versionParts[0], $versionParts[1], $versionParts[2]
$upgradeCode = $architectureSettings.UpgradeCode
$productIdentity = if ($Architecture -eq 'x64') {
    'Organessonium.Product.' + $msiVersion
} else {
    'Organessonium.Product.' + $Architecture + '.' + $msiVersion
}
$productCode = New-DeterministicGuid -Value $productIdentity
$packageCode = '{' + ([Guid]::NewGuid()).ToString().ToUpperInvariant() + '}'

$files = @(Get-ChildItem -LiteralPath $applicationDirectory -File -Recurse | Sort-Object FullName)
if ($files.Count -eq 0) {
    throw 'A publicação do aplicativo não contém arquivos para o MSI.'
}

$idtDirectory = Join-Path $workingDirectory 'idt'
$cabDirectory = Join-Path $workingDirectory 'cab'
$null = New-Item -ItemType Directory -Force -Path $idtDirectory
$null = New-Item -ItemType Directory -Force -Path $cabDirectory

$directoryRows = [System.Collections.Generic.List[object[]]]::new()
$directoryRows.Add(@('TARGETDIR', '', 'SourceDir'))
$directoryRows.Add(@('AppDataFolder', 'TARGETDIR', '.'))
$directoryRows.Add(@('INSTALLFOLDER', 'AppDataFolder', 'ORGNSNM|Organessonium'))
$directoryRows.Add(@('ProgramMenuFolder', 'TARGETDIR', '.'))
$directoryIds = @{ '' = 'INSTALLFOLDER' }
$relativeDirectories = @($files | ForEach-Object {
    (Get-RelativePathCompat -BasePath $applicationDirectory -Path $_.DirectoryName).Replace('\', '/')
} | Where-Object { $_ -ne '.' } | Sort-Object -Unique)
$directorySequence = 0
foreach ($relativeDirectory in $relativeDirectories) {
    $segments = $relativeDirectory.Split('/')
    $currentPath = ''
    foreach ($segment in $segments) {
        $parentPath = $currentPath
        $currentPath = if ([string]::IsNullOrEmpty($currentPath)) { $segment } else { $currentPath + '/' + $segment }
        if ($directoryIds.ContainsKey($currentPath)) {
            continue
        }
        $directorySequence++
        $directoryId = New-StableId -Prefix 'Dir_' -Value ($Architecture + ':' + $currentPath)
        $parentId = $directoryIds[$parentPath]
        $shortName = 'D{0:D7}' -f $directorySequence
        $directoryRows.Add(@($directoryId, $parentId, ($shortName + '|' + $segment)))
        $directoryIds[$currentPath] = $directoryId
    }
}

$componentRows = [System.Collections.Generic.List[object[]]]::new()
$fileRows = [System.Collections.Generic.List[object[]]]::new()
$featureComponentRows = [System.Collections.Generic.List[object[]]]::new()
$cabinetRows = [System.Collections.Generic.List[string]]::new()
$mainComponentId = $null
$mainFileId = $null
$sequence = 0
foreach ($file in $files) {
    $sequence++
    $relativePath = (Get-RelativePathCompat -BasePath $applicationDirectory -Path $file.FullName).Replace('\', '/')
    $relativeDirectory = [System.IO.Path]::GetDirectoryName($relativePath)
    if ([string]::IsNullOrWhiteSpace($relativeDirectory)) {
        $relativeDirectory = ''
    } else {
        $relativeDirectory = $relativeDirectory.Replace('\', '/')
    }
    $isMainExe = $file.FullName.Equals($applicationExe, [System.StringComparison]::OrdinalIgnoreCase)
    $fileId = if ($isMainExe) { 'AppExe' } else { New-StableId -Prefix 'File_' -Value ($Architecture + ':' + $relativePath) }
    $componentId = if ($isMainExe) { 'ApplicationFiles' } else { New-StableId -Prefix 'Cmp_' -Value ($Architecture + ':' + $relativePath) }
    $componentCode = if ($isMainExe -and $Architecture -eq 'x64') {
        '{F17D201F-DAC1-4F3F-BB5D-4E3578CC7B7D}'
    } else {
        New-DeterministicGuid -Value ('Organessonium.Component.' + $Architecture + '.' + $relativePath.ToLowerInvariant())
    }
    $shortExtension = [System.IO.Path]::GetExtension($file.Name).TrimStart('.').ToUpperInvariant()
    $shortExtension = -join @($shortExtension.ToCharArray() | Where-Object { [char]::IsLetterOrDigit($_) } | Select-Object -First 3)
    $shortBaseName = if ($isMainExe) { 'ORGNSNM' } else { 'F{0:D7}' -f $sequence }
    $shortName = if ([string]::IsNullOrEmpty($shortExtension)) { $shortBaseName } else { $shortBaseName + '.' + $shortExtension }
    $msiFileName = if ($shortName -eq $file.Name) { $shortName } else { $shortName + '|' + $file.Name }
    $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName).FileVersion
    if ($isMainExe -and [string]::IsNullOrWhiteSpace($fileVersion)) {
        $fileVersion = $Version
    }
    if ([string]::IsNullOrWhiteSpace($fileVersion)) {
        $fileVersion = ''
    }

    $componentRows.Add(@($componentId, $componentCode, $directoryIds[$relativeDirectory], $architectureSettings.ComponentAttributes, '', $fileId))
    $fileRows.Add(@($fileId, $componentId, $msiFileName, $file.Length, $fileVersion, '', '512', $sequence))
    $featureComponentRows.Add(@('Complete', $componentId))
    $cabinetRows.Add(('"{0}" "{1}"' -f $file.FullName, $fileId))
    if ($isMainExe) {
        $mainComponentId = $componentId
        $mainFileId = $fileId
    }
}
if ($null -eq $mainFileId) {
    throw 'Organessonium.exe não foi encontrado entre os arquivos publicados.'
}

Write-Idt -Name 'Property' -Columns @('Property', 'Value') -Types @('s72', 'l0') -PrimaryKeys @('Property') -Directory $idtDirectory -Rows @(
    @('ProductCode', $productCode),
    @('ProductLanguage', '1046'),
    @('ProductName', $productDisplayName),
    @('ProductVersion', $msiVersion),
    @('Manufacturer', $productDisplayName),
    @('UpgradeCode', $upgradeCode),
    @('ARPNOMODIFY', '1'),
    @('ARPNOREPAIR', '1'),
    @('ARPPRODUCTICON', 'OrganessoniumIcon')
)

Write-Idt -Name 'Directory' -Columns @('Directory', 'Directory_Parent', 'DefaultDir') -Types @('s72', 'S72', 'l255') -PrimaryKeys @('Directory') -Directory $idtDirectory -Rows $directoryRows.ToArray()

Write-Idt -Name 'Feature' -Columns @('Feature', 'Feature_Parent', 'Title', 'Description', 'Display', 'Level', 'Directory_', 'Attributes') -Types @('s38', 'S38', 'L64', 'L255', 'I2', 'i2', 'S72', 'i2') -PrimaryKeys @('Feature') -Directory $idtDirectory -Rows @(
    @('Complete', '', $productDisplayName, ('Instala o ' + $productDisplayName + '.'), '1', '1', 'INSTALLFOLDER', '0')
)

Write-Idt -Name 'Component' -Columns @('Component', 'ComponentId', 'Directory_', 'Attributes', 'Condition', 'KeyPath') -Types @('s72', 'S38', 's72', 'i2', 'S255', 'S72') -PrimaryKeys @('Component') -Directory $idtDirectory -Rows $componentRows.ToArray()

Write-Idt -Name 'File' -Columns @('File', 'Component_', 'FileName', 'FileSize', 'Version', 'Language', 'Attributes', 'Sequence') -Types @('s72', 's72', 'l255', 'i4', 'S72', 'S20', 'I2', 'i4') -PrimaryKeys @('File') -Directory $idtDirectory -Rows $fileRows.ToArray()

Write-Idt -Name 'FeatureComponents' -Columns @('Feature_', 'Component_') -Types @('s38', 's72') -PrimaryKeys @('Feature_', 'Component_') -Directory $idtDirectory -Rows $featureComponentRows.ToArray()

Write-Idt -Name 'Media' -Columns @('DiskId', 'LastSequence', 'DiskPrompt', 'Cabinet', 'VolumeLabel', 'Source') -Types @('i2', 'i4', 'L64', 'S255', 'S32', 'S72') -PrimaryKeys @('DiskId') -Directory $idtDirectory -Rows @(
    @('1', $sequence, '', '#app.cab', '', '')
)

Write-Idt -Name 'Shortcut' -Columns @('Shortcut', 'Directory_', 'Name', 'Component_', 'Target', 'Arguments', 'Description', 'Hotkey', 'Icon_', 'IconIndex', 'ShowCmd', 'WkDir', 'DisplayResourceDLL', 'DisplayResourceId', 'DescriptionResourceDLL', 'DescriptionResourceId') -Types @('s72', 's72', 'l128', 's72', 's72', 'S255', 'L255', 'I2', 'S72', 'I2', 'I2', 'S72', 'S255', 'I2', 'S255', 'I2') -PrimaryKeys @('Shortcut') -Directory $idtDirectory -Rows @(
    @('StartMenuShortcut', 'ProgramMenuFolder', ('ORGNSNM|' + $productDisplayName), $mainComponentId, ('[#' + $mainFileId + ']'), '', ('Abrir ' + $productDisplayName), '', 'OrganessoniumIcon', '0', '1', 'INSTALLFOLDER', '', '', '', '')
)

Write-Idt -Name 'Icon' -Columns @('Name', 'Data') -Types @('s72', 'v0') -PrimaryKeys @('Name') -Directory $idtDirectory -Rows @()

Write-Idt -Name 'Upgrade' -Columns @('UpgradeCode', 'VersionMin', 'VersionMax', 'Language', 'Attributes', 'Remove', 'ActionProperty') -Types @('s38', 'S20', 'S20', 'S255', 'i4', 'S255', 's72') -PrimaryKeys @('UpgradeCode', 'VersionMin', 'VersionMax', 'Language', 'Attributes') -Directory $idtDirectory -Rows @(
    @($upgradeCode, '', $msiVersion, '', '1', '', 'OLDPRODUCTS'),
    @($upgradeCode, $msiVersion, '', '', '258', '', 'NEWERPRODUCTFOUND')
)

Write-Idt -Name 'LaunchCondition' -Columns @('Condition', 'Description') -Types @('s255', 'l255') -PrimaryKeys @('Condition') -Directory $idtDirectory -Rows @(
    @('VersionNT >= 603', ('O ' + $productDisplayName + ' requer Windows 10 ou posterior.')),
    @('NOT NEWERPRODUCTFOUND', $newerVersionMessage)
)

Write-Idt -Name 'InstallExecuteSequence' -Columns @('Action', 'Condition', 'Sequence') -Types @('s72', 'S255', 'I2') -PrimaryKeys @('Action') -Directory $idtDirectory -Rows @(
    @('FindRelatedProducts', '', '25'),
    @('LaunchConditions', '', '100'),
    @('ValidateProductID', '', '700'),
    @('CostInitialize', '', '800'),
    @('FileCost', '', '900'),
    @('CostFinalize', '', '1000'),
    @('InstallValidate', '', '1400'),
    @('InstallInitialize', '', '1500'),
    @('ProcessComponents', '', '1600'),
    @('UnpublishFeatures', '', '1800'),
    @('RemoveShortcuts', '', '3200'),
    @('RemoveFiles', '', '3500'),
    @('InstallFiles', '', '4000'),
    @('RegisterUser', '', '6000'),
    @('RegisterProduct', '', '6100'),
    @('PublishFeatures', '', '6300'),
    @('PublishProduct', '', '6400'),
    @('CreateShortcuts', '', '6500'),
    @('InstallFinalize', '', '6600'),
    @('RemoveExistingProducts', 'OLDPRODUCTS', '6700')
)

Write-Idt -Name 'AdminExecuteSequence' -Columns @('Action', 'Condition', 'Sequence') -Types @('s72', 'S255', 'I2') -PrimaryKeys @('Action') -Directory $idtDirectory -Rows @(
    @('CostInitialize', '', '800'),
    @('FileCost', '', '900'),
    @('CostFinalize', '', '1000'),
    @('InstallValidate', '', '1400'),
    @('InstallInitialize', '', '1500'),
    @('InstallAdminPackage', '', '3900'),
    @('InstallFiles', '', '4000'),
    @('InstallFinalize', '', '6600')
)

$cabPath = Join-Path $cabDirectory 'app.cab'
$ddfPath = Join-Path $workingDirectory 'app.ddf'
$ddf = @(
    '.OPTION EXPLICIT',
    '.Set CabinetFileCountThreshold=0',
    '.Set FolderFileCountThreshold=0',
    '.Set FolderSizeThreshold=0',
    '.Set MaxCabinetSize=0',
    '.Set MaxDiskFileCount=0',
    '.Set MaxDiskSize=0',
    '.Set Cabinet=on',
    '.Set Compress=off',
    '.Set CabinetNameTemplate=app.cab',
    ('.Set DiskDirectoryTemplate={0}' -f $cabDirectory)
) + $cabinetRows.ToArray()
[System.IO.File]::WriteAllLines($ddfPath, $ddf, [System.Text.Encoding]::ASCII)
$makeCabLog = Join-Path $workingDirectory 'makecab.log'
Push-Location $workingDirectory
try {
    & "$env:SystemRoot\System32\makecab.exe" /F $ddfPath *> $makeCabLog
}
finally {
    Pop-Location
}
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $cabPath)) {
    if (Test-Path -LiteralPath $makeCabLog) {
        Get-Content -LiteralPath $makeCabLog -Tail 30 | Out-Host
    }
    throw 'MakeCab não conseguiu gerar o cabinet do instalador.'
}

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Force
}

$installer = $null
$database = $null
$summary = $null
$view = $null
$record = $null
try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.OpenDatabase($outputPath, 3)
    foreach ($table in @('Property', 'Directory', 'Feature', 'Component', 'File', 'FeatureComponents', 'Media', 'Shortcut', 'Icon', 'Upgrade', 'LaunchCondition', 'InstallExecuteSequence', 'AdminExecuteSequence')) {
        try {
            $database.Import($idtDirectory, $table + '.idt')
        }
        catch {
            throw "Falha ao importar a tabela MSI $table`: $($_.Exception.Message)"
        }
    }

    $view = $database.OpenView('INSERT INTO `Icon` (`Name`, `Data`) VALUES (?, ?)')
    $record = $installer.CreateRecord(2)
    $record.StringData(1) = 'OrganessoniumIcon'
    $record.SetStream(2, $applicationIcon)
    $view.Execute($record)
    $view.Close()
    Release-ComObject $record
    $record = $null
    Release-ComObject $view
    $view = $null

    $view = $database.OpenView('INSERT INTO `_Streams` (`Name`, `Data`) VALUES (?, ?)')
    $record = $installer.CreateRecord(2)
    $record.StringData(1) = 'app.cab'
    $record.SetStream(2, $cabPath)
    $view.Execute($record)
    $view.Close()
    Release-ComObject $record
    $record = $null
    Release-ComObject $view
    $view = $null

    $summary = $database.SummaryInformation(20)
    $summary.Property(1) = 1252
    $summary.Property(2) = $productDisplayName + ' Installer'
    $summary.Property(3) = $productDisplayName
    $summary.Property(4) = $productDisplayName
    $summary.Property(7) = $architectureSettings.Template
    $summary.Property(9) = $packageCode
    $summary.Property(14) = $architectureSettings.Schema
    $summary.Property(15) = 10
    $summary.Persist()
    $database.Commit()
}
finally {
    Release-ComObject $record
    Release-ComObject $view
    Release-ComObject $summary
    Release-ComObject $database
    Release-ComObject $installer
}

if (-not (Test-Path -LiteralPath $outputPath) -or (Get-Item -LiteralPath $outputPath).Length -le 0) {
    throw 'O Windows Installer não produziu um MSI válido.'
}
