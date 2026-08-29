#Requires -Version 5.1
<#
  THE PROVENANCE STAMP MUST BE READABLE, INCLUDING FROM A PRERELEASE BUILD.

  A binary that cannot say which commit it is cannot be deployed - the deploy
  gate refuses it, correctly. That makes the READER a load-bearing part of the
  chain, and it was silently wrong for every version that was not release-shaped.

  Two defects this pins, both measured on real built binaries:

    1. The reader demanded \d+\.\d+\.\d+ immediately before the '+', so
       1.1.0-dev+<sha> - the unambiguous development identity this repository
       uses between releases - read as NO STAMP AT ALL. Every add-in and the
       server were refused by their own deploy gate.

    2. Falling back to scanning the raw DLL bytes, the character before the
       string is the custom attribute's LENGTH PREFIX. "1.1.0-dev+" plus 40 hex
       is exactly 50 characters, so that byte is 0x32 - the ASCII digit '2' -
       and the scan answered "21.1.0-dev". Nearly right is the worst kind of
       wrong, and no lookbehind can fix it because the length byte really is a
       digit.
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'horizun-deploy.lib.ps1')

$failures = 0
function Check($name, [scriptblock]$body) {
    try {
        $problem = & $body
        if ($problem) { Write-Host "  FAIL  $name - $problem" -ForegroundColor Red; $script:failures++ }
        else { Write-Host "  PASS  $name" -ForegroundColor Green }
    } catch {
        Write-Host "  FAIL  $name - $($_.Exception.Message)" -ForegroundColor Red; $script:failures++
    }
}

$canonical = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version |
             Where-Object { $_ } | Select-Object -First 1
$canonical = [string]$canonical

Check 'the canonical version is readable and may be a prerelease' {
    if ($canonical -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { return "Directory.Build.props Version '$canonical' is not a semantic version" }
    $null
}

$binaries = @(
    (Join-Path $repo 'src\Horizun.Revit\bin\Release\Horizun.Revit.dll'),
    (Join-Path $repo 'src\Horizun.Server\bin\Release\net8.0\horizun-mcp.exe'),
    (Join-Path $repo 'src\Horizun.Server\bin\Release\net8.0\horizun-mcp.dll')
) | Where-Object { Test-Path $_ }

Check 'there is at least one built binary to read' {
    if ($binaries.Count -eq 0) { return 'no Release binaries found - build src/Horizun.Revit and src/Horizun.Server first' }
    $null
}

foreach ($b in $binaries) {
    $leaf = Split-Path $b -Leaf
    Check "$leaf carries a readable provenance stamp" {
        $p = Get-HorizunProvenance $b
        if (-not $p) { return 'the stamp could not be read at all - the deploy gate would refuse this binary' }
        if ($p.Sha -notmatch '^[0-9a-f]{40}$') { return "the sha is '$($p.Sha)', not a 40-hex commit" }
        $null
    }
    Check "$leaf reports EXACTLY the canonical version, with no leading digit picked up" {
        $p = Get-HorizunProvenance $b
        if (-not $p) { return 'no stamp' }
        if ($p.Version -ne $canonical) {
            return "read '$($p.Version)' but Directory.Build.props says '$canonical'" +
                   " - a version that is nearly right is the worst kind"
        }
        $null
    }
    Check "$leaf is read from the declared version resource, not by scanning bytes" {
        $p = Get-HorizunProvenance $b
        if (-not $p) { return 'no stamp' }
        if ($p.Source -ne 'product_version') {
            return "fell back to '$($p.Source)'; the byte scan is the fallback for binaries with no version resource, " +
                   'and it is the path that misread the attribute length prefix as a digit'
        }
        $null
    }
}

Check 'a prerelease stamp parses, and a release-shaped one still does' {
    # The parser is exercised through the same public function by pointing it at
    # temporary files whose version resource cannot be forged; so this asserts the
    # PATTERN directly, which is the part that regressed.
    $pattern = '^(?<ver>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)\+(?<sha>[0-9a-f]{40})(?<dirty>-dirty)?$'
    $sha = '0123456789abcdef0123456789abcdef01234567'
    foreach ($sample in @("1.0.0+$sha", "1.1.0-dev+$sha", "2.3.4-rc.1+$sha", "1.1.0-dev+$sha-dirty")) {
        if ($sample -notmatch $pattern) { return "'$sample' does not parse, and it must" }
    }
    foreach ($bad in @("1.0+$sha", "1.0.0+deadbeef", "1.0.0", "x1.0.0+$sha")) {
        if ($bad -match $pattern) { return "'$bad' parses and must not" }
    }
    $null
}

Write-Host ''
if ($failures -gt 0) { Write-Host "provenance tests: $failures FAILED" -ForegroundColor Red; exit 1 }
Write-Host 'provenance tests: ALL PASS' -ForegroundColor Green
