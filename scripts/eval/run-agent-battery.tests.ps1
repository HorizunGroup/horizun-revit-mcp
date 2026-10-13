#Requires -Version 5.1
# Offline tests. Wire envelopes follow Anthropic's SDKAssistantMessage,
# SDKUserMessage and SDKResultMessage (see the plan for the reference).
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'run-agent-battery.ps1')
$failed = 0
function Assert($name, $condition) {
    if ($condition) { Write-Host "  PASS  $name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $name" -ForegroundColor Red; $script:failed++ }
}
function Wire($events) { @($events | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 30 -Compress }) }
function Use($id, $name = 'mcp__horizun-revit__horizun_query_model') {
    @{ type='assistant'; uuid=[guid]::NewGuid().ToString(); session_id='synthetic'; parent_tool_use_id=$null
       message=@{ id='msg-synthetic'; role='assistant'; type='message'; content=@(
           @{type='tool_use'; id=$id; name=$name; input=@{categories=@('OST_Walls')}}) } }
}
function Reply($id, $payload, $error = $false) {
    @{type='user'; uuid=[guid]::NewGuid().ToString(); session_id='synthetic'; parent_tool_use_id=$null
      message=@{role='user'; content=@(@{type='tool_result'; tool_use_id=$id; is_error=$error
          content=@(@{type='text'; text=(ConvertTo-Json -InputObject $payload -Depth 20 -Compress)})})}}
}
$terminal = @{type='result'; subtype='success'; is_error=$false; duration_ms=800; duration_api_ms=600
    num_turns=1; result='Done'; session_id='synthetic'; total_cost_usd=0.01
    usage=@{input_tokens=40; output_tokens=12; cache_read_input_tokens=8; cache_creation_input_tokens=0}}
$check = { param($replies) @($replies | Where-Object { -not $_.IsError -and $_.Payload.matched_total -eq 2 }).Count -eq 1 }
$events = @(@{type='system'; subtype='init'; session_id='synthetic'; tools=@() },
    (Use 'toolu_1'), (Reply 'toolu_1' @{matched_total=2}), $terminal)
$r = Read-AgentStream -Lines (Wire $events) -Check $check -WallSeconds 1.25
Assert 'completion comes from tool replies' ($r.Completed -and $r.State -eq 'completed')
Assert 'calls and caller wall time' ($r.ToolCalls -eq 1 -and $r.WallSeconds -eq 1.25)
Assert 'tokens use final usage without double counting' ($r.Tokens.input_tokens -eq 40 -and $r.Tokens.output_tokens -eq 12)
$r = Read-AgentStream -Lines (Wire @((Use 'toolu_1'), (Reply 'toolu_1' @{matched_total=1}), $terminal)) -Check $check
Assert 'successful CLI is not a successful task' (-not $r.Completed -and $r.State -eq 'not_completed')
$r = Read-AgentStream -Lines (Wire @($terminal)) -Check $check
Assert 'agent claim alone never passes' (-not $r.Completed)
$r = Read-AgentStream -Lines (Wire @((Use 'toolu_1'), (Use 'toolu_1'),
    @{type='stream_event'; event=@{type='content_block_start'; content_block=@{type='tool_use'; id='toolu_1'}}},
    (Use 'toolu_2'), (Reply 'toolu_1' @{matched_total=2}), $terminal)) -Check $check
Assert 'unique tool_use ids, not progress or partial events' ($r.ToolCalls -eq 2)
$r = Read-AgentStream -Lines (Wire @((Use 'toolu_1'),
    (Reply 'toolu_1' @{error='bad input'; schema_help=@{uri='horizun://contract/tools/horizun_query_model'}} $true),
    (Use 'toolu_2'), (Reply 'toolu_2' @{error='see horizun://contract/tools/horizun_query_model'} $true),
    (Use 'toolu_3'), (Reply 'toolu_3' @{help='horizun://contract/tools/horizun_query_model'}),
    (Use 'toolu_4'), (Reply 'toolu_4' @{error='connection failed'} $true), $terminal)) -Check $check
Assert 'only errors with contract evidence are refusals' ($r.ContractRefusals -eq 2)
$wrapped = @{content=@(@{type='text'; text='ok'}); structuredContent=@{matched_total=2}; isError=$false}
$r = Read-AgentStream -Lines (Wire @((Use 'toolu_1'), (Reply 'toolu_1' $wrapped), $terminal)) -Check $check
Assert 'MCP structuredContent is decoded' ($r.Completed)
$e = Reply 'toolu_1' @{matched_total=0}
$e.tool_use_result = $wrapped
$r = Read-AgentStream -Lines (Wire @((Use 'toolu_1'), $e, $terminal)) -Check $check
Assert 'SDK structured output takes precedence for a single result' ($r.Completed)
$contentOnly = @{content=@(@{type='text'; text='{"matched_total":2}'}); isError=$false}
$e = Reply 'toolu_1' @{matched_total=0}
$e.tool_use_result = $contentOnly
$r = Read-AgentStream -Lines (Wire @((Use 'toolu_1'), $e, $terminal)) -Check $check
Assert 'MCP content-only structured output is decoded' ($r.Completed)
$r = Read-AgentStream -Lines (Wire @((Use 'toolu_z'), (Reply 'toolu_z' @{matched_total=2}),
    (Use 'toolu_a'), (Reply 'toolu_a' @{matched_total=1}), $terminal)) -Check {
        param($replies, $calls) $calls[0].Id -eq 'toolu_z' -and $replies[0].Id -eq 'toolu_z'
    }
Assert 'evidence retains call order rather than sorting opaque ids' ($r.Completed)
$r = Read-AgentStream -Lines (Wire @((Use 'toolu_1'), (Reply 'toolu_1' @{matched_total=2}))) -Check $check -ExitCode 7
Assert 'crashed process cannot pass even with evidence' (-not $r.Completed -and $r.State -eq 'crashed')
$r = Read-AgentStream -Lines (Wire $events) -Check $check -TimedOut
Assert 'timeout cannot pass even with a final result' (-not $r.Completed -and $r.State -eq 'timeout')
$r = Read-AgentStream -Lines (Wire @((Use 'toolu_1'))) -Check $check
Assert 'missing terminal result is incomplete stream' (-not $r.Completed -and $r.State -eq 'crashed')
$badResult = @{type='result'; subtype='error_during_execution'; is_error=$true; errors=@('synthetic crash')}
$r = Read-AgentStream -Lines (Wire @((Use 'toolu_1'), (Reply 'toolu_1' @{matched_total=2}), $badResult)) -Check $check
Assert 'CLI error result overrides mechanical evidence' (-not $r.Completed -and $r.State -eq 'crashed')
$r = Read-AgentStream -Lines @((Wire $events) + '{broken') -Check $check
Assert 'malformed stream fails closed' (-not $r.Completed -and $r.ParseErrors -eq 1)
$r = Read-AgentStream -Lines (Wire @((Reply 'unknown' @{matched_total=2}), $terminal)) -Check $check
Assert 'unmatched tool reply cannot provide evidence' (-not $r.Completed)
$r = Read-AgentStream -Lines (Wire $events) -Check { throw 'synthetic check failure' }
Assert 'check exception preserved and cannot pass' (-not $r.Completed -and $r.CheckError -match 'synthetic check failure')
$r = Read-AgentStream -Lines (Wire $events) -Check { 'agent says yes' }
Assert 'check must return exactly one Boolean' (-not $r.Completed -and $r.CheckError)
try { Assert-AgentRevitOwnership -Processes @([pscustomobject]@{Id=101}, [pscustomobject]@{Id=202}) -AllowRevitPid @(101); $blocked=$false }
catch { $blocked=$_.Exception.Message -match '202' }
Assert 'an undeclared Revit PID refuses the run' $blocked
Assert-AgentRevitOwnership -Processes @([pscustomobject]@{Id=101}) -AllowRevitPid @(101)
Assert 'declared process is permitted without interacting with it' $true
$comparison = @(Get-AgentModeComparison -Rows @(
    [pscustomobject]@{Mode='facade'; Completed=$true; ToolCalls=4; ContractRefusals=0; WallSeconds=2},
    [pscustomobject]@{Mode='facade'; Completed=$false; ToolCalls=8; ContractRefusals=2; WallSeconds=6},
    [pscustomobject]@{Mode='full'; Completed=$true; ToolCalls=5; ContractRefusals=1; WallSeconds=3}))
Assert 'mode comparison keeps failures in denominator and reports median/worst' (
    $comparison[0].Mode -eq 'facade' -and $comparison[0].Completed -eq 1 -and
    $comparison[0].Runs -eq 2 -and $comparison[0].CallsMedian -eq 6 -and
    $comparison[0].CallsWorst -eq 8 -and $comparison[0].ContractRefusals -eq 2 -and
    $comparison[0].SecondsMedian -eq 4 -and $comparison[0].SecondsWorst -eq 6 -and
    $comparison[1].Mode -eq 'full')
if ($failed) { throw "$failed agent battery tests failed." }
Write-Host 'All agent battery tests passed.' -ForegroundColor Green
