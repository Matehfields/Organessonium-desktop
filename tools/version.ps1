[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory = $true)]
    [ValidateSet('show', 'validate', 'bump-build', 'set-version')]
    [string]$Command,

    [string]$Version,

    # Build lida no início da rodada. Se outro agente já incrementou,
    # o comando falha em vez de incrementar duas vezes a mesma rodada.
    [int]$ExpectedBuild = -1,

    # Alterar a versão pública exige autorização explícita do mantenedor.
    [switch]$Authorized,

    [switch]$GitHubOutput
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Import-Module (Join-Path $PSScriptRoot 'Versioning.psm1') -Force

function Assert-LocalOnlyCommand {
    param([string]$Name)
    if ($env:GITHUB_ACTIONS -eq 'true') {
        throw "O comando '$Name' não pode ser executado dentro do GitHub Actions. A build é incrementada uma vez por rodada, antes do commit, fora da CI."
    }
}

switch ($Command) {
    'show' {
        $info = Get-OrganessoniumVersionInfo -RepositoryRoot $repoRoot
        Write-OrganessoniumVersionSummary -Info $info
        if ($GitHubOutput) { Write-OrganessoniumGitHubOutput -Info $info }
    }

    'validate' {
        $info = Get-OrganessoniumVersionInfo -RepositoryRoot $repoRoot
        Write-OrganessoniumVersionSummary -Info $info
        if ($GitHubOutput) { Write-OrganessoniumGitHubOutput -Info $info }
    }

    'bump-build' {
        Assert-LocalOnlyCommand -Name 'bump-build'
        $info = Get-OrganessoniumVersionInfo -RepositoryRoot $repoRoot
        if ($ExpectedBuild -ge 0 -and $info.Build -ne $ExpectedBuild) {
            throw "A build atual é $($info.Build), mas -ExpectedBuild informou $ExpectedBuild. Outro agente pode ter concluído uma rodada; revise antes de incrementar."
        }
        if ($info.Build -ge 65535) {
            throw 'Limite de build atingido (65535). Uma nova versão pública é necessária.'
        }
        $updated = Set-OrganessoniumVersionInfo -RepositoryRoot $repoRoot -Version $info.Version -Build ($info.Build + 1)
        Write-Host "Build incrementada: $($info.Build) -> $($updated.Build)." -ForegroundColor Green
        Write-OrganessoniumVersionSummary -Info $updated
    }

    'set-version' {
        Assert-LocalOnlyCommand -Name 'set-version'
        if (-not $Authorized) {
            throw 'Alterar a versão pública requer autorização explícita. Repita com -Authorized somente após o mantenedor aprovar a mudança.'
        }
        if ([string]::IsNullOrWhiteSpace($Version)) {
            throw 'Informe a nova versão com -Version MAJOR.MINOR.PATCH.'
        }
        $info = Get-OrganessoniumVersionInfo -RepositoryRoot $repoRoot
        if ($info.Build -ge 65535) {
            throw 'Limite de build atingido (65535).'
        }
        $updated = Set-OrganessoniumVersionInfo -RepositoryRoot $repoRoot -Version $Version.Trim() -Build ($info.Build + 1)
        Write-Host "Versão pública alterada: $($info.Version) -> $($updated.Version); build $($info.Build) -> $($updated.Build)." -ForegroundColor Green
        Write-OrganessoniumVersionSummary -Info $updated
    }
}
