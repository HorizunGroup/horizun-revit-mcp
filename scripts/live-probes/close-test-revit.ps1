#Requires -Version 5.1
<# Close disposable documents through the bridge, then request a clean window close.
   Dot-source to test Test-HzTestPath without contacting Revit. #>
[CmdletBinding()]
param([string]$TestDirectory, [Alias('Pid')][int]$RevitPid)

function Assert-HzClosePid([int]$SelectedPid, [object[]]$Instances) {
    if ($SelectedPid -gt 0) { return }
    if ($Instances.Count -gt 1) {
        throw "Multiple Revit instances are running (PID: $(($Instances.Id | Sort-Object) -join ', ')). Supply -Pid for the owned test instance."
    }
    throw 'Supply -Pid (or -RevitPid) for the owned test instance.'
}

function Test-HzTestPath([string]$Root, [string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Root) -or [string]::IsNullOrWhiteSpace($Path)) { return $false }
    try {
        if ($Root -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+[\\/])' -or $Path -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+[\\/])') { return $false }
        $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd([char[]]@('\','/'))
        $filePath = [IO.Path]::GetFullPath($Path)
        if (-not $filePath.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { return $false }
        if (-not (Test-Path -LiteralPath $rootPath -PathType Container) -or -not (Test-Path -LiteralPath $filePath -PathType Leaf)) { return $false }
        # Reject junctions and symlinks in every existing ancestor, including the root.
        $item = Get-Item -LiteralPath $filePath -Force -ErrorAction Stop
        while ($null -ne $item) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { return $false }
            $item = $item.Parent
        }
        return $true
    } catch { return $false }
}

function Get-HzCloseHealth($Result) {
    # hz-call's result is the structuredContent payload, not the MCP envelope.
    $health = if ($null -ne $Result.structuredContent) { $Result.structuredContent } else { $Result }
    if ($health.estado -eq 'puente_inaccesible' -or $health.error_code -eq 'puente_inaccesible') {
        throw 'el puente no es accesible desde este proceso (¿corre en un aislamiento?); ciérralo desde fuera'
    }
    if ($health.status -ne 'healthy') {
        $keys = if ($null -eq $Result) { '<none>' } else { @($Result.PSObject.Properties.Name) -join ', ' }
        $status = if ($null -eq $health.status) { '<missing>' } else { [string]$health.status }
        $estado = if ($null -eq $health.estado) { '<missing>' } else { [string]$health.estado }
        throw "Revit health is not healthy: status=$status, estado=$estado; result keys: $keys"
    }
    if ($null -eq $health.open_documents -or $null -eq $health.open_document_count -or
        @($health.open_documents).Count -ne $health.open_document_count -or $health.note -match 'open-document list is INCOMPLETE') {
        throw 'Revit health did not provide a complete open-document list.'
    }
    return $health
}

function Test-HzAnchorPath([string]$Path, $Health) {
    $root = $Health.data_paths.data_root
    $year = [string]$Health.revit_version
    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($root) -or $year -notmatch '^20(23|24|25|26|27)$') { return $false }
    try {
        $anchor = Join-Path (Join-Path $root 'anchor') ("HZ_ANCHOR_$year.rvt")
        return [IO.Path]::GetFullPath($Path) -ieq [IO.Path]::GetFullPath($anchor)
    } catch { return $false }
}

function Invoke-HzTestClose($HealthResult, [string]$Root, $Process, [int]$SelectedPid) {
    $health = Get-HzCloseHealth $HealthResult
    $documents = @($health.open_documents | Where-Object { -not $_.is_linked -and -not (Test-HzAnchorPath $_.path $health) })
    foreach ($doc in $documents) {
        if (-not (Test-HzTestPath $Root $doc.path)) { throw "Open document is outside the test folder or its path is unsafe: $($doc.path)" }
    }
    foreach ($doc in $documents) {
        $arguments = @{ operation = 'close'; target_document = $doc.path; discard_unsaved = $true; activate_other = $true; dry_run = $true }
        $dry = Call-HzCloseTool 'horizun_document_session' $arguments
        if (-not (Test-HzTestPath $Root $dry.path) -or [IO.Path]::GetFullPath($dry.path) -ine [IO.Path]::GetFullPath($doc.path)) { throw 'Rehearsal selected a different or unsafe document.' }
        $arguments.dry_run = $false
        $arguments.idempotency_key = [guid]::NewGuid().ToString()
        if ($dry.confirmation_token) { $arguments.confirmation_token = $dry.confirmation_token }
        $closed = Call-HzCloseTool 'horizun_document_session' $arguments
        if ($closed.closed -ne $true -or $closed.disk_changed -ne $false) { throw 'Close or unchanged disk state was not verified.' }
    }
    if (-not $Process.CloseMainWindow()) { throw "Could not request a clean window close for PID $SelectedPid." }
    if (-not $Process.WaitForExit(180000)) { throw "Revit process PID $SelectedPid did not exit within 3 minutes." }
}

function Read-HzCloseReply([string]$Tool, [string]$Path, [int]$ExitCode) {
    $reply = $null
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        try { $reply = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
        catch { throw "$Tool failed with exit code $ExitCode; response JSON is invalid: $($_.Exception.Message)" }
    }
    if ($null -eq $reply) { throw "$Tool failed with exit code $ExitCode; no response JSON was produced." }
    $result = $reply.result
    if ($result.estado -eq 'puente_inaccesible' -or $result.error_code -eq 'puente_inaccesible') {
        throw 'el puente no es accesible desde este proceso (¿corre en un aislamiento?); ciérralo desde fuera'
    }
    if ($ExitCode -ne 0 -or $reply.is_error) {
        $message = if ($result.mensaje) { $result.mensaje } elseif ($result.message) { $result.message } elseif ($result.error) { $result.error } else { $reply.raw }
        $suggestion = if ($null -ne $result.suggested_call) { ConvertTo-Json -InputObject $result.suggested_call -Depth 40 -Compress } else { '<none>' }
        throw "$Tool failed with exit code ${ExitCode}; error_code=$($result.error_code); message=$message; suggested_call=$suggestion"
    }
    return $result
}

if ($MyInvocation.InvocationName -eq '.') { return }
$ErrorActionPreference = 'Stop'
if (-not $TestDirectory) { throw 'Supply TestDirectory.' }
if ($RevitPid -le 0) { Assert-HzClosePid $RevitPid @(Get-Process -Name Revit -ErrorAction SilentlyContinue) }
$process = Get-Process -Id $RevitPid -ErrorAction Stop
if ($process.ProcessName -ne 'Revit') { throw 'The selected process is not Revit.' }
$caller = Join-Path (Split-Path -Parent $PSScriptRoot) 'hz-call.ps1'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('hz-close-' + [guid]::NewGuid().ToString('N') + '.json')
$argFile = "$temp.args"
function Call-HzCloseTool([string]$Tool, [hashtable]$Arguments) {
    $Arguments | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $argFile -Encoding utf8
    Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    $null = & (Get-Process -Id $PID).Path -ExecutionPolicy Bypass -NoProfile -File $caller -Tool $Tool -ArgumentsPath $argFile -TargetPid $RevitPid -Json $temp -Quiet
    return Read-HzCloseReply $Tool $temp $LASTEXITCODE
}
try {
    $health = Call-HzCloseTool 'horizun_health' @{ detail = 'full' }
    Invoke-HzTestClose $health $TestDirectory $process $RevitPid
} finally { Remove-Item -LiteralPath $temp, $argFile -Force -ErrorAction SilentlyContinue }
