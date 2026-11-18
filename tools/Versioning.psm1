Set-StrictMode -Version Latest

function Assert-OrganessoniumVersionValues {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][int]$Build
    )

    if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "Versão pública inválida: '$Version'. Use SemVer MAJOR.MINOR.PATCH, sem prefixo 'v' e sem zeros à esquerda."
    }

    $parts = $Version.Split('.') | ForEach-Object {
        [int]::Parse($_, [System.Globalization.NumberStyles]::None, [System.Globalization.CultureInfo]::InvariantCulture)
    }
    $major = $parts[0]
    $minor = $parts[1]
    $patch = $parts[2]

    if ($major -gt 255 -or $minor -gt 255) {
        throw 'MAJOR e MINOR devem ficar entre 0 e 255 para manter upgrades de MSI válidos.'
    }
    if ($patch -gt 65535) {
        throw 'PATCH deve ficar entre 0 e 65535 para caber nos metadados do Windows.'
    }
    if ($Build -lt 1 -or $Build -gt 65535) {
        throw 'O número da build deve ficar entre 1 e 65535.'
    }
}

function Get-OrganessoniumVersionInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot
    )

    $root = [System.IO.Path]::GetFullPath($RepositoryRoot)
    $propsPath = Join-Path $root 'Version.props'
    if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) {
        throw "Version.props não encontrado em $root."
    }

    try {
        [xml]$xml = [System.IO.File]::ReadAllText($propsPath)
    }
    catch {
        throw "Version.props não é um XML válido: $($_.Exception.Message)"
    }

    $versionNode = $xml.SelectSingleNode('/Project/PropertyGroup/OrganessoniumVersion')
    $buildNode = $xml.SelectSingleNode('/Project/PropertyGroup/OrganessoniumBuild')
    if ($null -eq $versionNode -or $null -eq $buildNode) {
        throw 'Version.props deve conter OrganessoniumVersion e OrganessoniumBuild em um PropertyGroup.'
    }

    $version = $versionNode.InnerText.Trim()
    $buildText = $buildNode.InnerText.Trim()
    $build = 0
    if (-not [int]::TryParse($buildText, [System.Globalization.NumberStyles]::None,
            [System.Globalization.CultureInfo]::InvariantCulture, [ref]$build)) {
        throw "OrganessoniumBuild inválido: '$buildText'."
    }

    Assert-OrganessoniumVersionValues -Version $version -Build $build

    return [pscustomobject]@{
        Version              = $version
        Build                = $build
        AssemblyVersion      = "$version.0"
        FileVersion          = "$version.$build"
        InformationalVersion = "$version+build.$build"
        PackageVersion       = "$version.0"
        Tag                  = "v$version"
        PropsPath            = $propsPath
    }
}

function Set-OrganessoniumVersionInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][int]$Build
    )

    Assert-OrganessoniumVersionValues -Version $Version -Build $Build
    $propsPath = Join-Path ([System.IO.Path]::GetFullPath($RepositoryRoot)) 'Version.props'
    if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) {
        throw "Version.props não encontrado em $propsPath."
    }

    $text = [System.IO.File]::ReadAllText($propsPath)
    $versionPattern = '(<OrganessoniumVersion>)[^<]*(</OrganessoniumVersion>)'
    $buildPattern = '(<OrganessoniumBuild>)[^<]*(</OrganessoniumBuild>)'
    if ($text -notmatch $versionPattern -or $text -notmatch $buildPattern) {
        throw 'Version.props não contém os elementos esperados.'
    }

    $versionReplacement = '${1}' + $Version + '${2}'
    $buildReplacement = '${1}' + $Build.ToString([System.Globalization.CultureInfo]::InvariantCulture) + '${2}'
    $text = [regex]::Replace($text, $versionPattern, $versionReplacement)
    $text = [regex]::Replace($text, $buildPattern, $buildReplacement)
    [System.IO.File]::WriteAllText($propsPath, $text, [System.Text.UTF8Encoding]::new($false))

    return Get-OrganessoniumVersionInfo -RepositoryRoot $RepositoryRoot
}

function Write-OrganessoniumVersionSummary {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Info
    )

    Write-Host ("Versão pública : {0}" -f $Info.Version)
    Write-Host ("Build          : {0}" -f $Info.Build)
    Write-Host ("AssemblyVersion: {0}" -f $Info.AssemblyVersion)
    Write-Host ("FileVersion    : {0}" -f $Info.FileVersion)
    Write-Host ("Informational  : {0}" -f $Info.InformationalVersion)
    Write-Host ("Pacote/MSIX    : {0}" -f $Info.PackageVersion)
    Write-Host ("Tag esperada   : {0}" -f $Info.Tag)
}

function Write-OrganessoniumGitHubOutput {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Info
    )

    $outputPath = $env:GITHUB_OUTPUT
    if ([string]::IsNullOrWhiteSpace($outputPath)) {
        throw 'GITHUB_OUTPUT não está definido. Use -GitHubOutput apenas dentro do GitHub Actions.'
    }

    $lines = @(
        "version=$($Info.Version)",
        "build=$($Info.Build)",
        "assembly_version=$($Info.AssemblyVersion)",
        "file_version=$($Info.FileVersion)",
        "informational_version=$($Info.InformationalVersion)",
        "package_version=$($Info.PackageVersion)",
        "tag=$($Info.Tag)"
    )
    [System.IO.File]::AppendAllText(
        $outputPath,
        (($lines -join "`n") + "`n"),
        [System.Text.UTF8Encoding]::new($false))
}

Export-ModuleMember -Function Assert-OrganessoniumVersionValues, Get-OrganessoniumVersionInfo,
    Set-OrganessoniumVersionInfo, Write-OrganessoniumVersionSummary, Write-OrganessoniumGitHubOutput
