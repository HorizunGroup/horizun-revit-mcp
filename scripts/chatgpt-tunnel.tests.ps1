#Requires -Version 5.1
<#
  The ChatGPT side: the credential, the diagnosis, and what each refusal says.

  The credential store is exercised for the four states that actually happen on a
  real machine - stored, revoked, CORRUPTED, and belonging to another Windows
  account - because the third and fourth are the ones a naive implementation gets
  wrong by reporting "not configured" and sending the user off to store a key it
  already has.

  Nothing here needs tunnel-client, a tunnel, an API key, ChatGPT, or a network.
  What those WOULD prove is exactly what this file refuses to claim.
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
    Remove-HorizunChatGptSecret -StateRoot $scrubRoot | Out-Null

    # ======================================================================
    Write-Host ""
    Write-Host "Diagnosis when tunnel-client is missing" -ForegroundColor Cyan

    $t = Get-HorizunTunnelClient -Override (Join-Path $root 'no-such-tunnel-client.exe')
    Assert 'a missing tunnel-client reports installed=false without throwing' ($t.installed -eq $false) $null
    Assert 'and it names the official place to get it' `
        ($t.download_from -eq 'https://github.com/openai/tunnel-client/releases/latest') $t.download_from

    $status = Join-Path $root 'status.json'
    $out = & pwsh -NoProfile -File $tunnelScript -Status -StateRoot (Join-Path $root 'cg') -StatusPath $status 2>&1 | Out-String
    Assert 'the wizard says tunnel-client is not installed and that Horizun never downloads it' `
        ($out -match 'tunnel-client is NOT installed' -and $out -match "never downloads it") $out
    $st = (Get-Content -LiteralPath $status -Raw | ConvertFrom-Json).integrations.chatgpt
    Assert 'the recorded state is pending_user_action, not failed - nothing is broken' `
        ($st.state -eq 'pending_user_action') $st.state
    Assert 'and the pending action names the download, the tunnel and the commands, in order' `
        ($st.pending_user_action -match 'releases/latest' -and
         $st.pending_user_action -match 'settings/organization/tunnels' -and
         $st.pending_user_action -match '-SetApiKey' -and
         $st.pending_user_action -match '-Init' -and
         $st.pending_user_action -match '-Start') $st.pending_user_action

    # ======================================================================
    Write-Host ""
    Write-Host "What it refuses, and how it says so" -ForegroundColor Cyan

    $out = & pwsh -NoProfile -File $tunnelScript -Start -StateRoot (Join-Path $root 'cg2') `
        -StatusPath (Join-Path $root 'status2.json') 2>&1 | Out-String
    Assert 'starting without the traffic acknowledgement is REFUSED' ($out -match 'REFUSED') $out
    Assert 'and the refusal explains what leaving this machine means, concretely' `
        ($out -match 'OpenAI-hosted infrastructure' -and $out -match 'element data') $out

    foreach ($bad in @('not-a-tunnel-id', 'tunnel_short', 'tunnel_ZZZZ0123456789abcdef0123456789ab')) {
        $out = & pwsh -NoProfile -File $tunnelScript -Init -TunnelId $bad -TunnelClientPath $env:ComSpec `
            -StateRoot (Join-Path $root "cg-$([guid]::NewGuid().ToString('N'))") `
            -StatusPath (Join-Path $root 'status3.json') 2>&1 | Out-String
        Assert "a malformed tunnel id '$bad' is refused before anything is written" `
            ($out -match 'not the documented tunnel id shape') $out
    }

    $out = & pwsh -NoProfile -File $tunnelScript -Init -TunnelId ('tunnel_' + ('a' * 32)) -TunnelClientPath $env:ComSpec `
        -StateRoot (Join-Path $root 'cg4') -StatusPath (Join-Path $root 'status4.json') 2>&1 | Out-String
    Assert 'a well-formed id with NO stored key is refused, naming -SetApiKey' `
        ($out -match 'no API key is stored' -and $out -match '-SetApiKey') $out

    # ======================================================================
    Write-Host ""
    Write-Host "Outdated, and connected-but-not-really" -ForegroundColor Cyan

    # A build with no --mcp-command cannot reach a stdio server at all. cmd.exe
    # stands in for one: it is an executable whose help mentions no such flag.
    $old = Get-HorizunTunnelClient -Override $env:ComSpec
    Assert 'a build whose help does not mention --mcp-command is detected as unusable' `
        ($old.installed -and $old.supports_mcp_command -eq $false) `
        "installed=$($old.installed) supports=$($old.supports_mcp_command)"
    Assert 'and its provenance is reported: sha256, signature and source' `
        ($old.sha256 -and $old.signature_status -and $old.source) `
        "sha=$($old.sha256) sig=$($old.signature_status) source=$($old.source)"

    $outdatedRoot = Join-Path $root 'outdated'
    $out = & pwsh -NoProfile -File $tunnelScript -Status -TunnelClientPath $env:ComSpec `
        -StateRoot $outdatedRoot -StatusPath (Join-Path $root 'status-old.json') 2>&1 | Out-String
    Assert 'the wizard says the build is too old and points at the current release' `
        ($out -match 'does not advertise --mcp-command' -and $out -match 'releases/latest') $out

    # Running is not the same as connected.
    $readyRoot = Join-Path $root 'ready'
    New-Item -ItemType Directory -Path $readyRoot -Force | Out-Null
    $r = Test-HorizunTunnelReady -StateRoot $readyRoot
    Assert 'with no admin endpoint recorded, readiness is UNKNOWN rather than assumed' `
        ((-not $r.checked) -and (-not $r.ready)) "checked=$($r.checked) ready=$($r.ready)"

    Set-Content -LiteralPath (Join-Path $readyRoot 'admin-endpoint.txt') -Value 'http://10.0.0.5:8080' -Encoding ASCII
    $r = Test-HorizunTunnelReady -StateRoot $readyRoot
    Assert 'a NON-loopback admin endpoint is refused rather than probed' `
        ((-not $r.checked) -and $r.detail -match 'non-loopback') $r.detail

    # A loopback port with nothing on it: reachable question, negative answer.
    Set-Content -LiteralPath (Join-Path $readyRoot 'admin-endpoint.txt') -Value 'http://127.0.0.1:1' -Encoding ASCII
    $r = Test-HorizunTunnelReady -StateRoot $readyRoot -TimeoutSec 3
    Assert 'a loopback endpoint that answers nothing reports checked but NOT ready' `
        ($r.checked -and (-not $r.ready)) "checked=$($r.checked) ready=$($r.ready) detail=$($r.detail)"

    # ======================================================================
    Write-Host ""
    Write-Host "Stopping when nothing is running" -ForegroundColor Cyan

    $stopRoot = Join-Path $root 'cg5'
    New-Item -ItemType Directory -Path $stopRoot -Force | Out-Null
    # A stale pid file pointing at a process that is not tunnel-client must not
    # be treated as our tunnel: pids are recycled, and killing a stranger's
    # process because a file says so is the worst possible failure here.
    Set-Content -LiteralPath (Join-Path $stopRoot 'tunnel-client.pid') -Value ([string]$PID) -Encoding ASCII
    $out = & pwsh -NoProfile -File $tunnelScript -Stop -StateRoot $stopRoot `
        -StatusPath (Join-Path $root 'status5.json') 2>&1 | Out-String
    Assert 'a stale pid belonging to another process is NOT treated as the tunnel' `
        ($out -match 'was not running') $out
    Assert 'this test process is still alive' (Get-Process -Id $PID -ErrorAction SilentlyContinue) 'it killed the test host'

    $out = & pwsh -NoProfile -File $tunnelScript -Revoke -StateRoot $stopRoot `
        -StatusPath (Join-Path $root 'status6.json') 2>&1 | Out-String
    Assert 'revoke names the OpenAI-side objects only the user can delete' `
        ($out -match 'settings/organization/tunnels' -and $out -match '(?i)only you can do it') $out
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed -gt 0) { Write-Host ""; Write-Host "$failed check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host ""
Write-Host 'chatgpt tunnel: the credential survives four states and nothing prints it.' -ForegroundColor Green
exit 0
