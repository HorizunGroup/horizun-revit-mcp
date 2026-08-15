<#
    Horizun MCP - original Horizun code.

    WHAT LEAKED, AND WHAT WOULD LEAK NEXT TIME.

    The client, project and model names were taken out of this repository by
    hand, twice, and each time by editing the files somebody remembered. That is
    the method that leaves the fourth file behind. This is the method that does
    not: it reads every tracked file, every time, in CI.

    THE WORDLIST IS NOT IN THIS REPOSITORY, and that is the whole design. A
    scanner that greps for "AcmeCorp" has to contain the string "AcmeCorp" - so
    the thing meant to prove the names are gone becomes the file that publishes
    them, and it is the file nobody thinks to check. So:

      * STRUCTURAL checks run always and need no list at all. They catch the
        shapes that leak regardless of who the client is: a home directory with
        a real user name in it, an e-mail address, a model or family filename
        that is not obviously a placeholder, a cloud project path.

      * The NAME check is opt-in, from a wordlist OUTSIDE the repository -
        %USERPROFILE%\.horizun\sensitive-terms.txt by default, one term per
        line, # for comments. Absent, it is reported as NOT RUN rather than
        silently passing. "No wordlist" and "no matches" are different answers
        and must never print the same way.

    It scans TRACKED FILES ONLY (git ls-files). Untracked working files are the
    operator's own business; what ships is what git carries.

    Exit codes:  0 clean   1 findings   2 could not run
#>
[CmdletBinding()]
param(
    # One term per line. Kept outside the repository on purpose - see above.
    #
    # USERPROFILE IS A WINDOWS VARIABLE and this script runs on the Linux CI runner
    # too, where it is null - so Join-Path threw "Cannot bind argument to parameter
    # 'Path'" and the whole step died before it scanned anything. HOME is the POSIX
    # equivalent; UserProfile resolves to the right one on both. The wordlist is not
    # THERE on the hosted runner either, which is fine and is what -RequireTerms
    # exists to distinguish: absent is reported, not crashed over.
    [string] $TermsFile,

    # Emit machine-readable findings as well as the human summary.
    [string] $Json,

    # Treat a missing wordlist as a failure. For a release gate, where "the name
    # check did not run" must not be indistinguishable from "it passed".
    [switch] $RequireTerms,

    # Scan a generated tree instead of the repository. With -AllFiles, every
    # file below this root is scanned (except .git).
    [string] $Root,
    [switch] $AllFiles
)

$ErrorActionPreference = 'Stop'

# Some sandboxed Windows runners expose neither a profile folder through .NET
# nor USERPROFILE. Resolve the optional default lazily so the mandatory
# structural scan can still run instead of failing during parameter binding.
if ([string]::IsNullOrWhiteSpace($TermsFile)) {
    $profileRoot = [Environment]::GetFolderPath('UserProfile')
    if ([string]::IsNullOrWhiteSpace($profileRoot)) { $profileRoot = $env:USERPROFILE }
    if ([string]::IsNullOrWhiteSpace($profileRoot)) { $profileRoot = $env:HOME }

    if ([string]::IsNullOrWhiteSpace($profileRoot)) {
        $TermsFile = Join-Path ([System.IO.Path]::GetTempPath()) 'horizun-sensitive-terms.txt'
    } else {
        $TermsFile = Join-Path $profileRoot '.horizun/sensitive-terms.txt'
    }
}

$repo = if ([string]::IsNullOrWhiteSpace($Root)) { Split-Path -Parent $PSScriptRoot }
        else { [IO.Path]::GetFullPath($Root) }
Push-Location $repo
try {
    if ($AllFiles) {
        $tracked = @(Get-ChildItem $repo -Recurse -File | Where-Object {
            $_.FullName -notlike "*$([IO.Path]::DirectorySeparatorChar).git$([IO.Path]::DirectorySeparatorChar)*"
        } | ForEach-Object { $_.FullName.Substring($repo.Length + 1).Replace('\','/') })
    } else {
        $tracked = @(git ls-files)
        if ($LASTEXITCODE -ne 0) { Write-Error "not a git repository: $repo"; exit 2 }
    }

    # Binaries and vendored payloads: nothing to read, and the Python standard
    # library shipped in the installer would drown every real finding.
    $skipExt = @('.png','.jpg','.jpeg','.gif','.ico','.exe','.dll','.pdb','.zip','.pyc','.pyd','.rvt','.rfa')
    $files = $tracked | Where-Object {
        $ext = [System.IO.Path]::GetExtension($_).ToLowerInvariant()
        ($skipExt -notcontains $ext) -and (Test-Path $_ -PathType Leaf)
    }

    $findings = New-Object System.Collections.Generic.List[object]

    function Add-Finding($file, $line, $rule, $text) {
        $findings.Add([pscustomobject]@{
            file = $file; line = $line; rule = $rule; text = $text.Trim()
        }) | Out-Null
    }

    # Public maintainer identities are required in CODEOWNERS and in the signing
    # governance policy. A private client-name term can legitimately be part of a
    # maintainer's public GitHub handle, so exempt only a hit that is fully inside
    # a GitHub handle (or its profile URL), and only in those two governance files.
    # The same word anywhere else in either file is still a finding.
    function Test-PublicGovernanceTermHit([string] $file, [string] $line, $hit) {
        $patterns = switch ($file.Replace('\','/')) {
            '.github/CODEOWNERS'      { @('@[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})') }
            'CODE-SIGNING-POLICY.md' { @(
                '@[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})',
                'https://github\.com/[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})'
            ) }
            default { @() }
        }
        foreach ($pattern in $patterns) {
            foreach ($span in [regex]::Matches($line, $pattern)) {
                if ($hit.Index -ge $span.Index -and
                    ($hit.Index + $hit.Length) -le ($span.Index + $span.Length)) {
                    return $true
                }
            }
        }
        return $false
    }

    # --- structural rules: no wordlist needed ------------------------------
    #
    # Each one is a SHAPE that leaks whoever the client turns out to be. The
    # allowances are narrow and named, so a new leak is not waved through by a
    # pattern that was widened to silence an old one.
    $structural = @(
        @{ Rule = 'user-home-path'
           # C:\Users\<name> with a real-looking name. %USERPROFILE% and the
           # placeholder <user> are the correct way to write it.
           Pattern = '(?i)[A-Z]:\\Users\\(?!<)[A-Za-z0-9._-]+'
           Allow   = '(?i)\\Users\\(<[^>]+>|%[^%]+%|\$env:|someone|user|USERNAME)\b' }

        @{ Rule = 'email-address'
           Pattern = '(?i)[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}'
           Allow   = '(?i)(noreply@|example\.(com|org)|@types/|@anthropic-ai)' }

        @{ Rule = 'cloud-project-path'
           # Spaces are IN these paths - "Autodesk Docs://Sample Project/..." - so the
           # match must run to the closing quote, not to the first space. The first
           # version stopped at the space, which truncated every hit to "Autodesk
           # Docs://Sample" and made the allowance below unmatchable: four placeholder
           # paths in the test suite reported as leaks.
           Pattern = '(?i)(Autodesk Docs|BIM 360|ACC)://[^"''\r\n]+'
           # An obvious placeholder is allowed as well as the named sample paths.
           # Caught by the scanner reading its OWN documentation, where the rules
           # table writes the pattern as "Autodesk Docs://…" - a leak report about
           # the description of the leak rule. Ellipsis and <angle brackets> are
           # placeholders in every file here; a real project name is neither.
           Allow   = '(?i)://(Sample Project|Example|Test)[/\\"'']|://(…|\.\.\.|<)' }

        # Generic credential patterns run even without the private name list.
        # They are assembled so the scanner does not match its own source.
        @{ Rule = 'private-key'
           Pattern = ('-----BEGIN ' + '(RSA |EC |OPENSSH )?PRIVATE KEY-----')
           Allow = $null }
        @{ Rule = 'github-token'
           Pattern = ('(?i)\bgh' + '[pousr]_[A-Za-z0-9]{30,}\b')
           Allow = $null }
        @{ Rule = 'aws-access-key'
           Pattern = ('\bAK' + 'IA[0-9A-Z]{16}\b')
           Allow = $null }
        @{ Rule = 'generic-secret-assignment'
           Pattern = ('(?i)\b(api[_-]?key|access[_-]?token|client[_-]?secret|password)\s*[:=]\s*["'']' + '[A-Za-z0-9_\-\./+=]{16,}["'']')
           Allow = '(?i)(example|placeholder|<[^>]+>|\$\{[^}]+\}|%[^%]+%)' }
    )

    foreach ($f in $files) {
        $n = 0
        foreach ($line in (Get-Content -LiteralPath $f -ErrorAction SilentlyContinue)) {
            $n++
            foreach ($rule in $structural) {
                $m = [regex]::Matches($line, $rule.Pattern)
                foreach ($hit in $m) {
                    if ($rule.Allow -and ($hit.Value -match $rule.Allow)) { continue }
                    Add-Finding $f $n $rule.Rule $hit.Value
                }
            }
        }
    }

    # --- the name check: opt-in, from outside the repo ---------------------
    $termsUsed = 0
    $termsRan  = $false
    if (Test-Path $TermsFile) {
        $terms = @(Get-Content -LiteralPath $TermsFile |
                   ForEach-Object { $_.Trim() } |
                   Where-Object { $_ -and -not $_.StartsWith('#') })
        $termsUsed = $terms.Count
        if ($termsUsed -gt 0) {
            $termsRan = $true
            foreach ($f in $files) {
                $n = 0
                foreach ($line in (Get-Content -LiteralPath $f -ErrorAction SilentlyContinue)) {
                    $n++
                    foreach ($t in $terms) {
                        $termHits = @([regex]::Matches(
                            $line,
                            [regex]::Escape($t),
                            [Text.RegularExpressions.RegexOptions]::IgnoreCase
                        ))
                        $unapproved = @($termHits | Where-Object {
                            -not (Test-PublicGovernanceTermHit $f $line $_)
                        })
                        if ($unapproved.Count -gt 0) {
                            # The TERM is not echoed into the output. A CI log is a
                            # published artifact, and a scanner that prints the
                            # secret it found has moved the leak rather than closed
                            # it. File and line are enough to act on.
                            Add-Finding $f $n 'sensitive-term' '<redacted: matched a term from the wordlist>'
                        }
                    }
                }
            }
        }
    }

    # --- report ------------------------------------------------------------
    $result = [pscustomobject]@{
        scanned_root       = $repo
        scanned_files      = $files.Count
        structural_rules   = $structural.Count
        term_check_ran     = $termsRan
        terms_file         = $TermsFile
        term_count         = $termsUsed
        finding_count      = $findings.Count
        findings           = $findings
        generated_utc      = (Get-Date).ToUniversalTime().ToString('o')
    }

    if ($Json) {
        $dir = Split-Path -Parent $Json
        if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
        $result | ConvertTo-Json -Depth 6 | Out-File -FilePath $Json -Encoding utf8
        Write-Host "wrote $Json"
    }

    Write-Host "scanned $($files.Count) tracked files"
    if ($termsRan) {
        Write-Host "name check: RAN against $termsUsed term(s) from $TermsFile"
    } else {
        # Said plainly. A gate that cannot tell "did not run" from "passed" is
        # the failure this whole script is written against.
        Write-Host "name check: NOT RUN - no wordlist at $TermsFile" -ForegroundColor Yellow
        Write-Host "            structural checks still ran; client NAMES were not checked."
    }

    if ($findings.Count -gt 0) {
        Write-Host ""
        Write-Host "$($findings.Count) finding(s):" -ForegroundColor Red
        $findings | ForEach-Object { Write-Host ("  {0}:{1}  [{2}]  {3}" -f $_.file, $_.line, $_.rule, $_.text) }
        exit 1
    }

    if ($RequireTerms -and -not $termsRan) {
        Write-Host ""
        Write-Host "FAILED: -RequireTerms was set and no wordlist was found." -ForegroundColor Red
        exit 1
    }

    Write-Host "clean."
    exit 0
}
finally { Pop-Location }
