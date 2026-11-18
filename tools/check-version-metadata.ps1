[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
}
$root = [System.IO.Path]::GetFullPath($RepositoryRoot)
Import-Module (Join-Path $root 'tools\Versioning.psm1') -Force
$info = Get-OrganessoniumVersionInfo -RepositoryRoot $root
$fullPath = [System.IO.Path]::GetFullPath($Path)
if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
    throw "Executável não encontrado: $fullPath"
}

$fileInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($fullPath)
$failures = [System.Collections.Generic.List[string]]::new()

if ($fileInfo.FileVersion -ne $info.FileVersion) {
    $failures.Add("FileVersion esperado $($info.FileVersion), encontrado $($fileInfo.FileVersion).")
}
$productVersion = [string]$fileInfo.ProductVersion
if (-not $productVersion.StartsWith($info.Version, [System.StringComparison]::Ordinal)) {
    $failures.Add("ProductVersion não começa com $($info.Version): $productVersion.")
}
if ($productVersion -notmatch [regex]::Escape("build.$($info.Build)")) {
    $failures.Add("ProductVersion não contém build.$($info.Build): $productVersion.")
}
if ($fileInfo.FileMajorPart -ne [int]$info.Version.Split('.')[0] -or
    $fileInfo.FileMinorPart -ne [int]$info.Version.Split('.')[1] -or
    $fileInfo.FileBuildPart -ne [int]$info.Version.Split('.')[2] -or
    $fileInfo.FilePrivatePart -ne $info.Build) {
    $failures.Add("Componentes numéricos do FileVersion não correspondem a $($info.FileVersion).")
}

if ($failures.Count -gt 0) {
    Write-Host "Metadados de versão incorretos em ${fullPath}:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host " - $_" -ForegroundColor Red }
    exit 1
}

Write-Host ("Metadados OK: FileVersion {0}; ProductVersion {1}" -f $fileInfo.FileVersion, $fileInfo.ProductVersion) -ForegroundColor Green
exit 0
