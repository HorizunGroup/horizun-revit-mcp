#Requires -Version 7.0
<#
  OPEN A DOCUMENT THE DRIVER DID NOT OPEN, ON PURPOSE.

  The one path in the year-matrix driver that has never met a real case is the
  most important one: "a document this run did not open is open - leave the Revit
  running and close nothing". Seven simulated cases cover it, and simulation is
  not the same as a real document in a real Revit that the driver's register has
  never heard of.

  This harness makes that document. It is run BY the driver as a harness, so it
  runs inside the rehearsal session - but it opens its file through the bridge
  DIRECTLY, which means the driver's register never learns about it. From the
  close path's point of view it is exactly what somebody else's model would be.

  IT IS NOT SOMEBODY'S MODEL. It takes an explicit -Path and refuses anything
  that is not a file this repository's own convention marks as disposable, and it
  refuses to run at all without the disposable token. The intended file is a copy
  made for this purpose and named so that nobody can mistake it:
  C:\hz-live\HZ_DESECHABLE_NO_REGISTRADO.rvt

  EXPECTED OUTCOME OF THE RUN THAT USES IT - and the reason it exists:
    * the year ends 'recovery_pending' with close.state = left_running_foreign_document
    * the record names this document, and closed_documents is EMPTY
    * the Revit is left running and its manifest is NOT restored
  Anything else is a finding. Recovery is a deliberate second step, documented in
  docs/PENDING-LIVE-PROCEDURES-2026-09-09.md; this harness never closes
  anything itself.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Document,
    [Parameter(Mandatory)][ValidateSet('yes-this-model-is-disposable')][string]$Disposable,
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$ArtifactDir,
    [switch]$Activate
)
. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')
$run = New-HzRun -Harness $PSCommandPath -Name 'unregistered-document' -Document $Document -WorkDir (Join-Path $ArtifactDir ('foreign-' + [guid]::NewGuid().ToString('N')))
try {
    # A NAME THAT CANNOT BE ANYTHING ELSE. This harness exists to prove a
    # protection; it must not be the thing that opens somebody's project.
    $leaf = Split-Path -Leaf $Path
    if ($leaf -notmatch '^HZ_DESECHABLE') {
        throw ("REFUSING: '$leaf' is not named as a file made for this test. This harness opens only a file whose " +
               "name starts with HZ_DESECHABLE, so that a real model can never be handed to it by accident.")
    }
    if (-not (Test-Path -LiteralPath $Path)) { throw "the disposable file is not on this machine: $Path" }
    # Every field read through Get-HzProp: the library runs every harness under
    # Set-StrictMode -Version Latest, where a reply without the property does not
    # answer "no documents" - it throws, in the middle of a real Revit session.
    $h = Get-HzHealth $run
    if ((Get-HzProp $h 'status') -ne 'healthy') { throw 'the bridge is not healthy; nothing was opened.' }
    $before = @(@(Get-HzProp $h 'open_documents') | ForEach-Object { [string](Get-HzProp $_ 'title') })
    $want = (Resolve-Path -LiteralPath $Path).Path
    $open = Invoke-HzToolStrict -Run $run -Tool 'horizun_open_document' -Label 'open-unregistered' -Arguments @{
        path = ($Path.Replace([char]92, '/'))
        activate = [bool]$Activate
        idempotency_key = ('foreign-doc-' + (Get-Date -Format 'yyyyMMddHHmmssfff'))
    }
    $after = Get-HzHealth $run
    $openDocs = @(Get-HzProp $after 'open_documents')
    $titles = @($openDocs | ForEach-Object { [string](Get-HzProp $_ 'title') })
    $isOpen = $false
    foreach ($doc in $openDocs) {
        $p = [string](Get-HzProp $doc 'path')
        if (-not $p) { continue }
        $resolved = (Resolve-Path -LiteralPath $p -ErrorAction SilentlyContinue)
        if ($resolved -and ($resolved.Path -eq $want)) { $isOpen = $true; break }
    }
    Add-HzProbe -Run $run -Id 'unregistered-document-open' -Name 'unregistered-document-open' `
        -Expected 'A disposable document the driver never opened is open in the rehearsal session, so the close path meets a real foreign document instead of a simulated one' `
        -Ok ($open.Ok -and $isOpen) `
        -Observed ("open before: " + ($before -join ', ') + " | after: " + ($titles -join ', ')) `
        -Evidence @{ path = $Path; open = $open.Raw; health_after = $titles }
    Add-HzProbe -Run $run -Id 'driver-must-now-leave-this-session-running' -Name 'driver-must-now-leave-this-session-running' `
        -Expected 'The driver ends this year as recovery_pending with close.state=left_running_foreign_document, naming this document, having closed nothing' `
        -Status 'not_covered' `
        -Because 'This harness only creates the condition. The assertion is the driver''s own summary for the year, read afterwards.'
}
catch {
    Add-HzProbe -Run $run -Id 'unregistered-document-open' -Name 'unregistered-document-open' `
        -Expected 'A disposable document the driver never opened is open in the rehearsal session' -Ok $false `
        -Evidence @{ error = $_.Exception.Message; stack = $_.ScriptStackTrace }
}
$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
