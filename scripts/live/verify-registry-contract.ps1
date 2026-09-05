<#
.SYNOPSIS
  The contract and the registry, held against each other by a running pair.

.DESCRIPTION
  A TOOL A CLIENT CAN SEE IS A TOOL A CLIENT WILL CALL. The per-call guard has
  always refused a command the loaded add-in does not register, and refused a
  server and an add-in built from different contracts - but tools/list
  advertised them anyway, so the refusal arrived in the middle of somebody's
  work instead of as an absence they could plan around.

  This harness measures that on real binaries, in two modes:

    -Mode matched    the server and the add-in were built from ONE tree. Nothing
                     is withheld, health.registry is clean, and every plugin
                     command the contract names is registered exactly once.

    -Mode mismatched the server and the add-in were built from DIFFERENT trees -
                     which this machine can produce honestly by pointing a fresh
                     server at the INSTALLED add-in. Every plugin tool is then
                     withheld, every host-resident tool is still listed, and
                     horizun://build/identity says which and why.

  The mismatched mode is not a simulation: it is two binaries that really
  disagree, and it is the case a client hits after half an update.

.PARAMETER Mode
  matched | mismatched - what this run expects of the pair it finds.

.PARAMETER RequireContractHash
  The server's contract hash, asserted before anything is measured. In
  mismatched mode this is still the SERVER's hash; the add-in's must differ.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('matched', 'mismatched')][string]$Mode,
    [string]$RequireContractHash,
    [string]$Document,
    [string]$ArtifactDir
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'horizun-live.lib.ps1')

$run = New-HzRun -Harness $PSCommandPath -Name ('registry-' + $Mode) -Document $Document
Write-Host "`n== registry and contract, mode $Mode ==" -ForegroundColor Cyan

# ---------------------------------------------------------------- the gate
# MEASURED 2026-09-03: with two builds that disagree about the contract, the
# per-call guard refuses EVERY plugin command - horizun_health included. So the
# client cannot ask the bridge what is wrong, and horizun://build/identity is
# the only diagnostic left. That is why the withheld list lives there, and why
# this harness does not require health in mismatched mode.
$health = $null
try { $health = Get-HzHealth $run } catch { }
if (-not $health -and $Mode -eq 'matched') {
    Add-HzProbe -Run $run -Id 'R0' -Name 'a bridge answered' -Expected 'horizun_health answers' `
        -Observed 'no answer' -Status 'failed'
    $done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
    exit $done.ExitCode
}
Add-HzProbe -Run $run -Id 'R0' -Name 'health is reachable exactly when the two halves agree' `
    -Expected $(if ($Mode -eq 'matched') { 'horizun_health answers' } else { 'horizun_health is REFUSED like every other plugin command, so build/identity is the only diagnostic' }) `
    -Observed ("health_answered={0}" -f ($null -ne $health)) `
    -Ok $(if ($Mode -eq 'matched') { $null -ne $health } else { $null -eq $health })

# Get-HzResource returns the resource BODY already parsed - hz-call unwraps the
# contents envelope. A run that re-parsed it read nothing and reported an empty
# contract hash, which is a harness fault dressed as a product one.
$serverContract = $null
$build = Get-HzResource -Run $run -Uri 'horizun://build/identity' -Label 'build-identity'
$identity = $build
if ($build) { $serverContract = [string](Get-HzProp $build 'contract_hash') }
if ($RequireContractHash -and $serverContract -ne $RequireContractHash) {
    throw ("the server's contract hash is '$serverContract' and this run was told to expect '$RequireContractHash'. " +
           'Nothing was measured.')
}
$addinCommit = if ($health) { [string](Get-HzProp $health 'horizun_commit') } else { $null }
$addinClean = if ($health) { Get-HzProp $health 'built_from_clean_tree' } else { $null }
$asm = if ($health) { Get-HzProp $health 'addin_assembly' } else { $null }
$addinSha = if ($asm) { [string](Get-HzProp $asm 'sha256') } else { $null }
Add-HzNote $run ("server contract {0}; add-in {1} clean={2}" -f $serverContract, $addinCommit, $addinClean)

$registry = if ($health) { Get-HzProp $health 'registry' } else { $null }
if ($Mode -eq 'matched') {
# R1. THE ADD-IN HASHES ITSELF. Without this every campaign records
# addin_sha256: null and no result is tied to the bytes that produced it.
Add-HzProbe -Run $run -Id 'R1' -Name 'the add-in publishes the assembly Revit loaded, hashed by the process that loaded it' `
    -Expected 'health.addin_assembly carries a path, a 64-character sha256, a byte count and a write time' `
    -Observed ("path={0} sha256={1} bytes={2}" -f
        $(if ($asm) { (Split-Path -Leaf ([string](Get-HzProp $asm 'path'))) } else { 'none' }),
        $(if ($addinSha) { $addinSha.Substring(0, [Math]::Min(12, $addinSha.Length)) } else { 'null' }),
        $(if ($asm) { [string](Get-HzProp $asm 'bytes') } else { 'null' })) `
    -Ok ($null -ne $asm -and $addinSha -and $addinSha.Length -eq 64 -and
         [long](Get-HzProp $asm 'bytes') -gt 0 -and [string](Get-HzProp $asm 'path')) `
    -Evidence @{ addin_assembly = $asm }

# R2. The registry verdict the add-in computed at startup.
$regClean = if ($registry) { Get-HzProp $registry 'clean' } else { $null }
$regCount = if ($registry) { [int](Get-HzProp $registry 'registered') } else { -1 }
$regContract = if ($registry) { [int](Get-HzProp $registry 'contract_commands') } else { -2 }
Add-HzProbe -Run $run -Id 'R2' -Name 'health publishes the startup comparison between the contract and what was registered' `
    -Expected 'registry.clean is true, registered equals contract_commands, and the four disagreement lists are empty' `
    -Observed ("clean={0} registered={1} contract_commands={2} missing={3} duplicates={4} unadvertised={5} case={6}" -f
        $regClean, $regCount, $regContract,
        @(Get-HzProp $registry 'missing').Count, @(Get-HzProp $registry 'duplicates').Count,
        @(Get-HzProp $registry 'unadvertised').Count, @(Get-HzProp $registry 'case_mismatches').Count) `
    -Ok ($regClean -eq $true -and $regCount -gt 0 -and $regCount -eq $regContract -and
         @(Get-HzProp $registry 'missing').Count -eq 0 -and @(Get-HzProp $registry 'duplicates').Count -eq 0) `
    -Evidence @{ registry = $registry }
}

# ------------------------------------------------- what the client is told
$tools = Get-HzToolList -Run $run
$names = @()
if ($tools) { $names = @($tools | ForEach-Object { [string]$_.name }) }
$withheld = @()
$withheldMeans = $null
$buildRegistry = if ($build) { Get-HzProp $build 'registry' } else { $null }
if ($buildRegistry) {
    $withheld = @(Get-HzProp $buildRegistry 'withheld')
    $withheldMeans = [string](Get-HzProp $buildRegistry 'means')
}
# WHICH TOOLS NEED REVIT IS THE CONTRACT'S ANSWER, NOT A LIST HERE. This began
# as six names typed into this file, and it was wrong within a release: a
# seventh host-resident tool was added and the harness reported it as a plugin
# tool that should have been withheld. horizun://contract/tools publishes a
# null command for exactly the tools answered in the server.
$contractTools = Get-HzResource -Run $run -Uri 'horizun://contract/tools' -Label 'contract-tools'
$hostResident = @()
$pluginTools = @()
foreach ($t in @(Get-HzProp $contractTools 'tools')) {
    $cmd = [string](Get-HzProp $t 'command')
    if ([string]::IsNullOrEmpty($cmd)) { $hostResident += [string](Get-HzProp $t 'name') }
    else { $pluginTools += [string](Get-HzProp $t 'name') }
}
if ($hostResident.Count -eq 0 -or $pluginTools.Count -eq 0) {
    throw 'horizun://contract/tools did not publish a command per tool; nothing could be classified.'
}
$hostListed = @($hostResident | Where-Object { $names -contains $_ })
Add-HzNote $run ("contract publishes {0} host-resident and {1} plugin tools" -f $hostResident.Count, $pluginTools.Count)

if ($Mode -eq 'matched') {
    Add-HzProbe -Run $run -Id 'R3' -Name 'a matched pair withholds nothing: every tool the server publishes is answerable' `
        -Expected 'build/identity withheld_count 0, and the plugin tools are in tools/list' `
        -Observed ("withheld={0} tools={1} health_listed={2}" -f $withheld.Count, $names.Count,
                   ($names -contains 'horizun_health')) `
        -Ok ($withheld.Count -eq 0 -and $names -contains 'horizun_health' -and $names.Count -gt 50) `
        -Evidence @{ withheld_count = $withheld.Count; tool_count = $names.Count; means = $withheldMeans }

    Add-HzProbe -Run $run -Id 'R4' -Name 'the add-in registers every plugin command the contract names, and no more' `
        -Expected 'registry.registered equals registry.contract_commands, with nothing unadvertised' `
        -Observed ("registered={0} contract={1} unadvertised={2}" -f $regCount, $regContract,
                   (@(Get-HzProp $registry 'unadvertised') -join ',')) `
        -Ok ($regCount -eq $regContract -and @(Get-HzProp $registry 'unadvertised').Count -eq 0) `
        -Evidence @{ registry = $registry }
}
else {
    # A REAL DISAGREEMENT, NOT A SIMULATED ONE: a fresh server pointed at the
    # add-in a previous release installed. Their contract hashes differ, so no
    # plugin tool can be called safely and none is advertised.
    $addinContract = $null
    if ($registry) { $addinContract = $null }   # the add-in does not publish its own hash in health
    $pluginListed = @($names | Where-Object { $pluginTools -contains $_ })

    Add-HzProbe -Run $run -Id 'R3' -Name 'two builds that disagree about the contract advertise NO plugin tool' `
        -Expected 'every listed tool is host-resident; every plugin tool appears in build/identity withheld' `
        -Observed ("listed={0} plugin_listed={1} withheld={2} of {3} plugin tools" -f
                   $names.Count, $pluginListed.Count, $withheld.Count, $pluginTools.Count) `
        -Ok ($pluginListed.Count -eq 0 -and $withheld.Count -eq $pluginTools.Count) `
        -Evidence @{ listed = $names; withheld_count = $withheld.Count }

    $reason = if ($withheld.Count -gt 0) { [string]$withheld[0].reason } else { '' }
    Add-HzProbe -Run $run -Id 'R4' -Name 'the diagnostic names the disagreement and what to do about it' `
        -Expected 'each withheld entry names its plugin command, both contract hashes and install.ps1' `
        -Observed (Limit-HzText $reason 200) `
        -Ok ($reason -match 'DIFFERENT' -and $reason -match 'install\.ps1' -and $reason -match [regex]::Escape($serverContract)) `
        -Evidence @{ first_withheld = $(if ($withheld.Count -gt 0) { $withheld[0] } else { $null }) }
}

# R5. Host-resident tools are never withheld, in either mode: they are answered
# here, and Revit is not involved in answering them.
Add-HzProbe -Run $run -Id 'R5' -Name 'a host-resident tool is listed whatever the add-in does' `
    -Expected 'every host-resident tool the profile allows is in tools/list' `
    -Observed ("host_resident_listed={0} of {1}: {2}" -f $hostListed.Count, $hostResident.Count, ($hostListed -join ',')) `
    -Ok ($hostListed.Count -eq $hostResident.Count) `
    -Evidence @{ listed = $hostListed }

# R6. A call for a withheld tool must still explain itself. Advertising nothing
# and refusing without a reason would be the same failure one level down.
$probeTool = 'horizun_clash'
$call = Invoke-HzTool -Run $run -Tool $probeTool -Label 'withheld-call' -Arguments @{ } -TimeoutSec 120
if ($Mode -eq 'mismatched') {
    $msg = [string]$call.Raw
    Add-HzProbe -Run $run -Id 'R6' -Name 'calling a withheld tool is refused with a reason, not with silence' `
        -Expected 'an error naming the two contracts and the redeploy' `
        -Observed (Limit-HzText $msg 200) `
        -Ok ($call.IsError -and $msg -match 'DIFFERENT command contracts?') `
        -Evidence @{ error = (Limit-HzText $msg 400) }
}
else {
    Add-HzProbe -Run $run -Id 'R6' -Name 'a listed tool is answerable: it is refused for its ARGUMENTS, never for its absence' `
        -Expected 'no "does not register" and no "DIFFERENT command contract" in the reply' `
        -Observed (Limit-HzText ([string]$call.Raw) 200) `
        -Ok (-not ([string]$call.Raw -match 'does not register|DIFFERENT command contract')) `
        -Evidence @{ reply = (Limit-HzText ([string]$call.Raw) 400) }
}

$done = Complete-HzRun -Run $run -ArtifactDir $ArtifactDir
exit $done.ExitCode
