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
Assert-True ($ci -match '(?s)windows-deployment:.*?runs-on:\s*windows-latest') `
    'CI must run deployment/installer suites on a disposable Windows runner.'
Assert-True ($pr -match '(?s)windows-deployment:.*?runs-on:\s*windows-latest') `
    'Pull requests must run deployment/installer suites on a disposable Windows runner.'
Assert-True ($ci -match '(?s)publish-validation-release:.*?needs:\s*\[revit-free,\s*windows-deployment\]') `
    'Validation-only publication must wait for both hosted source and Windows deployment gates.'
Assert-True ($ci -match '(?s)build-stage:.*?needs:\s*\[revit-free,\s*windows-deployment,\s*revit-addin\]') `
    'The self-hosted stage build must not proceed when the Windows deployment gate failed.'
Assert-True ($ci -match '(?s)revit-addin:.*?group:\s*\$\{\{\s*vars\.REVIT_RUNNER_GROUP\s*\}\}') `
    'Revit jobs must target the externally configured Revit runner group.'
foreach ($jobName in 'build-stage','compile-installer') {
    Assert-True ($ci -match "(?s)$([regex]::Escape($jobName)):.*?group:\s*\$\{\{\s*vars\.SIGNING_RUNNER_GROUP\s*\}\}.*?labels:\s*\[self-hosted, windows, revit, signing\]") `
        "$jobName must target the isolated build runner group."
}

function Get-JobBlock([string]$name) {
    $match = [regex]::Match($ci, "(?ms)^  $([regex]::Escape($name)):\s*\r?\n(?:(?!^  [a-zA-Z0-9_-]+:\s*$).)*")
    Assert-True $match.Success "Workflow job '$name' is missing."
    $match.Value
}

$selfHostedReleaseBlocks = (Get-JobBlock 'build-stage') + (Get-JobBlock 'compile-installer')
Assert-True ($selfHostedReleaseBlocks -notmatch 'SIGNPATH_API_TOKEN|secrets\.|environment:\s*release-signing') `
    'A SignPath credential or protected signing environment can reach a self-hosted release job.'
foreach ($jobName in 'build-stage','compile-installer') {
    Assert-True ((Get-JobBlock $jobName) -match "startsWith\(github\.ref,\s*'refs/tags/v'\).*?!contains\(github\.ref_name,\s*'-validation\.'\)") `
        "$jobName must run only for installable tags."
}
foreach ($jobName in 'sign-payload','sign-installer') {
    $block = Get-JobBlock $jobName
    Assert-True ($block -match 'runs-on:\s*windows-latest' -and
                 $block -match 'environment:\s*release-signing' -and
                 $block -match 'secrets\.SIGNPATH_API_TOKEN') `
        "$jobName must be the protected hosted boundary that consumes SIGNPATH_API_TOKEN."
}
Assert-True ((Get-JobBlock 'package') -match 'runs-on:\s*windows-latest' -and
             (Get-JobBlock 'package') -notmatch 'self-hosted|SIGNPATH_API_TOKEN') `
    'Final package verification, attestation and upload must run on a hosted runner without the SignPath token.'
Assert-True ([regex]::Matches($ci, 'signpath/github-action-submit-signing-request@c92b958760219087e01f8d67a1669ed57afe2627').Count -eq 2) `
    'The installable path must use the immutable SignPath v2.3 action exactly twice: payload, then installer.'
Assert-True ([regex]::Matches($ci, 'secrets\.SIGNPATH_API_TOKEN').Count -eq 2) `
    'SIGNPATH_API_TOKEN must appear exactly once in each hosted signing-request job.'
foreach ($handoff in
    'github-artifact-id: ${{ needs.build-stage.outputs.payload-artifact-id }}',
    'artifact-ids: ${{ needs.sign-payload.outputs.signed-payload-artifact-id }}',
    'github-artifact-id: ${{ needs.compile-installer.outputs.installer-artifact-id }}',
    'artifact-ids: ${{ needs.compile-installer.outputs.package-support-artifact-id }}',
    'artifact-ids: ${{ needs.sign-installer.outputs.signed-installer-artifact-id }}') {
    Assert-True ($ci.Contains($handoff)) "Immutable artifact-id hand-off is missing: $handoff"
}
$artifactIdDownloads = [regex]::Matches($ci, '(?m)^\s+artifact-ids:\s*\$\{\{')
$directArtifactIdDownloads = [regex]::Matches($ci, '(?m)^\s+artifact-ids:\s*\$\{\{[^\r\n]+\r?\n\s+path:[^\r\n]+\r?\n\s+merge-multiple:\s*true\s*$')
Assert-True ($artifactIdDownloads.Count -gt 0 -and $directArtifactIdDownloads.Count -eq $artifactIdDownloads.Count) `
    'Every artifact-id download must extract directly into its verified destination (merge-multiple: true).'
Assert-True ($ci -notmatch 'SIGNING_CERT_THUMBPRINT') `
    'Public release CI must not expose a local certificate-store signing fallback.'
foreach ($required in 'SIGNPATH_ORGANIZATION_ID','SIGNPATH_PROJECT_SLUG','SIGNPATH_PAYLOAD_POLICY_SLUG',
                      'SIGNPATH_PAYLOAD_ARTIFACT_CONFIGURATION_SLUG','SIGNPATH_INSTALLER_POLICY_SLUG',
                      'SIGNPATH_INSTALLER_ARTIFACT_CONFIGURATION_SLUG','SIGNPATH_API_TOKEN') {
    Assert-True ($ci -match [regex]::Escape($required)) "Release workflow does not consume required SignPath setting $required."
}
Assert-True ($ci -match 'SIGNING_RUNNER_GROUP.*!=.*REVIT_RUNNER_GROUP|SIGNING_RUNNER_GROUP.*-ne.*REVIT_RUNNER_GROUP') `
    'The workflow must fail closed unless signing and integration runner groups differ.'
Assert-True ($ci -match 'SIGNPATH_SELF_HOSTED_ORIGIN_APPROVED.*!=.*true') `
    'Stable signing must fail closed until SignPath approves the self-hosted Revit origin.'
Assert-True ([regex]::Matches($ci, '(?m)^\s+NUGET_PACKAGES:\s*\$\{\{\s*github\.workspace\s*\}\}.*?github\.run_id').Count -ge 2) `
    'Every self-hosted build/package route must use a run-isolated NuGet extraction root.'

$sdk = (Get-Content -LiteralPath (Join-Path $repo 'global.json') -Raw | ConvertFrom-Json).sdk.version
foreach ($workflow in @($ci, $pr)) {
    $setups = [regex]::Matches($workflow, 'uses:\s*actions/setup-dotnet@').Count
    $sdkPins = [regex]::Matches($workflow, "(?m)^\s+$([regex]::Escape($sdk))\s*$").Count
    $runtimeSdkPins = [regex]::Matches($workflow, '(?m)^\s+8\.0\.424\s*$').Count
    Assert-True ($setups -gt 0 -and $sdkPins -eq $setups -and $runtimeSdkPins -eq $setups) `
        "Every setup-dotnet step must install exact build SDK $sdk and SDK 8.0.424 carrying runtime 8.0.30."
}

# Child PowerShell processes do not make their non-zero exit code terminating in
# a parent pwsh script. In a multiline `run: |` block, a later successful child
# can therefore erase an earlier failure. A one-command `run: pwsh ...` returns
# that child's status directly; every other child call must be followed by an
# explicit LASTEXITCODE check.
foreach ($workflowPath in @($ciPath, $prPath)) {
    $lines = @(Get-Content -LiteralPath $workflowPath)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*run:\s+pwsh(?:\s|$)') { continue }
        if ($lines[$i] -notmatch '^\s+pwsh(?:\s|$)') { continue }
        $next = $i + 1
        while ($next -lt $lines.Count -and [string]::IsNullOrWhiteSpace($lines[$next])) { $next++ }
        Assert-True ($next -lt $lines.Count -and $lines[$next] -match '\$LASTEXITCODE\s+-ne\s+0') `
            "$(Split-Path -Leaf $workflowPath):$($i + 1) invokes child pwsh without immediately enforcing LASTEXITCODE."
    }
}

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
