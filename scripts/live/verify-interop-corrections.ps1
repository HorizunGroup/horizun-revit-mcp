#Requires -Version 7.0
<#
  Pending live: the review -> typed correction -> fresh model read -> PDF re-review loop.
  Run ONLY later, on a disposable owner-approved fixture with Revit already open.
  The plan must contain resolved ids, explicit parameter writes/revisions, dry_run:true
  and the same target_document. Without -Apply this probe only rehearses.
  -Apply is explicit approval of that fixture plan; inspect the rehearsal first.
  This probe never starts or closes Revit. PDF re-review is the peer repo's pending probe.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Document,
    [Parameter(Mandatory)][string]$PlanPath,
    [switch]$Apply,
    [string]$ArtifactDir
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')
$run = New-HzRun -Harness $PSCommandPath -Name 'interop-corrections' -Document $Document
$null = Invoke-HzToolStrict -Run $run -Tool 'horizun_health' -Label 'health'
$plan = Get-Content -LiteralPath $PlanPath -Raw | ConvertFrom-Json -AsHashtable
$requestArgs = @{ target_document = $Document; correction_plan = $plan; dry_run = $true }
$preview = (Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_corrections' -Arguments $requestArgs -Label 'rehearse').Result
$token = Get-HzProp $preview 'confirmation_token'
Add-HzProbe -Run $run -Id 'C1' -Name 'all review steps rehearse and bind the model facts' `
    -Expected 'rehearsed_cleanly=true; token; plan_resolved fingerprint' `
    -Ok ([bool](Get-HzProp $preview 'rehearsed_cleanly') -and [bool]$token -and [bool](Get-HzProp $preview 'plan_resolved')) `
    -Evidence $preview
if ($Apply) {
    if (-not $token) { throw 'No executable token; nothing is applied.' }
    $requestArgs.dry_run = $false
    $requestArgs.confirmation_token = $token
    $requestArgs.idempotency_key = 'interop-corrections-' + [guid]::NewGuid().ToString('N')
    $done = (Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_corrections' -Arguments $requestArgs -Label 'apply').Result
    Add-HzProbe -Run $run -Id 'C2' -Name 'fresh reads after the last write judge every planned target' `
        -Expected 'method=plan_vs_fresh_model_read; state=verified; host_verified=true; re_review.required=true' `
        -Ok ((Get-HzProp $done 'method') -eq 'plan_vs_fresh_model_read' -and (Get-HzProp $done 'state') -eq 'verified' -and
            [bool](Get-HzProp $done 'host_verified') -and [bool](Get-HzProp (Get-HzProp $done 're_review') 'required')) `
        -Evidence $done
    $replay = (Invoke-HzToolStrict -Run $run -Tool 'horizun_apply_corrections' -Arguments $requestArgs -Label 'replay').Result
    Add-HzProbe -Run $run -Id 'C3' -Name 'same key replays without another write' -Expected 'idempotency.status=replayed' `
        -Ok ((Get-HzProp (Get-HzProp $replay 'idempotency') 'status') -eq 'replayed') -Evidence $replay
} else {
    Add-HzProbe -Run $run -Id 'C2' -Name 'apply and fresh model reads' -Expected 'an explicitly approved disposable fixture' `
        -Status 'not_covered' -Observed '-Apply was not supplied'
}
Add-HzProbe -Run $run -Id 'C4' -Name 'PDF re-review closes the finding' -Expected 're-export and peer review delta' `
    -Status 'not_covered' -Observed 'pending in horizun-review; no peer called by the bridge'
$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
