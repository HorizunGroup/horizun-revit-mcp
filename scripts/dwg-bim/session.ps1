# -----------------------------------------------------------------------------
# Horizun Revit MCP - an ISOLATED Revit session for DWG -> BIM evaluation runs.
#
#   session.ps1 start [-Year 2026] [-ExpectPrefix HZ_] [-FailAction <action key>] [-DeclineAddIn <name>...]
#   session.ps1 stop  [-Year 2026] [-ExpectPrefix HZ_]
#
# start: closes a Revit that holds only disposable models (a Save dialog is answered
#        No only when it names a document starting with ExpectPrefix; anything else
#        stops the script), stages the add-in built from THIS tree with
#        dev-addin-session.ps1 (the permanent installation is not touched), starts
#        Revit, answers "Do Not Load" ONLY for unsigned add-ins named in -DeclineAddIn
#        (this process only, no trust stored), and waits until horizun_health answers.
#        -FailAction sets HORIZUN_TEST_FAIL_ACTION for the started Revit only.
# stop:  the same guarded close, then restores the original add-in manifest.
#
# Writes the matching server path to $env:TEMP\hz_srv.txt, which scripts/hz-call.ps1
# callers pass as HORIZUN_SERVER_EXE. No machine path is compiled in.
# -----------------------------------------------------------------------------
param(
    [Parameter(Mandatory = $true, Position = 0)][ValidateSet('start', 'stop')][string]$Operation,
    [int]$Year = 2026,
    [string]$ExpectPrefix = 'HZ_',
    [string]$FailAction = '',
    [string[]]$DeclineAddIn = @(),
    [int]$WaitMinutes = 10
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class HzSession {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
  public static IntPtr Child(IntPtr parent, string text) {
    IntPtr found = IntPtr.Zero;
    EnumChildWindows(parent, (h,l) => { var t = new StringBuilder(256); GetWindowText(h,t,256);
      if (t.ToString()==text) { found = h; return false; } return true; }, IntPtr.Zero);
    return found;
  }
  public static List<IntPtr> Dialogs(int pid, string title) {
    var r = new List<IntPtr>();
    EnumWindows((h,l) => { int p; GetWindowThreadProcessId(h, out p);
      if (p==pid && IsWindowVisible(h)) { var t = new StringBuilder(256); GetWindowText(h,t,256); if (t.ToString()==title) r.Add(h); }
      return true; }, IntPtr.Zero);
    return r;
  }
}
"@

function Close-Revit {
    foreach ($p in @(Get-Process Revit -ErrorAction SilentlyContinue)) { if ($p) { $p.CloseMainWindow() | Out-Null } }
    for ($i = 0; $i -lt 20 -and (Get-Process Revit -ErrorAction SilentlyContinue); $i++) {
        Start-Sleep -Seconds 3
        foreach ($p in @(Get-Process Revit -ErrorAction SilentlyContinue)) {
            foreach ($h in [HzSession]::Dialogs($p.Id, 'Save File')) {
                $el = [System.Windows.Automation.AutomationElement]::FromHandle($h)
                $texts = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
                         ForEach-Object { $_.Current.Name } | Where-Object { $_ -like 'Do you want to save*' }
                "dialog: $texts"
                # ONLY a disposable model is discarded; any other document stops the script untouched.
                if (-not ($texts -match [regex]::Escape($ExpectPrefix))) { throw "Save dialog names an unexpected document: $texts" }
                $no = [HzSession]::Child($h, '&No')
                if ($no -eq [IntPtr]::Zero) { throw 'no No button' }
                [HzSession]::SendMessage($no, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
                "answered No"
            }
        }
    }
    if (Get-Process Revit -ErrorAction SilentlyContinue) { throw 'Revit did not close' }
}

function Wait-Bridge([string]$server) {
    $A = [System.Windows.Automation.AutomationElement]
    $deadline = (Get-Date).AddMinutes($WaitMinutes)
    while ((Get-Date) -lt $deadline) {
        $p = Get-Process Revit -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($p) {
            $cond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $p.Id)
            foreach ($w in $A::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
                $dlg = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'Security - Unsigned Add-In')))
                if (-not $dlg) { continue }
                $body = ($dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
                         ForEach-Object { $_.Current.Name }) -join "`n"
                $named = $DeclineAddIn | Where-Object { $body -match ('Name:\s+' + [regex]::Escape($_) + '\s') }
                if (-not $named -or $body -match 'Horizun') { "UNEXPECTED security dialog, left alone:`n$body"; exit 2 }
                $btn = $dlg.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'Do Not Load')))
                [HzSession]::SendMessage([IntPtr]$btn.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
                "$(Get-Date -Format HH:mm:ss) declined $named for this Revit process"
            }
            $env:HORIZUN_SERVER_EXE = $server
            & powershell -ExecutionPolicy Bypass -File (Join-Path $repo 'scripts\hz-call.ps1') -Tool horizun_health -TimeoutSec 30 -Quiet *> $null
            if ($LASTEXITCODE -eq 0) { "$(Get-Date -Format HH:mm:ss) bridge up"; return }
        }
        Start-Sleep -Seconds 5
    }
    throw 'the bridge did not answer in time'
}

Set-Location $repo
Close-Revit
$dev = Join-Path $repo 'scripts\dev-addin-session.ps1'
# A year never staged has no ledger and nothing to restore; any other restore failure stops here,
# before a new manifest is staged over an installation in an unknown state.
$ledger = Join-Path $env:USERPROFILE ".horizun\geometry-dev\session-$Year.json"
if (Test-Path -LiteralPath $ledger) {
    powershell -ExecutionPolicy Bypass -File $dev -Year $Year -Restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "restoring the add-in manifest for Revit $Year failed; nothing else was changed" }
    if ($Operation -eq 'stop') { "restored the add-in manifest for Revit $Year"; exit 0 }
} elseif ($Operation -eq 'stop') { "no isolated session was ever staged for Revit ${Year}: nothing to restore"; exit 0 }

$out = powershell -ExecutionPolicy Bypass -File $dev -Year $Year -Enable
$line = ($out | Select-String 'Matching server').ToString()
$server = ($line -split ':\s+', 2)[1].Trim()
Set-Content -Path "$env:TEMP\hz_srv.txt" -Value $server -NoNewline
$exe = "C:\Program Files\Autodesk\Revit $Year\Revit.exe"
if ($FailAction) {
    # a TEST fault, for the started Revit only: the variable is set around the start and removed after
    $env:HORIZUN_TEST_FAIL_ACTION = $FailAction
    try { Start-Process -FilePath $exe } finally { Remove-Item Env:HORIZUN_TEST_FAIL_ACTION -ErrorAction SilentlyContinue }
} else {
    Start-Process -FilePath $exe
}
"server: $server"
Wait-Bridge $server
