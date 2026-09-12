#Requires -Version 5.1
function Invoke-HorizunQueryEfficiencyProbe {
    param([string]$Category,[scriptblock]$Call)
    try {
        $answers=@{}; $sizes=@{}; $times=@{}
        foreach($mode in @('full','compact','summary')) {
            $requestArgs=@{include_links=$false;categories=@($Category);response_mode=$mode;max_rows=100}
            $watch=[Diagnostics.Stopwatch]::StartNew()
            $answer=& $Call 'horizun_query_model' $requestArgs
            $watch.Stop();$times[$mode]=$watch.ElapsedMilliseconds
            if($answer.isError -or -not $answer.data) { throw "query $mode failed: $($answer.text)" }
            $answers[$mode]=$answer.data
            $sizes[$mode]=[Text.Encoding]::UTF8.GetByteCount(($answer.data|ConvertTo-Json -Depth 30 -Compress))
        }
        foreach($mode in @('compact','summary')) {
            if($answers[$mode].matched_total -ne $answers.full.matched_total -or
                $answers[$mode].coverage_complete -ne $answers.full.coverage_complete -or
                $answers[$mode].unreadable_total -ne $answers.full.unreadable_total -or
                ($answers[$mode].summary|ConvertTo-Json -Depth 20 -Compress) -ne ($answers.full.summary|ConvertTo-Json -Depth 20 -Compress)) {
                throw "$mode changed whole-set counts or coverage findings"
            }
        }
        if($answers.summary.PSObject.Properties.Name -contains 'rows') { throw 'summary unexpectedly returned rows' }
        if($answers.full.returned -gt 5 -and ($sizes.summary -ge $sizes.full -or $sizes.compact -ge $sizes.full)) {
            throw 'lean query modes did not reduce response bytes'
        }
        return @{outcome='pass';detail=(@{bytes=$sizes;milliseconds=$times;matched=$answers.full.matched_total;rows=$answers.full.returned}|ConvertTo-Json -Compress)}
    } catch { return @{outcome='fail';detail=$_.Exception.Message} }
}

function Invoke-HorizunWorkflowPreviewProbe {
    param([string]$Document,[long]$ElementId,[scriptblock]$Apply,[scriptblock]$Call)
    if($ElementId -le 0) { return @{outcome='unverified';detail='the piping write fixture did not provide a target for the workflow'} }
    try {
        $result=& $Apply 'horizun_execute_plan' @{
            target_document=$Document; workflow=@{name='pin_elements';element_ids=@($ElementId)}
        } 'workflow-pin'
        if($result.stage -ne 'apply' -or $result.answer.isError) { throw "workflow did not apply: $($result.answer.text)" }
        $preview=$result.dry.data.actions[0].data.change_preview
        if(-not $preview.fingerprint -or $preview.rows[0].element_id -ne $ElementId -or
            $preview.rows[0].captured_state.pinned -ne 'False' -or
            $preview.rows[0].proposed_values.pinned -ne 'True' -or
            $preview.fingerprint -ne $result.dry.data.actions[0].data.plan_resolved.fingerprint) {
            throw 'workflow preview did not identify its target, proposed pin state and confirmation fingerprint'
        }
        # A second rehearsal must observe the pin written by the first apply.
        $again=& $Call 'horizun_transform_elements' @{
            target_document=$Document;dry_run=$true;operations=@(@{operation='pin';element_ids=@($ElementId)})
        }
        if($again.isError -or $again.data.change_preview.rows[0].captured_state.pinned -ne 'True') {
            throw 'independent rehearsal did not observe the final pin state'
        }
        return @{outcome='pass';detail='named workflow committed; preview fingerprint matched; final pin state independently re-read'}
    } catch { return @{outcome='fail';detail=$_.Exception.Message} }
}

function Invoke-HorizunAsyncRecoveryProbe {
    param([string]$RunId,[string]$Category,[scriptblock]$Call)
    try {
        $requestArgs=@{tool='horizun_query_model';arguments=@{categories=@($Category);include_links=$false;max_rows=1};idempotency_key="async-proof-$RunId"}
        $first=& $Call 'horizun_submit_job' $requestArgs
        if($first.isError -or -not $first.data.job_id) { throw "async admission failed: $($first.text)" }
        $second=& $Call 'horizun_submit_job' $requestArgs
        if($second.isError -or $second.data.job_id -ne $first.data.job_id) { throw 'same-key submission did not return the same durable job' }
        $deadline=[DateTime]::UtcNow.AddSeconds(90); $job=$null
        do {
            $status=& $Call 'horizun_job_status' @{job_id=$first.data.job_id}
            if($status.isError) { throw "job status failed: $($status.text)" }
            $job=$status.data.jobs[0]
            if($job.finished) { break }
            Start-Sleep -Milliseconds 500
        } while([DateTime]::UtcNow -lt $deadline)
        if(-not $job.finished) { return @{outcome='unverified';detail="job $($first.data.job_id) remains active; observation deadline reached"} }
        if($job.state -ne 'ok' -or $job.record_complete -ne $true -or $job.result_present -ne $true -or
            $job.recovery.action -ne 'read_result' -or $job.recovery.resume_candidate -ne $false) {
            throw 'completed job did not expose complete results and safe recovery guidance'
        }
        $retry=@{tool=$requestArgs.tool;arguments=$requestArgs.arguments;resume_from_job_id=$first.data.job_id;idempotency_key="resume:$($first.data.job_id)"}
        $refused=& $Call 'horizun_submit_job' $retry
        if(-not $refused.isError) { throw 'a completed job was accepted for replay' }
        return @{outcome='pass';detail='same-key deduplication, durable result polling and refusal to replay a completed job exercised'}
    } catch { return @{outcome='fail';detail=$_.Exception.Message} }
}
