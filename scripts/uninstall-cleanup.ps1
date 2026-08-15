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
    if ($state.TrimEnd('\') -eq $profile -or $state.Length -le 3) {
        throw "Refusing an unsafe state path: $state"
    }
    if ((Test-Path -LiteralPath $state) -and $PSCmdlet.ShouldProcess($state, 'permanently remove settings, logs, jobs and ledgers')) {
        Remove-Item -LiteralPath $state -Recurse -Force
        if (Test-Path -LiteralPath $state) { throw "State directory still exists after deletion: $state" }
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
