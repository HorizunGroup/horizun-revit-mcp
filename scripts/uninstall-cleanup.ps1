#Requires -Version 5.1
<#
  Optional cleanup after removing Horizun binaries. Nothing is selected by
  default: configuration, job history and certificate trust are user data and
  security choices, not disposable installer files.

  Examples:
    .\uninstall-cleanup.ps1 -RemoveClients
    .\uninstall-cleanup.ps1 -PurgeState
    .\uninstall-cleanup.ps1 -RemoveSigningTrust
    .\uninstall-cleanup.ps1 -RemoveClients -PurgeState -RemoveSigningTrust
#>
[CmdletBinding(SupportsShouldProcess=$true, ConfirmImpact='High')]
param(
    [switch]$RemoveClients,
    [switch]$PurgeState,
    [switch]$RemoveSigningTrust,
    [switch]$RemoveSigningCertificate,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'

if (-not ($RemoveClients -or $PurgeState -or $RemoveSigningTrust -or $RemoveSigningCertificate)) {
    Write-Host 'Nothing selected. Choose -RemoveClients, -PurgeState, -RemoveSigningTrust or -RemoveSigningCertificate.' -ForegroundColor Yellow
    exit 0
}
$running = @(Get-Process | Where-Object { $_.ProcessName -match '^(?i:Revit|Codex|Claude|horizun-mcp)$' })
if ($running.Count -gt 0 -and -not $Force) {
    throw ('Close Revit, Codex and Claude first. Running: ' + (($running | ForEach-Object { "$($_.ProcessName) pid $($_.Id)" }) -join ', '))
}

if ($RemoveClients) {
    $register = Join-Path $PSScriptRoot 'register-client.ps1'
    if (-not (Test-Path $register)) { throw "Missing client helper: $register" }
    if ($PSCmdlet.ShouldProcess('Claude and Codex configuration', "remove only mcp server 'horizun-revit'")) {
        $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$register,'-Remove','-Client','Both','-SkipMissingClients')
        if ($Force) { $arguments += '-Force' }
        & powershell @arguments
        if ($LASTEXITCODE -ne 0) { throw "Client cleanup failed with exit code $LASTEXITCODE." }
    }
}

if ($PurgeState) {
    $state = if ($env:HORIZUN_DATA_ROOT) { [IO.Path]::GetFullPath($env:HORIZUN_DATA_ROOT) }
             else { [IO.Path]::GetFullPath((Join-Path $env:USERPROFILE '.horizun')) }
    $profile = [IO.Path]::GetFullPath($env:USERPROFILE).TrimEnd('\')
    $defaultState = [IO.Path]::GetFullPath((Join-Path $env:USERPROFILE '.horizun')).TrimEnd('\')
    $canonicalState = if (Test-Path -LiteralPath $state) { (Resolve-Path -LiteralPath $state).Path.TrimEnd('\') } else { $state.TrimEnd('\') }
    if ($canonicalState -eq $profile -or $canonicalState.Length -le 3) {
        throw "Refusing an unsafe state path: $state"
    }
    if ($canonicalState -ne $defaultState) {
        $marker = Join-Path $canonicalState '.horizun-data-root'
        $markerText = if (Test-Path -LiteralPath $marker -PathType Leaf) { (Get-Content -LiteralPath $marker -Raw).Trim() } else { '' }
        if ($markerText -ne 'Horizun data root v1') {
            throw ("Refusing to purge custom HORIZUN_DATA_ROOT '$canonicalState': it does not contain the " +
                   "ownership marker .horizun-data-root with content 'Horizun data root v1'. Unset the variable " +
                   'to purge the default profile state, or inspect and mark the custom root deliberately.')
        }
    }
    # Windows PowerShell 5.1 has historically differed in how recursive deletion
    # treats junctions. Never let a state tree redirect cleanup into another
    # directory: walk one level at a time and refuse every reparse point instead
    # of recursing through it.
    if (Test-Path -LiteralPath $canonicalState -PathType Container) {
        $stateItem = Get-Item -LiteralPath $canonicalState -Force
        if (($stateItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to purge a state root that is a link or junction: $canonicalState"
        }
        $pending = New-Object 'Collections.Generic.Queue[string]'
        $pending.Enqueue($canonicalState)
        $reparse = @()
        while ($pending.Count -gt 0) {
            $current = $pending.Dequeue()
            foreach ($entry in @(Get-ChildItem -LiteralPath $current -Force -ErrorAction Stop)) {
                if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    $reparse += $entry.FullName
                }
                elseif ($entry.PSIsContainer) { $pending.Enqueue($entry.FullName) }
            }
        }
        if ($reparse.Count -gt 0) {
            throw "Refusing to purge a state tree containing links or junctions: $($reparse -join '; ')"
        }
    }
    if ((Test-Path -LiteralPath $state) -and $PSCmdlet.ShouldProcess($state, 'permanently remove settings, logs, jobs and ledgers')) {
        Remove-Item -LiteralPath $canonicalState -Recurse -Force
        if (Test-Path -LiteralPath $canonicalState) { throw "State directory still exists after deletion: $canonicalState" }
    }
}

$subject = 'CN=Horizun Group (self-signed add-in signing)'
if ($RemoveSigningTrust) {
    foreach ($store in 'TrustedPublisher','Root') {
        foreach ($cert in @(Get-ChildItem "Cert:\CurrentUser\$store" -ErrorAction SilentlyContinue | Where-Object Subject -eq $subject)) {
            if ($PSCmdlet.ShouldProcess("Cert:\CurrentUser\$store\$($cert.Thumbprint)", 'remove Horizun signing trust')) {
                Remove-Item -LiteralPath "Cert:\CurrentUser\$store\$($cert.Thumbprint)" -Force
            }
        }
    }
}
if ($RemoveSigningCertificate) {
    foreach ($cert in @(Get-ChildItem 'Cert:\CurrentUser\My' -ErrorAction SilentlyContinue | Where-Object Subject -eq $subject)) {
        if ($PSCmdlet.ShouldProcess("Cert:\CurrentUser\My\$($cert.Thumbprint)", 'remove Horizun private signing certificate')) {
            Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force
        }
    }
}

Write-Host 'Selected cleanup completed.' -ForegroundColor Green
