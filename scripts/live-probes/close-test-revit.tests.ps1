#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'close-test-revit.ps1')
$scriptPath = Join-Path $PSScriptRoot 'close-test-revit.ps1'
$parameter = (Get-Command $scriptPath).Parameters['RevitPid']
if ($parameter.Aliases -notcontains 'Pid') { throw '-Pid alias is missing' }
Assert-HzClosePid 12 @([pscustomobject]@{Id=12}, [pscustomobject]@{Id=34})
$message = try { Assert-HzClosePid 0 @([pscustomobject]@{Id=34}, [pscustomobject]@{Id=12}); '' } catch { $_.Exception.Message }
if ($message -notmatch '12, 34' -or $message -notmatch '-Pid') { throw "Ambiguous instances not reported: $message" }
$message = try { Assert-HzClosePid 0 @([pscustomobject]@{Id=12}); '' } catch { $_.Exception.Message }
if ($message -notmatch 'owned test instance') { throw 'Missing PID did not refuse a single instance' }
$root = Join-Path ([IO.Path]::GetTempPath()) ('hz-close-paths-' + [guid]::NewGuid().ToString('N'))
$sibling = $root + '-other'
try {
    New-Item -ItemType Directory -Path $root, $sibling | Out-Null
    $inside = Join-Path $root 'model.rvt'; $outside = Join-Path $sibling 'model.rvt'
    Set-Content -LiteralPath $inside -Value 'fixture'; Set-Content -LiteralPath $outside -Value 'fixture'
    if (-not (Test-HzTestPath $root $inside)) { throw 'inside document rejected' }
    if (Test-HzTestPath $root $outside) { throw 'prefix sibling accepted' }
    $traversal = Join-Path $root ('..\' + (Split-Path -Leaf $sibling) + '\model.rvt')
    if (Test-HzTestPath $root $traversal) { throw 'traversal accepted' }
    if (Test-HzTestPath $root 'model.rvt') { throw 'relative path accepted' }
    if (Test-HzTestPath $root '') { throw 'empty path accepted' }

    # Sanitized projection of V7c full health, as saved by hz-call -Json:
    # result is structuredContent itself. Paths and process ID are fictitious.
    $dataRoot = Join-Path $root 'bridge'
    $anchor = Join-Path $dataRoot 'anchor\HZ_ANCHOR_2026.rvt'
    $fixture = @'
{"result":{"status":"healthy","estado":"listo","detail":"full","revit_version":"2026","data_paths":{"data_root":"ROOT"},"active_document":{"path":"ACTIVE"},"open_document_count":1,"open_documents":[{"path":"ACTIVE","is_linked":false,"is_active":true}]}}
'@ | ConvertFrom-Json
    $fixture.result.data_paths.data_root = $dataRoot
    $fixture.result.active_document.path = $inside
    $fixture.result.open_documents[0].path = $inside
    $script:calls = @(); $script:withToken = $true
    function Call-HzCloseTool([string]$Tool, [hashtable]$Arguments) {
        $script:calls += [pscustomobject]@{ tool=$Tool; path=$Arguments.target_document; dry=$Arguments.dry_run; token=$Arguments.confirmation_token; key=$Arguments.idempotency_key; discard=$Arguments.discard_unsaved; activate=$Arguments.activate_other }
        $payload = if ($Arguments.dry_run) {
            @{ path=$Arguments.target_document; confirmation_token=$(if ($script:withToken) { 'test-token' } else { $null }) }
        } else { @{ closed=$true; disk_changed=$false } }
        $record = Join-Path $root 'reply.json'
        @{ is_error=$false; result=$payload } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $record -Encoding UTF8
        return Read-HzCloseReply $Tool $record 0
    }
    function New-FakeProcess([bool]$Exits) {
        $fake = [pscustomobject]@{ closes=0; waitMs=0; exits=$Exits }
        $fake | Add-Member ScriptMethod CloseMainWindow { $this.closes++; return $true }
        $fake | Add-Member ScriptMethod WaitForExit { param($ms) $this.waitMs=$ms; return $this.exits }
        return $fake
    }
    $fake = New-FakeProcess $true
    Invoke-HzTestClose $fixture.result $root $fake 1234
    $key = [guid]::Empty
    if ($script:calls.Count -ne 2 -or -not $script:calls[0].dry -or $script:calls[1].dry -or $script:calls[1].token -ne 'test-token' -or -not [guid]::TryParse($script:calls[1].key, [ref]$key) -or -not $script:calls[0].discard -or -not $script:calls[1].activate -or $fake.closes -ne 1 -or $fake.waitMs -ne 180000) { throw 'Test copy was not rehearsed, closed and waited for' }
    $script:withToken = $false; $script:calls = @(); $fake = New-FakeProcess $true
    Invoke-HzTestClose $fixture.result $root $fake 1234
    if ($script:calls.Count -ne 2 -or $script:calls[1].token -or -not $script:calls[1].key -or $fake.closes -ne 1) { throw 'Token-free rehearsal did not reach window close' }
    $script:withToken = $true

    $record = Join-Path $root 'refusal.json'
    '{"is_error":true,"result":{"error_code":"entrada_invalida","mensaje":"bad argument","suggested_call":{"tool":"horizun_document_session","arguments":{"operation":"close"}}},"raw":"refused"}' | Set-Content -LiteralPath $record -Encoding UTF8
    $message = try { Read-HzCloseReply 'horizun_document_session' $record 1; '' } catch { $_.Exception.Message }
    if ($message -notmatch 'entrada_invalida' -or $message -notmatch 'bad argument' -or $message -notmatch 'suggested_call=.*horizun_document_session') { throw "Refusal was hidden: $message" }
    '{"is_error":true,"result":{"error_code":"esquema_invalido","suggested_call":{"tool":"horizun_document_session"}},"raw":"Error: undeclared argument"}' | Set-Content -LiteralPath $record -Encoding UTF8
    $message = try { Read-HzCloseReply 'horizun_document_session' $record 1; '' } catch { $_.Exception.Message }
    if ($message -notmatch 'esquema_invalido' -or $message -notmatch 'undeclared argument' -or $message -notmatch 'suggested_call=.*horizun_document_session') { throw "Structured refusal text was hidden: $message" }
    $message = try { Read-HzCloseReply 'horizun_document_session' (Join-Path $root 'missing.json') 2; '' } catch { $_.Exception.Message }
    if ($message -notmatch 'exit code 2' -or $message -notmatch 'no response JSON') { throw "Missing reply was hidden: $message" }
    '{"is_error":false,"result":{"estado":"puente_inaccesible"}}' | Set-Content -LiteralPath $record -Encoding UTF8
    $message = try { Read-HzCloseReply 'horizun_health' $record 0; '' } catch { $_.Exception.Message }
    if ($message -notmatch 'el puente no es accesible desde este proceso' -or $message -notmatch 'ciérralo desde fuera') { throw "Inaccessible bridge was not explained: $message" }
    $message = try { Get-HzCloseHealth (@{estado='puente_inaccesible'} | ConvertTo-Json | ConvertFrom-Json); '' } catch { $_.Exception.Message }
    if ($message -notmatch 'aislamiento') { throw "Health did not identify isolation: $message" }

    $fixture.result.active_document.path = $anchor
    $fixture.result.open_documents[0].path = $anchor
    $script:calls = @(); $fake = New-FakeProcess $true
    Invoke-HzTestClose $fixture.result $root $fake 1234
    if ($script:calls.Count -ne 0 -or $fake.closes -ne 1 -or $fake.waitMs -ne 180000) { throw 'Anchor was not skipped or process was not awaited' }
    $fixture.result.open_documents += [pscustomobject]@{path=$inside; is_linked=$false; is_active=$false}
    $fixture.result.open_document_count = 2
    $script:calls = @(); $fake = New-FakeProcess $true
    Invoke-HzTestClose $fixture.result $root $fake 1234
    if ($script:calls.Count -ne 2 -or $script:calls[0].path -ne $inside) { throw 'Active anchor masked an open test copy' }
    $fixture.result.open_documents = @($fixture.result.open_documents[0])
    $fixture.result.open_document_count = 1
    $fake = New-FakeProcess $false
    $message = try { Invoke-HzTestClose $fixture.result $root $fake 1234; '' } catch { $_.Exception.Message }
    if ($message -notmatch '1234' -or $message -notmatch '3 minutes') { throw "Missing timeout evidence: $message" }

    $fixture.result.open_documents += [pscustomobject]@{path=$outside; is_linked=$false; is_active=$false}
    $fixture.result.open_document_count = 2
    $script:calls = @(); $fake = New-FakeProcess $true
    $message = try { Invoke-HzTestClose $fixture.result $root $fake 1234; '' } catch { $_.Exception.Message }
    if ($message -notmatch 'outside the test folder' -or $fake.closes -ne 0 -or $script:calls.Count -ne 0) { throw "Outside document was not refused: $message" }
    $bad = '{"estado":"ocupado","ready":false,"error_code":"revit_ocupado"}' | ConvertFrom-Json
    $message = try { Get-HzCloseHealth $bad; '' } catch { $_.Exception.Message }
    if ($message -notmatch 'status=<missing>' -or $message -notmatch 'estado=ocupado' -or $message -notmatch 'error_code') { throw "Health diagnostic incomplete: $message" }
    $fixture.result.open_documents = @(); $fixture.result.open_document_count = 0
    $fake = New-FakeProcess $true; $script:calls = @()
    Invoke-HzTestClose $fixture.result $root $fake 1234
    if ($fake.closes -ne 1 -or $script:calls.Count -ne 0) { throw 'Empty document list did not proceed to close' }
    Write-Host 'Path filter passed.'
} finally { Remove-Item -LiteralPath $root, $sibling -Recurse -Force -ErrorAction SilentlyContinue }
