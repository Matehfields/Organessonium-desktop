[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$NotesFile,
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [string]$Title,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [switch]$IncludeStorePackages
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$directory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
Import-Module (Join-Path $repoRoot 'tools\Versioning.psm1') -Force
$info = Get-OrganessoniumVersionInfo -RepositoryRoot $repoRoot

if ($Tag -ne $info.Tag) {
    throw "A tag '$Tag' não corresponde à versão de Version.props ($($info.Tag))."
}
if ([string]::IsNullOrWhiteSpace($Repository)) {
    throw 'Informe -Repository ou defina GITHUB_REPOSITORY.'
}
if (-not (Test-Path -LiteralPath $NotesFile -PathType Leaf)) {
    throw "Arquivo de notas não encontrado: $NotesFile"
}
if ([string]::IsNullOrWhiteSpace($Title)) {
    $Title = "Organessônium $($info.Version) (build $($info.Build))"
}

$gh = Get-Command gh -ErrorAction SilentlyContinue
if ($null -eq $gh) {
    throw 'GitHub CLI (gh) não encontrado. Instale-o para publicar releases.'
}
& gh auth status 2>$null
if ($LASTEXITCODE -ne 0) {
    throw 'GitHub CLI não está autenticado. Execute "gh auth login" ou defina GH_TOKEN.'
}

$buildInfoPath = Join-Path $directory 'build-info.json'
$buildInfo = Get-Content -LiteralPath $buildInfoPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([bool]$buildInfo.dirty) {
    throw 'Os artefatos foram gerados com a árvore de trabalho suja (build-info.json dirty=true). Recompile a partir de um checkout limpo.'
}
$tagCommit = (& git -C $repoRoot rev-parse "$Tag^{commit}" 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($tagCommit)) {
    throw "A tag '$Tag' não existe no repositório local. Crie e envie a tag antes de publicar."
}
$tagCommit = $tagCommit.Trim()
if ([string]$buildInfo.commit -ne $tagCommit) {
    throw "Os artefatos foram gerados do commit $($buildInfo.commit), mas a tag $Tag aponta para $tagCommit."
}

& (Join-Path $PSScriptRoot 'Test-ReleaseArtifacts.ps1') -ArtifactDirectory $directory -RepositoryRoot $repoRoot
if ($LASTEXITCODE -ne 0) {
    throw 'A validação dos artefatos falhou; a release não será publicada.'
}

$selected = @(
    'Organessonium-installer-x86.msi',
    'Organessonium-installer-x64.msi',
    'Organessonium-installer-arm64.msi',
    'Organessonium-x86.exe',
    'Organessonium-x64.exe',
    'Organessonium-arm64.exe',
    'Organessonium-windows-source.zip',
    'ffmpeg-corresponding-source.zip',
    'build-info.json'
)
if ($IncludeStorePackages) {
    $selected += ('Organessonium_{0}.msixbundle' -f $info.PackageVersion)
    $selected += ('msix/Organessonium_{0}_x86.msix' -f $info.PackageVersion)
    $selected += ('msix/Organessonium_{0}_x64.msix' -f $info.PackageVersion)
    $selected += ('msix/Organessonium_{0}_arm64.msix' -f $info.PackageVersion)
}

$stage = Join-Path $directory 'github-release'
if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
$null = New-Item -ItemType Directory -Force -Path $stage
$stagedFiles = @()
foreach ($relativePath in $selected) {
    $source = Join-Path $directory $relativePath.Replace('/', '\')
    $destination = Join-Path $stage ([System.IO.Path]::GetFileName($relativePath))
    Copy-Item -LiteralPath $source -Destination $destination -Force
    $stagedFiles += $destination
}

$hashLines = foreach ($file in $stagedFiles) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(), [System.IO.Path]::GetFileName($file)
}
$releaseSums = Join-Path $stage 'SHA256SUMS.txt'
[System.IO.File]::WriteAllLines($releaseSums, $hashLines, [System.Text.UTF8Encoding]::new($false))
$stagedFiles += $releaseSums

$releaseJson = & gh release view $Tag --repo $Repository --json isDraft,assets,tagName 2>$null
$existingDraft = $false
if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($releaseJson)) {
    $release = $releaseJson | ConvertFrom-Json
    if ([bool]$release.isDraft) {
        $existingDraft = $true
        Write-Host "Retomando rascunho existente da release $Tag." -ForegroundColor Yellow
    }
    else {
        throw "A release $Tag já está publicada. Não sobrescreva uma versão publicada; crie uma nova versão pública."
    }
}

if (-not $existingDraft) {
    & gh release create $Tag --repo $Repository --draft --verify-tag --title $Title --notes-file $NotesFile
    if ($LASTEXITCODE -ne 0) {
        throw "Não foi possível criar o rascunho da release $Tag. Verifique se a tag remota existe."
    }
}
else {
    & gh release edit $Tag --repo $Repository --title $Title --notes-file $NotesFile
    if ($LASTEXITCODE -ne 0) {
        throw "Não foi possível atualizar o rascunho da release $Tag."
    }
}

$uploadArguments = @('release', 'upload', $Tag) + $stagedFiles + @('--repo', $Repository, '--clobber')
& gh @uploadArguments
if ($LASTEXITCODE -ne 0) {
    throw "O envio dos artefatos falhou. O rascunho da release $Tag foi preservado para recuperação."
}

$publishedJson = & gh release view $Tag --repo $Repository --json assets
if ($LASTEXITCODE -ne 0) {
    throw 'Não foi possível conferir os artefatos enviados. O rascunho foi preservado.'
}
$publishedAssets = @(($publishedJson | ConvertFrom-Json).assets)
foreach ($file in $stagedFiles) {
    $name = [System.IO.Path]::GetFileName($file)
    $asset = $publishedAssets | Where-Object { $_.name -eq $name } | Select-Object -First 1
    if ($null -eq $asset) {
        throw "O artefato $name não aparece na release. O rascunho foi preservado."
    }
    if ([long]$asset.size -ne (Get-Item -LiteralPath $file).Length) {
        throw "O tamanho de $name na release não corresponde ao arquivo local. O rascunho foi preservado."
    }
}

& gh release edit $Tag --repo $Repository --draft=false --latest
if ($LASTEXITCODE -ne 0) {
    throw "A publicação final falhou. A release $Tag permanece como rascunho e pode ser retomada com este script."
}

Write-Host "`nRelease $Tag publicada com $($stagedFiles.Count) artefato(s)." -ForegroundColor Green
Write-Host 'Confira a página de releases para validar notas, tamanhos e checksums.'
