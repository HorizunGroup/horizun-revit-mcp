#Requires -Version 7.0
<#
  Horizun schema facade: reproducible UTF-8 stdio measurements without Revit.
  Each process gets an EMPTY temporary discovery/settings root. No owner settings,
  client configuration or authentication files are read or copied by this probe.
  Full-mode describe uses the exact-schema resource, because its facade is absent.
#>
[CmdletBinding()]
param(
    [string]$ServerExe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Horizun.Server/bin/Release/net8.0/horizun-mcp.exe'),
    [string]$OutJson = (Join-Path ([IO.Path]::GetTempPath()) 'horizun-schema-facade-probe.json')
)
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
$observations = @()
foreach ($profile in 'safe_write', 'unsafe_code', 'unsafe_code+python') {
    foreach ($mode in 'full', 'facade') {
        $root = Join-Path ([IO.Path]::GetTempPath()) ('hz-schema-probe-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory $root | Out-Null
        $settings = @{ permission_profile = $profile.Split('+')[0]; enable_execute_python = $profile.EndsWith('+python') }
        [IO.File]::WriteAllText((Join-Path $root 'settings.json'), ($settings | ConvertTo-Json -Compress), $utf8)
        $psi = [Diagnostics.ProcessStartInfo]::new((Resolve-Path $ServerExe).Path)
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.StandardOutputEncoding = $utf8
        $psi.StandardInputEncoding = $utf8
        $psi.Environment['HORIZUN_DATA_ROOT'] = $root
        $psi.Environment['HORIZUN_SCHEMA_MODE'] = $mode
        foreach ($key in 'HORIZUN_TOOLSETS', 'HORIZUN_TOOL_PACKS', 'HORIZUN_REVIT_YEAR') { $null = $psi.Environment.Remove($key) }
        $requests = @(
            @{ id=1; method='initialize'; params=@{ protocolVersion='2025-11-25'; capabilities=@{}; clientInfo=@{name='schema-facade-probe';version='1'} } },
            @{ method='notifications/initialized'; params=@{} },
            @{ id=2; method='tools/list'; params=@{} },
            @{ id=3; method='tools/call'; params=@{name='horizun_health';arguments=@{}} }
        )
        if ($mode -eq 'facade') {
            $requests += @{ id=4; method='tools/call'; params=@{name='horizun_describe_tool';arguments=@{name='horizun_target'}} }
            $requests += @{ id=5; method='tools/call'; params=@{name='horizun_call_tool';arguments=@{name='horizun_target';arguments=@{}}} }
            $requests += @{ id=6; method='tools/call'; params=@{name='horizun_list_tools';arguments=@{}} }
        } else {
            $requests += @{ id=4; method='resources/read'; params=@{uri='horizun://contract/tools/horizun_target'} }
            $requests += @{ id=5; method='tools/call'; params=@{name='horizun_target';arguments=@{}} }
        }
        $proc = $null
        try {
            $proc = [Diagnostics.Process]::Start($psi)
            $stdout = $proc.StandardOutput.ReadToEndAsync()
            $stderr = $proc.StandardError.ReadToEndAsync()
            foreach ($request in $requests) {
                $request.jsonrpc = '2.0'
                $proc.StandardInput.WriteLine(($request | ConvertTo-Json -Depth 30 -Compress))
            }
            $proc.StandardInput.Close()
            if (-not $proc.WaitForExit(60000)) { $proc.Kill(); throw 'Isolated schema probe timed out.' }
            if ($proc.ExitCode -ne 0) { throw $stderr.GetAwaiter().GetResult() }
            $replies = @{}
            foreach ($line in ($stdout.GetAwaiter().GetResult() -split "`n")) {
                if (-not $line.Trim()) { continue }
                $doc = [Text.Json.JsonDocument]::Parse($line)
                try {
                    $id = [Text.Json.JsonElement]::new()
                    if ($doc.RootElement.TryGetProperty('id', [ref]$id) -and $id.ValueKind -eq 'Number') {
                        $replies[$id.GetInt32()] = $doc.RootElement.Clone()
                    }
                } finally { $doc.Dispose() }
            }
            foreach ($id in 1..5) {
                if (-not $replies.ContainsKey($id)) { throw "Missing response $id" }
                $errorElement = [Text.Json.JsonElement]::new()
                if ($replies[$id].TryGetProperty('error', [ref]$errorElement)) { throw $errorElement.GetRawText() }
            }
            $list = $replies[2].GetProperty('result')
            $raw = $list.GetRawText()
            $bytes = $utf8.GetByteCount($raw)
            $entry = [ordered]@{
                profile=$profile; mode=$mode; tools=$list.GetProperty('tools').GetArrayLength()
                result_utf8_bytes=$bytes; approx_tokens=[int][Math]::Ceiling($raw.Length / 4.0)
                approximation='ceil(UTF-16 characters/4)'; delta_vs_220_bytes=($bytes - 475647)
                ceiling_bytes=524288; headroom_bytes=(524288 - $bytes)
                tools_list_sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($raw))).ToLowerInvariant()
                health=($replies[3].GetProperty('result').GetRawText() | ConvertFrom-Json -AsHashtable)
                describe=($replies[4].GetProperty('result').GetRawText() | ConvertFrom-Json -AsHashtable)
                call=($replies[5].GetProperty('result').GetRawText() | ConvertFrom-Json -AsHashtable)
            }
            $healthText = ($entry.health.content | Where-Object type -EQ 'text').text -join "`n"
            if (-not $entry.health.isError -or $healthText -notmatch 'no Revit is reachable') {
                throw 'The isolated probe unexpectedly reached a bridge or lost its health refusal.'
            }
            if ($entry.call.isError -or $entry.call.structuredContent.targets_found -ne 0) {
                throw 'The isolated host call did not report zero targets.'
            }
            if ($mode -eq 'facade') {
                $listed = $replies[6].GetProperty('result').GetProperty('structuredContent').GetRawText()
                $entry.list_tools_payload_bytes = $utf8.GetByteCount($listed)
            }
            # Keep only portable evidence in the committed capture. Host replies
            # can include the temporary data root and the owner's profile path.
            $entry.health = 'no Revit is reachable'
            $entry.describe = if ($mode -eq 'facade') { 'exact horizun_target contract via horizun_describe_tool' } else { 'exact horizun_target contract via resources/read' }
            $entry.call = 'horizun_target: targets_found=0; selected_pid=null'
            $observations += $entry
            Write-Host "$profile / $mode : $($entry.tools) tools, $bytes B, ~$($entry.approx_tokens) tokens"
        } finally {
            if ($proc) { if (-not $proc.HasExited) { $proc.Kill() }; $proc.Dispose() }
            # Only the concrete temporary root created above is removed.
            if (-not $root.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected probe root' }
            Remove-Item -LiteralPath $root -Recurse -Force
        }
    }
}
[IO.File]::WriteAllText($OutJson, (ConvertTo-Json -InputObject $observations -Depth 40), $utf8)
Write-Host "Evidence: $OutJson"
