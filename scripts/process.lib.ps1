#Requires -Version 5.1
<#
  Starting a child process the same way under Windows PowerShell 5.1 and PowerShell 7.

  WHY THIS EXISTS. ProcessStartInfo.ArgumentList does not exist in Windows
  PowerShell 5.1 (.NET Framework), and the helpers that used it failed there. The
  portable interface is ProcessStartInfo.Arguments: ONE string that the child's C
  runtime splits back into argv. Getting that string right is a quoting problem
  with exact rules, so it is solved once, here, and tested.

  And a probe must keep what it learned: the exit code, stdout and stderr apart,
  and whether it timed out. A diagnosis that concatenates an error message into
  "help text" and forgets the exit code will read `unknown command "init"` as the
  help of a working client.
#>

function ConvertTo-HorizunWindowsArgument {
    <#
      One argv element quoted by the rules CommandLineToArgvW and the MSVC runtime
      apply when they split a command line: backslashes are literal unless they
      precede a double quote, where each pair becomes one and an odd one escapes
      the quote. Round-trips any string, including empty ones and ones that end
      in a backslash.
    #>
    param([AllowEmptyString()][string]$Value)
    if ($null -eq $Value) { $Value = '' }
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    $slashes = 0
    foreach ($ch in $Value.ToCharArray()) {
        if ($ch -eq [char]92) { $slashes++; continue }
        if ($ch -eq [char]34) {
            [void]$sb.Append([string]::new([char]92, $slashes * 2 + 1))
            [void]$sb.Append([char]34)
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) { [void]$sb.Append([string]::new([char]92, $slashes)); $slashes = 0 }
        [void]$sb.Append($ch)
    }
    if ($slashes -gt 0) { [void]$sb.Append([string]::new([char]92, $slashes * 2)) }
    [void]$sb.Append('"')
    return $sb.ToString()
}

function Join-HorizunWindowsArguments {
    param([string[]]$Arguments)
    if (-not $Arguments) { return '' }
    return (($Arguments | ForEach-Object { ConvertTo-HorizunWindowsArgument $_ }) -join ' ')
}

function Stop-HorizunChildProcess {
    <# Kill, tolerating a process that has already gone. Kill(bool) is .NET Core only. #>
    param($Process)
    if ($null -eq $Process) { return }
    try { if (-not $Process.HasExited) { $Process.Kill() } } catch { }
    try { [void]$Process.WaitForExit(5000) } catch { }
}

function Invoke-HorizunProcess {
    <#
      Run a program to completion with stdin CLOSED and a hard deadline, and keep
      every fact apart:

        started    whether Windows started it at all (a blocked or missing file,
                   an AppLocker/SmartScreen refusal, a bad image, all say false)
        exit_code  $null when it never started or had to be killed
        timed_out  it was still running at the deadline and was killed
        stdout / stderr, separately
        error      the exception text when it did not start

      A secret passed through -Environment is never part of the command line.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string[]]$Arguments = @(),
        [int]$TimeoutSec = 15,
        [hashtable]$Environment,
        [string]$WorkingDirectory
    )
    $result = [ordered]@{
        path       = $Path
        arguments  = @($Arguments)
        started    = $false
        exit_code  = $null
        timed_out  = $false
        stdout     = ''
        stderr     = ''
        error      = $null
        output_complete = $false
        elapsed_ms = 0
    }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Path
    $psi.Arguments = Join-HorizunWindowsArguments $Arguments
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $psi.StandardOutputEncoding = $utf8
    $psi.StandardErrorEncoding = $utf8
    if ($WorkingDirectory) { $psi.WorkingDirectory = $WorkingDirectory }
    if ($Environment) { foreach ($k in $Environment.Keys) { $psi.EnvironmentVariables[[string]$k] = [string]$Environment[$k] } }

    $clock = [Diagnostics.Stopwatch]::StartNew()
    $p = $null
    try { $p = [Diagnostics.Process]::Start($psi) }
    catch {
        $result.error = $_.Exception.Message
        $result.elapsed_ms = [int]$clock.ElapsedMilliseconds
        return [pscustomobject]$result
    }
    $result.started = $true
    try {
        try { $p.StandardInput.Close() } catch { }
        $out = $p.StandardOutput.ReadToEndAsync()
        $err = $p.StandardError.ReadToEndAsync()
        if (-not $p.WaitForExit([Math]::Max(1, $TimeoutSec) * 1000)) {
            $result.timed_out = $true
            Stop-HorizunChildProcess $p
        }
        else {
            $p.WaitForExit()                      # flush the async readers
            $result.exit_code = $p.ExitCode
        }
        # Both streams reach end-of-file when the process AND everything that
        # inherited its handles has let go. A child that kept them open would hold
        # a caller that reads to the end for ever; this bounded wait reports it.
        $outDone = $out.Wait(3000); $errDone = $err.Wait(3000)
        $result.output_complete = ($outDone -and $errDone)
        if ($outDone) { $result.stdout = $out.Result }
        if ($errDone) { $result.stderr = $err.Result }
    }
    finally { Stop-HorizunChildProcess $p }
    $result.elapsed_ms = [int]$clock.ElapsedMilliseconds
    return [pscustomobject]$result
}
