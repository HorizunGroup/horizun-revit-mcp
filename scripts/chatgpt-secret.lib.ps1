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

. (Join-Path $PSScriptRoot 'process.lib.ps1')

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

function Invoke-HorizunTunnelClient {
    <#
      Run a tunnel-client subcommand with the key in the ENVIRONMENT, and return
      what happened with nothing merged away: started, exit code, timeout, and the
      output scrubbed of anything that looks like the key before any caller can
      print it. The key is never an argument.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$StateRoot,
        [int]$TimeoutSec = 120,
        [switch]$WithoutKey
    )
    $envVars = @{}
    $secret = $null
    if (-not $WithoutKey -and (Test-HorizunChatGptSecret -StateRoot $StateRoot)) {
        $secret = Get-HorizunChatGptSecret -StateRoot $StateRoot
        $envVars['CONTROL_PLANE_API_KEY'] = $secret
    }
    $r = Invoke-HorizunProcess -Path $Path -Arguments $Arguments -TimeoutSec $TimeoutSec -Environment $envVars -WorkingDirectory $StateRoot
    $text = (($r.stdout + "`n" + $r.stderr)).Trim()
    if ($secret) {
        # A client that echoes its own configuration must not turn a diagnostic
        # into a place the key is written down.
        $text = $text.Replace($secret, '<redacted>')
        $secret = $null
    }
    $text = [regex]::Replace($text, 'sk-[A-Za-z0-9_\-]{8,}', '<redacted>')
    return [pscustomobject]@{
        started   = $r.started
        exit_code = $r.exit_code
        timed_out = $r.timed_out
        error     = $r.error
        output    = $text
    }
}
