#Requires -Version 5.1
<#
  The ChatGPT side: the credential, the diagnosis, and what each refusal says.

  The credential store is exercised for the four states that actually happen on a
  real machine - stored, revoked, CORRUPTED, and belonging to another Windows
  account - because the third and fourth are the ones a naive implementation gets
  wrong by reporting "not configured" and sending the user off to store a key it
  already has.

  Then the whole helper, run under the SAME PowerShell as this file (so running
  it under Windows PowerShell 5.1 tests the helper under 5.1): a compiled
  stand-in for tunnel-client that reproduces the official 0.0.14 behaviour -
  variants, --version, the two command-line layers, loopback /readyz and
  /metrics - drives init, doctor, start, status, stop and revoke.

  Nothing here needs the real tunnel-client, the installed server, an API key,
  ChatGPT or a network. The official binary is exercised separately; a real call
  from ChatGPT is exactly what this file refuses to claim.
#>
$ErrorActionPreference = 'Stop'

$failed = 0
function Assert($name, $condition, $detail) {
    if ($condition) { Write-Host "  PASS  $name" -ForegroundColor Green }
    else {
        Write-Host "  FAIL  $name" -ForegroundColor Red
        if ($detail) { Write-Host "        $detail" }
        $script:failed++
    }
}

. (Join-Path $PSScriptRoot 'chatgpt-secret.lib.ps1')
. (Join-Path $PSScriptRoot 'mcp-clients.lib.ps1')
. (Join-Path $PSScriptRoot 'chatgpt-tunnel.lib.ps1')

$root = Join-Path ([IO.Path]::GetTempPath()) ('hz-chatgpt-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$tunnelScript = Join-Path $PSScriptRoot 'chatgpt-tunnel.ps1'

try {
    # ======================================================================
    Write-Host ""
    Write-Host "The credential: four states, not two" -ForegroundColor Cyan

    $store = Join-Path $root 'store'
    $secret = 'sk-' + [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')

    Assert 'absent reads as absent' (-not (Test-HorizunChatGptSecret -StateRoot $store)) $null
    Assert 'and reading an absent credential returns null rather than throwing' `
        ($null -eq (Get-HorizunChatGptSecret -StateRoot $store)) $null

    Set-HorizunChatGptSecret -StateRoot $store -Secret $secret | Out-Null
    $file = Get-HorizunChatGptSecretPath -StateRoot $store
    Assert 'stored reads as stored' (Test-HorizunChatGptSecret -StateRoot $store) $null
    Assert 'it round-trips byte for byte' ((Get-HorizunChatGptSecret -StateRoot $store) -ceq $secret) $null

    $raw = [IO.File]::ReadAllBytes($file)
    $asText = [Text.Encoding]::UTF8.GetString($raw)
    $asAscii = [Text.Encoding]::ASCII.GetString($raw)
    Assert 'the ciphertext contains the key in NO encoding a grep would find' `
        (-not $asText.Contains($secret) -and -not $asAscii.Contains($secret)) $null
    Assert 'DPAPI actually expanded it - a plaintext file would be the same length' `
        ($raw.Length -gt $secret.Length) "$($raw.Length) bytes for a $($secret.Length)-character key"

    # ---- CORRUPTION. The distinction that matters: unreadable is not absent.
    $corrupt = Join-Path $root 'corrupt'
    New-Item -ItemType Directory -Path $corrupt -Force | Out-Null
    $corruptFile = Get-HorizunChatGptSecretPath -StateRoot $corrupt
    [IO.File]::WriteAllBytes($corruptFile, [byte[]](1..64))
    Assert 'a corrupted credential still reads as PRESENT' `
        (Test-HorizunChatGptSecret -StateRoot $corrupt) 'it reported absent, and the user would be told to store a key they already have'
    $threw = $false; $message = $null
    try { Get-HorizunChatGptSecret -StateRoot $corrupt | Out-Null } catch { $threw = $true; $message = $_.Exception.Message }
    Assert 'decrypting it THROWS rather than silently returning nothing' $threw $null
    Assert 'and the message says what actually happened and how to fix it' `
        ($message -match '(?i)cannot be decrypted' -and $message -match '(?i)-SetApiKey') $message

    # ---- Per-user isolation.
    #
    # WHAT ACTUALLY CARRIES IT is the scope the blob was PROTECTED with:
    # CurrentUser binds the ciphertext to this account's master key, so no other
    # Windows account can decrypt it. LocalMachine binds it to the machine, so
    # every account can - which is why using it here would be the defect.
    #
    # Measured first, and it disproved this test's original premise: Unprotect
    # reads the scope from the blob itself and will happily decrypt a
    # LocalMachine blob whatever scope the caller passes. So "pass the wrong
    # scope and watch it fail" proves nothing. What can be proved without a
    # second Windows account is that the scope parameter is real and that this
    # library uses the isolating one.
    Add-Type -AssemblyName System.Security -ErrorAction SilentlyContinue
    $plain = [Text.Encoding]::UTF8.GetBytes($secret)
    $userBlob = [Security.Cryptography.ProtectedData]::Protect($plain, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $machineBlob = [Security.Cryptography.ProtectedData]::Protect($plain, $null, [Security.Cryptography.DataProtectionScope]::LocalMachine)
    Assert 'the two DPAPI scopes really do produce different ciphertext for the same key' `
        ([Convert]::ToBase64String($userBlob) -ne [Convert]::ToBase64String($machineBlob)) $null

    $libSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'chatgpt-secret.lib.ps1') -Raw
    $scopesUsed = @($libSource -split 'DataProtectionScope\]::' | Select-Object -Skip 1 |
                    ForEach-Object { ($_ -split '[^A-Za-z]')[0] } | Sort-Object -Unique)
    Assert 'the credential library uses CurrentUser and ONLY CurrentUser' `
        (($scopesUsed -join ',') -eq 'CurrentUser') `
        ("scopes found: " + ($scopesUsed -join ', ') + " - a LocalMachine scope would let every account on this machine read the key")

    # ---- Revocation removes the ciphertext, it does not merely unlist it.
    $before = (Get-Item -LiteralPath $file).Length
    Assert 'revoking reports that something was removed' (Remove-HorizunChatGptSecret -StateRoot $store) $null
    Assert 'and the file is gone' (-not (Test-Path -LiteralPath $file)) $null
    Assert 'revoking again is a no-op rather than an error' `
        (-not (Remove-HorizunChatGptSecret -StateRoot $store)) $null
    Assert 'the store reads as absent again' (-not (Test-HorizunChatGptSecret -StateRoot $store)) "was $before bytes"

    # ======================================================================
    Write-Host ""
    Write-Host "Nothing prints the key" -ForegroundColor Cyan

    $scrubRoot = Join-Path $root 'scrub'
    New-Item -ItemType Directory -Path $scrubRoot -Force | Out-Null
    Set-HorizunChatGptSecret -StateRoot $scrubRoot -Secret $secret | Out-Null
    # A stand-in for a client that echoes its own configuration back at you.
    $echo = Join-Path $env:SystemRoot 'System32\cmd.exe'
    $r = Invoke-HorizunTunnelClient -Path $echo -Arguments @('/c', 'echo %CONTROL_PLANE_API_KEY%') -StateRoot $scrubRoot
    Assert 'a client that echoes the key has it scrubbed before any caller sees it' `
        (-not $r.output.Contains($secret)) $r.output
    Assert 'and what is left says it was redacted rather than looking like a blank' `
        ($r.output -match 'redacted') $r.output
    Assert 'the invoker keeps the exit code rather than folding it into text' ($r.exit_code -eq 0) "exit=$($r.exit_code)"
    Remove-HorizunChatGptSecret -StateRoot $scrubRoot | Out-Null

    # ======================================================================
    # Fixtures: a compiled stand-in for tunnel-client, and a stand-in MCP server
    # under a path with a space, an apostrophe and non-ASCII letters.
    # ======================================================================
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
    $build = Join-Path $root 'build'
    New-Item -ItemType Directory -Path $build -Force | Out-Null
    $fakeExe = Join-Path $build 'fake.exe'
    $cs = Join-Path $PSScriptRoot 'testdata\fake-tunnel-client.cs'
    $compile = Invoke-HorizunProcess -Path $csc -Arguments @('/nologo', '/target:exe', "/out:$fakeExe", $cs) -TimeoutSec 120
    if (-not (Test-Path -LiteralPath $fakeExe)) { throw "could not compile the fake tunnel-client: $($compile.stdout) $($compile.stderr)" }

    $uni = 'Ana Maria\' + [char]0x00D1 + 'and' + [char]0x00FA + " O'Neil"
    function New-Fake([string]$dir, [string]$name, [string]$mode = 'full', [string]$poll = 'fresh') {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        $p = Join-Path $dir $name
        Copy-Item -LiteralPath $fakeExe -Destination $p -Force
        Set-Content -LiteralPath (Join-Path $dir 'fake-mode.txt') -Value $mode -Encoding ASCII
        Set-Content -LiteralPath (Join-Path $dir 'fake-poll.txt') -Value $poll -Encoding ASCII
        return $p
    }
    function Set-FakePoll([string]$exe, [string]$poll) { Set-Content -LiteralPath (Join-Path (Split-Path -Parent $exe) 'fake-poll.txt') -Value $poll -Encoding ASCII }
    function Get-FakeArgv([string]$exe) {
        $f = Join-Path (Split-Path -Parent $exe) 'fake-argv.log'
        if (Test-Path -LiteralPath $f) { return @(Get-Content -LiteralPath $f -Encoding UTF8) } else { return @() }
    }

    $server = New-Fake (Join-Path $root "srv\$uni") 'horizun-mcp-server.exe'
    $hostExe = (Get-Process -Id $PID).Path
    function Invoke-Helper([string[]]$HelperArgs, [int]$TimeoutSec = 180) {
        # Captured output and a deadline: a helper that leaves a child holding its
        # stdout would hang here, and the deadline turns that into a failure.
        $r = Invoke-HorizunProcess -Path $hostExe -Arguments (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $tunnelScript) + $HelperArgs) -TimeoutSec $TimeoutSec
        $r | Add-Member -NotePropertyName text -NotePropertyValue ($r.stdout + "`n" + $r.stderr) -Force
        return $r
    }
    function Read-Report([string]$path) { if (Test-Path -LiteralPath $path) { return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json) } return $null }
    Write-Host "  (helper runs under $hostExe)" -ForegroundColor DarkGray

    # ======================================================================
    Write-Host ""
    Write-Host "Two command-line layers" -ForegroundColor Cyan

    $echoArgs = New-Fake (Join-Path $root 'echo') 'echo-args.exe'
    $tricky = @('plain', 'with space', 'trailing\', 'C:\dir with space\', 'quote"inside', '"', '', ('non-ascii ' + [char]0x00D1), "apostrophe O'Neil", 'a\\"b')
    $r = Invoke-HorizunProcess -Path $echoArgs -Arguments $tricky
    $back = @(($r.stdout -replace "`r", '').TrimEnd("`n") -split "`n")
    Assert 'every argv element survives the Windows command line exactly, including empty and trailing backslashes' `
        (($back.Count -eq $tricky.Count) -and (@(0..($tricky.Count - 1) | Where-Object { $back[$_] -cne $tricky[$_] }).Count -eq 0)) `
        ("sent: " + ($tricky -join ' | ') + "  got: " + ($back -join ' | '))

    $cmdPath = 'C:\Windows\System32\cmd.exe'
    Assert "tunnel-client's parser eats unprotected backslashes (the 2.0.1 defect, reproduced)" `
        ((ConvertFrom-HorizunTunnelCommandLine $cmdPath)[0] -ceq 'C:WindowsSystem32cmd.exe') $null
    $threw = $false; try { ConvertFrom-HorizunTunnelCommandLine "C:\Projects\Ana O'Neil\x.exe" | Out-Null } catch { $threw = $true }
    Assert 'and an apostrophe in an unquoted path does not parse at all' $threw $null
    $parsed = @()
    try { $serialized = ConvertTo-HorizunTunnelMcpCommand -ServerPath $server; $parsed = ConvertFrom-HorizunTunnelCommandLine $serialized }
    catch { $parsed = @("THREW: $($_.Exception.Message)") }
    Assert 'the serialized server command parses back to ONE argument: the path, with forward slashes' `
        ($parsed.Count -eq 1 -and $parsed[0] -ceq $server.Replace('\', '/')) ("got: " + ($parsed -join ' | '))
    $threw = $false; try { ConvertTo-HorizunTunnelMcpCommand -ServerPath 'relative\horizun-mcp.exe' | Out-Null } catch { $threw = $true }
    Assert 'a relative server path is refused rather than serialized' $threw $null

    # ======================================================================
    Write-Host ""
    Write-Host "Which tunnel-client, by evidence" -ForegroundColor Cyan

    $full = New-Fake (Join-Path $root "client\$uni full") 'tunnel-client.exe'
    $t = Get-HorizunTunnelClient -Override $full
    Assert 'the full client is compatible' ($t.status -eq 'compatible' -and $t.variant -eq 'full' -and $t.supports_mcp_command -eq $true) "status=$($t.status) variant=$($t.variant)"
    Assert 'its version comes from --version, and the retired `version` form is never sent' `
        ($t.version -eq '0.0.99+fake' -and @(Get-FakeArgv $full | Where-Object { $_ -match '^version\b' }).Count -eq 0 -and
         @(Get-FakeArgv $full | Where-Object { $_ -match '^--version\b' }).Count -ge 1) "version=$($t.version)"
    Assert 'every probe keeps its exit code' (@($t.probes | Where-Object { $null -eq $_.exit_code }).Count -eq 0) ($t.probes | ConvertTo-Json -Compress)

    $rt = New-Fake (Join-Path $root 'client\runtime') 'tunnel-client-runtime-cloudflared.exe' 'runtime'
    $t = Get-HorizunTunnelClient -Override $rt
    Assert 'the runtime variant is recognised as the wrong package' ($t.status -eq 'wrong_variant' -and $t.variant -eq 'runtime-cloudflared') "status=$($t.status) variant=$($t.variant)"
    Assert 'and the advice names the full ZIP and warns against renaming' `
        ($t.problem -match 'tunnel-client-v<version>-windows-(amd64|arm64)\.zip' -and $t.problem -match '(?i)rename') $t.problem

    $renamed = New-Fake (Join-Path $root 'client\renamed') 'tunnel-client.exe' 'runtime'
    $t = Get-HorizunTunnelClient -Override $renamed
    Assert 'a runtime RENAMED to tunnel-client.exe is still the wrong package' ($t.status -eq 'wrong_variant') "status=$($t.status)"

    $err = New-Fake (Join-Path $root 'client\errhelp') 'tunnel-client.exe' 'errhelp'
    $t = Get-HorizunTunnelClient -Override $err
    Assert 'a failed `init --help` whose ERROR mentions --mcp-command is not taken as support' `
        ($t.status -ne 'compatible' -and $t.supports_mcp_command -ne $true) "status=$($t.status) supports=$($t.supports_mcp_command)"
    Assert 'and the report carries that exit code' (@($t.probes | Where-Object { $_.arguments -eq 'init --help' -and $_.exit_code -eq 1 }).Count -eq 1) ($t.probes | ConvertTo-Json -Compress)

    $hang = New-Fake (Join-Path $root 'client\hang') 'tunnel-client.exe' 'hang'
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $t = Get-HorizunTunnelClient -Override $hang -ProbeTimeoutSec 2
    Assert 'a client that never answers is a timeout, reported as failed, within the deadline' `
        ($t.status -eq 'failed' -and $t.problem -match 'within 2 seconds' -and $clock.Elapsed.TotalSeconds -lt 30) "status=$($t.status) after $([int]$clock.Elapsed.TotalSeconds)s: $($t.problem)"
    Assert 'and the hung process was killed' (@(Get-Process -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $hang } catch { $false } }).Count -eq 0) $null

    $env:HORIZUN_TUNNEL_CLIENT = $full
    try { $t = Get-HorizunTunnelClient -Override (Join-Path $root 'nowhere\tunnel-client.exe') }
    catch { $t = [pscustomobject]@{ status = "THREW: $($_.Exception.Message)"; installed = $null; path = $null } }
    finally { Remove-Item Env:\HORIZUN_TUNNEL_CLIENT -ErrorAction SilentlyContinue }
    Assert 'an explicit path that does not exist is reported, NOT replaced by another executable' `
        ($t.status -eq 'explicit_missing' -and -not $t.installed -and $t.path -like '*nowhere*') "status=$($t.status) path=$($t.path)"

    # ======================================================================
    Write-Host ""
    Write-Host "The whole lifecycle, one profile directory" -ForegroundColor Cyan

    $life = Join-Path $root ("life " + [char]0x00E9)
    $lifeStatus = Join-Path $root 'life-status.json'
    New-Item -ItemType Directory -Path $life -Force | Out-Null
    $common = @('-StateRoot', $life, '-StatusPath', $lifeStatus, '-ServerPath', $server, '-ConnectWaitSec', '5', '-StartWaitSec', '15')
    $profileFile = Join-Path $life 'profiles\horizun-revit.yaml'
    $tunnelId = 'tunnel_' + ('0123456789abcdef' * 2)

    $r = Invoke-Helper (@('-Status', '-TunnelClientPath', $full, '-Json', (Join-Path $root 'r-status.json')) + $common)
    $rep = Read-Report (Join-Path $root 'r-status.json')
    Assert 'status with a compatible client and no key names -SetApiKey' ($rep.state -eq 'pending_user_action' -and $rep.pending_user_action -match 'SetApiKey') "$($rep.state): $($rep.pending_user_action)"
    Assert 'a proven client is remembered for later calls' (Test-Path -LiteralPath (Join-Path $life 'tunnel-client.selection.json')) $null

    Set-HorizunChatGptSecret -StateRoot $life -Secret $secret | Out-Null
    $r = Invoke-Helper (@('-Init', '-TunnelId', $tunnelId, '-Json', (Join-Path $root 'r-init.json')) + $common)
    $rep = Read-Report (Join-Path $root 'r-init.json')
    Assert 'init runs WITHOUT -TunnelClientPath, using the remembered client' (@(Get-FakeArgv $full | Where-Object { $_ -match '^init --sample' }).Count -eq 1) ($r.text)
    Assert 'init writes the profile in the integration''s own directory' (Test-Path -LiteralPath $profileFile) ($r.text)
    $yaml = if (Test-Path -LiteralPath $profileFile) { Get-Content -LiteralPath $profileFile -Raw -Encoding UTF8 } else { '' }
    Assert 'and the stored command is the server path, surviving the space, the apostrophe and the accents' `
        ($yaml.Contains($server.Replace('\', '/'))) $yaml
    Assert 'init passed --profile-dir and a LOOPBACK ephemeral health address' `
        (@(Get-FakeArgv $full | Where-Object { $_ -match '^init ' -and $_ -match '--profile-dir' -and $_ -match '--health-listen-addr 127\.0\.0\.1:0' }).Count -eq 1) ((Get-FakeArgv $full) -join "`n")

    $r = Invoke-Helper (@('-Init', '-TunnelId', $tunnelId) + $common)
    Assert 'a second init refuses to replace the profile without -Force' ($r.exit_code -eq 2 -and $r.text -match 'already exists') $r.text

    $r = Invoke-Helper (@('-Doctor', '-Json', (Join-Path $root 'r-doctor.json')) + $common)
    Assert 'doctor reads the same profile directory' (@(Get-FakeArgv $full | Where-Object { $_ -match '^doctor ' -and $_.Contains((Join-Path $life 'profiles')) }).Count -eq 1) ((Get-FakeArgv $full) -join "`n")
    Assert 'and reports healthy' ((Read-Report (Join-Path $root 'r-doctor.json')).actions | Where-Object { $_.action -match 'doctor reported' -and $_.ok }) $r.text

    $r = Invoke-Helper (@('-Start', '-IUnderstandTrafficLeavesThisMachine', '-Json', (Join-Path $root 'r-start.json')) + $common)
    $rep = Read-Report (Join-Path $root 'r-start.json')
    Assert 'start returns to a caller that captures its output: the tunnel holds none of the helper''s handles' `
        ((-not $r.timed_out) -and $r.output_complete) "timed_out=$($r.timed_out) output_complete=$($r.output_complete)"
    Assert 'start reports configured only with fresh contact with OpenAI' `
        ($rep.state -eq 'configured' -and $rep.evidence.connection.control_plane.state -eq 'fresh' -and $rep.evidence.connection.local_health.ok) "$($rep.state): $($rep.detail)"
    Assert 'and never claims the ChatGPT call itself' ($rep.evidence.connection.chatgpt_call.verified -eq $false -and $rep.detail -match 'not been verified') $rep.detail
    $rec = Get-Content -LiteralPath (Join-Path $life 'tunnel-runtime.json') -Raw | ConvertFrom-Json
    $tp = Get-Process -Id $rec.pid -ErrorAction SilentlyContinue
    Assert 'the running tunnel is recorded by pid, start time and executable' ($tp -and $rec.process_start_utc -and $rec.executable -eq $full) ($rec | ConvertTo-Json -Compress)
    Assert 'run used the same profile directory, the health URL file and a log file' `
        (@(Get-FakeArgv $full | Where-Object { $_ -match '^run ' -and $_.Contains((Join-Path $life 'profiles')) -and $_ -match '--health\.url-file' -and $_ -match '--log\.file' }).Count -eq 1) ((Get-FakeArgv $full) -join "`n")

    $r = Invoke-Helper (@('-Start', '-IUnderstandTrafficLeavesThisMachine') + $common)
    Assert 'a second start does not launch a second tunnel' (@(Get-FakeArgv $full | Where-Object { $_ -match '^run ' }).Count -eq 1 -and $r.text -match 'already running') $r.text

    Set-FakePoll $full 'stale'
    $r = Invoke-Helper (@('-Status', '-Json', (Join-Path $root 'r-stale.json')) + $common)
    $rep = Read-Report (Join-Path $root 'r-stale.json')
    Assert '/readyz green but the last successful poll is OLD: not connected' `
        ($rep.state -eq 'failed' -and $rep.evidence.connection.local_health.ok -and $rep.evidence.connection.control_plane.state -eq 'stale') "$($rep.state) $($rep.evidence.connection.control_plane.state)"
    Set-FakePoll $full 'never'
    $rep = $null; $r = Invoke-Helper (@('-Status', '-Json', (Join-Path $root 'r-never.json')) + $common); $rep = Read-Report (Join-Path $root 'r-never.json')
    Assert 'process alive, /readyz green, NO successful poll ever: not connected' `
        ($rep.state -eq 'failed' -and $rep.evidence.connection.control_plane.state -eq 'never') "$($rep.state) $($rep.evidence.connection.control_plane.state)"
    Set-FakePoll $full 'absent'
    $rep = $null; $r = Invoke-Helper (@('-Status', '-Json', (Join-Path $root 'r-absent.json')) + $common); $rep = Read-Report (Join-Path $root 'r-absent.json')
    Assert 'no poll metric at all: contact with OpenAI unknown, not assumed' `
        ($rep.state -eq 'failed' -and $rep.evidence.connection.control_plane.state -eq 'unknown') "$($rep.state) $($rep.evidence.connection.control_plane.state)"

    $diagJson = Join-Path $root 'diag.json'
    $d = Invoke-HorizunProcess -Path $hostExe -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'diagnose-integrations.ps1'),
        '-ChatGptStateRoot', $life, '-StatusPath', $lifeStatus, '-ServerPath', $server, '-Json', $diagJson) -TimeoutSec 240
    $row = @((Read-Report $diagJson).clients | Where-Object { $_.client -eq 'chatgpt-work' })[0]
    Assert 'the general diagnosis measures the tunnel NOW instead of trusting a recorded "configured"' `
        ($row -and $row.configured -eq $false -and $row.detail -match 'NOT connected') ("row: " + ($row | ConvertTo-Json -Compress))

    Set-FakePoll $full 'fresh'
    $r = Invoke-Helper (@('-Stop', '-Json', (Join-Path $root 'r-stop.json')) + $common)
    Start-Sleep -Milliseconds 500
    Assert 'stop stops exactly the recorded tunnel and sees it exit' (-not (Get-Process -Id $rec.pid -ErrorAction SilentlyContinue) -and $r.text -match 'saw it exit') $r.text
    Assert 'and the durable state is not "configured" afterwards' ((Get-Content -LiteralPath $lifeStatus -Raw | ConvertFrom-Json).integrations.chatgpt.state -eq 'pending_user_action') $null

    $r = Invoke-Helper (@('-Revoke', '-Json', (Join-Path $root 'r-revoke.json')) + $common)
    Assert 'revoke deletes the profile from the SAME directory init wrote it to' (-not (Test-Path -LiteralPath $profileFile)) $r.text
    Assert 'and forgets the key' (-not (Test-HorizunChatGptSecret -StateRoot $life)) $null
    Assert 'and names the OpenAI-side objects only the user can delete' ($r.text -match 'settings/organization/tunnels') $r.text

    # ======================================================================
    Write-Host ""
    Write-Host "Prerequisites stop dependent steps" -ForegroundColor Cyan

    $pre = Join-Path $root 'pre-wrong'
    $wrong = New-Fake (Join-Path $root 'client\pre-runtime') 'tunnel-client.exe' 'runtime'
    Set-HorizunChatGptSecret -StateRoot $pre -Secret $secret | Out-Null
    $r = Invoke-Helper @('-Init', '-TunnelId', $tunnelId, '-Doctor', '-Start', '-IUnderstandTrafficLeavesThisMachine', '-TunnelClientPath', $wrong,
                          '-StateRoot', $pre, '-StatusPath', (Join-Path $root 'pre-status.json'), '-ServerPath', $server, '-Json', (Join-Path $root 'r-pre.json'))
    $rep = Read-Report (Join-Path $root 'r-pre.json')
    Assert 'with the runtime package, init/doctor/start are NOT run' (@(Get-FakeArgv $wrong | Where-Object { $_ -match '^(init --sample|doctor|run) ' }).Count -eq 0) ((Get-FakeArgv $wrong) -join "`n")
    Assert 'no profile is created' (-not (Test-Path -LiteralPath (Join-Path $pre 'profiles\horizun-revit.yaml'))) $null
    Assert 'the report still exists, says which steps were blocked, and names the right package' `
        ($rep -and $rep.blocked.Count -eq 3 -and $rep.pending_user_action -match 'windows-(amd64|arm64)\.zip' -and $r.exit_code -eq 2) "exit=$($r.exit_code) blocked=$($rep.blocked.Count)"
    Assert 'the wrong package is never remembered as the selection' (-not (Test-Path -LiteralPath (Join-Path $pre 'tunnel-client.selection.json'))) $null

    $noSrv = Join-Path $root 'pre-noserver'
    $full2 = New-Fake (Join-Path $root 'client\pre-full') 'tunnel-client.exe'
    $r = Invoke-Helper @('-Init', '-TunnelId', $tunnelId, '-TunnelClientPath', $full2, '-StateRoot', $noSrv,
                          '-StatusPath', (Join-Path $root 'pre-status2.json'), '-ServerPath', (Join-Path $root 'missing\horizun-mcp.exe'), '-Json', (Join-Path $root 'r-pre2.json'))
    Assert 'a missing server blocks init before tunnel-client is asked to do anything' `
        ((@(Get-FakeArgv $full2 | Where-Object { $_ -match '^init --sample' }).Count -eq 0) -and $r.exit_code -eq 2) $r.text
    $r = Invoke-Helper @('-Start', '-IUnderstandTrafficLeavesThisMachine', '-TunnelClientPath', $full2, '-StateRoot', $noSrv,
                          '-StatusPath', (Join-Path $root 'pre-status3.json'), '-ServerPath', $server)
    Assert 'no stored key blocks start, naming -SetApiKey' ($r.exit_code -eq 2 -and $r.text -match 'SetApiKey' -and @(Get-FakeArgv $full2 | Where-Object { $_ -match '^run ' }).Count -eq 0) $r.text

    $early = Join-Path $root 'r-early.json'
    $r = Invoke-Helper @('-Status', '-TunnelClientPath', (Join-Path $root 'gone\tunnel-client.exe'), '-StateRoot', (Join-Path $root 'pre-early'),
                          '-StatusPath', (Join-Path $root 'pre-status4.json'), '-ServerPath', $server, '-Json', $early)
    $rep = Read-Report $early
    Assert 'an explicit client that does not exist still yields a report and a durable state' `
        ($rep -and $rep.evidence.tunnel_client.status -eq 'explicit_missing' -and
         (Get-Content -LiteralPath (Join-Path $root 'pre-status4.json') -Raw | ConvertFrom-Json).integrations.chatgpt.state) "state=$($rep.state)"

    $refused = Invoke-Helper @('-Start', '-StateRoot', (Join-Path $root 'pre-ack'), '-StatusPath', (Join-Path $root 'pre-status5.json'), '-ServerPath', $server)
    Assert 'starting without the traffic acknowledgement is REFUSED and explains what leaves the machine' `
        ($refused.text -match 'REFUSED' -and $refused.text -match 'OpenAI-hosted infrastructure') $refused.text
    foreach ($bad in @('not-a-tunnel-id', 'tunnel_short', 'tunnel_ZZZZ0123456789abcdef0123456789ab')) {
        $r = Invoke-Helper @('-Init', '-TunnelId', $bad, '-TunnelClientPath', $full2, '-StateRoot', (Join-Path $root "pre-id-$([guid]::NewGuid().ToString('N'))"),
                              '-StatusPath', (Join-Path $root 'pre-status6.json'), '-ServerPath', $server)
        Assert "a malformed tunnel id '$bad' is refused before anything is written" ($r.text -match 'not the documented tunnel id shape') $r.text
    }

    # ======================================================================
    Write-Host ""
    Write-Host "Process identity" -ForegroundColor Cyan

    $pidRoot = Join-Path $root 'pid-recycled'
    New-Item -ItemType Directory -Path $pidRoot -Force | Out-Null
    [pscustomobject]@{ schema = 1; pid = $PID; process_start_utc = '2001-01-01T00:00:00.0000000Z'; executable = $full
                       health_url_file = (Join-Path $pidRoot 'health.url'); log_file = (Join-Path $pidRoot 'x.log') } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $pidRoot 'tunnel-runtime.json') -Encoding UTF8
    $r = Invoke-Helper @('-Stop', '-StateRoot', $pidRoot, '-StatusPath', (Join-Path $root 'pid-status.json'), '-ServerPath', $server)
    Assert 'a recorded pid now held by ANOTHER process is not killed' ([bool](Get-Process -Id $PID -ErrorAction SilentlyContinue)) 'the test host was killed'
    Assert 'and the helper says the pid was reused, rather than claiming it stopped the tunnel' ($r.text -match 'different process' -and $r.text -notmatch 'saw it exit') $r.text

    $badRoot = Join-Path $root 'pid-unreadable'
    New-Item -ItemType Directory -Path $badRoot -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $badRoot 'tunnel-runtime.json') -Value '{ not json' -Encoding ASCII
    $r = Invoke-Helper @('-Stop', '-StateRoot', $badRoot, '-StatusPath', (Join-Path $root 'bad-status.json'), '-ServerPath', $server)
    Assert 'an unreadable process record: stop does not claim success' ($r.exit_code -eq 1 -and $r.text -match 'NOT VERIFIED') $r.text
    Assert 'and the record is kept for a person to inspect' (Test-Path -LiteralPath (Join-Path $badRoot 'tunnel-runtime.json')) $null
    Set-HorizunChatGptSecret -StateRoot $badRoot -Secret $secret | Out-Null
    $r = Invoke-Helper @('-Revoke', '-StateRoot', $badRoot, '-StatusPath', (Join-Path $root 'bad-status.json'), '-ServerPath', $server)
    Assert 'revoke with an unverifiable tunnel does not forget the key it may still be using' ((Test-HorizunChatGptSecret -StateRoot $badRoot) -and $r.exit_code -eq 1) $r.text

    $legacyRoot = Join-Path $root 'legacy'
    New-Item -ItemType Directory -Path $legacyRoot -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $legacyRoot 'tunnel-client.pid') -Value ([string]$PID) -Encoding ASCII
    $r = Invoke-Helper @('-Stop', '-StateRoot', $legacyRoot, '-StatusPath', (Join-Path $root 'legacy-status.json'), '-ServerPath', $server)
    Assert 'a 2.0.1 bare pid file is never acted on' ([bool](Get-Process -Id $PID -ErrorAction SilentlyContinue) -and $r.text -match 'NOT VERIFIED') $r.text

    # ======================================================================
    Write-Host ""
    Write-Host "Earlier installations" -ForegroundColor Cyan

    $oldDir = Join-Path $root 'old-default-profiles'
    New-Item -ItemType Directory -Path $oldDir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $oldDir 'horizun-revit.yaml') -Value 'legacy' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $oldDir 'someone-else.yaml') -Value 'not ours' -Encoding ASCII
    $env:TUNNEL_CLIENT_PROFILE_DIR = $oldDir
    try {
        $legRoot = Join-Path $root 'legacy-profile'
        Set-HorizunChatGptSecret -StateRoot $legRoot -Secret $secret | Out-Null
        $r = Invoke-Helper @('-Status', '-TunnelClientPath', $full2, '-StateRoot', $legRoot, '-StatusPath', (Join-Path $root 'leg-status.json'), '-ServerPath', $server, '-Json', (Join-Path $root 'r-leg.json'))
        Assert 'a profile left in tunnel-client''s default directory is reported' ((Read-Report (Join-Path $root 'r-leg.json')).evidence.legacy_profiles.Count -eq 1) $r.text
        $r = Invoke-Helper @('-Revoke', '-StateRoot', $legRoot, '-StatusPath', (Join-Path $root 'leg-status.json'), '-ServerPath', $server)
        Assert 'and revoke leaves that directory - and every other profile in it - untouched' `
            ((Test-Path -LiteralPath (Join-Path $oldDir 'horizun-revit.yaml')) -and (Test-Path -LiteralPath (Join-Path $oldDir 'someone-else.yaml'))) $r.text
    }
    finally { Remove-Item Env:\TUNNEL_CLIENT_PROFILE_DIR -ErrorAction SilentlyContinue }

    # ======================================================================
    Write-Host ""
    Write-Host "The key appears nowhere it should not" -ForegroundColor Cyan

    $argvHits = @()
    foreach ($logFile in @(Get-ChildItem -LiteralPath $root -Recurse -Filter 'fake-argv.log' -File)) {
        $argvHits += @(Get-Content -LiteralPath $logFile.FullName -Encoding UTF8 | Where-Object { $_.Contains($secret) })
    }
    Assert 'no tunnel-client invocation carried the key as an argument' ($argvHits.Count -eq 0) ($argvHits -join "`n")
    Assert 'run received it through its environment' (@(Get-FakeArgv $full | Where-Object { $_ -match '^run ' -and $_ -match 'KEY=present' }).Count -eq 1) ((Get-FakeArgv $full) -join "`n")
    Assert 'init and doctor did not need it on their command lines either' (@(Get-FakeArgv $full | Where-Object { $_ -match '^init --sample' -and $_ -match 'KEY=absent' }).Count -eq 1) ((Get-FakeArgv $full) -join "`n")
    $leaks = @(Get-ChildItem -LiteralPath $root -Recurse -File -Include '*.json', '*.yaml', '*.log', '*.url', '*.txt' |
               Where-Object { $_.FullName -notlike '*fake-argv.log' } |
               Where-Object { (Get-Content -LiteralPath $_.FullName -Raw -ErrorAction SilentlyContinue) -match [regex]::Escape($secret) })
    Assert 'no report, status file, profile or log contains the key' ($leaks.Count -eq 0) ($leaks.FullName -join ', ')

    # ======================================================================
    Write-Host ""
    Write-Host "What travels in the package, and what Setup says" -ForegroundColor Cyan

    $pack = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'pack.ps1') -Raw
    $staged = @([regex]::Matches($pack, "'([\w.-]+\.ps1)'") | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    $missing = @()
    foreach ($tool in @('chatgpt-tunnel.ps1', 'diagnose-integrations.ps1', 'install-claude-desktop-extension.ps1')) {
        $queue = New-Object System.Collections.Generic.Queue[string]; $queue.Enqueue($tool); $seen = @{}
        while ($queue.Count -gt 0) {
            $f = $queue.Dequeue(); if ($seen[$f]) { continue }; $seen[$f] = $true
            if ($staged -notcontains $f) { $missing += "$f (needed by $tool)" }
            $src = Join-Path $PSScriptRoot $f
            if (Test-Path -LiteralPath $src) {
                foreach ($m in [regex]::Matches((Get-Content -LiteralPath $src -Raw), "Join-Path \`$PSScriptRoot '([\w.-]+\.ps1)'")) { $queue.Enqueue($m.Groups[1].Value) }
            }
        }
    }
    Assert 'every library the installed ChatGPT and diagnosis helpers load is staged by pack.ps1' ($missing.Count -eq 0) ($missing -join ', ')

    $iss = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'installer\horizun-mcp.iss') -Raw
    Assert 'Setup no longer tells anyone ChatGPT Work was configured for them' `
        ($iss -notmatch 'ChatGPT Work (were|was) configured' -and $iss -notmatch 'ChatGPT Work quedaron configurados') $null
    foreach ($sheet in @('claude-desktop.en.html', 'claude-desktop.es.html')) {
        $html = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) "installer\instructions\$sheet") -Raw
        Assert "the $sheet sheet does not claim ChatGPT Work is configured" ($html -notmatch 'Codex and ChatGPT Work &mdash; was already configured' -and $html -notmatch 'Codex y ChatGPT Work &mdash; ya quedaron') $null
    }
}
finally {
    # Nothing a test started is left running.
    Get-Process -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -and $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) } catch { $false } } |
        ForEach-Object { try { $_.Kill() } catch { } }
    Start-Sleep -Milliseconds 300
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed -gt 0) { Write-Host ""; Write-Host "$failed check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host ""
Write-Host ("chatgpt tunnel: all checks passed under PowerShell {0}." -f $PSVersionTable.PSVersion) -ForegroundColor Green
exit 0
