#Requires -Version 7.0
# A large neutral file with a real-shaped private wordlist, all under TEMP.
# CPU time makes this independent of other applications using the machine.
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('hz-scan-perf-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
$termsPath = Join-Path $root 'private-terms.txt'
$fixtureRoot = Join-Path $root 'fixture'
$null = New-Item -ItemType Directory -Path $fixtureRoot
$terms = 0..63 | ForEach-Object { 'fixtureterm' + $_.ToString('00') }
[IO.File]::WriteAllLines($termsPath, [string[]]$terms)
$lines = [Collections.Generic.List[string]]::new()
1..12000 | ForEach-Object { $lines.Add('neutral coordination record with no private identity') }
$lines.Add('FIXTURETERM00 fixtureterm63 fixtureterm00')
[IO.File]::WriteAllLines((Join-Path $fixtureRoot 'payload.txt'), $lines)
$json = Join-Path $root 'result.json'
$psi = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
foreach ($arg in @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'scan-sensitive.ps1'),
    '-Root', $fixtureRoot, '-AllFiles', '-TermsFile', $termsPath, '-RequireTerms', '-Json', $json)) {
    $psi.ArgumentList.Add($arg)
}
$process = [Diagnostics.Process]::Start($psi)
$out = $process.StandardOutput.ReadToEndAsync()
$err = $process.StandardError.ReadToEndAsync()
$process.WaitForExit()
$null = $out.GetAwaiter().GetResult()
$errors = $err.GetAwaiter().GetResult()
$cpu = $process.TotalProcessorTime.TotalSeconds
if ($process.ExitCode -ne 1) { throw "Expected private leaks (exit 1), got $($process.ExitCode): $errors" }
$result = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
$findings = @($result.findings | Where-Object rule -EQ 'sensitive-term')
if ($findings.Count -ne 2 -or @($findings | Where-Object line -NE 12001).Count) {
    throw 'Every distinct matching term must still be reported once at its original line.'
}
Write-Host "scan CPU: $cpu seconds; both terms detected at line 12001."
if ($cpu -gt 6) { throw 'Wordlist scan exceeded 6 CPU seconds for 12k lines / 64 terms.' }
Write-Host 'PASS: large neutral payload stays fast without dropping any private-term finding.'
