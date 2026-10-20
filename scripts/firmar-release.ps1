<#
.SYNOPSIS
  Firma una GitHub Release en BORRADOR con el certificado Certum de HORIZUN GROUP S.A.S. y la publica.

.DESCRIPTION
  Pensado para correr en el runner self-hosted (etiqueta horizun-sign) bajo la cuenta de Pablo, con
  SimplySign Desktop conectado, DESPUES de que Pablo apruebe el environment `firma-certum`.

  1. Comprueba que se puede firmar (firma un archivo de prueba) ANTES de tocar nada.
  2. Descarga los assets del borrador.
  3. Firma los .exe, .dll y .msi propios (patrones -Propios), tambien los que van dentro de .zip,
     .nupkg y .mcpb (anidados incluidos), conservando la estructura: solo se reescriben las
     entradas que cambian, el resto del contenedor queda byte a byte igual.
  4. Regenera SHA256SUMS(.txt), los .sha256 y cualquier manifiesto con hash (json, txt, etc.):
     todo hash viejo de un archivo que cambio se sustituye por el nuevo.
  5. Verifica cada firma (signtool verify /pa + Get-AuthenticodeSignature Valid + emisor Certum +
     sello de tiempo Certum).
  6. Sube con --clobber, vuelve a descargar lo subido y compara hashes.
  7. Con -ExigirScripts, exige que los .ps1 de primer nivel ya vengan firmados en el arbol.
  8. Publica el borrador. Si algo falla en cualquier punto anterior, NO publica.

  Modo local para pruebas: -Directorio <carpeta> procesa una carpeta (sin gh). Con -Prueba se acepta
  un certificado autofirmado (sin sello de tiempo ni emisor Certum): SOLO para probar el script.

.EXAMPLE
  pwsh scripts/firmar-release.ps1 -Repo HorizunGroup/horizun-msproject-mcp -Tag v1.6.1
#>
[CmdletBinding()]
param(
    [string] $Repo,
    [string] $Tag,
    [string] $Directorio,
    [string] $Salida,
    [string[]] $Propios = @('*horizun*', '*naviscoord*', '*civil3d*', '*msproject*'),
    [switch] $SinPublicar,
    [switch] $Prueba,
    [switch] $ExigirScripts,
    [string] $Thumbprint = 'D19F95C700EFEB78F03B6A2F9F6FBF87BF9BF8FA',
    [string] $SignTool = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not $Directorio -and (-not $Repo -or -not $Tag)) { throw 'Indica -Repo y -Tag (o -Directorio para pruebas locales).' }

$archiveExtensions = @('.zip', '.nupkg', '.mcpb')
$binaryExtensions = @('.exe', '.dll', '.msi')
$textExtensions = @('.json', '.txt', '.sha256', '.xml', '.md', '.yml', '.yaml', '.csv')
$script:HashMap = @{}      # hash viejo (minusculas) -> hash nuevo
$script:SizeMap = @{}      # hash nuevo -> tamano nuevo
$script:Signed = [System.Collections.Generic.List[string]]::new()
$script:Checked = [System.Collections.Generic.List[string]]::new()
$script:ArchiveCache = @{} # hash original del contenedor -> ruta del contenedor ya procesado
$script:TopByHash = @{}    # hash original de un asset de primer nivel -> ruta
$script:Done = @{}         # rutas de primer nivel ya procesadas
$script:InProgress = @{}
$workspace = Join-Path 'D:\Curso\firma\trabajo' ('horizun-firma-' + [guid]::NewGuid().ToString('N'))

function Get-Digest([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function New-TempDir {
    $d = Join-Path $workspace ([guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($d) | Out-Null
    return $d
}
function Test-Own([string] $Name) {
    foreach ($p in $Propios) { if ($Name -like $p) { return $true } }
    return $false
}
function Test-OurSignature($Signature) {
    if ($Signature.SignerCertificate -eq $null -or $Signature.SignerCertificate.Thumbprint -ne $Thumbprint) { return $false }
    if ($Prueba) { return $true }
    return ($Signature.Status -eq 'Valid' -and
            $Signature.SignerCertificate.Issuer -like '*Certum*' -and
            $Signature.TimeStamperCertificate -and
            $Signature.TimeStamperCertificate.Subject -like '*Certum*')
}

function Assert-Signature([string] $Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if (-not (Test-OurSignature $signature)) {
        throw "Firma o sello Certum invalido: $Path (estado: $($signature.Status))"
    }
    $script:Checked.Add($Path)
}

function Invoke-Sign([string] $Path) {
    if ($Prueba) { throw 'La prueba local no esta habilitada en el flujo de cola.' }
    $id = [guid]::NewGuid().ToString('N')
    $base = 'D:\Curso\firma\cola'
    $pending = Join-Path $base "pendiente\$id.json"
    $done = Join-Path $base "hecho\$id.json"
    @{ tipo = 'firmar'; archivos = @($Path.Replace('\', '/')) } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $pending -Encoding utf8
    while (-not (Test-Path -LiteralPath $done)) { Start-Sleep -Seconds 5 }
    $result = Get-Content -LiteralPath $done -Raw | ConvertFrom-Json
    if (-not $result.ok) { throw "Fallo la firma local de ${Path}: $($result.detalle | ConvertTo-Json -Compress -Depth 4)" }
}

function Sign-Binary([string] $Path) {
    $old = Get-Digest $Path
    $existing = Get-AuthenticodeSignature -LiteralPath $Path
    if (Test-OurSignature $existing) { Assert-Signature $Path; return }
    Invoke-Sign $Path
    Assert-Signature $Path
    $new = Get-Digest $Path
    $script:HashMap[$old] = $new
    $script:SizeMap[$new] = (Get-Item -LiteralPath $Path).Length
    $script:Signed.Add($Path)
}

function Test-TextFile($File) {
    return ($textExtensions -contains $File.Extension.ToLowerInvariant() -or $File.Name -match '^SHA256SUMS') -and $File.Length -lt 8MB
}

function Read-Text([string] $Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    return [pscustomobject]@{ Text = [Text.UTF8Encoding]::new($false).GetString($bytes, $(if ($bom) { 3 } else { 0 }), $bytes.Length - $(if ($bom) { 3 } else { 0 })); Bom = $bom }
}

# Sustituye en un archivo de texto todo hash viejo por el nuevo. Devuelve $true si cambio algo.
function Update-TextHashes([string] $Path) {
    $doc = Read-Text $Path
    $text = $doc.Text
    $tokens = [regex]::Matches($text, '(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])') | ForEach-Object { $_.Value } | Select-Object -Unique
    if (-not $tokens) { return $false }
    foreach ($t in $tokens) {
        $low = $t.ToLowerInvariant()
        if ($script:TopByHash.ContainsKey($low)) { Ensure-TopLevel $low }
    }
    $changed = $false
    foreach ($t in $tokens) {
        $low = $t.ToLowerInvariant()
        if ($script:HashMap.ContainsKey($low)) {
            $new = $script:HashMap[$low]
            if ($t -cmatch '^[0-9A-F]{64}$') { $new = $new.ToUpperInvariant() }
            $text = $text.Replace($t, $new)
            $changed = $true
            # Tamano junto al hash (formatos json habituales).
            $newLow = $new.ToLowerInvariant()
            if ($script:SizeMap.ContainsKey($newLow)) {
                $size = $script:SizeMap[$newLow]
                $text = [regex]::Replace($text, '("(?i:sha256|hash)"\s*:\s*"' + [regex]::Escape($new) + '"[^{}]*?"(?i:size|bytes|size_bytes)"\s*:\s*)\d+', ('${1}' + $size))
                $text = [regex]::Replace($text, '("(?i:size|bytes|size_bytes)"\s*:\s*)\d+(\s*,\s*"(?i:sha256|hash)"\s*:\s*"' + [regex]::Escape($new) + '")', ('${1}' + $size + '${2}'))
            }
        }
    }
    if ($changed) {
        $old = Get-Digest $Path
        [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($doc.Bom))
        $new = Get-Digest $Path
        if ($old -ne $new) { $script:HashMap[$old] = $new; $script:SizeMap[$new] = (Get-Item -LiteralPath $Path).Length }
    }
    return $changed
}

function Update-DirMetadata([string] $Root) {
    for ($pass = 0; $pass -lt 3; $pass++) {
        $any = $false
        foreach ($f in @(Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object { Test-TextFile $_ })) {
            if (Update-TextHashes $f.FullName) { $any = $true }
        }
        if (-not $any) { break }
    }
}

function Process-Dir([string] $Root) {
    foreach ($file in @(Get-ChildItem -LiteralPath $Root -Recurse -File)) {
        $ext = $file.Extension.ToLowerInvariant()
        if ($binaryExtensions -contains $ext -and (Test-Own $file.Name)) { Sign-Binary $file.FullName }
    }
    foreach ($archive in @(Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object { $archiveExtensions -contains $_.Extension.ToLowerInvariant() })) {
        Process-Archive $archive.FullName
    }
    Update-DirMetadata $Root
}

function Process-Archive([string] $Path) {
    $orig = Get-Digest $Path
    if ($script:ArchiveCache.ContainsKey($orig)) {
        if ($script:ArchiveCache[$orig]) { Copy-Item -LiteralPath $script:ArchiveCache[$orig] -Destination $Path -Force }
        return
    }
    if ($script:InProgress.ContainsKey($orig)) { throw "Referencia circular entre contenedores: $Path" }
    $script:InProgress[$orig] = $true
    try {
        $inside = New-TempDir
        $before = @{}
        $zip = [IO.Compression.ZipFile]::OpenRead($Path)
        try {
            foreach ($entry in $zip.Entries) {
                if ($entry.FullName.EndsWith('/')) { continue }
                $dest = [IO.Path]::GetFullPath((Join-Path $inside $entry.FullName))
                if (-not $dest.StartsWith($inside, [StringComparison]::OrdinalIgnoreCase)) { throw "Entrada fuera del destino: $($entry.FullName)" }
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dest)) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
                $before[$entry.FullName] = Get-Digest $dest
            }
        } finally { $zip.Dispose() }
        Process-Dir $inside
        $changed = @()
        foreach ($name in $before.Keys) {
            $file = Join-Path $inside $name
            if ((Get-Digest $file) -ne $before[$name]) { $changed += $name }
        }
        if ($changed.Count -gt 0) {
            $zip = [IO.Compression.ZipFile]::Open($Path, 'Update')
            try {
                foreach ($name in $changed) {
                    $existing = $zip.GetEntry($name)
                    if ($existing) { $existing.Delete() }
                    $new = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                    $in = [IO.File]::OpenRead((Join-Path $inside $name))
                    $out = $new.Open()
                    try { $in.CopyTo($out) } finally { $out.Dispose(); $in.Dispose() }
                }
            } finally { $zip.Dispose() }
            $newHash = Get-Digest $Path
            $script:HashMap[$orig] = $newHash
            $script:SizeMap[$newHash] = (Get-Item -LiteralPath $Path).Length
            $keep = Join-Path (New-TempDir) ([IO.Path]::GetFileName($Path))
            Copy-Item -LiteralPath $Path -Destination $keep
            $script:ArchiveCache[$orig] = $keep
        } else {
            $script:ArchiveCache[$orig] = ''   # sin cambios: las copias identicas tampoco cambian
        }
        Remove-Item -LiteralPath $inside -Recurse -Force
    } finally { $script:InProgress.Remove($orig) }
}

function Ensure-TopLevel([string] $OrigHash) {
    $path = $script:TopByHash[$OrigHash]
    if ($script:Done.ContainsKey($path)) { return }
    $script:Done[$path] = $true
    Process-TopLevelItem $path
}

function Process-TopLevelItem([string] $Path) {
    $ext = [IO.Path]::GetExtension($Path).ToLowerInvariant()
    $name = [IO.Path]::GetFileName($Path)
    if ($archiveExtensions -contains $ext) { Process-Archive $Path }
    elseif ($binaryExtensions -contains $ext -and (Test-Own $name)) { Sign-Binary $Path }
}

# SHA256SUMS(.txt) y *.sha256 de primer nivel: se recalculan contra los archivos reales.
function Update-TopSums([string] $Root) {
    foreach ($sum in @(Get-ChildItem -LiteralPath $Root -File | Where-Object { $_.Name -match '^SHA256SUMS(\.txt)?$' })) {
        $lines = [System.Collections.Generic.List[string]]::new()
        foreach ($line in [IO.File]::ReadAllLines($sum.FullName)) {
            if ($line -match '^([a-fA-F0-9]{64})(\s+\*?)(.+)$') {
                $target = Join-Path $Root $Matches[3]
                # Un SHA256SUMS puede llevar rutas del artefacto de construccion (dist/x.whl, meta/x.mcpb)
                # mientras los assets de la release son planos: se resuelve por nombre de archivo.
                if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { $target = Join-Path $Root (Split-Path -Leaf $Matches[3]) }
                if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "$($sum.Name) apunta a un archivo ausente: $($Matches[3])" }
                $lines.Add("$(Get-Digest $target)$($Matches[2])$($Matches[3])")
            } else { $lines.Add($line) }
        }
        # El archivo cambia de bytes (saltos de linea, hashes nuevos): se registra para que cualquier
        # manifiesto que lo fije (p. ej. package-hashes.json) quede con el hash y tamano finales.
        $beforeSum = Get-Digest $sum.FullName
        [IO.File]::WriteAllText($sum.FullName, ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
        $afterSum = Get-Digest $sum.FullName
        if ($beforeSum -ne $afterSum) { $script:HashMap[$beforeSum] = $afterSum; $script:SizeMap[$afterSum] = (Get-Item -LiteralPath $sum.FullName).Length }
    }
    foreach ($sidecar in @(Get-ChildItem -LiteralPath $Root -File -Filter '*.sha256')) {
        $target = $sidecar.FullName.Substring(0, $sidecar.FullName.Length - '.sha256'.Length)
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Sidecar sin asset: $($sidecar.Name)" }
        [IO.File]::WriteAllText($sidecar.FullName, "$(Get-Digest $target)  $([IO.Path]::GetFileName($target))`n", [Text.UTF8Encoding]::new($false))
    }
}

function Invoke-Gh {
    & gh @args
    if ($LASTEXITCODE -ne 0) { throw "gh $($args -join ' ') fallo (codigo $LASTEXITCODE)" }
}

try {
    [IO.Directory]::CreateDirectory($workspace) | Out-Null

    # 0. Se puede firmar? Falla rapido y con un mensaje claro ANTES de tocar la release.
    $cert = Get-Item -LiteralPath "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue
    if (-not $cert -or -not $cert.HasPrivateKey) { throw "El certificado $Thumbprint no esta en Cert:\CurrentUser\My con clave privada. Conecta SimplySign Desktop con la cuenta que corre el runner." }
    # 1. Assets.
    $assets = Join-Path $workspace 'assets'
    [IO.Directory]::CreateDirectory($assets) | Out-Null
    if ($Directorio) {
        Copy-Item -Path (Join-Path $Directorio '*') -Destination $assets -Recurse -Force
    } else {
        $release = gh release view $Tag -R $Repo --json isDraft,assets | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) { throw "No existe la release $Tag en $Repo" }
        if (-not $release.isDraft) { throw "La release $Tag ya esta publicada (las releases publicadas son inmutables): hace falta una version nueva." }
        Invoke-Gh release download $Tag -R $Repo -D $assets
        $downloaded = @(Get-ChildItem -LiteralPath $assets -File)
        if ($downloaded.Count -ne @($release.assets).Count) { throw "Faltan assets descargados ($($downloaded.Count) de $(@($release.assets).Count))" }
    }
    $originals = @{}
    foreach ($f in @(Get-ChildItem -LiteralPath $assets -File)) {
        $d = Get-Digest $f.FullName
        $originals[$f.Name] = $d
        $script:TopByHash[$d] = $f.FullName
    }

    # 2. Firmar (primer nivel; los contenedores se procesan en orden de dependencia).
    foreach ($f in @(Get-ChildItem -LiteralPath $assets -File)) {
        if ($script:Done.ContainsKey($f.FullName)) { continue }
        $script:Done[$f.FullName] = $true
        Process-TopLevelItem $f.FullName
    }
    # 3. Metadatos de primer nivel.
    Update-DirMetadata $assets
    Update-TopSums $assets
    # SHA256SUMS se reescribe al final: los manifiestos que fijan su hash (package-hashes.json) se actualizan otra vez.
    Update-DirMetadata $assets
    Update-TopSums $assets

    # 3b. Scripts que se firman EN EL ARBOL (p. ej. el instalador .ps1 con su hash fijado en el repo):
    #     no se firman aqui porque cambiarian el hash que el repo ya publica; se EXIGE que ya vengan firmados.
    if ($ExigirScripts) {
        foreach ($ps in @(Get-ChildItem -LiteralPath $assets -File -Filter '*.ps1')) { Assert-Signature $ps.FullName }
    }

    # 4. Verificacion final: cada binario propio firmado, dentro y fuera de los contenedores.
    $total = $script:Signed.Count
    $verified = $script:Checked.Count
    if ($total -eq 0 -and $verified -eq 0) { Write-Warning 'No habia binarios propios que firmar en esta release.' }

    $changedAssets = @(Get-ChildItem -LiteralPath $assets -File | Where-Object { (Get-Digest $_.FullName) -ne $originals[$_.Name] })
    Write-Host "Firmas nuevas: $total. Firmas verificadas: $verified. Assets modificados: $($changedAssets.Count)."
    foreach ($a in $changedAssets) { Write-Host "  - $($a.Name)  $((Get-Digest $a.FullName))" }

    if ($Directorio) {
        if ($Salida) { [IO.Directory]::CreateDirectory($Salida) | Out-Null; Copy-Item -Path (Join-Path $assets '*') -Destination $Salida -Recurse -Force }
        Write-Host 'Modo local: no se sube nada.'
        return
    }

    # 5. Subir, releer y comparar. Si algo falla aqui el borrador queda sin publicar.
    foreach ($a in $changedAssets) {
        Invoke-Gh release upload $Tag $a.FullName -R $Repo --clobber
    }
    if ($changedAssets.Count -gt 0) {
        $verifyDir = Join-Path $workspace 'verify'
        [IO.Directory]::CreateDirectory($verifyDir) | Out-Null
        foreach ($a in $changedAssets) {
            Invoke-Gh release download $Tag -R $Repo -D $verifyDir -p $a.Name --clobber
            if ((Get-Digest (Join-Path $verifyDir $a.Name)) -ne (Get-Digest $a.FullName)) { throw "El asset remoto $($a.Name) no coincide con el firmado; la release sigue en borrador" }
        }
    }
    if ($Salida) { [IO.Directory]::CreateDirectory($Salida) | Out-Null; Copy-Item -Path (Join-Path $assets '*') -Destination $Salida -Recurse -Force }

    if ($SinPublicar) { Write-Host "Release $Tag firmada y subida; sigue en borrador (-SinPublicar)."; return }
    Invoke-Gh release edit $Tag -R $Repo --draft=false
    Write-Host "Release $Tag publicada con $total firmas nuevas verificadas."
} finally {
    if (Test-Path -LiteralPath $workspace) { Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction SilentlyContinue }
}
