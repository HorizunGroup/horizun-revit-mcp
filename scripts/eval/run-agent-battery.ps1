#Requires -Version 5.1
# Horizun agent evaluation. Dot-source for offline parsing; execution is explicit.
[CmdletBinding()]
param(
    [string]$ServerExe,
    [ValidateSet('facade', 'full')][string[]]$Modes = @('facade', 'full'),
    [ValidateRange(2023, 2027)][int]$RevitYear = 2026,
    [int[]]$AllowRevitPid = @(),
    [string]$TasksPath,
    [string]$OutputDirectory,
    [ValidateRange(1, 86400)][int]$TimeoutSec = 600,
    [ValidateRange(1, 100)][int]$Repetitions = 3,
    [string]$ClaudeExe = 'claude'
)

function Assert-AgentRevitOwnership {
    param([object[]]$Processes, [int[]]$AllowRevitPid)
    $foreign = @($Processes | Where-Object { $_.Id -notin $AllowRevitPid })
    if ($foreign.Count) { throw "Refusing battery: undeclared Revit PID(s): $($foreign.Id -join ', '). Nothing was sent." }
}

function Read-AgentStream {
    [CmdletBinding()]
    param([string[]]$Lines, [Parameter(Mandatory=$true)][scriptblock]$Check,
        [int]$ExitCode = 0, [switch]$TimedOut, [double]$WallSeconds = 0)
    $calls = @{}; $replies = @{}; $terminal = $null; $parseErrors = 0
    foreach ($line in $Lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $e = ConvertFrom-Json -InputObject $line -ErrorAction Stop }
        catch { $parseErrors++; continue }
        if ($e.type -eq 'result') { $terminal = $e }
        if ($e.type -eq 'assistant') {
            foreach ($b in $e.message.content) {
                if ($b.type -eq 'tool_use' -and $b.id) {
                    if (-not $calls.ContainsKey($b.id)) {
                        $calls[$b.id] = [pscustomobject]@{ Id=$b.id; Name=$b.name; Input=$b.input; Sequence=$calls.Count }
                    }
                }
            }
        }
        if ($e.type -ne 'user') { continue }
        $blocks = @($e.message.content | Where-Object { $_.type -eq 'tool_result' })
        foreach ($b in $blocks) {
            $raw = if ($b.content -is [string]) { $b.content } else {
                (@($b.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join "`n")
            }
            $payload = $null
            try { $payload = ConvertFrom-Json -InputObject $raw -ErrorAction Stop } catch { }
            # tool_use_result is per message, not per block. Do not attribute it
            # to several results when a user envelope batches multiple blocks.
            if ($blocks.Count -eq 1 -and $null -ne $e.tool_use_result) { $payload = $e.tool_use_result }
            $isError = ($b.is_error -eq $true -or $payload.isError -eq $true)
            $wirePayload = ConvertTo-Json -InputObject $payload -Depth 100 -Compress
            $refusal = $isError -and ($raw -match 'schema_help|horizun://contract/tools/' -or
                $wirePayload -match '"schema_help"|horizun://contract/tools/')
            if ($null -ne $payload.structuredContent) { $payload = $payload.structuredContent }
            elseif ($null -ne $payload.content) {
                $text = @($payload.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join "`n"
                try { $payload = ConvertFrom-Json -InputObject $text -ErrorAction Stop } catch { }
            }
            $replies[$b.tool_use_id] = [pscustomobject]@{
                Id=$b.tool_use_id; Name=$null; Input=$null; Payload=$payload
                IsError=$isError; ContractRefusal=$refusal; Raw=$raw
            }
        }
    }
    $orderedCalls = @($calls.Values | Sort-Object Sequence)
    $evidence = @($orderedCalls | Where-Object { $replies.ContainsKey($_.Id) } | ForEach-Object { $replies[$_.Id] })
    foreach ($reply in $evidence) { $reply.Name=$calls[$reply.Id].Name; $reply.Input=$calls[$reply.Id].Input }
    $checkError = $null; $passed = $false
    try {
        $answer = @(& $Check $evidence $orderedCalls)
        if ($answer.Count -ne 1 -or $answer[0] -isnot [bool]) { throw 'Check must return exactly one Boolean.' }
        $passed = $answer[0]
    } catch { $checkError = $_.Exception.Message }
    $state = if ($TimedOut) { 'timeout' }
        elseif ($ExitCode -ne 0 -or $null -eq $terminal -or $terminal.is_error -eq $true -or
            $terminal.subtype -ne 'success' -or $parseErrors -gt 0) { 'crashed' }
        elseif ($passed) { 'completed' } else { 'not_completed' }
    [pscustomobject]@{
        Completed=($state -eq 'completed'); State=$state; ToolCalls=$calls.Count
        ContractRefusals=@($evidence | Where-Object ContractRefusal).Count
        WallSeconds=$WallSeconds; Tokens=$terminal.usage; ExitCode=$ExitCode
        ParseErrors=$parseErrors; CheckError=$checkError; ResultErrors=$terminal.errors
        Replies=$evidence; Calls=$orderedCalls
    }
}

function ConvertTo-AgentArgument {
    param([AllowEmptyString()][string]$Value)
    # Windows CommandLineToArgvW quoting, including empty args and trailing slashes.
    '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}

function Get-AgentModeComparison {
    param([object[]]$Rows)
    foreach ($group in @($Rows | Group-Object Mode | Sort-Object Name)) {
        $calls=@($group.Group.ToolCalls | Sort-Object)
        $seconds=@($group.Group.WallSeconds | Sort-Object)
        $middle=[int][math]::Floor($group.Count / 2)
        $callsMedian=$calls[$middle]; $secondsMedian=$seconds[$middle]
        if ($group.Count % 2 -eq 0) {
            $callsMedian=($calls[$middle - 1] + $calls[$middle]) / 2.0
            $secondsMedian=($seconds[$middle - 1] + $seconds[$middle]) / 2.0
        }
        [pscustomobject]@{
            Mode=$group.Name; Runs=$group.Count
            Completed=@($group.Group | Where-Object Completed).Count
            CallsMedian=$callsMedian; CallsWorst=$calls[-1]
            ContractRefusals=($group.Group.ContractRefusals | Measure-Object -Sum).Sum
            SecondsMedian=$secondsMedian; SecondsWorst=$seconds[-1]
        }
    }
}

function Invoke-AgentTask {
    param([string]$Executable, [string]$Prompt, [string]$Config, [string]$Directory,
        [string]$StreamPath, [string]$ErrorPath, [int]$TimeoutSec)
    $args = @('-p', $Prompt, '--mcp-config', $Config, '--strict-mcp-config',
        '--output-format', 'stream-json', '--verbose', '--no-session-persistence',
        '--tools', '', '--allowedTools', 'mcp__horizun-revit__*', '--permission-mode', 'dontAsk',
        '--disable-slash-commands', '--setting-sources', '', '--no-chrome')
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName=$Executable
    $info.Arguments=(@($args | ForEach-Object { ConvertTo-AgentArgument $_ }) -join ' ')
    $info.WorkingDirectory=$Directory; $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    $info.StandardOutputEncoding=New-Object Text.UTF8Encoding($false)
    $info.StandardErrorEncoding=New-Object Text.UTF8Encoding($false)
    $process = New-Object Diagnostics.Process
    $process.StartInfo=$info
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $stdout=[IO.File]::Create($StreamPath); $stderr=[IO.File]::Create($ErrorPath)
    try {
        if (-not $process.Start()) { throw 'Claude did not start.' }
        # Drain both pipes concurrently, to disk rather than unbounded memory.
        $outCopy=$process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $errCopy=$process.StandardError.BaseStream.CopyToAsync($stderr)
        $timedOut=-not $process.WaitForExit($TimeoutSec * 1000)
        if ($timedOut) {
            # Kill only this invocation's tree (never a Revit process). Revit is
            # pre-existing and is not a child of Claude or the stdio server.
            & "$env:SystemRoot\System32\taskkill.exe" /PID $process.Id /T /F | Out-Null
            if (-not $process.WaitForExit(10000)) { throw 'Timed-out Claude could not be stopped.' }
        }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($outCopy, $errCopy), 10000)) {
            throw 'Claude output pipes did not close; capture is incomplete.'
        }
        [pscustomobject]@{ExitCode=$process.ExitCode; TimedOut=$timedOut; WallSeconds=$watch.Elapsed.TotalSeconds}
    } finally { $watch.Stop(); $stdout.Dispose(); $stderr.Dispose(); $process.Dispose() }
}

if ($MyInvocation.InvocationName -eq '.') { return }
$ErrorActionPreference='Stop'
if (-not $ServerExe -or -not (Test-Path -LiteralPath $ServerExe -PathType Leaf)) { throw '-ServerExe must name an existing dev-session server.' }
if (-not $TasksPath -or -not (Test-Path -LiteralPath $TasksPath -PathType Leaf)) { throw '-TasksPath must name a local task factory (Prompt + Check scriptblock per task).' }
Assert-AgentRevitOwnership -Processes @(Get-Process -ErrorAction Stop | Where-Object ProcessName -eq 'Revit') -AllowRevitPid $AllowRevitPid
$exe=(Get-Command $ClaudeExe -CommandType Application -ErrorAction Stop).Source
if ([IO.Path]::GetExtension($exe) -ne '.exe') { throw '-ClaudeExe must be the native Windows CLI executable.' }
$help=(& $exe --help | Out-String)
foreach ($flag in @('--mcp-config','--strict-mcp-config','--output-format','--verbose',
    '--no-session-persistence','--tools','--allowedTools','--permission-mode',
    '--disable-slash-commands','--setting-sources','--no-chrome')) {
    if (-not $help.Contains($flag)) { throw "Installed Claude CLI lacks $flag." }
}
$tasks=@(& ([IO.Path]::GetFullPath($TasksPath)))
if (-not $tasks.Count) { throw 'Task factory returned no tasks.' }
$seen=@{}
foreach ($task in $tasks) {
    if ($task.Id -notmatch '^[a-z0-9][a-z0-9-]*$' -or $seen.ContainsKey($task.Id) -or
        [string]::IsNullOrWhiteSpace($task.Prompt) -or $task.Check -isnot [scriptblock] -or
        $task.Prepare -isnot [scriptblock]) { throw 'Each task needs a unique safe Id, Prompt, Check and Prepare scriptblocks.' }
    $seen[$task.Id]=$true
}
$temp=Join-Path ([IO.Path]::GetTempPath()) ('horizun-agent-eval-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
if (-not $OutputDirectory) { $OutputDirectory=Join-Path $temp 'results' }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath (Join-Path $OutputDirectory 'summary.json')) { throw 'Output directory already contains a battery summary; choose a fresh directory.' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$rows=@()
try {
    foreach ($mode in $Modes) {
        foreach ($task in $tasks) {
            for ($repeat=1; $repeat -le $Repetitions; $repeat++) {
                Assert-AgentRevitOwnership -Processes @(Get-Process -ErrorAction Stop | Where-Object ProcessName -eq 'Revit') -AllowRevitPid $AllowRevitPid
                # The owner supplies/reset fixtures; this script never opens or
                # closes Revit. Prepare must throw if the fixture is not ready.
                & $task.Prepare $mode $repeat | Out-Null
                Assert-AgentRevitOwnership -Processes @(Get-Process -ErrorAction Stop | Where-Object ProcessName -eq 'Revit') -AllowRevitPid $AllowRevitPid
                $config=Join-Path $temp 'mcp.json'
                $settings=@{mcpServers=@{'horizun-revit'=@{command=[IO.Path]::GetFullPath($ServerExe); args=@()
                    env=@{HORIZUN_SCHEMA_MODE=$mode; HORIZUN_REVIT_YEAR="$RevitYear"}}}}
                [IO.File]::WriteAllText($config, ($settings | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
                $stem="$mode-$($task.Id)-$repeat"
                $stream=Join-Path $OutputDirectory "$stem.stream.jsonl"
                $errorFile=Join-Path $OutputDirectory "$stem.stderr.txt"
                Write-Host "Running $stem"
                $runError=$null
                try { $run=Invoke-AgentTask -Executable $exe -Prompt $task.Prompt -Config $config -Directory $temp -StreamPath $stream -ErrorPath $errorFile -TimeoutSec $TimeoutSec }
                catch { $runError=$_.Exception.Message; $run=[pscustomobject]@{ExitCode=-1; TimedOut=$false; WallSeconds=0} }
                $lines=if (Test-Path -LiteralPath $stream) { @(Get-Content -LiteralPath $stream -Encoding utf8) } else { @() }
                $result=Read-AgentStream -Lines $lines -Check $task.Check -ExitCode $run.ExitCode -TimedOut:$run.TimedOut -WallSeconds $run.WallSeconds
                $rows += [pscustomobject]@{Task=$task.Id; Mode=$mode; Repetition=$repeat; Completed=$result.Completed
                    State=$result.State; ToolCalls=$result.ToolCalls; ContractRefusals=$result.ContractRefusals
                    WallSeconds=$result.WallSeconds; Tokens=$result.Tokens; ExitCode=$result.ExitCode
                    ParseErrors=$result.ParseErrors; CheckError=$result.CheckError; RunError=$runError
                    Stream=$stream; Stderr=$errorFile}
                # Persist after every task, including crash/timeout, for recovery.
                $comparison=@(Get-AgentModeComparison -Rows $rows)
                $summary=[ordered]@{Schema='horizun.agent-battery/1'; RevitYear=$RevitYear;
                    ServerExe=[IO.Path]::GetFullPath($ServerExe); TasksPath=[IO.Path]::GetFullPath($TasksPath)
                    Comparison=$comparison; Runs=@($rows)}
                [IO.File]::WriteAllText((Join-Path $OutputDirectory 'summary.json'),
                    (ConvertTo-Json -InputObject $summary -Depth 20), (New-Object Text.UTF8Encoding($false)))
                $markdown=@('# Horizun 3.0.0: agent battery', '',
                    '| Mode | Completed / runs | Calls median / worst | Contract refusals | Seconds median / worst |',
                    '|---|---:|---:|---:|---:|')
                foreach ($group in $comparison) { $markdown += "| $($group.Mode) | $($group.Completed) / $($group.Runs) | $($group.CallsMedian) / $($group.CallsWorst) | $($group.ContractRefusals) | $([math]::Round($group.SecondsMedian, 2)) / $([math]::Round($group.SecondsWorst, 2)) |" }
                $markdown += @('', '| Task | Mode | Run | Completed | Calls | Contract refusals | Seconds | State |',
                    '|---|---|---:|---|---:|---:|---:|---|')
                foreach ($row in $rows) { $markdown += "| $($row.Task) | $($row.Mode) | $($row.Repetition) | $($row.Completed) | $($row.ToolCalls) | $($row.ContractRefusals) | $([math]::Round($row.WallSeconds, 2)) | $($row.State) |" }
                [IO.File]::WriteAllLines((Join-Path $OutputDirectory 'summary.md'), [string[]]$markdown, (New-Object Text.UTF8Encoding($false)))
                if ($result.State -eq 'timeout' -or $result.State -eq 'crashed') {
                    throw "Battery stopped after $stem ($($result.State)); inspect capture and reset the fixture before another run. A timed-out bridge command may still be running."
                }
            }
        }
    }
} finally {
    # Remove only the config we created. Captures and any other temp evidence stay.
    $config=Join-Path $temp 'mcp.json'
    if (Test-Path -LiteralPath $config) { Remove-Item -LiteralPath $config -Force }
}
Write-Host "Results: $OutputDirectory"
