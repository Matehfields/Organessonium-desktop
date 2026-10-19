[CmdletBinding()]
param([string]$MediaDirectory = (Join-Path $PSScriptRoot '..\Organessonium\Organessonium\media'))

$ErrorActionPreference = 'Stop'
$media = [IO.Path]::GetFullPath($MediaDirectory)
$work = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\media-build'))
$fixture = Join-Path $work ('gate-tests-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Force -Path $fixture, (Join-Path $fixture 'sources')
$manifestText = [IO.File]::ReadAllText((Join-Path $media 'source-manifest.json'))
try {
    foreach ($name in @('GPL-3.0.txt', 'BtbN-LICENSE.txt', 'NOTICE.md', 'THIRD-PARTY-NOTICES.txt')) {
        Copy-Item -LiteralPath (Join-Path $media $name) -Destination $fixture
    }
    # Hard links avoid copying gigabytes; only the fixture manifest is mutated.
    $null = New-Item -ItemType HardLink -Path (Join-Path $fixture 'runtime-package.zip') -Target (Join-Path $media 'runtime-package.zip')
    $null = New-Item -ItemType HardLink -Path (Join-Path $fixture 'sources\ffmpeg-corresponding-source.zip') -Target (Join-Path $media 'sources\ffmpeg-corresponding-source.zip')
    function Check([string]$Name, [bool]$Accepted, [scriptblock]$Change) {
        $manifest = $manifestText | ConvertFrom-Json
        & $Change $manifest
        [IO.File]::WriteAllText((Join-Path $fixture 'source-manifest.json'), ($manifest | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
        $start = [Diagnostics.ProcessStartInfo]::new('powershell.exe')
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $media 'Verify-Sources.ps1') + '" -ForRedistribution -MediaDirectory "' + $fixture + '"'
        $process = [Diagnostics.Process]::Start($start)
        try {
            $output = $process.StandardOutput.ReadToEndAsync()
            $errors = $process.StandardError.ReadToEndAsync()
            $process.WaitForExit()
            $null = $output.GetAwaiter().GetResult(); $null = $errors.GetAwaiter().GetResult()
            if (($process.ExitCode -eq 0) -ne $Accepted) { throw "Publication gate regression: $Name" }
            Write-Output "PASS $Name"
        }
        finally { $process.Dispose() }
    }
    Check 'complete package without external ffmpeg.exe' $true { param($m) }
    Check 'incomplete source review is blocked' $false { param($m) $m.redistributionReady = $false }
    Check 'wrong corresponding source digest is blocked' $false { param($m) $m.correspondingSourceArchive.sha256 = '0' * 64 }
    Check 'wrong embedded runtime digest is blocked' $false { param($m) $m.runtimeArchive.sha256 = '0' * 64 }
    Check 'uninventoried components are blocked' $false { param($m) $m.runtimeFiles = @($m.runtimeFiles | Select-Object -Skip 1) }
}
finally {
    $full = [IO.Path]::GetFullPath($fixture)
    if ($full.StartsWith($work + '\gate-tests-', [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
