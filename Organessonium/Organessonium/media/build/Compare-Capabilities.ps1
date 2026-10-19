[CmdletBinding()]
param([Parameter(Mandatory)][string]$Baseline, [Parameter(Mandatory)][string]$Candidate,
    [Parameter(Mandatory)][string]$Report)

$ErrorActionPreference = 'Stop'
function Read-Capabilities([string]$Executable, [string]$Kind) {
    $start = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($Executable))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Arguments = "-hide_banner -$Kind"
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Cannot read media capabilities: $Kind" }
        $null = $errors.GetAwaiter().GetResult()
        $pattern = switch ($Kind) {
            'decoders' { '^\s*[A-Z.]{6}\s+([a-zA-Z0-9_,]+)\s+' }
            'encoders' { '^\s*[A-Z.]{6}\s+([a-zA-Z0-9_,]+)\s+' }
            'filters' { '^\s*[A-Z.]{2,3}\s+([a-zA-Z0-9_,]+)\s+' }
            default { '^\s*[DE]{1,2}\s+([a-zA-Z0-9_,]+)\s+' }
        }
        return @([regex]::Matches($output.GetAwaiter().GetResult(), '(?m)' + $pattern) |
            ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    }
    finally { $process.Dispose() }
}
$checks = foreach ($kind in @('decoders', 'encoders', 'demuxers', 'muxers', 'filters')) {
    $before = @(Read-Capabilities $Baseline $kind)
    $after = @(Read-Capabilities $Candidate $kind)
    if ($before.Count -lt 100 -or $after.Count -lt 100) { throw "Incomplete capability inventory: $kind" }
    [ordered]@{ kind = $kind; baseline = $before.Count; candidate = $after.Count;
        missing = @($before | Where-Object { $_ -notin $after }) }
}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Report), ($checks | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
foreach ($check in $checks) {
    if ($check.missing.Count -gt 0) { throw "Media capabilities missing ($($check.kind)): $($check.missing -join ', ')" }
    Write-Output "$($check.kind): retained $($check.baseline), candidate $($check.candidate)."
}
