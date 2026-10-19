[CmdletBinding()]
param([string]$WorkDirectory = (Join-Path $PSScriptRoot '..\..\..\..\artifacts\media-build'))

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Use PowerShell 7 (pwsh).' }
$prepare = Join-Path $PSScriptRoot 'Prepare-Environment.ps1'
# The MSYS2 runtime upgrade requires a new shell before the second pass.
& $prepare -WorkDirectory $WorkDirectory -Command 'pacman -Syu --noconfirm'
& $prepare -WorkDirectory $WorkDirectory -Command 'pacman -Syu --noconfirm'
$native = @('gcc', 'ffmpeg', 'cmake', 'ninja', 'nasm', 'rust', 'meson', 'autotools', 'pkgconf',
    'aribb24', 'libaribcaption', 'openh264', 'kvazaar', 'chromaprint', 'libdvdnav', 'libdvdread',
    'libopenmpt', 'lv2', 'lilv', 'rubberband', 'snappy', 'twolame', 'vmaf', 'zeromq', 'librist',
    'amf-headers', 'ffnvcodec-headers', 'vulkan-headers', 'opencl-headers', 'clang')
$packages = @('base-devel', 'git') + @($native | ForEach-Object { 'mingw-w64-ucrt-x86_64-' + $_ })
& $prepare -WorkDirectory $WorkDirectory -Command ('pacman -S --needed --noconfirm ' + ($packages -join ' '))
& $prepare -WorkDirectory $WorkDirectory -Command 'pacman -Q > toolchain-packages.txt'
Write-Output 'Toolchain ready. A new dependency revision still needs source collection and review before redistribution.'
