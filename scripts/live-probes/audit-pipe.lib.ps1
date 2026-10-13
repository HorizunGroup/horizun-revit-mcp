#Requires -Version 7.0
<#
  Raw access to a running add-in's pipe, for the live probes of the 2026-10-08 audit
  (lote 5): the control messages (__horizun_cancel_job, __horizun_cancel_queued,
  __horizun_request_status) and connection admission can only be exercised below the
  MCP server, because the server is what decides when to send them.

  Everything here talks to ONE Revit, named by its process id, through the discovery
  file that Revit itself published - the same pipe name and token the server uses.
  Nothing is written to the model by this file; the commands it sends are the caller's.
#>

function Get-HzBridge {
    param([Parameter(Mandatory)][int]$RevitPid)
    $file = Join-Path $env:USERPROFILE ".horizun\discovery"
    $hit = Get-ChildItem -LiteralPath $file -Filter "revit-*-$RevitPid.json" -ErrorAction Stop | Select-Object -First 1
    if (-not $hit) { throw "No discovery file for Revit pid $RevitPid." }
    $d = Get-Content -LiteralPath $hit.FullName -Raw | ConvertFrom-Json
    [pscustomobject]@{ Pid = $RevitPid; PipeName = [string]$d.pipe_name; Token = [string]$d.auth_token; Protocol = $d.protocol_version }
}

function Connect-HzPipe {
    param([Parameter(Mandatory)]$Bridge, [int]$ConnectMs = 3000)
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $Bridge.PipeName, [System.IO.Pipes.PipeDirection]::InOut,
                                                         [System.IO.Pipes.PipeOptions]::Asynchronous)
    $pipe.Connect($ConnectMs)
    return $pipe
}

function Send-HzLine {
    param([Parameter(Mandatory)]$Pipe, [Parameter(Mandatory)]$Bridge, [Parameter(Mandatory)][string]$Command,
          [hashtable]$Params = @{}, [string]$WireId)
    if (-not $WireId) { $WireId = 'probe-' + [guid]::NewGuid().ToString('N') }
    $line = @{ id = $WireId; command = $Command; params = $Params; token = $Bridge.Token } | ConvertTo-Json -Depth 30 -Compress
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($line + "`n")
    $Pipe.Write($bytes, 0, $bytes.Length)
    $Pipe.Flush()
    return $WireId
}

function Read-HzLine {
    param([Parameter(Mandatory)]$Pipe, [int]$TimeoutMs = 600000)
    $buffer = New-Object System.Collections.Generic.List[byte]
    $one = New-Object byte[] 65536
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    while ($true) {
        $remaining = [int][Math]::Max(1, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
        $task = $Pipe.ReadAsync($one, 0, $one.Length)
        if (-not $task.Wait($remaining)) { throw "No reply line within $TimeoutMs ms." }
        $n = $task.Result
        if ($n -le 0) { break }
        for ($i = 0; $i -lt $n; $i++) {
            if ($one[$i] -eq 10) {
                return ([Text.Encoding]::UTF8.GetString($buffer.ToArray()) | ConvertFrom-Json)
            }
            $buffer.Add($one[$i])
        }
    }
    if ($buffer.Count -gt 0) { return ([Text.Encoding]::UTF8.GetString($buffer.ToArray()) | ConvertFrom-Json) }
    throw 'The pipe closed without a reply.'
}

<# One request, one reply, one connection. #>
function Invoke-HzRaw {
    param([Parameter(Mandatory)]$Bridge, [Parameter(Mandatory)][string]$Command, [hashtable]$Params = @{},
          [string]$WireId, [int]$TimeoutMs = 600000)
    $pipe = Connect-HzPipe -Bridge $Bridge
    try {
        [void](Send-HzLine -Pipe $pipe -Bridge $Bridge -Command $Command -Params $Params -WireId $WireId)
        return Read-HzLine -Pipe $pipe -TimeoutMs $TimeoutMs
    }
    finally { $pipe.Dispose() }
}
