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

    $integrations | Add-Member -NotePropertyName $Client -NotePropertyValue ([pscustomobject]$entry) -Force
    $doc | Add-Member -NotePropertyName 'integrations' -NotePropertyValue $integrations -Force

    # Depth matters here exactly as it does in the client registrar: the default
    # of 2 turns nested objects into the literal text "System.Object[]".
    $out = $doc | ConvertTo-Json -Depth 30
    $null = $out | ConvertFrom-Json      # never replace a status file with one that will not parse
    $tmp = "$path.tmp-$([guid]::NewGuid().ToString('N'))"
    Set-Content -LiteralPath $tmp -Value $out -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $path -Force
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
    $tmp = "$path.tmp-$([guid]::NewGuid().ToString('N'))"
    Set-Content -LiteralPath $tmp -Value $out -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $path -Force
    return $true
}
