[CmdletBinding()]
param(
    [string]$WorkDirectory = (Join-Path $PSScriptRoot '..\..\..\..\artifacts\media-build'),
    [string]$Command = 'printf "Isolated MSYS2 ready: "; uname -m; pacman --version | head -n 2'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$archiveName = 'msys2-base-x86_64-20260927.tar.xz'
$expectedHash = 'ea2f31a0b6ade63914ce441ffb022f0f6aa96982bfefa2326460a26d5fb01322'
$work = [IO.Path]::GetFullPath($WorkDirectory)
$null = New-Item -ItemType Directory -Force -Path $work
$archive = Join-Path $work $archiveName
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
    Invoke-WebRequest -UseBasicParsing -Uri "https://repo.msys2.org/distrib/x86_64/$archiveName" -OutFile "$archive.download"
    if ((Get-FileHash -LiteralPath "$archive.download" -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
        throw 'O SHA-256 do ambiente MSYS2 não corresponde à revisão fixada.'
    }
    Move-Item -LiteralPath "$archive.download" -Destination $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
    throw 'O cache do ambiente MSYS2 não corresponde à revisão fixada.'
}
$bash = Join-Path $work 'msys64\usr\bin\bash.exe'
if (-not (Test-Path -LiteralPath $bash -PathType Leaf)) {
    & tar.exe -xf $archive -C $work
    if ($LASTEXITCODE -ne 0) { throw 'Não foi possível extrair o ambiente MSYS2.' }
}
# No installer, registry, global PATH, WSL, service or scheduled task is used.
$start = [Diagnostics.ProcessStartInfo]::new($bash)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.RedirectStandardInput = $true
$start.WorkingDirectory = $work
$start.Environment['MSYSTEM'] = 'UCRT64'
$start.Environment['CHERE_INVOKING'] = '1'
$start.Environment['MSYS2_PATH_TYPE'] = 'minimal'
$start.ArgumentList.Add('--login')
$start.ArgumentList.Add('-c')
$start.ArgumentList.Add($Command)
$process = [Diagnostics.Process]::Start($start)
$process.StandardInput.Close()
$errors = $process.StandardError.ReadToEndAsync()
while (($line = $process.StandardOutput.ReadLine()) -ne $null) { Write-Output $line }
$process.WaitForExit()
$buildErrors = $errors.GetAwaiter().GetResult()
[IO.File]::WriteAllText((Join-Path $work 'last-command-stderr.log'), $buildErrors)
Write-Output (($buildErrors -split "`n" | Select-Object -Last 60) -join "`n")
if ($process.ExitCode -ne 0) { throw ('O comando no ambiente MSYS2 falhou (0x{0:X8}).' -f ($process.ExitCode -band 0xffffffffL)) }
$process.Dispose()
Write-Output "Ambiente isolado: $work"
