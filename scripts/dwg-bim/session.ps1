# -----------------------------------------------------------------------------
# Horizun Revit MCP - an ISOLATED Revit session for DWG -> BIM evaluation runs.
#
#   session.ps1 start [-Year 2026] [-ExpectPrefix HZ_] [-FailAction <action key>] [-DeclineAddIn <name>...]
#   session.ps1 stop  [-Year 2026] [-ExpectPrefix HZ_]
#
# start: closes the Revit THIS SCRIPT STARTED, and no other (a Save dialog is answered
#        No only when it names a document starting with ExpectPrefix; anything else
#        stops the script). Any other Revit of the same year stops the script untouched;
#        a Revit of another year is never looked at. Stages the add-in built from THIS tree with
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
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  /// every visible top-level dialog (#32770) of ONE process, whatever its title or language
  public static List<IntPtr> Dialogs(int pid) {
    var r = new List<IntPtr>();
    EnumWindows((h,l) => { int p; GetWindowThreadProcessId(h, out p);
      if (p==pid && IsWindowVisible(h)) { var c = new StringBuilder(64); GetClassName(h,c,64); if (c.ToString()=="#32770") r.Add(h); }
      return true; }, IntPtr.Zero);
    return r;
  }
}
"@

# THE REVIT THIS SCRIPT STARTED, recorded when it starts: pid, executable and start time (a pid is reused).
$owned = Join-Path $env:USERPROFILE ".horizun\geometry-dev\session-$Year.revit.json"
function Owned-Revit {
    if (-not (Test-Path -LiteralPath $owned)) { return $null }
    $o = Get-Content -LiteralPath $owned -Raw | ConvertFrom-Json
    $p = Get-Process -Id $o.pid -ErrorAction SilentlyContinue
    if (-not $p) { return $null }
    # ticks, not text: ConvertFrom-Json turns an ISO time back into a DateTime (measured: the text
    # comparison never matched, so the script did not recognise the Revit it had just started)
    try { $start = $p.StartTime.ToUniversalTime().Ticks } catch { return $null }
    if ($p.Path -ne $o.exe -or [long]$o.start_ticks -ne $start) { return $null }
    return $p
}

# ONLY THE REVIT THIS SCRIPT STARTED IS EVER CLOSED. MEASURED 2026-09-18: the first version sent a
# close to EVERY Revit.exe - it closed a person's own Revit 2023 session, of another year, holding a
# document of theirs - and its Save guard read the English dialog only, so it never saw that session's
# Spanish "Guardar archivo" and left it to the person. Now a Revit this script did not start is never
# sent anything: of this year it stops the script (its manifest cannot change under it), of another
# year it is not even looked at.
function Close-Revit {
    $p = Owned-Revit
    if ($p) {
        $p.CloseMainWindow() | Out-Null
        for ($i = 0; $i -lt 20 -and -not $p.HasExited; $i++) {
            Start-Sleep -Seconds 3
            foreach ($h in [HzSession]::Dialogs($p.Id)) {
                $el = [System.Windows.Automation.AutomationElement]::FromHandle($h)
                $texts = ($el.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
                          ForEach-Object { $_.Current.Name }) -join ' | '
                if ($texts -notmatch '(?i)save|guardar') { continue }
                "dialog: $texts"
                # ONLY a disposable model is discarded; any other document stops the script untouched.
                if (-not ($texts -match [regex]::Escape($ExpectPrefix))) { throw "Save dialog names an unexpected document, left for a person: $texts" }
                $no = [HzSession]::Child($h, '&No')
                if ($no -eq [IntPtr]::Zero) { $no = [HzSession]::Child($h, 'No') }
                if ($no -eq [IntPtr]::Zero) { throw 'no No button' }
                [HzSession]::SendMessage($no, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
                "answered No"
            }
            $p.Refresh()
        }
        if (-not $p.HasExited) { throw "the isolated Revit $Year (PID $($p.Id)) did not close" }
        # MEASURED (Revit 2024, 2026-09-18): a Revit.exe the closing one launched outlives it by seconds.
        # A CHILD of the owned process is ours and is waited for; anything else stays refused below.
        for ($i = 0; $i -lt 30; $i++) {
            $children = @(Get-CimInstance Win32_Process -Filter "Name='Revit.exe'" | Where-Object { $_.ParentProcessId -eq $p.Id })
            if ($children.Count -eq 0) { break }
            "waiting for $($children.Count) process(es) the isolated Revit launched: $(($children | ForEach-Object { $_.ProcessId }) -join ', ')"
            Start-Sleep -Seconds 2
        }
        Remove-Item -LiteralPath $owned
    }
    $others = @(Get-Process Revit -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*\Revit $Year\*" })
    if ($others.Count -gt 0) {
        $who = ($others | ForEach-Object { "PID $($_.Id) '$($_.MainWindowTitle)'" }) -join '; '
        throw "Revit $Year is running and was NOT started by this script ($who). It is left alone; nothing was changed."
    }
}

function Wait-Bridge([string]$server) {
    $A = [System.Windows.Automation.AutomationElement]
    $deadline = (Get-Date).AddMinutes($WaitMinutes)
    while ((Get-Date) -lt $deadline) {
        $p = Owned-Revit
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
            # Revit 2023 raises Autodesk's own "External Tool Failure" for its Insights add-in at every start;
            # it holds the UI thread. Closed ONLY when it names Insights, and only on the Revit this script started.
            foreach ($h in [HzSession]::Dialogs($p.Id)) {
                $el = $A::FromHandle($h)
                $body = ($el.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
                         ForEach-Object { $_.Current.Name }) -join ' | '
                if ($el.Current.Name -ne 'External Tools - External Tool Failure' -or $body -notmatch '"Insights"') { continue }
                $close = $el.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'Close')))
                if ($close) { $close.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); "$(Get-Date -Format HH:mm:ss) closed Autodesk's Insights failure notice" }
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
    try { $proc = Start-Process -FilePath $exe -PassThru } finally { Remove-Item Env:HORIZUN_TEST_FAIL_ACTION -ErrorAction SilentlyContinue }
} else {
    $proc = Start-Process -FilePath $exe -PassThru
}
@{ pid = $proc.Id; exe = $exe; start_ticks = $proc.StartTime.ToUniversalTime().Ticks } |
    ConvertTo-Json | Set-Content -LiteralPath $owned -Encoding utf8
"server: $server"
Wait-Bridge $server
