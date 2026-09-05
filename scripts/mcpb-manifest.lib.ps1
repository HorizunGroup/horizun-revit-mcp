#Requires -Version 5.1
<#
  The Claude Desktop extension manifest: ONE definition, and its checks.

  The builder writes it, the repair path rewrites it for a machine that did not
  substitute ${HOME}, and the packaging gate asserts it. Three copies of a
  manifest is three chances for the shipped extension to differ from the one the
  tests approved, so all three call in here.

  Conforms to the MCPB manifest spec, manifest_version 0.3
  (github.com/modelcontextprotocol/mcpb/blob/main/MANIFEST.md).
#>

# The registered name, the same one used in every other client's configuration.
# A machine with several bridges shows this string in its server list.
$script:HorizunMcpbName = 'horizun-revit'

# ${HOME} is a substitution variable the manifest spec defines for mcp_config.
# Written with forward slashes: they are legal in Windows paths at the Win32 API
# level, and they survive JSON without doubling every separator - which is the
# single most common way a hand-written client configuration ends up pointing
# nowhere.
$script:HorizunMcpbPortableCommand = '${HOME}/AppData/Local/Programs/Horizun/MCP/server/horizun-mcp.exe'

function New-HorizunMcpbManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$Command,
        $Tools = @()
    )

    $manifest = [ordered]@{
        manifest_version = '0.3'
        name             = $script:HorizunMcpbName
        display_name     = 'Horizun Revit MCP'
        version          = $Version
        description      = 'Drive a running Autodesk Revit from Claude: read the model, audit it, and make verified writes.'
        long_description = @'
Horizun Revit MCP is the bridge between this client and an Autodesk Revit
running on the same machine. It reads the open model, audits it, and performs
typed writes that are re-read from the model after the commit - a command never
reports work it did not verify.

**This extension does not carry the server.** It runs the `horizun-mcp.exe` that
`install.ps1` (or the Windows installer) already put on this machine, because the
server and the Revit add-in share a contract hash and are updated together. A
bundled copy would be frozen at install time and the add-in would refuse to pair
with it.

**Before it can answer anything**, Revit 2023-2027 must be installed, the Horizun
add-in loaded, and a document open. Call `horizun_health` first: it names the
Revit version, the process and the active document, and every other command acts
on that document.

**Arbitrary code is off by default.** `horizun_execute_python` is not exposed
until the owner of the machine grants it from inside Revit or through the
administrative script. Connecting a new client does not grant it.
'@
        author = [ordered]@{
            name  = 'Horizun Group'
            url   = 'https://horizunhub.com'
        }
        repository = [ordered]@{
            type = 'git'
            url  = 'https://github.com/HorizunGroup/horizun-revit-mcp.git'
        }
        homepage      = 'https://horizunhub.com'
        documentation = 'https://github.com/HorizunGroup/horizun-revit-mcp#readme'
        support       = 'https://github.com/HorizunGroup/horizun-revit-mcp/issues'
        icon          = 'icon-256.png'
        icons         = @(
            [ordered]@{ src = 'icon-128.png'; size = '128x128' },
            [ordered]@{ src = 'icon-256.png'; size = '256x256' }
        )
        license  = 'Apache-2.0'
        keywords = @('revit', 'bim', 'autodesk', 'aec', 'construction')
        server   = [ordered]@{
            type        = 'binary'
            entry_point = $Command
            mcp_config  = [ordered]@{
                command = $Command
                args    = @()
                env     = [ordered]@{}
            }
        }
        # The advertised list is what the server answers on a fresh install. It
        # GROWS when the owner grants Python access, and the server sends
        # notifications/tools/list_changed when it does - so this is declared
        # generated rather than fixed.
        tools_generated = $true
        compatibility   = [ordered]@{
            platforms = @('win32')
        }
    }
    if ($Tools -and @($Tools).Count -gt 0) { $manifest['tools'] = @($Tools) }
    return $manifest
}

function Test-HorizunMcpbManifest {
    <#
      Every check returns a sentence, not a boolean, so a failure names the field.
      Returns an empty array when the manifest is good.

      TWO DISTRIBUTIONS, and the difference is the whole point.

        Published  the artifact inside the installer and hashed in the manifest.
                   Its command MUST use ${HOME}: an account name in a published
                   file is a privacy defect.

        Local      the artifact the wizard generates on THIS machine for the user
                   to install. Its command MUST be a literal absolute path that
                   EXISTS, and must NOT use ${HOME} - because whether a host
                   substitutes that variable is a host behaviour this project
                   cannot prove, and the file a user actually installs must not
                   depend on an unproven behaviour.

      So ${HOME} is a convenience in the shipped copy, never a dependency of the
      copy that gets installed.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [string]$ExpectedVersion,
        [ValidateSet('Published', 'Local')][string]$Distribution = 'Published')

    $p = New-Object System.Collections.Generic.List[string]
    function Get-Field($obj, [string]$name) {
        if ($null -eq $obj) { return $null }
        if ($obj -is [System.Collections.IDictionary]) { if ($obj.Contains($name)) { return $obj[$name] } return $null }
        $prop = $obj.PSObject.Properties[$name]
        if ($null -eq $prop) { return $null }
        return $prop.Value
    }

    # --- the five fields the spec calls required --------------------------------
    foreach ($required in @('manifest_version', 'name', 'version', 'description', 'author', 'server')) {
        if (-not (Get-Field $Manifest $required)) { $p.Add("manifest: the required field '$required' is missing or empty") | Out-Null }
    }
    $mv = [string](Get-Field $Manifest 'manifest_version')
    if ($mv -and $mv -ne '0.3') { $p.Add("manifest_version is '$mv'; this tree writes and validates 0.3") | Out-Null }

    $name = [string](Get-Field $Manifest 'name')
    if ($name -and $name -ne $script:HorizunMcpbName) {
        $p.Add("name is '$name'; the integration this repository registers, removes and diagnoses is '$($script:HorizunMcpbName)'") | Out-Null
    }

    $version = [string](Get-Field $Manifest 'version')
    if ($version -and $version -notmatch '^\d+\.\d+\.\d+$') { $p.Add("version '$version' is not semantic") | Out-Null }
    if ($ExpectedVersion -and $version -ne $ExpectedVersion) {
        $p.Add("version is '$version' but this tree is at '$ExpectedVersion'; an extension that lies about its version cannot be diagnosed") | Out-Null
    }

    $author = Get-Field $Manifest 'author'
    if ($author -and -not (Get-Field $author 'name')) { $p.Add('author.name is required by the spec and is missing') | Out-Null }

    # --- the server block -------------------------------------------------------
    $server = Get-Field $Manifest 'server'
    if ($server) {
        $type = [string](Get-Field $server 'type')
        if ($type -notin @('node', 'python', 'binary', 'uv')) { $p.Add("server.type '$type' is not one of node, python, binary, uv") | Out-Null }
        $entry = [string](Get-Field $server 'entry_point')
        if (-not $entry) { $p.Add('server.entry_point is missing') | Out-Null }
        $cfg = Get-Field $server 'mcp_config'
        if (-not $cfg) { $p.Add('server.mcp_config is missing') | Out-Null }
        else {
            $command = [string](Get-Field $cfg 'command')
            if (-not $command) { $p.Add('server.mcp_config.command is missing') | Out-Null }
            if ($command -and $entry -and $command -ne $entry) {
                $p.Add('server.entry_point and mcp_config.command name different executables; this extension runs one server') | Out-Null
            }
            $problems = @(Test-HorizunMcpbCommand -Command $command -Distribution $Distribution)
            foreach ($one in $problems) { $p.Add($one) | Out-Null }
            # An OrderedDictionary answers PSObject.Properties with its .NET
            # members - Count, Keys, IsReadOnly - so asking that way reported
            # seven environment variables in a manifest that has none.
            $env = Get-Field $cfg 'env'
            $envKeys = @()
            if ($env -is [System.Collections.IDictionary]) { $envKeys = @($env.Keys) }
            elseif ($env) { $envKeys = @($env.PSObject.Properties.Name) }
            foreach ($k in $envKeys) {
                if ($k) { $p.Add("server.mcp_config.env carries '$k'; this extension passes no environment, and an env entry is where a secret would end up in a published file") | Out-Null }
            }
        }
    }

    # --- compatibility ----------------------------------------------------------
    $compat = Get-Field $Manifest 'compatibility'
    $platforms = @(Get-Field $compat 'platforms')
    if ($platforms -notcontains 'win32') {
        $p.Add('compatibility.platforms must contain win32: the server is a Windows binary and Revit runs only on Windows') | Out-Null
    }
    foreach ($plat in $platforms) {
        if ($plat -ne 'win32') { $p.Add("compatibility.platforms claims '$plat'; there is no Revit and no server for it") | Out-Null }
    }

    return $p.ToArray()
}

function Test-HorizunMcpbCommand {
    <#
      The command must be absolute, must name the server, and must match its
      distribution: ${HOME} for the published copy, a literal existing path for
      the copy generated on a machine. See Test-HorizunMcpbManifest for why.
    #>
    [CmdletBinding()]
    param([string]$Command, [ValidateSet('Published', 'Local')][string]$Distribution = 'Published')
    $p = New-Object System.Collections.Generic.List[string]
    if (-not $Command) { return $p.ToArray() }

    $expanded = $Command.Replace('${HOME}', $env:USERPROFILE.Replace('\', '/'))
    $isRooted = $expanded -match '^[A-Za-z]:[\\/]' -or $expanded -match '^[\\/][\\/]'
    if (-not $isRooted) {
        $p.Add("server.mcp_config.command '$Command' is not an absolute path; the extension must name the installed server, not a name resolved against an unknown working directory") | Out-Null
    }
    if ($Command -notmatch '(?i)horizun-mcp\.exe$') {
        $p.Add("server.mcp_config.command '$Command' does not end in horizun-mcp.exe") | Out-Null
    }

    $usesHome = $Command -like '*${HOME}*'
    $literalUser = $Command -match '(?i)[\\/]Users[\\/]([^\\/]+)' -and $Matches[1] -notmatch '^\$\{'

    if ($Distribution -eq 'Published') {
        if ($literalUser) {
            $p.Add("server.mcp_config.command carries a literal user directory ('$($Matches[1])'); a published extension must use `${HOME}") | Out-Null
        }
    }
    else {
        # A locally generated extension must not depend on a substitution whose
        # support in the host has not been demonstrated. It names the real file.
        if ($usesHome) {
            $p.Add('server.mcp_config.command uses ${HOME}; a locally generated extension must name the resolved path, because whether the host substitutes that variable is not something this project has proved') | Out-Null
        }
        if (-not (Test-Path -LiteralPath ($Command -replace '/', '\') -PathType Leaf)) {
            $p.Add("server.mcp_config.command '$Command' does not exist on this machine; a locally generated extension names a file that is there, or it installs cleanly and shows no tools") | Out-Null
        }
    }
    return $p.ToArray()
}

function Get-HorizunMcpbManifestFromPackage {
    <# Read manifest.json out of a built .mcpb without extracting it. #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)
    Add-Type -AssemblyName System.IO.Compression -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq 'manifest.json' } | Select-Object -First 1
        if (-not $entry) { throw "the package has no manifest.json at its root: $Path" }
        $reader = New-Object IO.StreamReader($entry.Open(), [Text.UTF8Encoding]::new($false))
        try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
        return [pscustomobject]@{
            Manifest = ($text | ConvertFrom-Json)
            Text     = $text
            Entries  = @($zip.Entries | ForEach-Object { $_.FullName })
        }
    }
    finally { $zip.Dispose() }
}

function Convert-HorizunMcpbToLocal {
    <#
      Turn the portable package shipped by the installer into the package this
      machine actually installs. Only manifest.json changes: icons, README,
      license and the server-generated tool list remain byte-for-byte identical.

      This deliberately needs only the published .mcpb and the installed server.
      The post-install wizard runs from client-tools, where the source tree,
      Directory.Build.props and artwork do not exist.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [Parameter(Mandatory = $true)][string]$ServerPath
    )

    $source = [IO.Path]::GetFullPath($PackagePath)
    $output = [IO.Path]::GetFullPath($OutputPath)
    $command = [IO.Path]::GetFullPath($ServerPath)
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "the published extension does not exist: $source" }
    if (-not (Test-Path -LiteralPath $command -PathType Leaf)) { throw "the installed server does not exist: $command" }

    $read = Get-HorizunMcpbManifestFromPackage -Path $source
    $publishedProblems = @(Test-HorizunMcpbManifest -Manifest $read.Manifest -Distribution Published)
    if ($publishedProblems.Count -gt 0) {
        throw ('the source extension is not a valid published package: ' + ($publishedProblems -join '; '))
    }

    $read.Manifest.server.entry_point = $command
    $read.Manifest.server.mcp_config.command = $command
    $localProblems = @(Test-HorizunMcpbManifest -Manifest $read.Manifest -Distribution Local)
    if ($localProblems.Count -gt 0) {
        throw ('the rewritten extension is not valid for this machine: ' + ($localProblems -join '; '))
    }
    $manifestBytes = [Text.UTF8Encoding]::new($false).GetBytes(($read.Manifest | ConvertTo-Json -Depth 20))

    Add-Type -AssemblyName System.IO.Compression -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $entries = New-Object System.Collections.Generic.List[object]
    $inputZip = [IO.Compression.ZipFile]::OpenRead($source)
    try {
        $manifestCount = @($inputZip.Entries | Where-Object { $_.FullName -eq 'manifest.json' }).Count
        if ($manifestCount -ne 1) { throw "the package must contain exactly one root manifest.json; found $manifestCount" }
        foreach ($entry in $inputZip.Entries) {
            if ($entry.FullName -eq 'manifest.json') { continue }
            $stream = $entry.Open()
            try {
                $memory = New-Object IO.MemoryStream
                try {
                    $stream.CopyTo($memory)
                    $bytes = $memory.ToArray()
                }
                finally { $memory.Dispose() }
            }
            finally { $stream.Dispose() }
            $entries.Add([pscustomobject]@{ Name = $entry.FullName; Bytes = $bytes }) | Out-Null
        }
    }
    finally { $inputZip.Dispose() }

    $outDir = Split-Path -Parent $output
    if ($outDir -and -not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
    $writePath = $output
    if ($source -eq $output) { $writePath = "$output.horizun-new" }
    Remove-Item -LiteralPath $writePath -Force -ErrorAction SilentlyContinue

    $fixed = [DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $outputZip = [IO.Compression.ZipFile]::Open($writePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $all = New-Object System.Collections.Generic.List[object]
        $all.Add([pscustomobject]@{ Name = 'manifest.json'; Bytes = $manifestBytes }) | Out-Null
        foreach ($preserved in $entries) { $all.Add($preserved) | Out-Null }
        foreach ($item in $all) {
            $entry = $outputZip.CreateEntry($item.Name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $fixed
            $stream = $entry.Open()
            try { $stream.Write($item.Bytes, 0, $item.Bytes.Length) }
            finally { $stream.Dispose() }
        }
    }
    finally { $outputZip.Dispose() }

    if ($writePath -ne $output) { Move-Item -LiteralPath $writePath -Destination $output -Force }
    $verified = Get-HorizunMcpbManifestFromPackage -Path $output
    $verifyProblems = @(Test-HorizunMcpbManifest -Manifest $verified.Manifest -Distribution Local)
    if ($verifyProblems.Count -gt 0) {
        Remove-Item -LiteralPath $output -Force -ErrorAction SilentlyContinue
        throw ('the package failed its read-back validation: ' + ($verifyProblems -join '; '))
    }
    return $verified
}
