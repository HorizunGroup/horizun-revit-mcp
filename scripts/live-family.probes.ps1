#Requires -Version 5.1
# A generated non-client RFA fixture, saved separately and loaded only into the
# disposable write document. Callbacks use the harness's existing MCP session.
function Invoke-HorizunFamilyProbe {
    param([string]$Document, [string]$Template, [int]$Year, [string]$OutputDirectory,
          [string]$RunId, [scriptblock]$Apply, [scriptblock]$Call)
    if (-not $Template -or -not (Test-Path -LiteralPath $Template -PathType Leaf)) {
        return @{ outcome = 'not_covered'; detail = 'a generic-model RFT is required for the RFA write probe' }
    }
    $name = "HZ_RELEASE_FAMILY_${Year}_$RunId"
    $output = Join-Path $OutputDirectory "$name.rfa"
    $spec = @{
        target_document = $Document; template_path = $Template; output_path = $output
        units = 'mm'; load_into_project = $true; overwrite = $false
        parameters = @(@{ name='HZ_TestDepth'; data_type='length'; group='geometry' })
        types = @(@{ name='Small'; values=@{ HZ_TestDepth=150 } },
                  @{ name='Large'; values=@{ HZ_TestDepth=300 } })
        forms = @(@{ key='body'; kind='extrusion'; plane='xy'; depth=150
            end_parameter='HZ_TestDepth'
            profile=@(,@(@(-50,-50,0),@(50,-50,0),@(50,50,0),@(-50,50,0))) })
    }
    try {
        $result = & $Apply 'horizun_create_family' $spec 'family'
        if ($result.stage -ne 'apply') {
            return @{ outcome='unverified'; detail="RFA rehearsal failed: $($result.answer.text)" }
        }
        $a = $result.answer
        if ($a.isError -or $a.data.output_verified -ne $true -or
            $a.data.family_document_verification.verified -ne $true -or
            $a.data.loaded_family.requested_type_names_verified -ne $true -or
            $a.data.forms_verified -ne 1 -or $a.data.types_requested -ne 2 -or
            -not (Test-Path -LiteralPath $output -PathType Leaf)) {
            return @{ outcome='fail'; detail="RFA creation/load did not verify: $($a.text)" }
        }
        # Separate calls measure the persisted file version and project symbols.
        # A create command's own success claim alone is not this probe's oracle.
        $disk = & $Call 'horizun_document_session' @{ operation='inspect'; file_path=$output }
        if ($disk.isError -or $disk.data.file.revit_version -ne "$Year" -or
            $disk.data.versions_match -ne $true) {
            return @{ outcome='fail'; detail='saved RFA version did not match the host' }
        }
        $read = & $Call 'horizun_query_model' @{
            family=$name; include_types=$true; include_links=$false; max_rows=10
            return_fields=@('family','type','is_element_type'); parameter_format='compact'
            return_parameters=@('HZ_TestDepth')
        }
        if ($read.isError -or $read.data.coverage_complete -ne $true -or $read.data.truncated -eq $true) {
            return @{ outcome='unverified'; detail='loaded RFA symbols could not be queried with complete coverage' }
        }
        foreach ($expected in @(@{name='Small'; depth=150}, @{name='Large'; depth=300})) {
            $rows = @($read.data.rows | Where-Object {
                $_.family -eq $name -and $_.type -eq $expected.name -and $_.is_element_type -eq $true
            })
            if ($rows.Count -ne 1 -or $null -eq $rows[0].parameters.HZ_TestDepth -or
                [Math]::Abs([double]$rows[0].parameters.HZ_TestDepth - $expected.depth / 304.8) -gt 1e-7) {
                return @{ outcome='fail'; detail="loaded type $($expected.name) did not retain its length value" }
            }
        }
        return @{ outcome='pass'; detail="RFA created, saved in $Year, loaded and two type values independently re-read; artifact $output" }
    } catch { return @{ outcome='fail'; detail=$_.Exception.Message } }
}
