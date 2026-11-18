[CmdletBinding()]
param(
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$Tag = 'media-assets-v1',
    [string]$MediaDirectory,
    [string]$Token = $(if ($env:GH_TOKEN) { $env:GH_TOKEN } elseif ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } else { '' })
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($MediaDirectory)) {
    $MediaDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'Organessonium\Organessonium\media'
}
$media = [System.IO.Path]::GetFullPath($MediaDirectory)
$manifestPath = Join-Path $media 'source-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Manifesto de mídia não encontrado: $manifestPath"
}
if ([string]::IsNullOrWhiteSpace($Repository)) {
    throw 'Informe -Repository no formato proprietario/repositorio ou defina GITHUB_REPOSITORY.'
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$assets = @(
    [pscustomobject]@{
        Name = 'runtime-package.zip'
        Sha256 = [string]$manifest.runtimeArchive.sha256
        Destination = Join-Path $media 'runtime-package.zip'
    },
    [pscustomobject]@{
        Name = 'ffmpeg-corresponding-source.zip'
        Sha256 = [string]$manifest.correspondingSourceArchive.sha256
        Destination = Join-Path $media 'sources\ffmpeg-corresponding-source.zip'
    }
)
foreach ($asset in $assets) {
    if ([string]::IsNullOrWhiteSpace($asset.Sha256)) {
        throw "O manifesto não define o SHA-256 de $($asset.Name)."
    }
}

function Get-Sha256Hex {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$pending = @()
foreach ($asset in $assets) {
    if (Test-Path -LiteralPath $asset.Destination -PathType Leaf) {
        $existing = Get-Sha256Hex -Path $asset.Destination
        if ($existing -eq $asset.Sha256) {
            Write-Host "Já presente e verificado: $($asset.Name)" -ForegroundColor Green
            continue
        }
        Write-Warning "O arquivo local $($asset.Name) não corresponde ao manifesto; ele será baixado novamente."
    }
    $pending += $asset
}

$tempDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('organessonium-media-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Force -Path $tempDirectory
try {
    foreach ($asset in $pending) {
        Write-Host "Baixando $($asset.Name) da release $Tag..." -ForegroundColor Cyan
        $downloaded = Join-Path $tempDirectory $asset.Name
        $gh = Get-Command gh -ErrorAction SilentlyContinue
        if ($null -ne $gh) {
            & $gh.Source release download $Tag --repo $Repository --dir $tempDirectory --pattern $asset.Name --clobber
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $downloaded -PathType Leaf)) {
                throw "Não foi possível baixar $($asset.Name) da release $Tag. Verifique se a release de mídia existe e se a autenticação do GitHub está disponível."
            }
        }
        else {
            $url = "https://github.com/$Repository/releases/download/$Tag/$($asset.Name)"
            $headers = @{}
            if (-not [string]::IsNullOrWhiteSpace($Token)) {
                $headers['Authorization'] = "Bearer $Token"
            }
            try {
                Invoke-WebRequest -Uri $url -OutFile $downloaded -Headers $headers -UseBasicParsing
            }
            catch {
                throw "Falha ao baixar $url. Instale o GitHub CLI (gh) ou configure GH_TOKEN para repositórios privados. Detalhe: $($_.Exception.Message)"
            }
        }

        $actual = Get-Sha256Hex -Path $downloaded
        if ($actual -ne $asset.Sha256) {
            throw "SHA-256 divergente para $($asset.Name). Esperado: $($asset.Sha256). Encontrado: $actual."
        }

        $parent = Split-Path -Parent $asset.Destination
        $null = New-Item -ItemType Directory -Force -Path $parent
        Move-Item -LiteralPath $downloaded -Destination $asset.Destination -Force
        Write-Host "Verificado e instalado: $($asset.Destination)" -ForegroundColor Green
    }
}
finally {
    if (Test-Path -LiteralPath $tempDirectory) {
        Remove-Item -LiteralPath $tempDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

& (Join-Path $media 'Verify-Sources.ps1') -ForRedistribution
if ($LASTEXITCODE -ne 0) {
    throw 'A verificação de fontes de mídia falhou após o download.'
}
Write-Host 'Mídia pronta para a compilação de distribuição.' -ForegroundColor Green
