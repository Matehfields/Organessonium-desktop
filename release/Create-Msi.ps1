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
    [string]$WorkingDirectory
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
        [System.Text.Encoding]::ASCII)
}

function Release-ComObject {
    param([object]$Value)
    if ($null -ne $Value -and [System.Runtime.InteropServices.Marshal]::IsComObject($Value)) {
        [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($Value)
    }
}

$applicationExe = (Resolve-Path -LiteralPath $ApplicationExe).Path
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
$upgradeCode = '{6CA5E42D-31EF-49FC-9C65-C784DB28C1D5}'
$componentCode = '{F17D201F-DAC1-4F3F-BB5D-4E3578CC7B7D}'
$productCode = New-DeterministicGuid -Value ('Organessonium.Product.' + $msiVersion)
$packageCode = '{' + ([Guid]::NewGuid()).ToString().ToUpperInvariant() + '}'
$fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($applicationExe).FileVersion
if ([string]::IsNullOrWhiteSpace($fileVersion)) {
    $fileVersion = $Version
}

$idtDirectory = Join-Path $workingDirectory 'idt'
$cabDirectory = Join-Path $workingDirectory 'cab'
$null = New-Item -ItemType Directory -Force -Path $idtDirectory
$null = New-Item -ItemType Directory -Force -Path $cabDirectory

Write-Idt -Name 'Property' -Columns @('Property', 'Value') -Types @('s72', 'l0') -PrimaryKeys @('Property') -Directory $idtDirectory -Rows @(
    @('ProductCode', $productCode),
    @('ProductLanguage', '1046'),
    @('ProductName', 'Organessonium'),
    @('ProductVersion', $msiVersion),
    @('Manufacturer', 'Organessonium'),
    @('UpgradeCode', $upgradeCode),
    @('ALLUSERS', '1'),
    @('ARPNOMODIFY', '1'),
    @('ARPNOREPAIR', '1'),
    @('ARPPRODUCTICON', 'OrganessoniumIcon')
)

Write-Idt -Name 'Directory' -Columns @('Directory', 'Directory_Parent', 'DefaultDir') -Types @('s72', 'S72', 'l255') -PrimaryKeys @('Directory') -Directory $idtDirectory -Rows @(
    @('TARGETDIR', '', 'SourceDir'),
    @('ProgramFiles64Folder', 'TARGETDIR', '.'),
    @('INSTALLFOLDER', 'ProgramFiles64Folder', 'ORGNSNM|Organessonium'),
    @('ProgramMenuFolder', 'TARGETDIR', '.')
)

Write-Idt -Name 'Feature' -Columns @('Feature', 'Feature_Parent', 'Title', 'Description', 'Display', 'Level', 'Directory_', 'Attributes') -Types @('s38', 'S38', 'L64', 'L255', 'I2', 'i2', 'S72', 'i2') -PrimaryKeys @('Feature') -Directory $idtDirectory -Rows @(
    @('Complete', '', 'Organessonium', 'Instala o Organessonium.', '1', '1', 'INSTALLFOLDER', '0')
)

Write-Idt -Name 'Component' -Columns @('Component', 'ComponentId', 'Directory_', 'Attributes', 'Condition', 'KeyPath') -Types @('s72', 'S38', 's72', 'i2', 'S255', 'S72') -PrimaryKeys @('Component') -Directory $idtDirectory -Rows @(
    @('ApplicationFiles', $componentCode, 'INSTALLFOLDER', '256', '', 'AppExe')
)

$applicationLength = (Get-Item -LiteralPath $applicationExe).Length
Write-Idt -Name 'File' -Columns @('File', 'Component_', 'FileName', 'FileSize', 'Version', 'Language', 'Attributes', 'Sequence') -Types @('s72', 's72', 'l255', 'i4', 'S72', 'S20', 'I2', 'i4') -PrimaryKeys @('File') -Directory $idtDirectory -Rows @(
    @('AppExe', 'ApplicationFiles', 'ORGNSNM.EXE|Organessonium.exe', $applicationLength, $fileVersion, '0', '512', '1')
)

Write-Idt -Name 'FeatureComponents' -Columns @('Feature_', 'Component_') -Types @('s38', 's72') -PrimaryKeys @('Feature_', 'Component_') -Directory $idtDirectory -Rows @(
    @('Complete', 'ApplicationFiles')
)

Write-Idt -Name 'Media' -Columns @('DiskId', 'LastSequence', 'DiskPrompt', 'Cabinet', 'VolumeLabel', 'Source') -Types @('i2', 'i4', 'L64', 'S255', 'S32', 'S72') -PrimaryKeys @('DiskId') -Directory $idtDirectory -Rows @(
    @('1', '1', '', '#app.cab', '', '')
)

Write-Idt -Name 'Shortcut' -Columns @('Shortcut', 'Directory_', 'Name', 'Component_', 'Target', 'Arguments', 'Description', 'Hotkey', 'Icon_', 'IconIndex', 'ShowCmd', 'WkDir', 'DisplayResourceDLL', 'DisplayResourceId', 'DescriptionResourceDLL', 'DescriptionResourceId') -Types @('s72', 's72', 'l128', 's72', 's72', 'S255', 'L255', 'I2', 'S72', 'I2', 'I2', 'S72', 'S255', 'I2', 'S255', 'I2') -PrimaryKeys @('Shortcut') -Directory $idtDirectory -Rows @(
    @('StartMenuShortcut', 'ProgramMenuFolder', 'ORGNSNM|Organessonium', 'ApplicationFiles', '[#AppExe]', '', 'Abrir Organessonium', '', 'OrganessoniumIcon', '0', '1', 'INSTALLFOLDER', '', '', '', '')
)

Write-Idt -Name 'Icon' -Columns @('Name', 'Data') -Types @('s72', 'v0') -PrimaryKeys @('Name') -Directory $idtDirectory -Rows @()

Write-Idt -Name 'Upgrade' -Columns @('UpgradeCode', 'VersionMin', 'VersionMax', 'Language', 'Attributes', 'Remove', 'ActionProperty') -Types @('s38', 'S20', 'S20', 'S255', 'i4', 'S255', 's72') -PrimaryKeys @('UpgradeCode', 'VersionMin', 'VersionMax', 'Language', 'Attributes') -Directory $idtDirectory -Rows @(
    @($upgradeCode, '', $msiVersion, '', '1', '', 'OLDPRODUCTS'),
    @($upgradeCode, $msiVersion, '', '', '258', '', 'NEWERPRODUCTFOUND')
)

Write-Idt -Name 'LaunchCondition' -Columns @('Condition', 'Description') -Types @('s255', 'l255') -PrimaryKeys @('Condition') -Directory $idtDirectory -Rows @(
    @('VersionNT64 >= 1000', 'O Organessonium requer Windows 10 ou posterior em 64 bits.'),
    @('NOT NEWERPRODUCTFOUND', 'Uma versão mais recente do Organessonium já está instalada.')
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
    ('.Set DiskDirectoryTemplate={0}' -f $cabDirectory),
    ('"{0}" "AppExe"' -f $applicationExe)
)
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
    $summary.Property(2) = 'Organessonium Installer'
    $summary.Property(3) = 'Organessonium'
    $summary.Property(4) = 'Organessonium'
    $summary.Property(7) = 'x64;1046'
    $summary.Property(9) = $packageCode
    $summary.Property(14) = 200
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

if (-not (Test-Path -LiteralPath $outputPath) -or (Get-Item -LiteralPath $outputPath).Length -lt 1MB) {
    throw 'O Windows Installer não produziu um MSI válido.'
}
