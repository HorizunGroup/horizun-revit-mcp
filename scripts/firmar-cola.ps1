#Requires -Version 5.1
<#
.SYNOPSIS
  Signs files with the Certum certificate of HORIZUN GROUP S.A.S. through the local signing queue.

.DESCRIPTION
  The release signing machine does not hold the key in a build: a signing service watches
  <Queue>\pendiente for {"tipo":"firmar","archivos":[...]} requests, signs them with SimplySign
  (which a person keeps connected) and writes <Queue>\hecho\<id>.json. The service only signs
  files under D:\, so every file is copied to a private folder under -WorkRoot, signed there,
  verified and copied back. A file already signed by that certificate with a timestamp is left
  as it is.

  Used twice by a release:
    - on the staged payload (server exe/dll and the add-in of every Revit year), before Setup
      is compiled (pack.ps1 -SignStage);
    - by Inno Setup as its sign tool (pack.ps1 -InstallerOnly -Sign), which signs Setup, the
      uninstaller and the temporary self-copy Setup runs from %TEMP%.

  Nothing is reported as signed unless Authenticode reads it back as Valid, by the expected
  thumbprint, with a Certum issuer and a Certum timestamp.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0, ValueFromRemainingArguments)]
    [string[]]$Path,
    [string]$Queue = 'D:\Curso\firma\cola',
    [string]$WorkRoot = 'D:\Curso\firma\trabajo',
    [string]$Thumbprint = 'D19F95C700EFEB78F03B6A2F9F6FBF87BF9BF8FA',
    [int]$TimeoutMinutes = 60
)
$ErrorActionPreference = 'Stop'
# REHEARSAL ONLY: HORIZUN_FIRMA_PRUEBA_* point the whole chain at a test queue and a
# locally trusted test certificate, so pack -SignStage / -Sign can be exercised end to
# end without the release key. A release run never sets them; the issuer and timestamp
# checks then stay Certum.
$issuerLike = '*Certum*'; $stampLike = '*Certum*'
if ($env:HORIZUN_FIRMA_PRUEBA_COLA) {
    $Queue = $env:HORIZUN_FIRMA_PRUEBA_COLA
    $WorkRoot = Join-Path $Queue 'trabajo'
    $Thumbprint = $env:HORIZUN_FIRMA_PRUEBA_THUMBPRINT
    $issuerLike = '*'; $stampLike = $null
    Write-Warning "[firma] REHEARSAL: test queue $Queue, certificate $Thumbprint (not a release signature)"
}

function Test-HorizunCertum([string]$File) {
    $s = Get-AuthenticodeSignature -LiteralPath $File
    return ($s.Status -eq 'Valid' -and $s.SignerCertificate -and
            $s.SignerCertificate.Thumbprint -eq $Thumbprint -and
            $s.SignerCertificate.Issuer -like $issuerLike -and
            ($null -eq $stampLike -or ($s.TimeStamperCertificate -and $s.TimeStamperCertificate.Subject -like $stampLike)))
}

$files = @($Path | ForEach-Object { (Resolve-Path -LiteralPath $_.Trim('"')).Path })
$todo = @($files | Where-Object { -not (Test-HorizunCertum $_) })
foreach ($f in $files) { if ($todo -notcontains $f) { Write-Host "[firma] already signed: $f" } }
if ($todo.Count -eq 0) { return }

foreach ($dir in 'pendiente', 'hecho') {
    if (-not (Test-Path -LiteralPath (Join-Path $Queue $dir))) { throw "No signing queue at $Queue ($dir missing). Nothing was signed." }
}
$id = 'horizun-revit-' + [guid]::NewGuid().ToString('N')
$work = Join-Path $WorkRoot $id
New-Item -ItemType Directory -Force $work | Out-Null
try {
    # One subfolder per file keeps equal names (horizun-mcp.dll of two layouts) apart.
    $pairs = @()
    for ($i = 0; $i -lt $todo.Count; $i++) {
        $d = Join-Path $work $i
        New-Item -ItemType Directory -Force $d | Out-Null
        $copy = Join-Path $d (Split-Path $todo[$i] -Leaf)
        Copy-Item -LiteralPath $todo[$i] -Destination $copy -Force
        $pairs += [pscustomobject]@{ Original = $todo[$i]; Copy = $copy }
    }
    $request = Join-Path $Queue "pendiente\$id.json"
    $done = Join-Path $Queue "hecho\$id.json"
    $requested = Get-Date
    @{ tipo = 'firmar'; archivos = @($pairs | ForEach-Object { $_.Copy }) } | ConvertTo-Json -Depth 3 |
        Set-Content -LiteralPath $request -Encoding utf8
    Write-Host "[firma] queued $($pairs.Count) file(s) as $id; waiting for the signing service (SimplySign must be connected)"

    $warned = $false
    while (-not (Test-Path -LiteralPath $done)) {
        if (((Get-Date) - $requested).TotalMinutes -gt $TimeoutMinutes) {
            Remove-Item -LiteralPath $request -Force -ErrorAction SilentlyContinue
            throw "The signing service did not answer within $TimeoutMinutes min (is it running, and SimplySign connected?). Nothing was signed."
        }
        $flag = Join-Path $Queue 'SIMPLYSIGN_DESCONECTADO.txt'
        if (-not $warned -and (Test-Path $flag) -and (Get-Item $flag).LastWriteTime -gt $requested) {
            Write-Warning 'The signing service reports SimplySign DISCONNECTED: connect it with the phone code; the request keeps waiting.'
            $warned = $true
        }
        Start-Sleep -Seconds 5
    }
    $result = Get-Content -LiteralPath $done -Raw | ConvertFrom-Json
    if (-not $result.ok) { throw "The signing service refused: $($result.detalle | ConvertTo-Json -Compress -Depth 4). Nothing was copied back." }

    foreach ($p in $pairs) {
        if (-not (Test-HorizunCertum $p.Copy)) {
            $s = Get-AuthenticodeSignature -LiteralPath $p.Copy
            throw "Signed copy of $($p.Original) does not read back as Certum-signed by $Thumbprint (status $($s.Status)). Nothing was copied back."
        }
    }
    foreach ($p in $pairs) {
        Copy-Item -LiteralPath $p.Copy -Destination $p.Original -Force
        if (-not (Test-HorizunCertum $p.Original)) { throw "Copy-back of $($p.Original) did not keep its signature." }
        Write-Host "[firma] signed: $($p.Original)"
    }
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
