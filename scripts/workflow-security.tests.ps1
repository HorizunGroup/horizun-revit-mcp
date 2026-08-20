#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$workflowDir = Join-Path $repo '.github/workflows'
$ciPath = Join-Path $workflowDir 'ci.yml'
$prPath = Join-Path $workflowDir 'pr.yml'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

$ci = Get-Content -LiteralPath $ciPath -Raw
$pr = Get-Content -LiteralPath $prPath -Raw
$prExecutable = (($pr -split '\r?\n') | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"

# The protected workflow contains arbitrary repository execution on persistent
# machines. An `if:` on one of its jobs is insufficient: a PR can edit its own
# workflow file before GitHub evaluates that condition.
Assert-True ($ci -notmatch '(?m)^\s{0,2}pull_request(?:_target)?\s*:') `
    'ci.yml must never be triggered by pull_request or pull_request_target.'
Assert-True ($pr -match '(?m)^\s{0,2}pull_request\s*:') `
    'pr.yml must remain the explicit pull-request entry point.'
Assert-True ($prExecutable -notmatch '(?im)self-hosted|\bsigning\b') `
    'pr.yml must use only disposable hosted runners and must not mention the signing route.'
Assert-True ($prExecutable -notmatch '(?im)^\s*(?:id-token|attestations|artifact-metadata)\s*:\s*write\s*$') `
    'pr.yml must not receive provenance, attestation, or artifact-metadata write permissions.'
Assert-True ($prExecutable -notmatch '(?im)^\s*contents\s*:\s*write\s*$') `
    'pr.yml must not receive repository write permission.'
Assert-True ($ci -match '(?s)revit-addin:.*?group:\s*\$\{\{\s*vars\.REVIT_RUNNER_GROUP\s*\}\}') `
    'Revit jobs must target the externally configured Revit runner group.'
Assert-True ($ci -match '(?s)package:.*?group:\s*\$\{\{\s*vars\.SIGNING_RUNNER_GROUP\s*\}\}.*?labels:\s*\[self-hosted, windows, revit, signing\]') `
    'The package/signing job must target the separately labelled signing runner group.'
Assert-True ($ci -match 'SIGNING_RUNNER_GROUP.*!=.*REVIT_RUNNER_GROUP|SIGNING_RUNNER_GROUP.*-ne.*REVIT_RUNNER_GROUP') `
    'The workflow must fail closed unless signing and integration runner groups differ.'
Assert-True ($ci -match 'SIGNPATH_SELF_HOSTED_ORIGIN_APPROVED.*!=.*true') `
    'Stable signing must fail closed until SignPath approves the self-hosted Revit origin.'
Assert-True ([regex]::Matches($ci, '(?m)^\s+NUGET_PACKAGES:\s*\$\{\{\s*runner\.temp\s*\}\}').Count -ge 2) `
    'Every self-hosted build/package route must use a run-isolated NuGet extraction root.'

$workflowFiles = Get-ChildItem -LiteralPath $workflowDir -Filter '*.yml' -File
$badUses = [System.Collections.Generic.List[string]]::new()
foreach ($file in $workflowFiles) {
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $file.FullName) {
        $lineNumber++
        if ($line -notmatch '^\s*(?:-\s*)?uses:\s*([^\s#]+)') { continue }
        $reference = $Matches[1]
        if ($reference.StartsWith('./')) { continue }
        if ($reference -notmatch '^[^/@\s]+/[^@\s]+@[0-9a-fA-F]{40}$') {
            $badUses.Add("$($file.Name):$lineNumber -> $reference")
        }
    }
}
Assert-True ($badUses.Count -eq 0) `
    "Every external action must be pinned to an immutable 40-character commit SHA:`n  $($badUses -join "`n  ")"

# Credential persistence is unnecessary in this repository. On a self-hosted
# machine it also leaves a write-capable token available to later repository
# scripts if a future job grants broader permissions.
foreach ($file in $workflowFiles) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    $checkouts = [regex]::Matches($text, '(?m)^\s*- uses:\s*actions/checkout@[0-9a-fA-F]{40}.*(?:\r?\n\s+.*){0,4}')
    foreach ($checkout in $checkouts) {
        Assert-True ($checkout.Value -match '(?m)^\s+persist-credentials:\s*false\s*$') `
            "$($file.Name) contains a checkout that persists GitHub credentials."
    }
}

Write-Host "workflow security: PASS ($($workflowFiles.Count) workflows, immutable actions, PR isolated from self-hosted/signing)"
