# -----------------------------------------------------------------------------
# Horizun Revit MCP — original Horizun code.
#
# The part of the year-matrix driver that decides whether a Revit may be closed,
# and how. Dot-sourced by run-year-matrix.ps1; every outside effect goes through
# a $Probes table so the same decisions can be exercised against helper
# processes and canned answers without a Revit and without anybody's session.
#
# THE RULE THIS FILE EXISTS FOR: having started a process does not prove it is
# still exclusively yours. On 2026-09-08 a rehearsal started a Revit, the person
# at the machine began working in it, and an automatic "close what I started"
# closed their session. So:
#
#   - a session is identified by pid AND start time AND executable path, and the
#     identity is re-checked immediately before anything is closed;
#   - the documents open at that moment are read through the bridge and matched
#     against a REGISTER of what this run itself opened, by the identity the
#     bridge actually publishes - the document's path, normalised - because a
#     TITLE IS NOT PROOF OF OWNERSHIP: '^HZ_' matched HZ_PROYECTO_USUARIO just
#     as well as it matched a fixture, and the bridge's own anchor is identified
#     by living under the bridge's anchor directory, not by being called
#     HZ_ANCHOR-something;
#   - one document this run cannot account for, one identity that does not
#     match, one answer the bridge cannot give (INCLUDING an open-document list
#     the bridge itself flags as incomplete - that is not "zero documents"), and
#     the process is LEFT RUNNING;
#   - only this run's own registered fixtures are closed, aimed at the PATH the
#     register holds, through the bridge's own rehearsal-then-token close, and
#     the rehearsal's resolved title/path is compared with the intended document
#     before the token is spent: a close is never allowed to land on a document
#     other than the one that was proved to be ours;
#   - the session and the open set are re-read before EVERY close and once more
#     before the process is asked to exit, so a document that appears mid-way
#     stops the sequence;
#   - the process is asked to exit normally; it is never killed;
#   - no dialog is answered by its title, no click goes anywhere, no window is
#     brought to the front;
#   - a manifest is restored only when no Revit of that year is running AND no
#     Revit whose year could not be determined is running (an unreadable
#     MainModule is not "no Revit"), the restore's exit code and the disk state
#     are both checked against a snapshot taken BEFORE anything changed, and
#     anything that could not be put back - or could not be READ - is written
#     down as recovery pending instead of being forced, forgotten, or reported
#     as nothing to do.
# -----------------------------------------------------------------------------
# No Set-StrictMode here: this file is dot-sourced by the driver and strict mode
# would leak into it (pwsh 7.6: a function returning an empty array yields $null
# in the caller, and $null.Count then throws instead of reading 0). The functions
# are nevertheless written to survive strict mode, because the tests run under it.

function Get-HzNormalizedPath {
    <#
    .SYNOPSIS
      One spelling for one file: full, backslashes, no trailing separator, lower
      case. Two paths that name the same file must compare equal, or "is this
      the document I opened" is decided by typography.
    #>
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    $p = $Path.Trim().Replace('/', '\')
    try { $p = [IO.Path]::GetFullPath($p) } catch { }
    return $p.TrimEnd('\').ToLowerInvariant()
}

function Get-HzField {
    <#
    .SYNOPSIS
      One named field out of a hashtable, an ordered dictionary or an object
      parsed from JSON - $null when it is not there. Strict mode makes a missing
      PROPERTY throw, and a bridge reply with no 'open_documents' at all is
      exactly the case this code must survive rather than die inside.
    #>
    param($Object, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) { return $Object[$Name] }
        return $null
    }
    $names = @()
    try { $names = @($Object.PSObject.Properties.Name) } catch { $names = @() }
    if ($names -contains $Name) { return $Object.$Name }
    return $null
}

function Test-HzFieldPresent {
    param($Object, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Object) { return $false }
    if ($Object -is [System.Collections.IDictionary]) { return [bool]$Object.Contains($Name) }
    $names = @()
    try { $names = @($Object.PSObject.Properties.Name) } catch { $names = @() }
    return ($names -contains $Name)
}

function Get-HzAnchorDirectory {
    <#
    .SYNOPSIS
      Where the bridge keeps its own empty anchor project: <data root>\anchor,
      resolved the way the add-in resolves it (HORIZUN_DATA_ROOT first, then the
      user profile). The anchor is then identified by WHERE IT LIVES - a
      document of the user's called HZ_ANCHOR_something is not in there.
    #>
    $root = $env:HORIZUN_DATA_ROOT
    if ([string]::IsNullOrWhiteSpace($root)) { $root = Join-Path $env:USERPROFILE '.horizun' }
    return (Join-Path $root.Trim() 'anchor')
}

function Get-HzManifestAssembly {
    <#
    .SYNOPSIS
      The <Assembly> a Revit add-in manifest declares, resolved against the
      manifest's own directory when it is relative (the installed manifest says
      'Horizun\Horizun.Revit.dll'), plus the reason it could not be read.
      A FILE WITH THE RIGHT NAME IS NOT AN INSTALLATION: after a restore the
      manifest has to point at the installed DLL, not at a development copy.
    #>
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return @{ present = $false; assembly = $null; error = $null } }
    try {
        [xml]$doc = Get-Content -LiteralPath $Path -Raw
        # SelectNodes, not dynamic XML properties: a manifest without <AddIn>
        # would make a property access throw under strict mode.
        $nodes = $doc.SelectNodes('/RevitAddIns/AddIn/Assembly')
        if ($null -eq $nodes -or $nodes.Count -eq 0) {
            return @{ present = $true; assembly = $null; error = 'the manifest declares no <Assembly>' }
        }
        $raw = [string]$nodes[0].InnerText
        if ([string]::IsNullOrWhiteSpace($raw)) {
            return @{ present = $true; assembly = $null; error = 'the manifest <Assembly> is empty' }
        }
        $resolved = $raw.Trim()
        if (-not [IO.Path]::IsPathRooted($resolved)) {
            $resolved = Join-Path (Split-Path -Parent $Path) $resolved
        }
        return @{ present = $true; assembly = (Get-HzNormalizedPath $resolved); assembly_declared = $raw.Trim(); error = $null }
    }
    catch {
        return @{ present = $true; assembly = $null; error = ("the manifest could not be read: " + $_.Exception.Message) }
    }
}

function Test-HzCloseRehearsalMatches {
    <#
    .SYNOPSIS
      THE LAST LINK IN THE OWNERSHIP CHAIN. Everything above decides which
      document may be closed; this decides whether the close the bridge is about
      to perform is aimed at THAT document. The rehearsal reply names the title
      and path it resolved, and they are compared with the register's before the
      confirmation token is spent - because a target matched by name can resolve
      somewhere else, and an ownership proof that ends in an unchecked call is
      not a proof.
      Returns @{ ok; why }.
    #>
    param([string]$ExpectTitle, [string]$ExpectPath, [string]$ResolvedTitle, [string]$ResolvedPath)
    $want = Get-HzNormalizedPath $ExpectPath
    $got = Get-HzNormalizedPath $ResolvedPath
    if ($want) {
        if ($got -ne $want) {
            return @{ ok = $false
                      why = ("the close rehearsal resolved '{0}' (path '{1}'), not the document this run registered (path '{2}'). Nothing was closed." -f
                             $ResolvedTitle, $got, $want) }
        }
        return @{ ok = $true }
    }
    if ($got) {
        return @{ ok = $false
                  why = ("the close rehearsal resolved a document WITH a path ('{0}'), and the registered document had none. Nothing was closed." -f $got) }
    }
    if ($ExpectTitle -and $ResolvedTitle -and ($ResolvedTitle -ne $ExpectTitle)) {
        return @{ ok = $false
                  why = ("the close rehearsal resolved '{0}', not the registered '{1}'. Nothing was closed." -f $ResolvedTitle, $ExpectTitle) }
    }
    if (-not $ExpectTitle -and -not $ExpectPath) {
        return @{ ok = $false; why = 'nothing was registered for this close to be compared against. Nothing was closed.' }
    }
    return @{ ok = $true }
}

function ConvertFrom-HzHealthReply {
    <#
    .SYNOPSIS
      What hz-call wrote for horizun_health, turned into @{ ok; documents; ... },
      or into the reason it cannot be used.

      THE LIST HAS TO BE THERE, AND IT HAS TO BE COMPLETE. health names an
      incomplete enumeration in its own note ("The open-document list is
      INCOMPLETE: ..."), and a reply carrying no open_documents at all has not
      answered the question. Both used to arrive here as an empty array, which
      reads as "nothing is open" - the one wrong answer that ends with somebody's
      Revit being asked to exit.

      A function rather than lines inside the probe, because a probe that shells
      out to a real bridge cannot be exercised against a canned reply, and this
      is where the judgement lives.
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$ReplyPath)
    if ([string]::IsNullOrWhiteSpace($ReplyPath) -or -not (Test-Path -LiteralPath $ReplyPath)) {
        return @{ ok = $false; error = 'no reply file' }
    }
    $reply = $null
    try { $reply = Get-Content -LiteralPath $ReplyPath -Raw | ConvertFrom-Json }
    catch { return @{ ok = $false; error = ('the reply file is not JSON: ' + $_.Exception.Message); reply = $ReplyPath } }
    if ((Get-HzField $reply 'is_error') -or -not (Get-HzField $reply 'result')) {
        return @{ ok = $false; error = [string](Get-HzField $reply 'raw'); reply = $ReplyPath }
    }
    $result = Get-HzField $reply 'result'
    if (-not (Test-HzFieldPresent $result 'open_documents')) {
        return @{ ok = $false; error = 'the health reply carries no open_documents field'; reply = $ReplyPath }
    }
    $note = [string](Get-HzField $result 'note')
    if ($note -match 'INCOMPLETE') {
        return @{ ok = $false; error = ('the bridge reports its own open-document list as incomplete: ' + $note); reply = $ReplyPath }
    }
    $docs = @()
    foreach ($d in @(Get-HzField $result 'open_documents')) {
        if ($null -eq $d) { continue }
        $docs += [ordered]@{
            title = [string](Get-HzField $d 'title')
            path = [string](Get-HzField $d 'path')
            is_active = (Get-HzField $d 'is_active')
            is_linked = ((Get-HzField $d 'is_linked') -eq $true)
        }
    }
    $count = Get-HzField $result 'open_document_count'
    if ($null -ne $count -and ([int]$count -ne $docs.Count)) {
        return @{ ok = $false
                  error = ("health reports open_document_count={0} but published {1} documents; the list is not the count" -f $count, $docs.Count)
                  reply = $ReplyPath }
    }
    return @{ ok = $true; documents = $docs; document_count = $docs.Count
              process_id = (Get-HzField $result 'process_id'); modal = (Get-HzField $result 'modal_dialog')
              note = $note; reply = $ReplyPath }
}

function Read-HzCloseRehearsal {
    <#
    .SYNOPSIS
      The close rehearsal's reply: its confirmation token, but only after the
      document it resolved is the one the caller registered. Same reason as
      above - the judgement is testable here and untestable inside the probe.
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$ReplyPath, [string]$ExpectTitle, [string]$ExpectPath)
    if ([string]::IsNullOrWhiteSpace($ReplyPath) -or -not (Test-Path -LiteralPath $ReplyPath)) {
        return @{ ok = $false; error = 'no rehearsal reply' }
    }
    $dry = $null
    try { $dry = Get-Content -LiteralPath $ReplyPath -Raw | ConvertFrom-Json }
    catch { return @{ ok = $false; error = ('the rehearsal reply is not JSON: ' + $_.Exception.Message) } }
    $dryResult = Get-HzField $dry 'result'
    if ((Get-HzField $dry 'is_error') -or -not $dryResult) {
        return @{ ok = $false; error = ('close rehearsal refused: ' + [string](Get-HzField $dry 'raw')) }
    }
    # THE AIM IS CHECKED WHETHER OR NOT A TOKEN IS NEEDED. It is the point of
    # reading this reply at all.
    $aimed = Test-HzCloseRehearsalMatches -ExpectTitle $ExpectTitle -ExpectPath $ExpectPath `
        -ResolvedTitle ([string](Get-HzField $dryResult 'title')) -ResolvedPath ([string](Get-HzField $dryResult 'path'))
    if (-not $aimed.ok) { return @{ ok = $false; error = ($aimed.why + " See $ReplyPath") } }
    # A TOKEN IS ONLY ISSUED WHEN THE CLOSE WOULD LOSE WORK. The bridge says so in
    # would_discard_unsaved, and for a document with nothing to discard it answers
    # "this close would discard nothing, so it needs no token: call again with
    # dry_run=false" - and issues none. Demanding one anyway made a CLEAN fixture
    # impossible to close: measured 2026-09-09 recovering a session whose harness
    # had never run, where the refusal read as if the bridge had objected to the
    # close itself. When the flag is missing, a token is still required: not knowing
    # whether work would be lost is not permission to lose it.
    $token = [string](Get-HzField $dryResult 'confirmation_token')
    $wouldDiscard = Get-HzField $dryResult 'would_discard_unsaved'
    if ($wouldDiscard -eq $false) {
        return @{ ok = $true; token = $null; needs_token = $false
                  why = 'the rehearsal reports nothing to discard, so this close needs no token' }
    }
    if (-not $token) {
        return @{ ok = $false
                  error = ("the close rehearsal issued no confirmation token and does not report that there is nothing to discard (would_discard_unsaved='{0}'). Nothing was closed. See {1}" -f $wouldDiscard, $ReplyPath) }
    }
    return @{ ok = $true; token = $token; needs_token = $true }
}

function Read-HzCloseApply {
    <#
    .SYNOPSIS
      The close itself: closed=true with the bridge's own evidence, NOT "it did
      not return an error". An absent flag is an unverified close.
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$ReplyPath)
    if ([string]::IsNullOrWhiteSpace($ReplyPath) -or -not (Test-Path -LiteralPath $ReplyPath)) {
        return @{ ok = $false; error = 'no close reply' }
    }
    $reply = $null
    try { $reply = Get-Content -LiteralPath $ReplyPath -Raw | ConvertFrom-Json }
    catch { return @{ ok = $false; error = ('the close reply is not JSON: ' + $_.Exception.Message) } }
    if (Get-HzField $reply 'is_error') { return @{ ok = $false; error = [string](Get-HzField $reply 'raw') } }
    $result = Get-HzField $reply 'result'
    $closed = Get-HzField $result 'closed'
    if ($closed -ne $true) {
        return @{ ok = $false
                  error = ("the close reply does not report closed=true (closed='{0}'); treating it as unverified. See {1}" -f $closed, $ReplyPath) }
    }
    return @{ ok = $true; closed = $true; evidence = [string](Get-HzField $result 'closed_evidence'); reply = $ReplyPath }
}

function New-HzMatrixProbes {
    <#
    .SYNOPSIS
      The real outside world: processes, the bridge, dev-addin-session.ps1, the
      manifests on disk. Tests replace any entry.
    #>
    param([string]$Repo, [string]$ServerExe)
    $scriptsLive = Join-Path $Repo 'scripts\live'
    $hzCall = Join-Path $Repo 'scripts\hz-call.ps1'
    # A CLOSURE DOES NOT CARRY THIS FILE'S FUNCTIONS. GetNewClosure() rebinds the
    # scriptblock to a new dynamic module whose function lookup falls back to the
    # GLOBAL scope - and how the functions get there depends on how the DRIVER was
    # started: `pwsh -File run-year-matrix.ps1` dot-sources them where a closure
    # can see them, and `pwsh -Command "& run-year-matrix.ps1"` - which is the
    # form the review's own reproducible command uses - dot-sources them into a
    # child scope where it cannot. Measured 2026-09-09 against a real Revit: the
    # first health call inside the close path died with "Get-HzField is not
    # recognized", the run left the Revit alone and wrote its recovery record, and
    # the same code passed every offline test because the tests run with -File.
    # So the helpers travel as VARIABLES, which is exactly what a closure keeps.
    $readHealth = ${function:ConvertFrom-HzHealthReply}
    $readRehearsal = ${function:Read-HzCloseRehearsal}
    $readApply = ${function:Read-HzCloseApply}
    $normalise = ${function:Get-HzNormalizedPath}
    # Every block that reads $scriptsLive, $hzCall or $ServerExe ends in
    # .GetNewClosure(): without it the hashtable's scriptblocks run with those
    # names unbound and dev-addin-session is called with a null path (measured
    # 2026-09-08: every year stopped at "enable_failed ... Path is null" - the
    # guard held, nothing started, nothing was swapped).
    return @{
        GetProcess = { param([int]$ProcessId)
            $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
            if (-not $p) { return $null }
            $exe = $null; try { $exe = $p.MainModule.FileName } catch { $exe = $null }
            $start = $null; try { $start = $p.StartTime } catch { $start = $null }
            return [pscustomobject]@{ Id = $p.Id; StartTime = $start; Exe = $exe; HasExited = $p.HasExited; Process = $p }
        }
        # EVERY Revit on the machine, with what could and could not be read about
        # it. The old probe filtered by `MainModule.FileName -like "*\Revit $Year\*"`
        # inside a try/catch that swallowed the failure, so a Revit whose
        # executable could not be read DISAPPEARED from the list - and a manifest
        # was then swapped with a Revit possibly running on it. Classification
        # happens in Get-HzRevitProcessClasses; this only reports.
        RevitProcesses = {
            $rows = @()
            foreach ($p in @(Get-Process Revit -ErrorAction SilentlyContinue)) {
                $exe = $null; $exeError = $null
                try { $exe = $p.MainModule.FileName } catch { $exeError = $_.Exception.Message }
                # A process that has already exited is not a running Revit; one
                # whose exit state cannot be read is treated as running, which is
                # the safe direction.
                $exited = $false; $exitedKnown = $true
                try { $exited = [bool]$p.HasExited } catch { $exited = $false; $exitedKnown = $false }
                $start = $null; try { $start = $p.StartTime } catch { $start = $null }
                $rows += [pscustomobject]@{
                    Id = $p.Id; Exe = $exe; ExeError = $exeError
                    HasExited = $exited; ExitStateReadable = $exitedKnown; StartTime = $start
                }
            }
            return ,$rows
        }
        # What the bridge says is open in THIS year's Revit. Never guessed, and
        # never reduced to "zero documents" when the answer is incomplete.
        Health = { param([string]$Year, [string]$Dir)
            $out = Join-Path $Dir ("close-health-" + (Get-Date -Format 'yyyyMMddHHmmssfff') + ".json")
            $env:HORIZUN_SERVER_EXE = $ServerExe; $env:HORIZUN_REVIT_YEAR = $Year
            & pwsh -NoProfile -File $hzCall -Tool horizun_health -Json $out -Quiet -TimeoutSec 120 | Out-Null
            return (& $readHealth -ReplyPath $out)
        }.GetNewClosure()
        # Close ONE document this run owns: rehearsal for the token, then the
        # discard the token authorises. The rehearsal names the title and path it
        # resolved, and THAT is compared with the document the caller proved was
        # ours before the token is spent - a close aimed by name is a close that
        # can land somewhere else. Nothing else is ever discarded.
        CloseDocument = { param($Request)
            # Read through the hashtable's own indexer, not through this file's
            # helpers: see the note above New-HzMatrixProbes about what a closure
            # can and cannot see.
            $Year = [string]$Request['year']
            $Dir = [string]$Request['dir']
            $Target = [string]$Request['target']
            $expectTitle = [string]$Request['expect_title']
            $expectPath = & $normalise ([string]$Request['expect_path'])
            if ([string]::IsNullOrWhiteSpace($Target)) { return @{ ok = $false; error = 'no target was given for the close' } }
            $env:HORIZUN_SERVER_EXE = $ServerExe; $env:HORIZUN_REVIT_YEAR = $Year
            $tag = (Get-Date -Format 'yyyyMMddHHmmssfff')
            $dryArgs = Join-Path $Dir "close-$tag.dry.args.json"; $dryOut = Join-Path $Dir "close-$tag.dry.out.json"
            (@{ operation = 'close'; target_document = $Target; dry_run = $true; activate_other = $true } |
                ConvertTo-Json -Depth 5) | Set-Content -LiteralPath $dryArgs -Encoding utf8
            & pwsh -NoProfile -File $hzCall -Tool horizun_document_session -ArgumentsPath $dryArgs -Json $dryOut -Quiet -TimeoutSec 300 | Out-Null
            # WHICH DOCUMENT DID THE REHEARSAL FIND? Its own reply says, and if
            # that is not the document the caller proved was ours, the token is
            # not spent. This is the end of the ownership chain: everything above
            # decides what may be closed, and this refuses to close anything else.
            $rehearsal = & $readRehearsal -ReplyPath $dryOut -ExpectTitle $expectTitle -ExpectPath $expectPath
            if (-not $rehearsal.ok) { return @{ ok = $false; error = [string]$rehearsal.error } }
            $args2 = Join-Path $Dir "close-$tag.args.json"; $out2 = Join-Path $Dir "close-$tag.out.json"
            $applyArgs = @{ operation = 'close'; target_document = $Target
                            discard_unsaved = $true; activate_other = $true
                            idempotency_key = "year-matrix-close-$Year-$tag" }
            # Only when one was issued: a token is the answer to "this would lose
            # work", and a document with nothing to lose is not asked the question.
            if ($rehearsal.token) { $applyArgs['confirmation_token'] = $rehearsal.token }
            ($applyArgs | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath $args2 -Encoding utf8
            & pwsh -NoProfile -File $hzCall -Tool horizun_document_session -ArgumentsPath $args2 -Json $out2 -Quiet -TimeoutSec 300 | Out-Null
            return (& $readApply -ReplyPath $out2)
        }.GetNewClosure()
        # Ask the process to exit the way a person would: its main window's close.
        CloseMainWindow = { param($ProcessInfo)
            try { return [bool]$ProcessInfo.Process.CloseMainWindow() } catch { return $false }
        }
        WaitExit = { param($ProcessInfo, [int]$Seconds)
            try { return [bool]$ProcessInfo.Process.WaitForExit($Seconds * 1000) } catch { return $false }
        }
        # Every visible top-level window of THIS process, with its child texts and the
        # handle of a button labelled OK. Read-only; Test-HzCrashDialog decides.
        ProcessWindows = { param($ProcessInfo)
            Initialize-HzExitNative
            $procId = [uint32]$ProcessInfo.Process.Id
            $found = New-Object System.Collections.Generic.List[object]
            $cb = [HzExit.Native+EnumProc] { param($h, $p)
                $o = [uint32]0; $null = [HzExit.Native]::GetWindowThreadProcessId($h, [ref]$o)
                if ($o -eq $procId -and [HzExit.Native]::IsWindowVisible($h)) { $found.Add($h) }
                return $true }
            $null = [HzExit.Native]::EnumWindows($cb, [IntPtr]::Zero)
            $rows = @()
            foreach ($w in $found) {
                $texts = New-Object System.Collections.Generic.List[string]; $ok = $null
                $ccb = [HzExit.Native+EnumProc] { param($h, $p)
                    $t = [HzExit.Native]::Text($h)
                    if ($t) { $texts.Add($t); if ($t -eq 'OK' -and [HzExit.Native]::Class($h) -eq 'Button') { $script:HzOkHandle = $h } }
                    return $true }
                $script:HzOkHandle = $null
                $null = [HzExit.Native]::EnumChildWindows($w, $ccb, [IntPtr]::Zero)
                $rows += [pscustomobject]@{ title = [HzExit.Native]::Text($w); class = [HzExit.Native]::Class($w)
                                            texts = @($texts); handle = $w; ok_handle = $script:HzOkHandle }
            }
            return ,$rows
        }
        ConfirmCrashDialog = { param($Window)
            Initialize-HzExitNative
            try { $null = [HzExit.Native]::SendMessage($Window.ok_handle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero); return $true } catch { return $false }
        }
        Enable = { param([string]$Year)
            # The script's lines go to the host, not into the return value: a function
            # returns EVERYTHING it emits, and "[lines..., 0]" is not an exit code of 0
            # (measured 2026-09-08: every year read as enable_failed after a successful
            # enable, then as restore_failed after a successful restore).
            & pwsh -NoProfile -File (Join-Path $scriptsLive 'dev-addin-session.ps1') -Year $Year -Enable 2>&1 | ForEach-Object { Write-Host $_ }
            return [int]$LASTEXITCODE
        }.GetNewClosure()
        Restore = { param([string]$Year)
            & pwsh -NoProfile -File (Join-Path $scriptsLive 'dev-addin-session.ps1') -Year $Year -Restore 2>&1 | ForEach-Object { Write-Host $_ }
            return [int]$LASTEXITCODE
        }.GetNewClosure()
        ManifestState = { param([string]$Year)
            $dir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year"
            $installed = Get-HzManifestAssembly (Join-Path $dir 'Horizun.addin')
            $dev = Get-HzManifestAssembly (Join-Path $dir 'Horizun-dev-session.addin')
            $aside = Get-HzManifestAssembly (Join-Path $dir 'Horizun.addin.dev-session-aside')
            return @{
                installed_present = [bool]$installed.present
                installed_assembly = $installed.assembly
                installed_manifest_error = $installed.error
                dev_present = [bool]$dev.present
                dev_assembly = $dev.assembly
                aside_present = [bool]$aside.present
                aside_assembly = $aside.assembly
                expected_installed_assembly = (Get-HzNormalizedPath (Join-Path $dir 'Horizun\Horizun.Revit.dll'))
                dev_dll = (Join-Path $env:USERPROFILE ".horizun\dev-addin\$Year\Horizun\Horizun.Revit.dll")
            }
        }
        # PRESENT, HASH, AND THE REASON THERE IS NO HASH - three different facts.
        # The old probe returned $null both for "no DLL" and for "the DLL could
        # not be hashed", and every comparison downstream was written as
        # `$expected -and $actual -and ...`, which SKIPS the comparison exactly
        # when the file it should be protecting has gone missing.
        InstalledDllState = { param([string]$Year)
            $dll = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year\Horizun\Horizun.Revit.dll"
            if (-not (Test-Path -LiteralPath $dll)) { return @{ present = $false; sha256 = $null; error = $null; path = $dll } }
            try { return @{ present = $true; sha256 = (Get-FileHash -LiteralPath $dll).Hash.ToLower(); error = $null; path = $dll } }
            catch { return @{ present = $true; sha256 = $null; error = $_.Exception.Message; path = $dll } }
        }
    }
}

function Get-HzRepoStatus {
    # `git status --porcelain` as an array of lines; @() when the tree is clean.
    # The driver used to compute "clean" as `[string](& git ...) -eq ''`, which
    # is False even on a clean tree: an empty command result cast to [string]
    # is $null, and $null -eq '' is false. Every summary of 2026-09-08 said
    # repo_tracked_clean=false while each harness record said clean. Count the
    # lines instead, and keep them so a dirty run explains itself.
    param([Parameter(Mandatory)][string]$Repo)
    $lines = @(& git -C $Repo status --porcelain 2>$null | Where-Object { $null -ne $_ -and $_ -ne '' })
    return ,$lines
}

function ConvertTo-HzExitCode {
    <#
    .SYNOPSIS
      The last integer a probe emitted, or null. A probe that lets the child
      process's lines through returns an array; the exit code is its last item.
    #>
    param($Value)
    if ($null -eq $Value) { return $null }
    $items = @($Value)
    if ($items.Count -eq 0) { return $null }
    $last = $items[-1]
    $n = 0
    if ([int]::TryParse([string]$last, [ref]$n)) { return $n }
    return $null
}

function Get-HzRevitProcessClasses {
    <#
    .SYNOPSIS
      Every Revit on the machine, in three named groups: the TARGET year, ANOTHER
      year that could be identified, and one whose year could NOT be determined.
      The third group is the one that matters: a Revit whose MainModule cannot be
      read is not "no Revit", and the old filter dropped it silently - which is
      how a manifest could be swapped under a running Revit.
      A process that has already exited is not counted; one whose exit state
      cannot be read is, because that is the safe direction.
    #>
    param([Parameter(Mandatory)]$Probes, [Parameter(Mandatory)][string]$Year)
    $target = @(); $other = @(); $unknown = @()
    # ASSIGN, THEN WRAP. The probe returns `,$rows` so that a direct assignment
    # always yields an array; `@(& $Probes.RevitProcesses)` then produces an array
    # whose single element is that array, and every field read off it comes back
    # null - which classified this machine's perfectly readable Revit 2025 as "a
    # process whose year could not be determined" (measured 2026-09-09, before
    # this line existed). Assigning first unrolls the protective wrapper, and the
    # @() after it works for a probe that returns a bare array too.
    $processes = & $Probes.RevitProcesses
    foreach ($p in @($processes)) {
        if ($null -eq $p) { continue }
        $exited = Get-HzField $p 'HasExited'
        $exitReadable = Get-HzField $p 'ExitStateReadable'
        if (($exited -eq $true) -and ($exitReadable -ne $false)) { continue }   # gone between listing and reading
        $exe = [string](Get-HzField $p 'Exe')
        $row = [ordered]@{ pid = (Get-HzField $p 'Id'); exe = $exe; year = $null
                           why = $null; exe_error = [string](Get-HzField $p 'ExeError') }
        if ([string]::IsNullOrWhiteSpace($exe)) {
            $row.why = if ($row.exe_error) { 'its executable could not be read: ' + $row.exe_error }
                       else { 'its executable path came back empty' }
            $unknown += $row
            continue
        }
        $m = [regex]::Match($exe, '\\Revit\s+(\d{4})\\', 'IgnoreCase')
        if (-not $m.Success) {
            $row.why = "its executable path '$exe' does not name a Revit year"
            $unknown += $row
            continue
        }
        $row.year = $m.Groups[1].Value
        if ($row.year -eq $Year) { $target += $row } else { $other += $row }
    }
    return @{ target = @($target); other_year = @($other); unknown = @($unknown) }
}

function Test-HzManifestChangeAllowed {
    <#
    .SYNOPSIS
      May this run rename a manifest for $Year right now? Only with no Revit of
      that year running and no Revit whose identity could not be determined.
      Called immediately before the change, never once at the top of a year:
      a Revit can start while a harness runs.
      Returns @{ ok; blocked_by; pids; why; classes }.
    #>
    param([Parameter(Mandatory)]$Probes, [Parameter(Mandatory)][string]$Year)
    $c = Get-HzRevitProcessClasses -Probes $Probes -Year $Year
    if ($c.target.Count -gt 0) {
        return @{ ok = $false; blocked_by = 'target_year'; classes = $c
                  pids = @($c.target | ForEach-Object { $_.pid })
                  why = ("Revit {0} is running (pid {1}); a manifest is not swapped under a running Revit." -f
                         $Year, (($c.target | ForEach-Object { $_.pid }) -join ',')) }
    }
    if ($c.unknown.Count -gt 0) {
        $detail = ($c.unknown | ForEach-Object { "pid $($_.pid) ($($_.why))" }) -join '; '
        return @{ ok = $false; blocked_by = 'unknown_year'; classes = $c
                  pids = @($c.unknown | ForEach-Object { $_.pid })
                  why = ("a Revit process is running whose year could not be determined, so it MAY be Revit {0}: {1}. " -f $Year, $detail) +
                        "Nothing is renamed on a maybe; the process is not touched and no permission is escalated to find out." }
    }
    return @{ ok = $true; blocked_by = $null; pids = @(); why = $null; classes = $c }
}

function New-HzSessionIdentity {
    <#
    .SYNOPSIS
      Pid + start time + executable: the three facts that together say "this is
      the process I started", re-read from the OS rather than remembered.
    #>
    param([Parameter(Mandatory)]$Probes, [Parameter(Mandatory)][int]$ProcessId, [string]$ExpectedExe, [int]$SettleSeconds = 15)
    # A process just started may not answer for its main module yet (measured
    # 2026-09-08: Revit 2025 reported no executable at the instant it was started,
    # and a later comparison would have called it "not ours"). Ask again for a
    # few seconds; what is recorded is what was READ, plus what was expected.
    $p = $null
    $deadline = (Get-Date).AddSeconds($SettleSeconds)
    do {
        $p = & $Probes.GetProcess $ProcessId
        if (-not $p) { throw "process $ProcessId is not running; no identity can be recorded" }
        if ($p.Exe -and $p.StartTime) { break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    if ($ExpectedExe -and $p.Exe -and ($p.Exe -ne $ExpectedExe)) {
        throw "process $ProcessId runs '$($p.Exe)', not '$ExpectedExe'; refusing to adopt it"
    }
    return [ordered]@{
        pid = $p.Id
        start_time = $(if ($p.StartTime) { $p.StartTime.ToUniversalTime().ToString('o') } else { $null })
        exe = $p.Exe
        exe_expected = $ExpectedExe
    }
}

function Get-HzSessionIdentityState {
    <#
    .SYNOPSIS
      alive | exited | mismatch | unknown, judged against the recorded identity.
      A reused pid with another start time or another executable is 'mismatch'.
    #>
    param([Parameter(Mandatory)]$Probes, [Parameter(Mandatory)]$Identity)
    $p = & $Probes.GetProcess ([int]$Identity.pid)
    if (-not $p -or $p.HasExited) { return @{ state = 'exited'; process = $null } }
    if ($null -eq $p.StartTime -or -not $p.Exe) { return @{ state = 'unknown'; process = $p; why = 'start time or executable unreadable' } }
    # THE RECORDED TIME HAS TO BE READ THE WAY IT WAS WRITTEN. New-HzSessionIdentity
    # writes round-trip ('o'), but an identity that has been through JSON comes back
    # as a DateTime object, and [string] on that yields the LOCAL CULTURE's spelling
    # with no offset and no fractional seconds - which [DateTimeOffset]::Parse then
    # read as a local time five hours away from itself. Measured 2026-09-09
    # recovering a real session: the process was the right one and the comparison
    # said 'mismatch', which strands a Revit for a reason that is not true.
    # A time that cannot be read round-trip is 'unknown', never a mismatch: not
    # being able to compare is not the same as having compared.
    $raw = Get-HzField $Identity 'start_time'
    $recorded = $null
    if ($raw -is [DateTimeOffset]) { $recorded = $raw }
    elseif ($raw -is [datetime]) { $recorded = [DateTimeOffset]$raw }
    elseif (-not [string]::IsNullOrWhiteSpace([string]$raw)) {
        # ISO 8601 WITH AN EXPLICIT OFFSET, and nothing else. TryParseExact with 'o'
        # is not the check: DateTime.ToString('o') ends in 'Z' and DateTimeOffset's
        # 'o' wants '+00:00', so the exact-format test rejected this file's own
        # timestamps. The shape is asserted here and the offset is required, so a
        # local-culture spelling - or an ISO string with no offset, which means
        # nothing on its own - is 'unknown' rather than quietly shifted by hours.
        $parsed = [DateTimeOffset]::MinValue
        if (([string]$raw -match '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$') -and
            [DateTimeOffset]::TryParse([string]$raw, [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind, [ref]$parsed)) { $recorded = $parsed }
        else {
            return @{ state = 'unknown'; process = $p
                      why = ("the recorded start time '{0}' is not a round-trip timestamp, so it cannot be compared with the one the OS reports" -f $raw) }
        }
    }
    if (-not $recorded) { return @{ state = 'unknown'; process = $p; why = 'no start time was recorded' } }
    $delta = [Math]::Abs(($p.StartTime.ToUniversalTime() - $recorded.UtcDateTime).TotalSeconds)
    # The executable to compare with: the one read at start, else the one the
    # driver launched (recorded as exe_expected). Neither known -> unknown.
    $wantExe = [string](Get-HzField $Identity 'exe')
    if (-not $wantExe) { $wantExe = [string](Get-HzField $Identity 'exe_expected') }
    if (-not $wantExe) { return @{ state = 'unknown'; process = $p; why = 'no executable was recorded for the session' } }
    if ($delta -gt 2 -or ($p.Exe -ne $wantExe)) {
        return @{ state = 'mismatch'; process = $p
                  why = ("pid {0} now runs '{1}' started {2:o}; recorded '{3}' started {4}" -f $p.Id, $p.Exe, $p.StartTime.ToUniversalTime(), $wantExe, $Identity.start_time) }
    }
    return @{ state = 'alive'; process = $p }
}

function New-HzRehearsalLedger {
    <#
    .SYNOPSIS
      THE REGISTER OF WHAT THIS RUN OPENED. Only a document registered here -
      matched by the identity the bridge publishes, which is its PATH - may be
      closed, plus the bridge's own anchor, recognised by living under the
      bridge's anchor directory.

      There is deliberately NO title-pattern grant any more. '^HZ_' was a grant,
      and HZ_PROYECTO_USUARIO satisfies it: a prefix is a naming convention, not
      a proof of ownership, and the difference is somebody's workday.
    #>
    param([string]$AnchorDir = (Get-HzAnchorDirectory))
    return [ordered]@{
        session_pid = $null
        session_identity = $null
        anchor_dir = (Get-HzNormalizedPath $AnchorDir)
        documents = @()            # @{ title; path; path_normalized; pathless_declared; source_file; kind; registered_utc; session_pid }
        files = @()                # files the rehearsal created (artifacts it may delete later; never deleted here)
        dialogs_closed = @()
        registration_failures = @()
    }
}

function Register-HzRehearsalSession {
    <#
    .SYNOPSIS
      Bind the register to ONE Revit process. A document registered under
      another session's pid is not this session's document.
    #>
    param([Parameter(Mandatory)]$Ledger, [Parameter(Mandatory)]$Identity)
    $Ledger.session_pid = [int]$Identity.pid
    $Ledger.session_identity = $Identity
}

function Add-HzRehearsalDocument {
    <#
    .SYNOPSIS
      Register ONE document this run opened or created, AFTER the bridge
      confirmed it. Either a path (the normal case) or an explicit -Pathless
      declaration (a detached open, which Revit gives no path until it is saved)
      - never a bare title, because a title alone would be exactly the proof
      this file refuses to accept.
      Returns $true when it registered, $false with a reason in the ledger's
      registration_failures when it would not.
    #>
    param([Parameter(Mandatory)]$Ledger, [Parameter(Mandatory)][string]$Title, [string]$Path,
          [switch]$Pathless, [string]$SourceFile, [string]$Kind = 'opened', [int]$SessionPid = 0)
    if ([string]::IsNullOrWhiteSpace($Title)) {
        $Ledger.registration_failures += 'a document with no title cannot be registered'
        return $false
    }
    $np = Get-HzNormalizedPath $Path
    if (-not $np -and -not $Pathless) {
        $Ledger.registration_failures += ("'{0}' has no path and no explicit pathless declaration; it is NOT registered, and anything unregistered is treated as somebody else's" -f $Title)
        return $false
    }
    $owner = $SessionPid
    if ($owner -eq 0 -and $Ledger.session_pid) { $owner = [int]$Ledger.session_pid }
    $already = @($Ledger.documents | Where-Object {
        if ($np) { $_.path_normalized -eq $np } else { (-not $_.path_normalized) -and $_.title -eq $Title } })
    if ($already.Count -gt 0) { return $true }
    $Ledger.documents += [ordered]@{
        title = $Title
        path = $Path
        path_normalized = $np
        pathless_declared = [bool]$Pathless
        source_file = $SourceFile
        kind = $Kind
        session_pid = $owner
        registered_utc = (Get-Date).ToUniversalTime().ToString('o')
    }
    return $true
}

function Read-HzSessionDocuments {
    <#
    .SYNOPSIS
      What is open in THIS session, or why that is not known. One place, because
      every caller needs the same three guarantees: the bridge answered, the
      list is complete, and it answered FOR THE PID this run started.
      Returns @{ ok; documents; why; health }.
    #>
    param([Parameter(Mandatory)]$Probes, [Parameter(Mandatory)]$Identity,
          [Parameter(Mandatory)][string]$Year, [Parameter(Mandatory)][string]$Dir)
    $health = & $Probes.Health $Year $Dir
    if (-not (Get-HzField $health 'ok')) {
        return @{ ok = $false; health = $health
                  why = ("the bridge could not report what is open ({0}). Nothing is closed without that answer." -f [string](Get-HzField $health 'error')) }
    }
    if (-not (Test-HzFieldPresent $health 'process_id') -or -not (Get-HzField $health 'process_id')) {
        return @{ ok = $false; health = $health
                  why = "the bridge did not say which process answered, so its answer cannot be tied to the pid this run started. Left running." }
    }
    if ([int](Get-HzField $health 'process_id') -ne [int]$Identity.pid) {
        return @{ ok = $false; health = $health
                  why = ("the bridge answered for pid {0}, not for this run's pid {1}. Left running." -f (Get-HzField $health 'process_id'), $Identity.pid) }
    }
    return @{ ok = $true; documents = @(Get-HzField $health 'documents'); health = $health }
}

function Register-HzOpenedDocument {
    <#
    .SYNOPSIS
      Register the document an open just produced, USING THE IDENTITY THE BRIDGE
      PUBLISHES rather than the arguments that were sent. The file a caller names
      and the document Revit ends up with are not always the same thing: a
      detached open renames it (HZ_CLOSED_L -> HZ_CLOSED_L_detached) and gives it
      NO path until it is saved, and the register has to hold what the close path
      will compare against.

      Registration is only ever done AFTER the open reported success, and it
      fails - loudly, registering nothing - when the bridge cannot say which
      document that was. An unregistered document is then treated as somebody
      else's, which leaves the session open. That is the intended direction.
      Returns @{ ok; why; title; path; pathless }.
    #>
    param([Parameter(Mandatory)]$Probes, [Parameter(Mandatory)]$Ledger, [Parameter(Mandatory)]$Identity,
          [Parameter(Mandatory)][string]$Year, [Parameter(Mandatory)][string]$Dir,
          [string]$SourceFile, [string]$ExpectedTitle)
    $read = Read-HzSessionDocuments -Probes $Probes -Identity $Identity -Year $Year -Dir $Dir
    if (-not $read.ok) { return @{ ok = $false; why = ('the open could not be registered: ' + $read.why) } }
    $docs = @($read.documents)
    # The open asks for activation, so the ACTIVE document is the one it produced;
    # the title the open reported is the cross-check, and the fallback when the
    # bridge cannot decide which is active (two documents with one identity).
    $cand = @($docs | Where-Object { (Get-HzField $_ 'is_active') -eq $true })
    if ($cand.Count -ne 1 -and $ExpectedTitle) {
        $cand = @($docs | Where-Object { [string](Get-HzField $_ 'title') -eq $ExpectedTitle })
    }
    if ($cand.Count -ne 1) {
        return @{ ok = $false
                  why = ("the bridge did not identify exactly one document to register ({0} candidates among {1} open; expected title '{2}')" -f
                         $cand.Count, $docs.Count, $ExpectedTitle) }
    }
    $doc = $cand[0]
    $title = [string](Get-HzField $doc 'title')
    if ($ExpectedTitle -and $title -ne $ExpectedTitle) {
        return @{ ok = $false
                  why = ("the open reported '{0}' and the active document is '{1}'; nothing is registered on a disagreement" -f $ExpectedTitle, $title) }
    }
    $path = [string](Get-HzField $doc 'path')
    if ($path) {
        $ok = Add-HzRehearsalDocument -Ledger $Ledger -Title $title -Path $path -SourceFile $SourceFile -Kind 'opened'
        return @{ ok = [bool]$ok; title = $title; path = $path; pathless = $false
                  why = $(if ($ok) { $null } else { 'the register refused the entry; see registration_failures' }) }
    }
    # No path: Revit gives a detached (or never-saved) document none. Registered
    # EXPLICITLY as pathless, which is a much narrower grant than a title match -
    # it only owns a document that also has no path and whose title is unique
    # among both the open documents and the register.
    $ok = Add-HzRehearsalDocument -Ledger $Ledger -Title $title -Pathless -SourceFile $SourceFile -Kind 'opened_detached'
    return @{ ok = [bool]$ok; title = $title; path = $null; pathless = $true
              why = $(if ($ok) { "registered as pathless: Revit reports no path for '$title' (a detached or unsaved open)" }
                      else { 'the register refused the entry; see registration_failures' }) }
}

function Register-HzHarnessDocuments {
    <#
    .SYNOPSIS
      Adopt the models a HARNESS created for itself, from the manifest it wrote
      (schema horizun.harness-documents/v1, see verify-live.ps1), into the SAME
      register every other owned document lives in - kind 'harness_scratch'.

      Measured 2026-09-24: verify-live creates and opens its own disposable models
      (HZ_LINKSRC_<tag>.rvt, w12-linkcopy-<tag>.rvt, ...) under
      %TEMP%\horizun-live-<run>, nothing registered them, and all five years ended
      in left_running_foreign_document with the manifest not restored.

      A manifest is a CLAIM, and a claim is not ownership. An entry is registered
      only when EVERY one of these holds, and anything else is reported and left
      out - which leaves any such document foreign, as before:
        - the schema is exactly horizun.harness-documents/v1 and the register is
          bound to this session's pid;
        - scratch_root is a DIRECT child of the temp directory, named
          horizun-live-<probe_run>, an existing directory and not a reparse point
          (a junction there could point at anybody's folder);
        - that directory was CREATED AT OR AFTER the moment the Revit this run
          started came up - so an old folder, or one somebody else made before
          this session existed, is never adopted;
        - the path is absolute, has no '..' segment, lies INSIDE scratch_root
          (normalised, case-insensitive), is a .rvt/.rfa file that exists and is
          not a reparse point, and is declared created_by_harness.
      The close path is unchanged: it still closes by registered path, through
      the bridge's rehearsal-then-token close, discarding changes (these are the
      harness's own disposable copies), and a document of that folder that the
      manifest did NOT list is still foreign.
      Returns [ordered]@{ state; why; manifest; scratch_root; registered; rejected }
      with state registered | no_manifest | manifest_rejected.
    #>
    param([Parameter(Mandatory)]$Ledger, [Parameter(Mandatory)]$Identity, [Parameter(Mandatory)][string]$ManifestPath,
          [string]$TempRoot = ([IO.Path]::GetTempPath()))
    $result = [ordered]@{ state = $null; why = $null; manifest = $ManifestPath; scratch_root = $null
                          registered = @(); rejected = @() }
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        $result.state = 'no_manifest'
        $result.why = 'the harness wrote no documents manifest; nothing it created is registered, and anything it left open stays foreign'
        return $result
    }
    $m = $null
    try { $m = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json } catch { $m = $null }
    if ($null -eq $m) {
        $result.state = 'manifest_rejected'; $result.why = 'the manifest is not readable JSON'
        return $result
    }
    if ([string](Get-HzField $m 'schema') -ne 'horizun.harness-documents/v1') {
        $result.state = 'manifest_rejected'
        $result.why = ("unknown manifest schema '{0}'; only horizun.harness-documents/v1 is read" -f [string](Get-HzField $m 'schema'))
        return $result
    }
    # The register has to be THIS session's, exactly as for a close.
    if (-not $Ledger.session_pid -or ([int]$Ledger.session_pid -ne [int](Get-HzField $Identity 'pid'))) {
        $result.state = 'manifest_rejected'
        $result.why = 'the register is not bound to this session; nothing can be attributed to it'
        return $result
    }
    # When THIS run's Revit came up, read the way Get-HzSessionIdentityState reads
    # it: round-trip with an explicit offset, or not at all.
    $raw = Get-HzField $Identity 'start_time'
    $sessionStart = $null
    if ($raw -is [DateTimeOffset]) { $sessionStart = $raw }
    elseif ($raw -is [datetime]) { $sessionStart = [DateTimeOffset]$raw }
    elseif (-not [string]::IsNullOrWhiteSpace([string]$raw)) {
        $parsed = [DateTimeOffset]::MinValue
        if (([string]$raw -match '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$') -and
            [DateTimeOffset]::TryParse([string]$raw, [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind, [ref]$parsed)) { $sessionStart = $parsed }
    }
    if (-not $sessionStart) {
        $result.state = 'manifest_rejected'
        $result.why = 'the session has no readable start time, so the scratch folder cannot be proved younger than it'
        return $result
    }

    $rootRaw = [string](Get-HzField $m 'scratch_root')
    $result.scratch_root = $rootRaw
    $tempNorm = Get-HzNormalizedPath $TempRoot
    $rootNorm = Get-HzNormalizedPath $rootRaw
    $probeRun = [string](Get-HzField $m 'probe_run')
    $rootWhy = $null
    if (-not $rootNorm -or -not [IO.Path]::IsPathRooted($rootRaw)) { $rootWhy = 'scratch_root is missing or not an absolute path' }
    elseif ($rootRaw -match '(^|[\\/])\.\.([\\/]|$)') { $rootWhy = "scratch_root contains a '..' segment" }
    elseif (-not $tempNorm -or ((Get-HzNormalizedPath ([IO.Path]::GetDirectoryName($rootNorm))) -ne $tempNorm)) {
        $rootWhy = ("scratch_root '{0}' is not directly under the temp directory '{1}'" -f $rootRaw, $TempRoot)
    }
    elseif ([string]::IsNullOrWhiteSpace($probeRun) -or
            ([IO.Path]::GetFileName($rootNorm) -ne ('horizun-live-' + $probeRun).ToLowerInvariant())) {
        $rootWhy = ("scratch_root '{0}' is not named horizun-live-<probe_run> (probe_run '{1}')" -f $rootRaw, $probeRun)
    }
    else {
        $di = [IO.DirectoryInfo]::new($rootNorm)
        if (-not $di.Exists) { $rootWhy = "scratch_root '$rootRaw' does not exist" }
        elseif (($di.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            $rootWhy = "scratch_root '$rootRaw' is a reparse point (junction or link); where it leads is not the harness's folder"
        }
        elseif ($di.CreationTimeUtc -lt $sessionStart.UtcDateTime) {
            $rootWhy = ("scratch_root '{0}' was created {1:o}, BEFORE this run's Revit started {2:o}; an older folder is never adopted" -f
                        $rootRaw, $di.CreationTimeUtc, $sessionStart.UtcDateTime)
        }
    }
    if ($rootWhy) {
        $result.state = 'manifest_rejected'; $result.why = $rootWhy
        foreach ($d in @(Get-HzField $m 'documents')) {
            if ($null -eq $d) { continue }
            $result.rejected += [ordered]@{ path = [string](Get-HzField $d 'path'); why = 'scratch_root refused: ' + $rootWhy }
        }
        return $result
    }

    foreach ($d in @(Get-HzField $m 'documents')) {
        if ($null -eq $d) { continue }
        $p = [string](Get-HzField $d 'path')
        $why = $null
        if ([string]::IsNullOrWhiteSpace($p)) { $why = 'the entry has no path' }
        elseif (-not [IO.Path]::IsPathRooted($p)) { $why = 'the path is not absolute' }
        elseif ($p -match '(^|[\\/])\.\.([\\/]|$)') { $why = "the path contains a '..' segment" }
        elseif ((Get-HzField $d 'created_by_harness') -ne $true) { $why = 'the entry does not declare created_by_harness' }
        else {
            $np = Get-HzNormalizedPath $p
            if (-not $np.StartsWith($rootNorm + '\')) { $why = ("the path is outside scratch_root '{0}'" -f $rootRaw) }
            elseif ([IO.Path]::GetExtension($np) -notin @('.rvt', '.rfa')) { $why = 'not a Revit model or family file' }
            elseif (-not (Test-Path -LiteralPath $p -PathType Leaf)) { $why = 'the file does not exist' }
            elseif (((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                $why = 'the file is a reparse point'
            }
        }
        if ($why) {
            $result.rejected += [ordered]@{ path = $p; why = $why }
            continue
        }
        $full = [IO.Path]::GetFullPath($p)
        $ok = Add-HzRehearsalDocument -Ledger $Ledger -Title ([IO.Path]::GetFileNameWithoutExtension($full)) -Path $full `
            -SourceFile $ManifestPath -Kind 'harness_scratch'
        if ($ok) { $result.registered += $full }
        else { $result.rejected += [ordered]@{ path = $p; why = 'the register refused the entry; see registration_failures' } }
    }
    $result.state = 'registered'
    if (@($result.rejected).Count -gt 0) {
        $result.why = ("{0} entr(ies) refused and NOT registered; a document among them left open stays foreign" -f @($result.rejected).Count)
    }
    return $result
}

function Test-HzOnlyRehearsalDocuments {
    <#
    .SYNOPSIS
      Sort the open documents into: this run's own (closable), the bridge's
      anchor (left to Revit), and foreign (a reason to close NOTHING).

      Ownership is decided on the identity the bridge publishes:
        - the normalised PATH matches a registered document -> own;
        - the path is under the bridge's anchor directory -> anchor;
        - a path nobody registered -> foreign, EVEN IF THE TITLE MATCHES one
          registered document (same name, another file, another session);
        - no path at all -> only a document registered as explicitly pathless
          owns it, and only when that title is unambiguous among both the open
          documents and the register;
        - neither a title nor a path -> foreign: an identity that cannot be read
          is not an identity that can be trusted.
    #>
    param([Parameter(Mandatory)]$Ledger, [Parameter(Mandatory)][AllowEmptyCollection()]$OpenDocuments)
    $docs = @($OpenDocuments)
    $own = @(); $anchors = @(); $foreign = @()
    foreach ($d in $docs) {
        $title = [string](Get-HzField $d 'title')
        $path = [string](Get-HzField $d 'path')
        $np = Get-HzNormalizedPath $path
        $row = [ordered]@{ title = $title; path = $path; path_normalized = $np; verdict = $null; why = $null; target = $null; is_linked = ((Get-HzField $d 'is_linked') -eq $true) }
        if (-not $title -and -not $np) {
            $row.verdict = 'foreign'; $row.why = 'the bridge published neither a title nor a path for this document'
            $foreign += $row; continue
        }
        if ($np -and $Ledger.anchor_dir -and $np.StartsWith(($Ledger.anchor_dir + '\'))) {
            $leaf = [IO.Path]::GetFileName($np)
            if ($leaf -like 'hz_anchor_*') {
                $row.verdict = 'anchor'; $row.why = "the bridge's own anchor project, under $($Ledger.anchor_dir); it closes with Revit"
                $anchors += $row; continue
            }
        }
        if ($np) {
            $match = @($Ledger.documents | Where-Object { $_.path_normalized -and ($_.path_normalized -eq $np) })
            if ($match.Count -eq 1) {
                $row.verdict = 'own'; $row.target = $match[0].path
                if ($match[0].title -ne $title) { $row.why = ("registered as '{0}', now titled '{1}'; the path is the identity" -f $match[0].title, $title) }
                $own += $row; continue
            }
            if ($match.Count -gt 1) {
                $row.verdict = 'foreign'; $row.why = 'more than one register entry claims this path; an ambiguous claim is not a claim'
                $foreign += $row; continue
            }
            $byTitle = @($Ledger.documents | Where-Object { $_.title -eq $title })
            $row.verdict = 'foreign'
            $row.why = if ($byTitle.Count -gt 0) {
                ("this run registered a document called '{0}' at '{1}', and this one is at '{2}' - same name, another file" -f
                 $title, $byTitle[0].path, $path)
            } else { "this run did not open '$title' ($path)" }
            $foreign += $row; continue
        }
        # No path: only an explicitly pathless registration can own it.
        $pathless = @($Ledger.documents | Where-Object { $_.pathless_declared -and ($_.title -eq $title) })
        $sameTitleOpen = @($docs | Where-Object { [string](Get-HzField $_ 'title') -eq $title })
        if ($pathless.Count -eq 1 -and $sameTitleOpen.Count -eq 1) {
            $row.verdict = 'own'; $row.target = $title
            $row.why = 'registered as a pathless document (a detached open); its title is unique among the open documents'
            $own += $row; continue
        }
        $row.verdict = 'foreign'
        $row.why = if ($pathless.Count -eq 0) { "'$title' has no path and this run registered no pathless document by that name" }
                   elseif ($sameTitleOpen.Count -gt 1) { "'$title' has no path and more than one open document shares the title; which is which cannot be decided" }
                   else { "'$title' has no path and more than one register entry shares the title" }
        $foreign += $row
    }
    return @{ ok = ($foreign.Count -eq 0); foreign = @($foreign); own = @($own); anchors = @($anchors)
              foreign_titles = @($foreign | ForEach-Object { $_.title }) }
}

function Get-HzModalDialogTitle {
    <#
    .SYNOPSIS
      The dialog title a bridge refusal names - "Revit has a MODAL DIALOG open:
      '<title>' [#32770] ..." - or $null when the text names none.
    #>
    param([string]$Text)
    if (-not $Text) { return $null }
    $m = [regex]::Match($Text, "MODAL DIALOG open: '([^']+)'")
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

function Test-HzDialogTitleAllowed {
    <#
    .SYNOPSIS
      A startup-dialog title the driver may close with WM_CLOSE. Save/discard and
      security prompts are never in that set, whatever the caller lists.
    #>
    param([string]$Title, [string[]]$Allowed)
    if (-not $Title -or -not $Allowed) { return $false }
    if ($Title -match '(?i)save|guardar|discard|descartar|security|seguridad|unsigned|firm|sync|sincron|close|cerrar') { return $false }
    foreach ($a in $Allowed) { if ($Title -like $a) { return $true } }
    return $false
}

function Initialize-HzExitNative {
    if ('HzExit.Native' -as [type]) { return }
    Add-Type -Namespace HzExit -Name Native -MemberDefinition @"
public delegate bool EnumProc(System.IntPtr h, System.IntPtr p);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, System.IntPtr p);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumChildWindows(System.IntPtr parent, EnumProc cb, System.IntPtr p);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr SendMessage(System.IntPtr h, uint msg, System.IntPtr w, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetWindowText(System.IntPtr h, System.Text.StringBuilder s, int max);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetClassName(System.IntPtr h, System.Text.StringBuilder s, int max);
public static string Text(System.IntPtr h) { var b = new System.Text.StringBuilder(2048); GetWindowText(h, b, 2048); return b.ToString(); }
public static string Class(System.IntPtr h) { var b = new System.Text.StringBuilder(256); GetClassName(h, b, 256); return b.ToString(); }
"@
}

function Test-HzCrashDialog {
    <#
    .SYNOPSIS
      Is the ONLY visible window of an exiting Revit its own unrecoverable-error
      dialog? $Windows: @{ title; class; texts[]; handle; ok_handle }. Returns
      @{ ok; window; why }. Anything else - a second window, a save prompt, a text
      that does not say the program will be terminated - is never pressed.
    #>
    param([object[]]$Windows)
    $visible = @($Windows | Where-Object { $null -ne $_ })
    $dialogs = @($visible | Where-Object { [string]$_.class -eq '#32770' })
    if ($dialogs.Count -ne 1) { return @{ ok = $false; window = $null; why = ("{0} dialog(s) visible; only exactly one is ever pressed" -f $dialogs.Count) } }
    $others = @($visible | Where-Object { [string]$_.class -ne '#32770' -and -not ([string]$_.title -match 'Monitor$') -and [string]$_.title })
    if ($others.Count -gt 0) { return @{ ok = $false; window = $null; why = 'another titled window is still open: ' + ((@($others | ForEach-Object { $_.title })) -join ', ') } }
    $d = $dialogs[0]
    $text = (@($d.texts) -join ' ')
    if ($text -notmatch 'An unrecoverable error has occurred' -or $text -notmatch 'will now be terminated') {
        return @{ ok = $false; window = $null; why = "the only dialog is not Revit's unrecoverable-error notice: '" + [string]$d.title + "'" }
    }
    if ($text -match '(?i)do you want to save|save changes|save the|guardar los cambios|desea guardar|discard|descartar|synchroni') {
        return @{ ok = $false; window = $null; why = 'the dialog mentions saving, discarding or synchronising; it is a person''s decision' }
    }
    if (-not $d.ok_handle) { return @{ ok = $false; window = $null; why = 'the dialog has no OK button to press' } }
    return @{ ok = $true; window = $d; why = $null }
}

function Close-HzRehearsalSession {
    <#
    .SYNOPSIS
      Close this run's Revit the safe way, or leave it running and say why.
      Returns an ordered record with state:
        closed | already_exited | left_running_identity | left_running_health |
        left_running_foreign_document | left_running_close_refused |
        left_running_close_unverified | left_running_no_exit
    #>
    param(
        [Parameter(Mandatory)]$Probes,
        [Parameter(Mandatory)]$Identity,
        [Parameter(Mandatory)]$Ledger,
        [Parameter(Mandatory)][string]$Year,
        [Parameter(Mandatory)][string]$Dir,
        [int]$ExitTimeoutSec = 240
    )
    $record = [ordered]@{ state = $null; why = $null; identity = $Identity; open_before = @()
                          closed_documents = @(); left_open = @(); checks = @() }
    $id = Get-HzSessionIdentityState -Probes $Probes -Identity $Identity
    if ($id.state -eq 'exited') { $record.state = 'already_exited'; return $record }
    if ($id.state -ne 'alive') {
        $record.state = 'left_running_identity'
        $record.why = "the process is not verifiably the one this run started ($($id.state)): $($id.why). Left running."
        return $record
    }
    # THE REGISTER HAS TO BELONG TO THIS SESSION. An unbound register, or one
    # bound to another pid, cannot attribute a single document to this Revit.
    if (-not $Ledger.session_pid) {
        $record.state = 'left_running_identity'
        $record.why = 'the register was never bound to a session pid, so no document in it can be attributed to this Revit. Left running.'
        return $record
    }
    if ([int]$Ledger.session_pid -ne [int]$Identity.pid) {
        $record.state = 'left_running_identity'
        $record.why = ("the register belongs to pid {0} and this session is pid {1}. Left running." -f $Ledger.session_pid, $Identity.pid)
        return $record
    }

    $read = Read-HzSessionDocuments -Probes $Probes -Identity $Identity -Year $Year -Dir $Dir
    if (-not $read.ok) {
        # A third-party startup dialog can hold the UI thread AFTER the bridge
        # published: Revit 2023 raised "External Tools - External Tool Failure"
        # a minute after start (2026-09-09) and the close path found health
        # refused. If the refusal names a modal and the probes can dismiss it -
        # only an allow-listed title, only on this pid, never a save, discard or
        # security prompt: that decision stays with the probe - ask once more.
        $modal = Get-HzModalDialogTitle -Text ([string]$read.why)
        if ($modal -and $Probes.ContainsKey('DismissStartupDialog')) {
            $shut = @(& $Probes.DismissStartupDialog $modal)
            if ($shut.Count -gt 0) {
                $record.dismissed_dialogs = $shut
                Start-Sleep -Seconds 2
                $read = Read-HzSessionDocuments -Probes $Probes -Identity $Identity -Year $Year -Dir $Dir
            }
        }
    }
    if (-not $read.ok) {
        # An unanswered health and a health that answered for another pid are two
        # different findings; the second is an identity finding.
        $record.state = if ($read.why -match 'answered for pid|did not say which process') { 'left_running_identity' } else { 'left_running_health' }
        $record.why = $read.why
        return $record
    }
    $record.open_before = @($read.documents | ForEach-Object { [string](Get-HzField $_ 'title') })
    $check = Test-HzOnlyRehearsalDocuments -Ledger $Ledger -OpenDocuments @($read.documents)
    $record.checks += [ordered]@{ when = 'before_any_close'; own = @($check.own | ForEach-Object { $_.title })
                                  anchors = @($check.anchors | ForEach-Object { $_.title })
                                  foreign = @($check.foreign) }
    if (-not $check.ok) {
        $record.state = 'left_running_foreign_document'
        $record.left_open = @($check.foreign | ForEach-Object { $_.title })
        $record.foreign = @($check.foreign)
        $record.why = ("a document this run did not open is open: {0}. Somebody may be working in it. Left running, nothing closed." -f
                       (($check.foreign | ForEach-Object { "'" + $_.title + "' (" + $_.why + ")" }) -join '; '))
        return $record
    }

    # ONE DOCUMENT AT A TIME, AND THE WHOLE PICTURE RE-READ BEFORE EACH ONE. The
    # first close takes seconds; in those seconds a person can open a model. So
    # identity and the open set are proved again before every close, not once.
    $planned = @($check.own).Count
    for ($i = 0; $i -lt $planned; $i++) {
        $id = Get-HzSessionIdentityState -Probes $Probes -Identity $Identity
        if ($id.state -eq 'exited') { $record.state = 'already_exited'; return $record }
        if ($id.state -ne 'alive') {
            $record.state = 'left_running_identity'
            $record.why = "the session stopped being verifiably ours part-way through the close sequence ($($id.state)): $($id.why). Left running."
            return $record
        }
        $read = Read-HzSessionDocuments -Probes $Probes -Identity $Identity -Year $Year -Dir $Dir
        if (-not $read.ok) {
            $record.state = 'left_running_health'
            $record.why = "part-way through the close sequence: $($read.why)"
            return $record
        }
        $now = Test-HzOnlyRehearsalDocuments -Ledger $Ledger -OpenDocuments @($read.documents)
        $record.checks += [ordered]@{ when = ("before_close_" + ($i + 1)); own = @($now.own | ForEach-Object { $_.title })
                                      anchors = @($now.anchors | ForEach-Object { $_.title })
                                      foreign = @($now.foreign) }
        if (-not $now.ok) {
            $record.state = 'left_running_foreign_document'
            $record.left_open = @($now.foreign | ForEach-Object { $_.title })
            $record.foreign = @($now.foreign)
            $record.why = ("a document this run did not open appeared DURING the close sequence: {0}. Left running; nothing further was closed." -f
                           (($now.foreign | ForEach-Object { "'" + $_.title + "' (" + $_.why + ")" }) -join '; '))
            return $record
        }
        # A LINKED model cannot be closed on its own: it unloads with its host, and
        # document_session refuses it by path (measured 2026-09-24 on
        # w12-linkcopy2). Own links are left to the process exit below.
        $closable = @(@($now.own) | Where-Object { -not $_.is_linked })
        if ($closable.Count -eq 0) { break }
        $doc = $closable[0]
        $target = if ($doc.target) { $doc.target } else { $doc.title }
        $r = & $Probes.CloseDocument ([ordered]@{
            year = $Year; dir = $Dir; target = $target
            expect_title = $doc.title; expect_path = $doc.path })
        if (-not (Get-HzField $r 'ok')) {
            $record.state = 'left_running_close_refused'
            $record.left_open += $doc.title
            $record.why = "the bridge refused to close '$($doc.title)': $([string](Get-HzField $r 'error')). Left running."
            return $record
        }
        # AND IT IS ONLY CLOSED WHEN IT IS GONE. The reply says closed=true; the
        # session is re-read to see the document actually absent, because "the
        # call did not fail" has never been the same fact.
        $after = Read-HzSessionDocuments -Probes $Probes -Identity $Identity -Year $Year -Dir $Dir
        if (-not $after.ok) {
            $record.state = 'left_running_close_unverified'
            $record.why = ("'{0}' was reported closed but the session could not be re-read to confirm it: {1}" -f $doc.title, $after.why)
            return $record
        }
        $stillThere = @(@($after.documents) | Where-Object {
            $p = Get-HzNormalizedPath ([string](Get-HzField $_ 'path'))
            if ($doc.path_normalized) { $p -eq $doc.path_normalized } else { [string](Get-HzField $_ 'title') -eq $doc.title } })
        if ($stillThere.Count -gt 0) {
            $record.state = 'left_running_close_unverified'
            $record.left_open += $doc.title
            $record.why = ("the bridge reported '{0}' closed and it is still open. Left running." -f $doc.title)
            return $record
        }
        $record.closed_documents += $doc.title
    }

    # Identity and the open set once more, right before the close request: the
    # checks above took time, and this is the last chance to find a document that
    # is not ours before the whole process is asked to exit.
    $id = Get-HzSessionIdentityState -Probes $Probes -Identity $Identity
    if ($id.state -eq 'exited') { $record.state = 'already_exited'; return $record }
    if ($id.state -ne 'alive') { $record.state = 'left_running_identity'; $record.why = $id.why; return $record }
    $final = Read-HzSessionDocuments -Probes $Probes -Identity $Identity -Year $Year -Dir $Dir
    if (-not $final.ok) {
        $record.state = 'left_running_health'
        $record.why = "before asking the process to exit: $($final.why)"
        return $record
    }
    $lastCheck = Test-HzOnlyRehearsalDocuments -Ledger $Ledger -OpenDocuments @($final.documents)
    $record.checks += [ordered]@{ when = 'before_process_exit'; own = @($lastCheck.own | ForEach-Object { $_.title })
                                  anchors = @($lastCheck.anchors | ForEach-Object { $_.title })
                                  foreign = @($lastCheck.foreign) }
    if (-not $lastCheck.ok) {
        $record.state = 'left_running_foreign_document'
        $record.left_open = @($lastCheck.foreign | ForEach-Object { $_.title })
        $record.foreign = @($lastCheck.foreign)
        $record.why = ("a document this run did not open is open at the end of the close sequence: {0}. The process is left running." -f
                       (($lastCheck.foreign | ForEach-Object { "'" + $_.title + "' (" + $_.why + ")" }) -join '; '))
        return $record
    }
    $ownLinks = @(@($lastCheck.own) | Where-Object { $_.is_linked })
    if ($ownLinks.Count -gt 0) { $record.unloaded_with_host = @($ownLinks | ForEach-Object { $_.title }) }
    $ownOpen = @(@($lastCheck.own) | Where-Object { -not $_.is_linked })
    if ($ownOpen.Count -gt 0) {
        $record.state = 'left_running_close_unverified'
        $record.left_open = @($ownOpen | ForEach-Object { $_.title })
        $record.why = ("documents this run opened are still open after the close sequence: {0}. Left running." -f
                       (($ownOpen | ForEach-Object { $_.title }) -join ', '))
        return $record
    }
    $asked = & $Probes.CloseMainWindow $id.process
    if (-not $asked) {
        $record.state = 'left_running_no_exit'
        $record.why = 'the process has no main window to ask; it is not killed.'
        return $record
    }
    if (& $Probes.WaitExit $id.process $ExitTimeoutSec) { $record.state = 'closed'; return $record }
    # REVIT CRASHED WHILE EXITING (measured 2026-09-24 on 2025 and 2027: the main
    # frame closed, then "An unrecoverable error has occurred. The program will now
    # be terminated." sat modal and the process never ended). The process is
    # already terminating and says nothing needs saving; its OK is the only way out.
    # It is pressed ONLY when that dialog is the one and only visible window of this
    # pid - a save, discard or any other prompt is a person's decision and stops it.
    if ($Probes.ContainsKey('ProcessWindows') -and $Probes.ContainsKey('ConfirmCrashDialog')) {
        # Flattened: a probe may return its rows wrapped (`return ,$rows`) or bare.
        $windows = @(& $Probes.ProcessWindows $id.process | ForEach-Object { $_ })
        $crash = Test-HzCrashDialog -Windows $windows
        $record.exit_windows = @($windows | ForEach-Object { [ordered]@{ title = $_.title; text = (@($_.texts) -join ' | ') } })
        if ($crash.ok) {
            $pressed = & $Probes.ConfirmCrashDialog $crash.window
            if ($pressed -and (& $Probes.WaitExit $id.process 60)) {
                $record.state = 'closed_after_revit_crash'
                $record.why = 'Revit raised its own unrecoverable-error dialog while exiting (after its main window closed); its OK ended the process. Nothing was saved or discarded by this driver.'
                return $record
            }
        }
        elseif ($crash.why) { $record.crash_dialog_not_pressed = $crash.why }
    }
    $record.state = 'left_running_no_exit'
    $record.why = "the process did not exit within $ExitTimeoutSec s after a normal close request (a dialog may be waiting for a person). It is not killed."
    return $record
}

function Get-HzYearStateAtStart {
    <#
    .SYNOPSIS
      The snapshot a restore is judged against, taken BEFORE anything changes:
      which manifests exist, what the installed one points at, and whether the
      installed DLL is there and hashable. Also whether that snapshot is itself
      readable - because a restore verified against an unreadable baseline is
      not verified.
    #>
    param([Parameter(Mandatory)]$Probes, [Parameter(Mandatory)][string]$Year)
    $m = & $Probes.ManifestState $Year
    $d = & $Probes.InstalledDllState $Year
    # An installation is present at the start whether its manifest is in place or
    # already renamed aside by an earlier session: both mean this year HAS one,
    # and the end state must therefore have Horizun.addin back.
    $installationExpected = ([bool](Get-HzField $m 'installed_present')) -or ([bool](Get-HzField $m 'aside_present'))
    $problems = @()
    if (Get-HzField $m 'installed_manifest_error') { $problems += ('installed manifest: ' + [string](Get-HzField $m 'installed_manifest_error')) }
    if ((Get-HzField $d 'present') -and -not (Get-HzField $d 'sha256')) {
        $problems += ('installed DLL present but not hashable: ' + [string](Get-HzField $d 'error'))
    }
    return [ordered]@{
        year = $Year
        captured_utc = (Get-Date).ToUniversalTime().ToString('o')
        manifest = $m
        installed_dll = $d
        installation_expected_at_end = $installationExpected
        dev_present_at_start = [bool](Get-HzField $m 'dev_present')
        readable = ($problems.Count -eq 0)
        unreadable_because = $problems
    }
}

function Test-HzInstallationMatchesStart {
    <#
    .SYNOPSIS
      Does the year's installation look exactly as it did at the start?
      Returns @{ ok; state; why }. Every branch is explicit, and "could not be
      read" is never folded into "is not there":
        restore_incomplete               a development manifest or an aside is still there
        installed_manifest_missing       there was an installation and Horizun.addin is gone
        installed_manifest_wrong_target  it exists and points somewhere else
        installed_appeared               there was none and now there is one
        installed_dll_missing            the DLL that was there is gone
        installed_dll_changed            it is there with another hash
        installed_dll_appeared           there was none and now there is one
        restore_unverifiable             something expected could not be read
    #>
    param([Parameter(Mandatory)]$StateAtStart, [Parameter(Mandatory)]$Now)
    $m = $Now
    if ((Get-HzField $m 'dev_present') -or (Get-HzField $m 'aside_present')) {
        return @{ ok = $false; state = 'restore_incomplete'
                  why = ("a development session is still in place: dev manifest present={0}, aside present={1}" -f
                         (Get-HzField $m 'dev_present'), (Get-HzField $m 'aside_present')) }
    }
    if ($StateAtStart.installation_expected_at_end) {
        if (-not (Get-HzField $m 'installed_present')) {
            return @{ ok = $false; state = 'installed_manifest_missing'
                      why = "this year HAD an installed manifest when the run started and Horizun.addin is not there now. Nothing is written to invent one." }
        }
        if (Get-HzField $m 'installed_manifest_error') {
            return @{ ok = $false; state = 'restore_unverifiable'
                      why = ('the installed manifest is there but could not be read, so what it points at is unknown: ' + [string](Get-HzField $m 'installed_manifest_error')) }
        }
        $expected = [string](Get-HzField $m 'expected_installed_assembly')
        $actual = [string](Get-HzField $m 'installed_assembly')
        if (-not $actual) {
            return @{ ok = $false; state = 'restore_unverifiable'
                      why = 'the installed manifest declares no assembly, so it cannot be confirmed to point at the installed add-in' }
        }
        if ($actual -ne $expected) {
            return @{ ok = $false; state = 'installed_manifest_wrong_target'
                      why = ("Horizun.addin exists but points at '{0}' instead of the installed add-in '{1}'. A file with the right NAME is not an installation." -f $actual, $expected) }
        }
    }
    else {
        if (Get-HzField $m 'installed_present') {
            return @{ ok = $false; state = 'installed_appeared'
                      why = "this year had NO installed manifest when the run started and one exists now; that change was not this run's to make, and it is not undone here." }
        }
    }
    $startDll = Get-HzField $StateAtStart 'installed_dll'
    $nowDll = Get-HzField $Now 'installed_dll_state'
    if ($null -eq $nowDll) { return @{ ok = $false; state = 'restore_unverifiable'; why = 'the installed DLL was not re-read after the restore' } }
    if (Get-HzField $startDll 'present') {
        if (-not (Get-HzField $nowDll 'present')) {
            return @{ ok = $false; state = 'installed_dll_missing'
                      why = ("the installed DLL was there when the run started ({0}) and is not there now" -f [string](Get-HzField $startDll 'path')) }
        }
        $want = [string](Get-HzField $startDll 'sha256')
        $got = [string](Get-HzField $nowDll 'sha256')
        if (-not $want) {
            return @{ ok = $false; state = 'restore_unverifiable'
                      why = ('the installed DLL could not be hashed when the run started, so no comparison is possible: ' + [string](Get-HzField $startDll 'error')) }
        }
        if (-not $got) {
            return @{ ok = $false; state = 'restore_unverifiable'
                      why = ('the installed DLL is there but could not be hashed now: ' + [string](Get-HzField $nowDll 'error')) }
        }
        if ($got -ne $want) {
            return @{ ok = $false; state = 'installed_dll_changed'
                      why = ("the installed DLL changed during the run ({0} vs {1})" -f $got, $want) }
        }
    }
    elseif (Get-HzField $nowDll 'present') {
        return @{ ok = $false; state = 'installed_dll_appeared'
                  why = 'there was no installed DLL when the run started and there is one now; that change was not this run to make' }
    }
    if (-not $StateAtStart.readable) {
        return @{ ok = $false; state = 'restore_unverifiable'
                  why = ('the start snapshot itself could not be read completely: ' + (($StateAtStart.unreadable_because) -join '; ')) }
    }
    return @{ ok = $true; state = 'verified' }
}

function Restore-HzYearSession {
    <#
    .SYNOPSIS
      Put the year's manifest back, only when no Revit of that year - and no
      Revit of an unknown year - is running, and believe it only after the exit
      code AND the disk agree with the snapshot taken before anything changed.
      state: restored | nothing_to_restore | deferred_revit_running |
             deferred_revit_unknown_year | restore_failed | restore_incomplete |
             installed_manifest_missing | installed_manifest_wrong_target |
             installed_appeared | installed_dll_missing | installed_dll_changed |
             installed_dll_appeared | restore_unverifiable
    #>
    param([Parameter(Mandatory)]$Probes, [Parameter(Mandatory)][string]$Year,
          [Parameter(Mandatory)]$StateAtStart)
    if ($null -eq $StateAtStart -or -not (Test-HzFieldPresent $StateAtStart 'manifest')) {
        return @{ ok = $false; state = 'restore_unverifiable'
                  why = 'no start snapshot was captured for this year; a restore cannot be verified against nothing' }
    }
    $before = & $Probes.ManifestState $Year
    $needsRestore = ((Get-HzField $before 'dev_present') -or (Get-HzField $before 'aside_present'))
    if (-not $needsRestore) {
        # NOTHING TO UNDO IS NOT THE SAME AS NOTHING TO CHECK. The old code
        # returned ok/nothing_to_restore here with installed_present=false and an
        # unreadable DLL hash, because the comparison was written as
        # `$expected -and $actual -and ...` - which skips exactly when the file
        # it protects has gone missing.
        $now = @{} + $before
        $now['installed_dll_state'] = (& $Probes.InstalledDllState $Year)
        $v = Test-HzInstallationMatchesStart -StateAtStart $StateAtStart -Now $now
        if (-not $v.ok) {
            return @{ ok = $false; state = $v.state; why = ('nothing had to be undone, but the installation is not as it was: ' + $v.why)
                      manifest = $before; installed_dll = $now['installed_dll_state'] }
        }
        return @{ ok = $true; state = 'nothing_to_restore'; manifest = $before; installed_dll = $now['installed_dll_state'] }
    }
    # RE-CHECKED HERE, immediately before the change - not once at the top of the
    # year. A Revit can be started while the harnesses run.
    $allowed = Test-HzManifestChangeAllowed -Probes $Probes -Year $Year
    if (-not $allowed.ok) {
        $state = if ($allowed.blocked_by -eq 'unknown_year') { 'deferred_revit_unknown_year' } else { 'deferred_revit_running' }
        return @{ ok = $false; state = $state
                  why = ($allowed.why + ' Restore when it has closed.'); manifest = $before
                  blocking_pids = @($allowed.pids); revit_processes = $allowed.classes }
    }
    $code = $null
    try { $code = & $Probes.Restore $Year }
    catch { return @{ ok = $false; state = 'restore_failed'; why = "dev-addin-session -Restore threw: $($_.Exception.Message)"; manifest = (& $Probes.ManifestState $Year) } }
    $code = ConvertTo-HzExitCode $code
    if ($null -eq $code) { return @{ ok = $false; state = 'restore_failed'; why = 'dev-addin-session -Restore returned no exit code'; manifest = (& $Probes.ManifestState $Year) } }
    if ($code -ne 0) { return @{ ok = $false; state = 'restore_failed'; why = "dev-addin-session -Restore exited $code"; manifest = (& $Probes.ManifestState $Year) } }
    $after = & $Probes.ManifestState $Year
    $dllAfter = & $Probes.InstalledDllState $Year
    $now = @{} + $after
    $now['installed_dll_state'] = $dllAfter
    $v = Test-HzInstallationMatchesStart -StateAtStart $StateAtStart -Now $now
    if (-not $v.ok) {
        return @{ ok = $false; state = $v.state; why = ('-Restore exited 0 and ' + $v.why); manifest = $after; installed_dll = $dllAfter }
    }
    return @{ ok = $true; state = 'restored'; manifest = $after; installed_dll = $dllAfter
              installed_dll_sha256 = [string](Get-HzField $dllAfter 'sha256') }
}

function Invoke-HzYearEnable {
    <#
    .SYNOPSIS
      Enable the development session and PROVE it: no Revit of this year and none
      of an unknown year running, exit code zero, and the signed DLL on disk. A
      caller that gets ok=false must not start Revit.
    #>
    param([Parameter(Mandatory)]$Probes, [Parameter(Mandatory)][string]$Year)
    $allowed = Test-HzManifestChangeAllowed -Probes $Probes -Year $Year
    if (-not $allowed.ok) {
        return @{ ok = $false; blocked_by = $allowed.blocked_by; blocking_pids = @($allowed.pids)
                  why = ('the manifest cannot be swapped right now: ' + $allowed.why) }
    }
    $code = $null
    try { $code = & $Probes.Enable $Year } catch { return @{ ok = $false; why = "dev-addin-session -Enable threw: $($_.Exception.Message)" } }
    $code = ConvertTo-HzExitCode $code
    if ($null -eq $code) { return @{ ok = $false; why = 'dev-addin-session -Enable returned no exit code' } }
    if ($code -ne 0) { return @{ ok = $false; why = "dev-addin-session -Enable exited $code" } }
    $m = & $Probes.ManifestState $Year
    if (-not (Get-HzField $m 'dev_present')) { return @{ ok = $false; why = 'dev-addin-session -Enable exited 0 but wrote no development manifest' } }
    $devDll = [string](Get-HzField $m 'dev_dll')
    if ($devDll -and -not (Test-Path -LiteralPath $devDll)) { return @{ ok = $false; why = "no signed development DLL at $devDll" } }
    return @{ ok = $true }
}

function Write-HzRecoveryPending {
    <#
    .SYNOPSIS
      What could not be put back, written where the next person (or run) finds
      it, with the exact steps to recover. Never deletes anything.
    #>
    param([Parameter(Mandatory)][string]$Dir, [Parameter(Mandatory)][string]$Year, [Parameter(Mandatory)]$Record)
    $path = Join-Path $Dir "recovery-pending-$Year.json"
    $body = [ordered]@{
        schema = 'horizun.year-matrix.recovery/1'
        written_utc = (Get-Date).ToUniversalTime().ToString('o')
        year = $Year
        record = $Record
        how_to_recover = @(
            "1. Look at the Revit $Year that is still running (pid in 'record'): if a person is working in it, leave it to them.",
            "2. When that Revit is closed (by its owner, normally), run: pwsh -File scripts/live/dev-addin-session.ps1 -Year $Year -Restore",
            "3. Check %APPDATA%\Autodesk\Revit\Addins\${Year}: Horizun.addin present and pointing at Horizun\Horizun.Revit.dll, Horizun-dev-session.addin and Horizun.addin.dev-session-aside absent.",
            "4. Delete this file only after the manifest is back.")
    }
    ($body | ConvertTo-Json -Depth 20) | Set-Content -LiteralPath $path -Encoding utf8
    return $path
}
