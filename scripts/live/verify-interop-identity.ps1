#Requires -Version 5.1
<#
  3.0.0 INTEROPERABILITY, decision D0 - model identity, LIVE and READ-ONLY.

  Pending for the final matrix (written 2026-10-12 while the owner's Revit was open: nothing
  here has been run against a Revit yet). Nothing is written to any model; the only file this
  writes is a throw-away project-integrations.json under the run's work folder.

    I1  get_document_info publishes model_identity: every D0 key present; on 2024+ a
        creation_guid and identity_proven=true; on 2023 creation_guid=null, identity_proven=false
        and a note that says why
    I2  the evidence registers (rehearsal) and, written into a throw-away registry, resolves back
        to the same model_ref - matched_by the binding key, never by Project Information
    I3  (with -SecondDocument, a project made from the SAME template) the two share
        project_info_unique_id and still do NOT match: different creation_guid on 2024+, and on
        2023 no strong key at all, so resolve_model refuses
    I4  horizun_exchange emit (rehearsal) of a one-line CSV names the registered model's evidence
        and caps a declared 'verified' to self_reported_verified

  Run it per year with the documents opened by the harness, never on somebody else's Revit.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Document,
    [string]$SecondDocument,
    [string]$ArtifactDir
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')

$run = New-HzRun -Harness $PSCommandPath -Name 'interop-identity' -Document $Document
$work = Join-Path ([IO.Path]::GetTempPath()) ('hz-interop-live-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work
$keys = @('creation_guid', 'central_guid', 'cloud_model_guid', 'unique_document_guid', 'project_info_unique_id',
          'cloud_project_guid', 'revit_version', 'title', 'path', 'binding_key', 'identity_proven', 'identity_note')

function Get-HzIdentity([string]$Doc) {
    $null = Set-HzActiveDocument $run $Doc
    $info = (Invoke-HzToolStrict -Run $run -Tool 'get_document_info' -Label "info-$Doc" -Arguments @{}).Result
    [pscustomobject]@{ Info = $info; Identity = (Get-HzProp $info 'model_identity') }
}

# ---------------------------------------------------------------- I1
$a = Get-HzIdentity $Document
$id = $a.Identity
$year = [string](Get-HzProp $a.Info 'revit_version')
$missing = @($keys | Where-Object { $null -eq $id -or -not ($id.PSObject.Properties.Name -contains $_) })
$is2023 = $year -like '2023*'
$okYear = if ($is2023) {
    $null -eq (Get-HzProp $id 'creation_guid') -and (Get-HzProp $id 'identity_proven') -eq $false -and [string](Get-HzProp $id 'identity_note') -match '2023'
} else {
    [string](Get-HzProp $id 'creation_guid') -match '^[0-9a-f-]{36}$' -and [string](Get-HzProp $id 'binding_key') -eq 'creation_guid' -and (Get-HzProp $id 'identity_proven') -eq $true
}
Add-HzProbe -Run $run -Id 'I1' -Name 'get_document_info publishes the D0 evidence, and 2023 is never proven' `
    -Expected 'every key present; 2024+: creation_guid + proven; 2023: null + unproven + note' `
    -Observed ("year={0} missing=[{1}] binding={2} proven={3}" -f $year, ($missing -join ','), (Get-HzProp $id 'binding_key'), (Get-HzProp $id 'identity_proven')) `
    -Ok ($missing.Count -eq 0 -and $okYear) -Evidence @{ model_identity = $id }

# ---------------------------------------------------------------- I2
$registry = Join-Path $work 'project-integrations.json'
$evidence = $id
if ($null -eq (Get-HzProp $id 'binding_key')) {
    Add-HzProbe -Run $run -Id 'I2' -Name 'the evidence registers and resolves back' -Status 'not_applicable' `
        -Expected 'a strong key to bind' -Observed 'no strong key on this document (expected on a file-based 2023 model)' `
        -Because 'D0 refuses to register a model on Project Information alone'
    $refused = Invoke-HzTool -Run $run -Tool 'horizun_project_context' -Label 'register-weak' -Arguments @{
        operation = 'register_model'; integrations_path = $registry; model = @{ name = 'A'; evidence = $evidence } }
    Add-HzProbe -Run $run -Id 'I2b' -Name 'a model with no strong key is refused, not registered' `
        -Expected 'an error saying without strong evidence' -Observed (Limit-HzText ([string]$refused.Text) 200) `
        -Ok ([bool]$refused.IsError -and [string]$refused.Text -match 'without strong evidence')
} else {
    $reh = (Invoke-HzToolStrict -Run $run -Tool 'horizun_project_context' -Label 'register' -Arguments @{
        operation = 'register_model'; integrations_path = $registry; project_code = 'HZTEST'; model = @{ name = 'A'; evidence = $evidence } }).Result
    $doc = Get-HzProp $reh 'document'
    [IO.File]::WriteAllText($registry, ($doc | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false)))
    $res = (Invoke-HzToolStrict -Run $run -Tool 'horizun_project_context' -Label 'resolve' -Arguments @{
        operation = 'resolve_model'; integrations_path = $registry; evidence = $evidence }).Result
    $ref = [string](Get-HzPath $reh 'model', 'model_ref')
    Add-HzProbe -Run $run -Id 'I2' -Name 'the evidence registers and resolves back to the same model_ref' `
        -Expected "matched, model_ref=$ref, matched_by=binding key" `
        -Observed ("state={0} ref={1} by={2} proven={3}" -f (Get-HzProp $res 'state'), (Get-HzProp $res 'model_ref'), (Get-HzProp $res 'matched_by'), (Get-HzProp $res 'identity_proven')) `
        -Ok ([string](Get-HzProp $res 'state') -eq 'matched' -and [string](Get-HzProp $res 'model_ref') -eq $ref -and
             [string](Get-HzProp $res 'matched_by') -eq [string](Get-HzProp $id 'binding_key') -and (Get-HzProp $res 'identity_proven') -eq (-not $is2023)) `
        -Evidence @{ resolve = $res }

    # ------------------------------------------------------------ I4
    $csv = Join-Path $work 'ledger.csv'
    [IO.File]::WriteAllText($csv, "issue_id,state`nC-1,open`n", (New-Object Text.UTF8Encoding($false)))
    $em = (Invoke-HzToolStrict -Run $run -Tool 'horizun_exchange' -Label 'emit' -Arguments @{
        operation = 'emit'; kind = 'coordination.ledger'; files = @(, @{ path = $csv; format = 'horizun.coordination-ledger.csv/2'; rows = 1 })
        verification_state = 'verified'; model_ref = $ref; integrations_path = $registry; exchange_root = (Join-Path $work 'exchange') }).Result
    Add-HzProbe -Run $run -Id 'I4' -Name 'emit names the registered evidence and caps a declared verified' `
        -Expected 'self_reported_verified, host_verified=false, model_evidence = the registered evidence, nothing written' `
        -Observed ("state={0} host={1} written={2}" -f (Get-HzPath $em 'manifest', 'verification', 'state'), (Get-HzPath $em 'manifest', 'verification', 'host_verified'), (Get-HzProp $em 'written')) `
        -Ok ([string](Get-HzPath $em 'manifest', 'verification', 'state') -eq 'self_reported_verified' -and (Get-HzProp $em 'written') -eq $false -and
             [string](Get-HzPath $em 'manifest', 'source', 'model_ref') -eq $ref) -Evidence @{ manifest = (Get-HzProp $em 'manifest') }
}

# ---------------------------------------------------------------- I3
if ($SecondDocument) {
    $b = Get-HzIdentity $SecondDocument
    $sameTemplate = [string](Get-HzProp $a.Identity 'project_info_unique_id') -eq [string](Get-HzProp $b.Identity 'project_info_unique_id')
    if ($null -eq (Get-HzProp $b.Identity 'binding_key')) {
        $r3 = Invoke-HzTool -Run $run -Tool 'horizun_project_context' -Label 'resolve-second' -Arguments @{
            operation = 'resolve_model'; integrations_path = $registry; evidence = $b.Identity }
        $ok3 = [bool]$r3.IsError -and [string]$r3.Text -match 'without strong evidence'
        $obs3 = Limit-HzText ([string]$r3.Text) 200
    } else {
        $r3 = (Invoke-HzToolStrict -Run $run -Tool 'horizun_project_context' -Label 'resolve-second' -Arguments @{
            operation = 'resolve_model'; integrations_path = $registry; evidence = $b.Identity }).Result
        $ok3 = [string](Get-HzProp $r3 'state') -eq 'no_match'
        $obs3 = "state=" + (Get-HzProp $r3 'state')
    }
    Add-HzProbe -Run $run -Id 'I3' -Name 'two projects from one template share Project Information and do not match' `
        -Expected 'same project_info_unique_id; no_match (2024+) or refused for no strong key (2023)' `
        -Observed ("same_template={0} {1}" -f $sameTemplate, $obs3) -Ok ($sameTemplate -and $ok3) `
        -Evidence @{ first = $a.Identity; second = $b.Identity }
} else {
    Add-HzProbe -Run $run -Id 'I3' -Name 'two projects from one template do not match' -Status 'not_covered' `
        -Expected 'a second document from the same template' -Observed 'no -SecondDocument given'
}

try { Remove-Item -LiteralPath $work -Recurse -Force } catch { }
$end = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $end.ExitCode
