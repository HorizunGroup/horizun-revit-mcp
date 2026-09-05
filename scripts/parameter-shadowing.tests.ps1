#Requires -Version 5.1
<#
  A PARAMETER OVERWRITTEN BY A LOCAL THAT DIFFERS ONLY IN CASE.

  PowerShell variable names are case-insensitive, so `$out` and `$Out` are one
  variable. A script that takes `[string]$Out` as its destination and later does
  `$out = & dotnet test ... | Out-String` has not shadowed its parameter - it has
  DESTROYED it, replacing a path with a log.

  That is not hypothetical. scripts/generate-diagnostics-state.ps1 did exactly
  this, and the way it failed is the reason this gate exists:

    * it died at the LAST line of the script, in Set-Content, with
      "A parameter cannot be found that matches parameter name 'Encoding'" -
      an error that names neither the variable nor the assignment that broke it;
    * both halves of the loop that broke it ran perfectly in isolation;
    * and it only failed on the SLOW path. With -SkipTests the loop never ran,
      the parameter survived, and the script worked. The fast path everybody
      uses was the one that could not reproduce it.

  So the collision is found by reading, not by running. For every script that
  declares parameters, this compares each parameter name against every variable
  ASSIGNED in the body and fails when two differ only by case.

  A self-assignment under another spelling (`$installDir = $InstallDir`) is NOT
  reported: it is one variable assigned to itself, so it changes nothing and
  hides nothing. Only an assignment that puts a DIFFERENT value into the
  parameter can replace its meaning, and that is what fails here.

  Read-only. Exit 0 when no script overwrites its own parameter.
#>
[CmdletBinding()]
param([string]$Root)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
$scriptDir = Join-Path $Root 'scripts'

$failures = New-Object System.Collections.ArrayList
$checked = 0

foreach ($file in Get-ChildItem -LiteralPath $scriptDir -Filter '*.ps1' -Recurse) {
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $file.FullName, [ref]$tokens, [ref]$errors)
    if ($errors -and $errors.Count -gt 0) {
        $null = $failures.Add([pscustomobject]@{
            File = $file.Name; Parameter = '(parse)'; Assigned = $errors[0].Message; Line = 0 })
        continue
    }

    # The script's own parameters - the top-level param() block only. A
    # function's parameters are its own scope and are not at risk here.
    $paramBlock = $ast.ParamBlock
    if ($null -eq $paramBlock) { continue }
    $checked++

    $paramNames = @($paramBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
    if ($paramNames.Count -eq 0) { continue }

    # Every assignment in the script body, EXCLUDING those inside a function
    # (different scope) and excluding the param block itself.
    $assignments = $ast.FindAll({
        param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst]
    }, $true)

    foreach ($a in $assignments) {
        $left = $a.Left
        if ($left -isnot [System.Management.Automation.Language.VariableExpressionAst]) { continue }
        $name = $left.VariablePath.UserPath

        # Inside a function definition? Then it is that function's scope.
        $inFunction = $false
        $node = $a.Parent
        while ($null -ne $node) {
            if ($node -is [System.Management.Automation.Language.FunctionDefinitionAst]) { $inFunction = $true; break }
            $node = $node.Parent
        }
        if ($inFunction) { continue }

        foreach ($p in $paramNames) {
            # Same variable, different spelling. An assignment with the EXACT
            # same casing is an ordinary, deliberate reassignment - scripts do
            # legitimately default their own parameters - so only a case
            # MISMATCH is reported.
            if ($name -ieq $p -and $name -cne $p) {
                # `$installDir = $InstallDir` assigns the variable TO ITSELF -
                # one variable under two spellings - so it changes nothing and
                # can hide nothing. Only an assignment that puts a DIFFERENT
                # value there can replace the parameter's meaning.
                $rhs = $a.Right.Extent.Text.Trim()
                if ($rhs -ieq ('$' + $p)) { continue }
                $null = $failures.Add([pscustomobject]@{
                    File      = $file.Name
                    Parameter = '$' + $p
                    Assigned  = '$' + $name
                    Line      = $a.Extent.StartLineNumber
                })
            }
        }
    }
}

Write-Host ''
Write-Host ("  scripts with a param() block checked: {0}" -f $checked)

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host '  A PARAMETER IS OVERWRITTEN BY A LOCAL THAT DIFFERS ONLY IN CASE:' -ForegroundColor Red
    foreach ($f in $failures) {
        Write-Host ("    {0}:{1}  parameter {2} is overwritten by {3}" -f
            $f.File, $f.Line, $f.Parameter, $f.Assigned) -ForegroundColor Red
    }
    Write-Host ''
    Write-Host '  PowerShell variable names are case-insensitive: these are ONE variable.' -ForegroundColor Yellow
    Write-Host '  Rename the local. The failure this produces surfaces far from its cause.' -ForegroundColor Yellow
    exit 1
}

Write-Host '  PASS: no script overwrites its own parameter with a differently-cased local.' -ForegroundColor Green
exit 0
