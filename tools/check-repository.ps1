[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [switch]$SkipSecrets
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
}
$root = [System.IO.Path]::GetFullPath($RepositoryRoot)
$failures = [System.Collections.Generic.List[string]]::new()

function Add-Failure {
    param([string]$Message)
    $failures.Add($Message)
}

function Test-RequiredFile {
    param([string]$RelativePath)
    if (-not (Test-Path -LiteralPath (Join-Path $root $RelativePath) -PathType Leaf)) {
        Add-Failure "arquivo obrigatório ausente: $RelativePath"
    }
}

$requiredFiles = @(
    'Version.props',
    'Directory.Build.props',
    'global.json',
    'NuGet.config',
    '.gitignore',
    '.gitattributes',
    'README.md',
    'CHANGELOG.md',
    'git.md',
    'AGENTS.md',
    'CONTRIBUTING.md',
    'SECURITY.md',
    'build-releases.bat',
    'release/Build-Releases.ps1',
    'release/Get-MediaAssets.ps1',
    'release/Get-ReleaseNotes.ps1',
    'release/Test-ReleaseArtifacts.ps1',
    'release/Publish-Release.ps1',
    'tools/version.ps1',
    'tools/Versioning.psm1',
    'tools/run-tests.ps1',
    'tools/check-version-metadata.ps1',
    'tests/Organessonium.NetworkTests/Organessonium.NetworkTests.csproj',
    'Organessonium/Organessonium.slnx',
    'Organessonium/Organessonium/Organessonium.csproj',
    '.github/workflows/ci.yml',
    '.github/workflows/build.yml',
    '.github/workflows/release.yml',
    '.github/dependabot.yml'
)
foreach ($relativePath in $requiredFiles) { Test-RequiredFile -RelativePath $relativePath }

Import-Module (Join-Path $root 'tools\Versioning.psm1') -Force
try {
    $versionInfo = Get-OrganessoniumVersionInfo -RepositoryRoot $root
    Write-Host ("Versão validada: {0} (build {1})" -f $versionInfo.Version, $versionInfo.Build) -ForegroundColor Green
}
catch {
    Add-Failure $_.Exception.Message
}

if (Test-Path -LiteralPath (Join-Path $root 'release\release-settings.json')) {
    try {
        $settings = Get-Content -LiteralPath (Join-Path $root 'release\release-settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($settings.PSObject.Properties.Name -contains 'version') {
            Add-Failure 'release/release-settings.json ainda contém "version"; a versão deve vir apenas de Version.props.'
        }
    }
    catch {
        Add-Failure "release/release-settings.json inválido: $($_.Exception.Message)"
    }
}

$tracked = @(& git -C $root ls-files)
if ($LASTEXITCODE -ne 0) {
    Add-Failure 'git ls-files falhou.'
}
$forbiddenTracked = @($tracked | Where-Object {
    $_ -match '(^|/)(bin|obj|artifacts|release-work|Logs)(/|$)' -or
    $_ -match '(?i)(release-settings\.local\.json|\.(pfx|p12|pem|key|snk|db|sqlite|msi|msix|msixbundle|exe)$)' -or
    $_ -match '(^|/)runtime-package\.zip$'
})
if ($forbiddenTracked.Count -gt 0) {
    Add-Failure ("arquivos gerados/sensíveis rastreados: " + ($forbiddenTracked -join ', '))
}

$ignoreChecks = @(
    'Organessonium/Organessonium/media/runtime-package.zip',
    'Organessonium/Organessonium/media/sources/ffmpeg-corresponding-source.zip',
    'release/release-settings.local.json',
    'artifacts/ci/test.bin',
    'tests/TestResults/result.trx'
)
foreach ($path in $ignoreChecks) {
    & git -C $root check-ignore -q -- $path
    if ($LASTEXITCODE -ne 0) {
        Add-Failure "o .gitignore não cobre: $path"
    }
}

$workflowDirectory = Join-Path $root '.github\workflows'
foreach ($workflow in @('ci.yml', 'build.yml', 'release.yml')) {
    $workflowPath = Join-Path $workflowDirectory $workflow
    if (-not (Test-Path -LiteralPath $workflowPath)) { continue }
    $content = [System.IO.File]::ReadAllText($workflowPath)
    if ($content -match 'pull_request_target') {
        Add-Failure "$workflow usa pull_request_target, proibido para código de forks."
    }
    if ($content -notmatch '(?m)^permissions:\s*$') {
        Add-Failure "$workflow não declara permissões explícitas no nível do workflow."
    }
}
$ciPath = Join-Path $workflowDirectory 'ci.yml'
if (Test-Path -LiteralPath $ciPath) {
    $ci = [System.IO.File]::ReadAllText($ciPath)
    if ($ci -match '\$\{\{\s*secrets\.') {
        Add-Failure 'ci.yml não deve consumir secrets; pull requests de forks não recebem segredos.'
    }
}
$releasePath = Join-Path $workflowDirectory 'release.yml'
if (Test-Path -LiteralPath $releasePath) {
    $release = [System.IO.File]::ReadAllText($releasePath)
    if ($release -notmatch '(?m)^\s+environment:\s*release\s*$') {
        Add-Failure 'release.yml deve usar o ambiente protegido "release".'
    }
    if ($release -notmatch 'workflow_dispatch') {
        Add-Failure 'release.yml deve ser acionado apenas por workflow_dispatch.'
    }
}

$changelogPath = Join-Path $root 'CHANGELOG.md'
if (Test-Path -LiteralPath $changelogPath) {
    $changelog = [System.IO.File]::ReadAllText($changelogPath)
    if ($changelog -notmatch '(?m)^## \[[^\]]+\]') {
        Add-Failure 'CHANGELOG.md deve conter pelo menos uma seção "## [versão]".'
    }
}

if (-not $SkipSecrets) {
    try {
        & (Join-Path $root 'tools\check-secrets.ps1') -RepositoryRoot $root
        if ($LASTEXITCODE -ne 0) {
            Add-Failure 'a varredura de segredos encontrou ocorrências.'
        }
    }
    catch {
        Add-Failure "a varredura de segredos falhou: $($_.Exception.Message)"
    }
}

if ($failures.Count -gt 0) {
    Write-Host "`nVerificação do repositório falhou:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host " - $_" -ForegroundColor Red }
    exit 1
}

Write-Host "`nVerificação do repositório concluída sem falhas." -ForegroundColor Green
exit 0
