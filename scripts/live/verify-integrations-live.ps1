#Requires -Version 5.1
<#
  The integration checks that need this machine's real clients, run for real -
  and each one labelled with what it is actually worth.

  EVERY RESULT CARRIES AN EVIDENCE LEVEL, because "8 passed" said next to a
  client integration reads as "the client works", and none of these checks can
  earn that:

    unit              logic, in isolation
    offline_contract  a contract or artifact checked without any client
    local_process     the server launched HERE and answered - no client involved
    client_detection  the client was found and read on disk - it did not run
    client_e2e        the CLIENT ITSELF discovered the tools and called one

  THIS FILE CANNOT PRODUCE A SINGLE client_e2e AND DOES NOT TRY. Detecting Claude
  Desktop, exercising a copy of its configuration and launching the server
  directly are all useful; not one of them is Claude Desktop invoking
  horizun_health. The totals are reported per level so the difference cannot be
  read past.

  IT DOES NOT INSTALL ANYTHING. Installing the extension is a step inside Claude
  Desktop's own UI; replacing the installed server is the owner's decision. Both
  are reported as pending, named, with everything before them verified.

    pwsh -File scripts/live/verify-integrations-live.ps1 -Json out.json

  Exit codes: 0 every runnable check passed   1 something failed
#>
[CmdletBinding()]
param([string]$Json, [string]$ServerPath, [string]$PackagePath)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repo 'scripts\mcp-clients.lib.ps1')
. (Join-Path $repo 'scripts\mcp-stdio.lib.ps1')
. (Join-Path $repo 'scripts\mcpb-manifest.lib.ps1')

$LEVELS = @('unit', 'offline_contract', 'local_process', 'client_detection', 'client_e2e')
$results = New-Object System.Collections.Generic.List[object]
function Record($id, $level, $what, $status, $observed, $why) {
    if ($level -notin $LEVELS) { throw "'$level' is not an evidence level. The five are: $($LEVELS -join ', ')" }
    # A check in THIS file may not call itself client_e2e. Nothing here drives a
    # client, so the label would be a claim the script cannot support - and the
    # whole point of labelling is that the label is load-bearing.
    if ($level -eq 'client_e2e') {
        throw "verify-integrations-live.ps1 drives no client; it cannot record client_e2e for '$id'."
    }
    $results.Add([pscustomobject]@{ id = $id; check = $what; evidence_level = $level; status = $status
                                    observed = $observed; why_not_passed = $why }) | Out-Null
    $colour = switch ($status) { 'passed' { 'Green' } 'failed' { 'Red' } default { 'Yellow' } }
    Write-Host ("  {0,-4} {1,-17} {2,-9} {3}" -f $id, $level, $status, $what) -ForegroundColor $colour
    if ($observed) { Write-Host ("         {0}" -f $observed) -ForegroundColor DarkGray }
    if ($why) { Write-Host ("         {0}" -f $why) -ForegroundColor DarkGray }
}

if (-not $ServerPath) { $ServerPath = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe' }
if (-not $PackagePath) {
    $props = [xml](Get-Content (Join-Path $repo 'Directory.Build.props'))
    $v = [string]($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    $PackagePath = Join-Path $repo "dist\stage\server\integrations\claude-desktop\horizun-revit-$v.mcpb"
}

Write-Host ""
Write-Host "Live integration checks" -ForegroundColor Cyan

# --- L1: the command the extension declares, expanded the way the spec says ----
$pkg = Get-HorizunMcpbManifestFromPackage -Path $PackagePath
$declared = [string]$pkg.Manifest.server.mcp_config.command
# ${HOME} is the documented substitution. Expanding it HERE is the whole point:
# it proves the shipped string resolves to a real executable on a real machine.
$expanded = $declared.Replace('${HOME}', $env:USERPROFILE.Replace('\', '/')).Replace('/', '\')
Record 'L1' 'offline_contract' 'the extension command, with ${HOME} expanded, is a file that exists' `
    $(if (Test-Path -LiteralPath $expanded -PathType Leaf) { 'passed' } else { 'failed' }) `
    "declared: $declared" `
    $(if (Test-Path -LiteralPath $expanded -PathType Leaf) { $null } else { "expanded to $expanded, which does not exist" })

# --- L2: it is not merely a file - it speaks MCP --------------------------------
$probe = $null
if (Test-Path -LiteralPath $expanded -PathType Leaf) {
    $probe = Invoke-HorizunMcpProbe -Command $expanded -ListTools -TimeoutSec 120
    Record 'L2' 'local_process' 'that command completes initialize and answers tools/list' `
        $(if ($probe.ok) { 'passed' } else { 'failed' }) `
        $(if ($probe.ok) { "$($probe.server_info.name) $($probe.server_info.version), protocol $($probe.protocol_version), $($probe.tool_count) tools, listChanged=$($probe.list_changed)" } else { $null }) `
        $probe.problem
}
else { Record 'L2' 'local_process' 'that command completes initialize and answers tools/list' 'not_run' $null 'the command does not exist' }

# --- L3: dynamic tool changes are advertised ------------------------------------
# Claude Desktop refreshes its tool list on notification rather than on restart.
# A server that does not advertise listChanged silently requires
# a restart after the owner grants Python, and nobody is told why.
Record 'L3' 'local_process' 'the server advertises tools.listChanged, so a granted tool appears without a restart' `
    $(if ($probe -and $probe.list_changed) { 'passed' } elseif ($probe) { 'failed' } else { 'not_run' }) `
    $(if ($probe) { "capabilities.tools.listChanged = $($probe.list_changed)" } else { $null }) `
    $(if ($probe -and -not $probe.list_changed) { 'the server did not advertise it' } else { $null })

# --- L4: Revit closed is a sentence, not a crash --------------------------------
$revit = @(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue)
if (Test-Path -LiteralPath $expanded -PathType Leaf) {
    $h = Invoke-HorizunMcpProbe -Command $expanded -CallTool 'horizun_health' -TimeoutSec 120
    $text = ($h.call_result | ConvertTo-Json -Depth 6 -Compress)
    if ($revit.Count -eq 0) {
        $named = $h.ok -and $h.call_is_error -and $text -match '(?i)revit'
        Record 'L4' 'local_process' 'with Revit CLOSED, horizun_health answers a diagnosable error rather than hanging or crashing' `
            $(if ($named) { 'passed' } else { 'failed' }) `
            ("isError=$($h.call_is_error), " + $text.Substring(0, [Math]::Min(160, $text.Length))) `
            $(if ($named) { $null } else { 'the reply did not name Revit as the missing half' })
    }
    else {
        $healthy = $h.ok -and -not $h.call_is_error
        Record 'L4' 'local_process' 'with Revit RUNNING, horizun_health answers healthy through the extension''s own command' `
            $(if ($healthy) { 'passed' } else { 'failed' }) `
            $text.Substring(0, [Math]::Min(220, $text.Length)) `
            $(if ($healthy) { $null } else { 'health did not come back clean' })
    }
}
else { Record 'L4' 'local_process' 'horizun_health through the extension command' 'not_run' $null 'the command does not exist' }

# --- L5: a real Claude Desktop, found where it really keeps its files -----------
$cd = Get-HorizunClaudeDesktop
Record 'L5' 'client_detection' 'Claude Desktop is detected, with its configuration and extension store located' `
    $(if ($cd.installed -and $cd.config_exists) { 'passed' } elseif ($cd.installed) { 'failed' } else { 'not_run' }) `
    $(if ($cd.installed) { "version $($cd.version), $($cd.packaging) packaging; $($cd.extensions.Count) extension(s) already installed" } else { $null }) `
    $(if (-not $cd.installed) { 'Claude Desktop is not installed on this machine' } elseif (-not $cd.config_exists) { 'the configuration file was not found' } else { $null })

# --- L6: existing clients keep working ------------------------------------------
$existing = Get-HorizunExistingClients
$stillThere = @($existing | Where-Object { $_.registered })
Record 'L6' 'client_detection' 'the clients that already worked are still registered, untouched by this work' `
    $(if ($stillThere.Count -eq $existing.Count) { 'passed' } else { 'failed' }) `
    (($existing | ForEach-Object { "$($_.client)=$($_.registered)" }) -join ', ') `
    $(if ($stillThere.Count -ne $existing.Count) { 'a client that was registered no longer is' } else { $null })

# --- L7: no listener, anywhere ---------------------------------------------------
# The security claim is that the server opens no port. Measured, not asserted:
# launch it and look at this machine's listening sockets for its process id.
if (Test-Path -LiteralPath $expanded -PathType Leaf) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $expanded
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $p = [Diagnostics.Process]::Start($psi)
    try {
        $p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"listener-probe","version":"1"}}}')
        $p.StandardInput.Flush()
        Start-Sleep -Seconds 3
        $listening = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
                       Where-Object { $_.OwningProcess -eq $p.Id })
        Record 'L7' 'local_process' 'the MCP server opens NO listening socket' `
            $(if ($listening.Count -eq 0) { 'passed' } else { 'failed' }) `
            ("pid $($p.Id): $($listening.Count) listening socket(s)") `
            $(if ($listening.Count -gt 0) { (($listening | ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)" }) -join ', ') } else { $null })
    }
    finally {
        try { $p.StandardInput.Close() } catch { }
        if (-not $p.WaitForExit(10000)) { try { $p.Kill() } catch { } }
    }
}
else { Record 'L7' 'local_process' 'the MCP server opens no listening socket' 'not_run' $null 'the command does not exist' }

$passed = @($results | Where-Object { $_.status -eq 'passed' })
$failed = @($results | Where-Object { $_.status -eq 'failed' })
$other = @($results | Where-Object { $_.status -notin @('passed', 'failed') })
$e2e = @($results | Where-Object { $_.evidence_level -eq 'client_e2e' -and $_.status -eq 'passed' })

Write-Host ""
Write-Host "  By evidence level - the number that matters is the last one" -ForegroundColor Cyan
foreach ($lvl in $LEVELS) {
    $rows = @($results | Where-Object { $_.evidence_level -eq $lvl })
    if ($rows.Count -eq 0) { continue }
    Write-Host ("    {0,-17} {1} passed / {2} total" -f $lvl,
        @($rows | Where-Object { $_.status -eq 'passed' }).Count, $rows.Count)
}
Write-Host ("    {0,-17} {1}" -f 'client_e2e', $e2e.Count) -ForegroundColor $(if ($e2e.Count -gt 0) { 'Green' } else { 'Yellow' })
Write-Host ""
Write-Host ("  {0} passed, {1} failed, {2} not run or blocked - and {3} of them are end-to-end through a client." `
    -f $passed.Count, $failed.Count, $other.Count, $e2e.Count) `
    -ForegroundColor $(if ($failed.Count -eq 0) { 'Green' } else { 'Red' })
Write-Host "  No client ran a Horizun tool in this file. That is a separate, external step." -ForegroundColor Yellow

if ($Json) {
    $dir = Split-Path -Parent $Json
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    [pscustomobject]@{
        generated_utc   = (Get-Date).ToUniversalTime().ToString('o')
        commit          = (& git -C $repo rev-parse HEAD)
        clean_tree      = ((@(& git -C $repo status --porcelain 2>$null) -join '').Trim().Length -eq 0)
        # Repo-relative, and %USERPROFILE% for anything outside it. The account
        # name reached evidence through this field: expanded_command was redacted
        # and this one was not, which is exactly how a leak survives a fix.
        package         = ($PackagePath -replace [regex]::Escape($repo + '\'), '' `
                                        -replace [regex]::Escape($env:USERPROFILE), '%USERPROFILE%')
        package_sha256  = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToLower()
        declared_command = $declared
        # REDACTED, and this is not decoration: the flattened account name has
        # reached evidence before. What matters is that the substitution resolved
        # to a real executable, which is L1's verdict - not which account it
        # resolved for.
        expanded_command = ($expanded -replace [regex]::Escape($env:USERPROFILE), '%USERPROFILE%')
        revit_running   = ($revit.Count -gt 0)
        claude_desktop  = [ordered]@{ installed = $cd.installed; version = $cd.version; packaging = $cd.packaging
                                      other_extension_count = $cd.extensions.Count }
        results         = $results
        totals          = [ordered]@{ passed = $passed.Count; failed = $failed.Count; not_run_or_blocked = $other.Count }
        totals_by_evidence_level = $(
            $byLevel = [ordered]@{}
            foreach ($lvl in $LEVELS) {
                $rows = @($results | Where-Object { $_.evidence_level -eq $lvl })
                $byLevel[$lvl] = [ordered]@{ total = $rows.Count
                                             passed = @($rows | Where-Object { $_.status -eq 'passed' }).Count }
            }
            [pscustomobject]$byLevel)
        client_e2e_passed = $e2e.Count
        client_e2e_means  = ('A client discovered the tools and called one. This harness drives no client, so it ' +
                             'cannot produce this level and refuses to record it. Claude Desktop invoking ' +
                             'horizun_health is the external step that would.')
    } | ConvertTo-Json -Depth 8 | Out-File -FilePath $Json -Encoding utf8
    Write-Host "  wrote $Json"
}

if ($failed.Count -gt 0) { exit 1 }
exit 0
