#Requires -Version 5.1
# Exercises spatial-links.probes.ps1 WITHOUT Revit: fakes play a document whose link
# holds one wall, and a crossing host wall whose reply names that linked wall.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'spatial-links.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'spatial-links' }
$fail = 0
function Check($ok, $what) { if ($ok) { Write-Host "  PASS  $what" } else { Write-Host "  FAIL  $what"; $script:fail++ } }

function New-Fake([string]$mode) {
    $src = Join-Path $env:TEMP ('hz-fake-host-' + [guid]::NewGuid().ToString('N') + '.rvt')
    Set-Content -LiteralPath $src -Value 'rvt' -Encoding ascii
    $s = @{ Mode = $mode; Next = 500; Deleted = @(); Src = $src }
    $reply = { param($data, $isError = $false, $text = 'fake') [pscustomobject]@{ isError = $isError; data = $data; text = $text } }
    $call = {
        param($tool, $a)
        switch ($tool) {
            'horizun_health' { return & $reply ([pscustomobject]@{ open_documents = @([pscustomobject]@{ title = 'HZ_WRITE'; path = $s.Src }) }) }
            'horizun_query_model' {
                if ($s.Mode -eq 'no-link-wall') { return & $reply ([pscustomobject]@{ rows = @() }) }
                $bb = [pscustomobject]@{ min = @(1000, 2000, 0); max = @(7000, 2200, 3000) }
                return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 77; source_kind = 'link'; is_element_type = $false; bounding_box = $bb }) })
            }
        }
        return & $reply $null $true
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        $ok = { param($d) @{ stage = 'apply'; answer = [pscustomobject]@{ isError = $false; data = $d; text = 'ok' } } }
        switch ($tool) {
            'horizun_manage_links' { return & $ok ([pscustomobject]@{ link_type_id = 900; link_instance_id = 901 }) }
            'horizun_delete_verified' { $s.Deleted = @($a.ids); return & $ok ([pscustomobject]@{ ok = $true }) }
            'horizun_create_elements' {
                $id = $s.Next; $s.Next++
                $d = [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $id }) }
                if ($a.elements[0].kind -eq 'wall') {
                    $f = if ($s.Mode -eq 'no-finding') { @() } else { @([pscustomobject]@{ severity = 'warning'; reason = "two wall overlap without being joined (in link 'L')"; b = [pscustomobject]@{ id = 77; source = 'link' } }) }
                    $d | Add-Member spatial_check ([pscustomobject]@{ status = 'warnings'; findings = $f; links_examined = 1; link_neighbours_examined = 1; links_skipped = @() })
                }
                return & $ok $d
            }
        }
    }.GetNewClosure()
    return @{ State = $s; Ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = (Join-Path $env:TEMP ('hz-sl-' + [guid]::NewGuid().ToString('N'))); RunId = 't1'; WriteGate = $false; Call = $call; Apply = $apply } }
}

$h = New-Fake 'ok'; $r = @(& $module.Run $h.Ctx)
Check ($r.Count -eq 3) 'three cases'
Check (@($r | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0) ('a host wall across a linked wall is named, the link examined, all cleaned: ' + (($r | ForEach-Object { $_.Outcome }) -join ','))
Check (($h.State.Deleted -contains 900) -and $h.State.Deleted.Count -eq 3) 'the wall, the level and the link type are deleted'

$h = New-Fake 'no-finding'; $r = @(& $module.Run $h.Ctx)
Check ($r[0].Outcome -eq 'fail') 'no finding naming the link fails the first case'

$h = New-Fake 'no-link-wall'; $r = @(& $module.Run $h.Ctx)
Check ($r[0].Outcome -eq 'not_covered' -and $r[2].Outcome -eq 'pass') 'a link without a wall is not_covered, and the link is still removed'

$h = New-Fake 'ok'; $h.Ctx.WriteGate = $true; $r = @(& $module.Run $h.Ctx)
Check (@($r | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 3) 'write tier closed: all not_covered'

if ($fail -gt 0) { Write-Host "spatial-links probe tests: $fail FAILED"; exit 1 }
Write-Host 'spatial-links probe tests: ALL PASS'
