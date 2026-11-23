#Requires -Version 5.1
<#
  Durable per-client integration state, inside install-status.json.

  install-status.json already answers "did the installation finish?" with a single
  `state`. It cannot answer "is Claude Desktop configured?" because installation
  state and per-client state are different facts with different outcomes.

  So this adds ONE property, `integrations`, beside the existing fields. Both
  writers of that file (complete-install.ps1 and refresh-install-status.ps1)
  rewrite the whole document, so both carry this block forward; a reader that
  predates it simply does not look at it.

  THE SIX STATES, and what each one promises:

    configured             the change was made and read back from the file
    verified               beyond configured: the client answered over MCP
    pending_client_restart made, but the client is running and will not see it
    pending_user_action    the remaining step can only be taken by the user
    unsupported            this client cannot do this on this machine, and why
    failed                 attempted and did not succeed; nothing left half-done

  `pending_user_action` is the one that matters most. Every integration here has a
  step no script may take - a UI action inside a client - and the difference
  between naming that step and
  quietly reporting success is the difference between a status file and a story.
#>

$script:HorizunIntegrationStates = @(
    'configured', 'verified', 'pending_client_restart', 'pending_user_action', 'unsupported', 'failed'
)


# -----------------------------------------------------------------------------
# ATOMIC REPLACE OF A STATUS FILE, SAFE WHEN THE DESTINATION EXISTS OR IS IN USE.
#
# Field report (Pro 3.7.0, end-to-end install): install-status.json ended in
# completion_failed with "Cannot create a file when that file already exists.
# Attempt 1 of 3", a generation-<guid>.json.tmp-... was left behind, and an
# earlier run ended the same way with "file in use". Move-Item -Force is not an
# atomic replace: on Windows PowerShell it deletes the destination and then moves,
# so a second writer (the detached worker, the registrar) or a reader holding the
# file (antivirus, a status reader) turns it into exactly those two errors.
#
# Publish-HorizunAtomicFile replaces with [IO.File]::Replace when the destination
# exists (one NTFS operation; readers see the old or the new file, never none),
# moves when it does not, retries sharing/lock failures with backoff, and on a
# final failure deletes its own temporary file and throws the last error.
# Remove-HorizunOrphanTempFiles sweeps "<dest>.tmp-*" left by runs that died.
# -----------------------------------------------------------------------------
function Publish-HorizunAtomicFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$TempPath,
        [Parameter(Mandatory = $true)][string]$Destination,
        [int]$Attempts = 8,
        [int]$DelayMs = 125
    )
    # [IO.File] resolves relative paths against the process directory, not the
    # PowerShell location the caller sees; resolve both once against the latter.
    $TempPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($TempPath)
    $Destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
    $last = $null
    try {
        for ($i = 1; $i -le $Attempts; $i++) {
            try {
                if ([IO.File]::Exists($Destination)) {
                    # Read-only/hidden attributes make Replace fail with access denied.
                    $attrs = [IO.File]::GetAttributes($Destination)
                    if ($attrs -band ([IO.FileAttributes]::ReadOnly -bor [IO.FileAttributes]::Hidden)) {
                        [IO.File]::SetAttributes($Destination, [IO.FileAttributes]::Normal)
                    }
                    [IO.File]::Replace($TempPath, $Destination, [NullString]::Value, $true)
                }
                else {
                    [IO.File]::Move($TempPath, $Destination)
                }
                return
            }
            catch [System.IO.FileNotFoundException] {
                # The destination vanished between the check and Replace (another writer
                # deleting it): the next attempt moves instead. A missing TEMP file is fatal.
                $last = $_.Exception
                if (-not [IO.File]::Exists($TempPath)) { throw }
            }
            catch [System.IO.IOException], [System.UnauthorizedAccessException] {
                # "already exists" (a writer recreated it between check and move), "in use"
                # (a reader or scanner holds it), access denied while a handle is closing.
                $last = $_.Exception
            }
            if ($i -lt $Attempts) { Start-Sleep -Milliseconds ($DelayMs * $i) }
        }
        throw $last
    }
    finally {
        if ([IO.File]::Exists($TempPath)) { try { [IO.File]::Delete($TempPath) } catch { } }
    }
}

function Write-HorizunAtomicText {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text,
          [string]$Encoding = 'UTF8')
    $Path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $tmp = "$Path.tmp-$([guid]::NewGuid().ToString('N'))"
    Set-Content -LiteralPath $tmp -Value $Text -Encoding $Encoding
    Publish-HorizunAtomicFile -TempPath $tmp -Destination $Path
}

function Remove-HorizunOrphanTempFiles {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path, [int]$OlderThanMinutes = 10)
    $dir = Split-Path -Parent $Path
    if (-not $dir -or -not (Test-Path -LiteralPath $dir -PathType Container)) { return 0 }
    $cutoff = (Get-Date).AddMinutes(-$OlderThanMinutes)
    $removed = 0
    # Only this file's own temporaries (<leaf>.tmp-*, <leaf>.generation-*.json.tmp-*,
    # <leaf>.current.tmp-*), never anything else in the folder.
    $leaf = Split-Path -Leaf $Path
    foreach ($f in @(Get-ChildItem -LiteralPath $dir -File -ErrorAction SilentlyContinue | Where-Object {
                $_.Name.StartsWith("$leaf.", [StringComparison]::OrdinalIgnoreCase) -and $_.Name -like '*.tmp-*' -and
                $_.LastWriteTime -lt $cutoff })) {
        try { Remove-Item -LiteralPath $f.FullName -Force -ErrorAction Stop; $removed++ } catch { }
    }
    return $removed
}

function Get-HorizunStatusPath {
    param([string]$StatusPath)
    if ($StatusPath) { return $StatusPath }
    return (Join-Path $env:LOCALAPPDATA 'Horizun\install-status.json')
}

function Get-HorizunIntegrationStatus {
    <# The whole integrations block, or $null. Never throws on a damaged file. #>
    [CmdletBinding()]
    param([string]$StatusPath)
    $path = Get-HorizunStatusPath $StatusPath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    try { $doc = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { return $null }
    if ($null -eq $doc) { return $null }
    $prop = $doc.PSObject.Properties['integrations']
    if ($null -eq $prop) { return $null }
    return $prop.Value
}

function Set-HorizunIntegrationState {
    <#
      Read, modify, write - preserving every other property in the file, including
      the ones complete-install.ps1 owns. A whole-document write here would erase
      a pending completion generation and leave a resume entry pointing at work
      nothing is tracking any more.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Client,
        [Parameter(Mandatory = $true)][string]$State,
        [Parameter(Mandatory = $true)][string]$Detail,
        # The single step the user must take, when there is one. Required for
        # pending_user_action, refused for configured and verified.
        [string]$PendingUserAction,
        $Evidence,
        [string]$StatusPath
    )
    if ($State -notin $script:HorizunIntegrationStates) {
        throw ("'{0}' is not an integration state. The six are: {1}" -f $State, ($script:HorizunIntegrationStates -join ', '))
    }
    if ($State -eq 'pending_user_action' -and -not $PendingUserAction) {
        throw 'pending_user_action without naming the action is a status file that says "wait" and nothing else.'
    }
    if ($PendingUserAction -and $State -in @('configured', 'verified')) {
        throw "a $State integration has no pending user action; report the state that is actually true."
    }

    $path = Get-HorizunStatusPath $StatusPath
    $dir = Split-Path -Parent $path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $doc = $null
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        try { $doc = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { $doc = $null }
    }
    if ($null -eq $doc) {
        # No installation status yet: the integration record is still worth
        # keeping, so start a minimal document rather than dropping the fact.
        $doc = [pscustomobject]@{ schema = 1; updated_utc = (Get-Date).ToUniversalTime().ToString('o')
                                  state = 'unknown'
                                  detail = 'No installation status existed when an integration recorded its state.' }
    }

    $integrations = $null
    $prop = $doc.PSObject.Properties['integrations']
    if ($prop) { $integrations = $prop.Value }
    if ($null -eq $integrations) { $integrations = [pscustomobject]@{} }

    $entry = [ordered]@{
        state       = $State
        detail      = $Detail
        updated_utc = (Get-Date).ToUniversalTime().ToString('o')
    }
    if ($PendingUserAction) { $entry['pending_user_action'] = $PendingUserAction }
    if ($null -ne $Evidence) { $entry['evidence'] = $Evidence }
    $previous = $integrations.PSObject.Properties[$Client]
    if ($previous -and $null -ne $previous.Value) {
        # Keep one complete preceding attempt, without an unbounded nested chain.
        $snapshot = [ordered]@{}
        foreach ($field in $previous.Value.PSObject.Properties) {
            if ($field.Name -ne 'previous_attempt') { $snapshot[$field.Name] = $field.Value }
        }
        $entry['previous_attempt'] = [pscustomobject]$snapshot
    }

    $integrations | Add-Member -NotePropertyName $Client -NotePropertyValue ([pscustomobject]$entry) -Force
    $doc | Add-Member -NotePropertyName 'integrations' -NotePropertyValue $integrations -Force

    # Depth matters here exactly as it does in the client registrar: the default
    # of 2 turns nested objects into the literal text "System.Object[]".
    $out = $doc | ConvertTo-Json -Depth 30
    $null = $out | ConvertFrom-Json      # never replace a status file with one that will not parse
    Write-HorizunAtomicText -Path $path -Text $out
    return [pscustomobject]$entry
}

function Remove-HorizunIntegrationState {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Client, [string]$StatusPath)
    $path = Get-HorizunStatusPath $StatusPath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    try { $doc = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { return $false }
    $prop = $doc.PSObject.Properties['integrations']
    if (-not $prop -or $null -eq $prop.Value) { return $false }
    if ($Client -notin @($prop.Value.PSObject.Properties.Name)) { return $false }
    $prop.Value.PSObject.Properties.Remove($Client)
    $out = $doc | ConvertTo-Json -Depth 30
    $null = $out | ConvertFrom-Json
    Write-HorizunAtomicText -Path $path -Text $out
    return $true
}
