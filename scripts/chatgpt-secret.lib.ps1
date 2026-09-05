#Requires -Version 5.1
<#
  The ChatGPT runtime API key: stored by Windows, never by this repository.

  WHERE IT LIVES. Encrypted with DPAPI at CurrentUser scope and written to
  %LOCALAPPDATA%\Horizun\integrations\chatgpt\control-plane-api-key.dpapi. DPAPI
  keys the ciphertext to this Windows account: another account on this machine
  cannot read it, and the file is useless if copied elsewhere. That is the OS
  secure store, not a home-made one.

  WHERE IT NEVER GOES:
    - not into the repository, the installer, or any packaged file;
    - not onto a command line, because /proc-equivalent process listing on Windows
      shows the full command line of every process to every user session;
    - not into a log, a diagnostic, or a -Json report. The reports carry
      api_key_stored: true and nothing else.

  The only place the plaintext exists is the environment block of the
  tunnel-client child process, which is exactly the interface OpenAI documents
  (CONTROL_PLANE_API_KEY).
#>

function Get-HorizunChatGptSecretPath {
    param([Parameter(Mandatory = $true)][string]$StateRoot)
    return (Join-Path $StateRoot 'control-plane-api-key.dpapi')
}

function ConvertFrom-HorizunSecureString {
    <# SecureString to plaintext, freeing the unmanaged copy in every case. #>
    param([Parameter(Mandatory = $true)][System.Security.SecureString]$Secure)
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($Secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringUni($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($bstr) }
}

function Set-HorizunChatGptSecret {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$StateRoot,
        [Parameter(Mandatory = $true)][string]$Secret
    )
    if (-not (Test-Path -LiteralPath $StateRoot)) { New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null }
    Add-Type -AssemblyName System.Security -ErrorAction SilentlyContinue
    $bytes = [Text.Encoding]::UTF8.GetBytes($Secret)
    try {
        $protected = [Security.Cryptography.ProtectedData]::Protect(
            $bytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $path = Get-HorizunChatGptSecretPath -StateRoot $StateRoot
        [IO.File]::WriteAllBytes($path, $protected)
    }
    finally { [Array]::Clear($bytes, 0, $bytes.Length) }
    return $true
}

function Test-HorizunChatGptSecret {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$StateRoot)
    return (Test-Path -LiteralPath (Get-HorizunChatGptSecretPath -StateRoot $StateRoot) -PathType Leaf)
}

function Get-HorizunChatGptSecret {
    <#
      Decrypt. Returns $null when there is nothing stored, and THROWS when there
      is something stored that this account cannot decrypt - because that is a
      different situation from "not configured" and silently treating it as one
      sends the caller off to store a key it already has.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$StateRoot)
    $path = Get-HorizunChatGptSecretPath -StateRoot $StateRoot
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    Add-Type -AssemblyName System.Security -ErrorAction SilentlyContinue
    $protected = [IO.File]::ReadAllBytes($path)
    try {
        $bytes = [Security.Cryptography.ProtectedData]::Unprotect(
            $protected, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    }
    catch {
        throw ("The stored ChatGPT API key cannot be decrypted by this Windows account. " +
               "DPAPI ties it to the account that stored it, so a copied profile or a different user " +
               "produces exactly this. Re-run with -SetApiKey to store it again. ($($_.Exception.Message))")
    }
    try { return [Text.Encoding]::UTF8.GetString($bytes) }
    finally { [Array]::Clear($bytes, 0, $bytes.Length) }
}

function Remove-HorizunChatGptSecret {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$StateRoot)
    $path = Get-HorizunChatGptSecretPath -StateRoot $StateRoot
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    # Overwrite before unlinking: a deleted file's blocks survive on disk, and
    # "revoke" has to mean the ciphertext is gone, not merely unlisted.
    try {
        $len = (Get-Item -LiteralPath $path).Length
        if ($len -gt 0) { [IO.File]::WriteAllBytes($path, (New-Object byte[] $len)) }
    }
    catch { }
    Remove-Item -LiteralPath $path -Force
    return $true
}

function Test-HorizunTunnelReady {
    <#
      Is the tunnel actually CONNECTED, or merely running?

      Those are different, and the difference is invisible from a process list. A
      tunnel-client that has lost its outbound path keeps running; every ChatGPT
      tool call then fails while nothing on this machine looks wrong. OpenAI
      documents `/healthz`, `/readyz`, `/metrics` and a `/ui` on the client's
      LOOPBACK-ONLY admin surface, so readiness is a question that can be asked.

      This never exposes that surface and never changes its binding - it only
      reads it, on localhost, if the client published where it is listening.

      Returns .checked=$false when the address is unknown, which is an honest
      "cannot tell" rather than a guess in either direction.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$StateRoot, [int]$TimeoutSec = 5)

    $result = [ordered]@{ checked = $false; ready = $false; endpoint = $null; detail = $null }

    # The admin address is whatever the client was configured with. Only a value
    # this integration recorded is used; nothing is scanned for and no port is
    # guessed, because probing arbitrary local ports is not diagnosis.
    $addrFile = Join-Path $StateRoot 'admin-endpoint.txt'
    $endpoint = $null
    if (Test-Path -LiteralPath $addrFile -PathType Leaf) {
        $endpoint = (Get-Content -LiteralPath $addrFile -Raw).Trim()
    }
    elseif ($env:HORIZUN_TUNNEL_ADMIN_URL) { $endpoint = $env:HORIZUN_TUNNEL_ADMIN_URL.Trim() }
    if (-not $endpoint) {
        $result.detail = 'no admin endpoint is recorded for this profile, so readiness cannot be read'
        return [pscustomobject]$result
    }

    # Loopback only. An admin surface reachable from elsewhere is not something
    # this script will interrogate, let alone encourage.
    try { $uri = [uri]$endpoint } catch { $result.detail = "not a URL: $endpoint"; return [pscustomobject]$result }
    if ($uri.Host -notin @('localhost', '127.0.0.1', '::1', '[::1]')) {
        $result.detail = "refusing to probe a non-loopback admin endpoint ($($uri.Host))"
        return [pscustomobject]$result
    }

    $result.endpoint = $endpoint.TrimEnd('/') + '/readyz'
    try {
        $r = Invoke-WebRequest -Uri $result.endpoint -TimeoutSec $TimeoutSec -UseBasicParsing -ErrorAction Stop
        $result.checked = $true
        $result.ready = ($r.StatusCode -ge 200 -and $r.StatusCode -lt 300)
        $result.detail = "HTTP $($r.StatusCode)"
    }
    catch {
        $result.checked = $true
        $result.ready = $false
        $result.detail = $_.Exception.Message
    }
    return [pscustomobject]$result
}

function Invoke-HorizunTunnelClient {
    <#
      Run a tunnel-client subcommand with the key in the ENVIRONMENT, capture its
      output, and return it. The key is never an argument, and the returned output
      is scrubbed of anything that looks like one before any caller can print it.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$StateRoot,
        [int]$TimeoutSec = 120
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Path
    # ProcessStartInfo.ArgumentList is unavailable in Windows PowerShell 5.1.
    # Quote with the Windows command-line escaping rules; secrets never enter it.
    $psi.Arguments = (($Arguments | ForEach-Object {
        if ($_ -notmatch '[\s"]') { $_ }
        else { '"' + ([regex]::Replace($_, '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"' }
    }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $StateRoot
    $secret = $null
    if (Test-HorizunChatGptSecret -StateRoot $StateRoot) {
        $secret = Get-HorizunChatGptSecret -StateRoot $StateRoot
        $psi.EnvironmentVariables['CONTROL_PLANE_API_KEY'] = $secret
    }
    $p = [Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync()
    $err = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { try { $p.Kill() } catch { } }
    $text = (($out.Result + "`n" + $err.Result)).Trim()
    if ($secret) {
        # A client that echoes its own configuration must not turn a diagnostic
        # into a place the key is written down.
        $text = $text.Replace($secret, '<redacted>')
        $secret = $null
    }
    $text = [regex]::Replace($text, 'sk-[A-Za-z0-9_\-]{8,}', '<redacted>')
    return [pscustomobject]@{ exit_code = $p.ExitCode; output = $text }
}
