; ----------------------------------------------------------------------------
; Horizun MCP — installer.
;
; Installs the MCP server once, and the Revit add-in once PER INSTALLED REVIT
; YEAR — with the artifact built against THAT YEAR'S OWN RevitAPI. That is the
; whole reason this file is not three lines.
;
; The runtimes differ (2024 and earlier host .NET Framework 4.8, 2025-2026 host
; .NET 8, 2027 hosts .NET 10) and a runtime mismatch never loads at all. But the
; APIs differ too, between every one of these years, and that failure is worse:
; a plugin compiled against 2026 and loaded by 2025 loads fine and then throws at
; the first call to a method that version does not have - which reads as a broken
; command rather than a broken install. The payload therefore carries one folder
; per year, not one per runtime.
;
; A year that is not installed is skipped, not guessed at. A year whose deploy
; FAILS is reported as failed and rolled back to whatever was there before.
;
; Nothing outside Horizun's own folders is touched: no other add-in is
; modified. Client registration is completed only while that client is closed;
; otherwise a per-user helper records the pending work and waits safely.
;
; ---------------------------------------------------------------------------
; RELEASE SIGNING AND REVIT TRUST:
;
;   Signing the DLL is NOT enough. A signed add-in from a publisher the machine
;   does not know still raises a dialog — it changes from the red "Unsigned
;   Add-In" to "Signed Add-In" with an Always Load button, shown once per
;   certificate per machine instead of per binary. To get NO dialog at all on a
;   clean machine, the release must carry a valid public Authenticode chain.
;   Stable CI signs the five add-ins, server exe/dll and this wrapper, and then
;   rechecks them on a clean hosted Windows runner. This installer never imports
;   a private root certificate or weakens the machine's trust policy.
; ---------------------------------------------------------------------------
; ----------------------------------------------------------------------------

#define AppName        "Horizun Revit MCP"
#define AppPublisher   "Horizun Group"
#define AppHubUrl      "https://horizunhub.com"
; The version is passed in by pack.ps1, read from the csproj - so there is ONE
; place to bump it. The default below only exists so this file still compiles if
; somebody runs ISCC by hand.
#ifndef AppVersion
  #define AppVersion "0.0.0-unpacked"
#endif
#define AppExeName     "horizun-mcp.exe"

[Setup]
AppId={{8F3B6A21-9E44-4E77-A0C5-6C1D2E9A7B10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
; Shown in Apps & Features and on the wizard, so the bridge is identifiably part
; of the Hub wherever Windows surfaces it later - which is the only place most
; people will ever see this product's name again.
AppPublisherURL={#AppHubUrl}
AppSupportURL={#AppHubUrl}
AppUpdatesURL={#AppHubUrl}
DefaultDirName={autopf}\Horizun\MCP
DefaultGroupName=Horizun
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=horizun-mcp-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Per-user install: the add-in folders live under %APPDATA% and need no elevation.
PrivilegesRequired=lowest
UninstallDisplayName={#AppName} {#AppVersion}

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Files]
; Extract into Setup's private temporary payload. The post-install code swaps this
; exact directory into place transactionally. Copying directly over {app}\server
; retained files removed by a newer release and mixed two versions.
Source: "..\dist\stage\server\*"; DestDir: "{tmp}\HorizunPayload\server"; Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall

; --- The plugin, both runtimes, staged for the per-year copy below. ---
; One payload per YEAR, each compiled against that year's own RevitAPI. Sharing a
; binary between years that share a target framework was the old scheme, and it
; shipped the 2024 build to Revit 2023 and the 2026 build to Revit 2025.
Source: "..\dist\stage\plugin\2023\*"; DestDir: "{tmp}\HorizunPayload\plugin\2023"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist deleteafterinstall
Source: "..\dist\stage\plugin\2024\*"; DestDir: "{tmp}\HorizunPayload\plugin\2024"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist deleteafterinstall
Source: "..\dist\stage\plugin\2025\*"; DestDir: "{tmp}\HorizunPayload\plugin\2025"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist deleteafterinstall
Source: "..\dist\stage\plugin\2026\*"; DestDir: "{tmp}\HorizunPayload\plugin\2026"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist deleteafterinstall
Source: "..\dist\stage\plugin\2027\*"; DestDir: "{tmp}\HorizunPayload\plugin\2027"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist deleteafterinstall
Source: "..\dist\stage\manifest.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\stage\Horizun.addin";  DestDir: "{app}";              Flags: ignoreversion

[Icons]
Name: "{group}\Horizun Revit MCP (carpeta)"; Filename: "{app}"
Name: "{group}\Horizun Hub"; Filename: "{#AppHubUrl}"
Name: "{group}\Configurar Horizun en Codex y Claude"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\server\client-tools\register-client.ps1"" -Client Both -SkipMissingClients"; \
  WorkingDir: "{app}\server\client-tools"
Name: "{group}\Completar y verificar instalación de Horizun"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\server\client-tools\complete-install.ps1"" -Client Auto"; \
  WorkingDir: "{app}\server\client-tools"
Name: "{group}\Estado de instalación de Horizun"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\server\client-tools\complete-install.ps1"" -StatusOnly"; \
  WorkingDir: "{app}\server\client-tools"
Name: "{group}\Verificar clientes MCP de Horizun"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\server\client-tools\verify-clients.ps1"""; \
  WorkingDir: "{app}\server\client-tools"
Name: "{group}\Limpieza avanzada antes de desinstalar"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\server\client-tools\uninstall-cleanup.ps1"""; \
  WorkingDir: "{app}\server\client-tools"

[Tasks]
; OPT-IN, and unchecked by default. An installer that opens a browser nobody
; asked for is the kind of thing people warn each other about, and this one is
; going to be installed by people who were told it is safe.
Name: "openhub"; Description: "Ver Horizun Hub - las herramientas y flujos construidos sobre este puente"; \
  Flags: unchecked

[Run]
Filename: "{#AppHubUrl}"; Description: "Abrir Horizun Hub"; \
  Flags: shellexec nowait postinstall skipifsilent; Tasks: openhub
; Complete the client side automatically. If Codex or Claude is open, the helper
; records the pending work and waits outside Setup; it never writes under a live
; client. It also verifies the installed manifest and finishes horizun_health
; after the first Revit start. The process is hidden because every durable result
; is written to %LOCALAPPDATA%\Horizun\install-status.json.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File ""{app}\server\client-tools\complete-install.ps1"" -Client {param:HORIZUNCLIENT|Auto} {param:HORIZUNNOLIVE|}"; \
  WorkingDir: "{app}\server\client-tools"; Flags: runhidden nowait; Check: ShouldCompleteInstall

[UninstallRun]
; A pending first-start verification must not survive removal with a command that
; points at files the uninstaller is about to delete.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\server\client-tools\complete-install.ps1"" -CancelPending"; \
  Flags: runhidden waituntilterminated skipifdoesntexist

[Code]
const
  { Supported Revit years. There is no runtime column any more: the payload folder
    IS the year, because each year is compiled against its own RevitAPI. Sharing a
    binary between years with the same target framework was the old scheme, and it
    shipped the 2024 build to Revit 2023 and the 2026 build to Revit 2025 - which
    fails at the first API call, not at load. 2022 is gone: it is not supported. }
  YearsCount = 5;
var
  Years: array[0..4] of String;
  InstalledYears: String;
  FailedYears: String;
  FoundAny: Boolean;
  ServerInstalled: Boolean;
  ServerFailure: String;
  UninstallFailures: String;

function ShouldCompleteInstall(): Boolean;
begin
  Result := CompareText(ExpandConstant('{param:HORIZUNCLIENT|Auto}'), 'None') <> 0;
end;

procedure InitYears;
begin
  Years[0] := '2023';
  Years[1] := '2024';
  Years[2] := '2025';
  Years[3] := '2026';
  Years[4] := '2027';
end;

{ Revit holds a lock on the plugin it has loaded. Copying over it fails per-file and
  xcopy says nothing useful, so the install would "succeed" and leave the old build
  in place - the user then reports a bug that was fixed in a version they are not
  running. Refuse up front instead. }
function RevitIsRunning: Boolean;
var
  Code: Integer;
begin
  Result := False;
  if Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq Revit.exe" | find /I "Revit.exe"',
          '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Result := (Code = 0);
end;

function BridgeIsRunning: Boolean;
var
  Code: Integer;
begin
  Result := False;
  if Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq {#AppExeName}" | find /I "{#AppExeName}"',
          '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Result := (Code = 0);
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if RevitIsRunning then
  begin
    MsgBox('Revit is running.' + #13#10#13#10 +
           'It holds the add-in files open, so they cannot be replaced and you would end up ' +
           'still running the old build without being told.' + #13#10#13#10 +
           'Close every Revit window and run this installer again. Nothing has been changed.',
           mbError, MB_OK);
    Result := False;
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  if RevitIsRunning or BridgeIsRunning then
  begin
    SuppressibleMsgBox('Close every Revit window and every Codex/Claude client using Horizun before uninstalling.' + #13#10#13#10 +
      'Those processes hold the installed files open. Nothing was removed.', mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

function RevitInstalled(Year: String): Boolean;
var
  ProgramFiles64: String;
begin
  (* Presence of the program folder is the check. Registry layouts move between
     Revit versions; a folder that contains Revit.exe does not.

     ProgramW6432 rather than the commonpf constant: this installer runs as a
     32-bit process, where that constant resolves to "Program Files (x86)" - and
     Revit is 64-bit, so the check silently found nothing and the installer
     reported success having deployed to no Revit at all. The environment
     variable names the real folder whatever bitness we run as. *)
  ProgramFiles64 := GetEnv('ProgramW6432');
  if ProgramFiles64 = '' then ProgramFiles64 := ExpandConstant('{commonpf}');
  Result := FileExists(ProgramFiles64 + '\Autodesk\Revit ' + Year + '\Revit.exe');
end;

function AddinsDir(Year: String): String;
begin
  Result := ExpandConstant('{userappdata}') + '\Autodesk\Revit\Addins\' + Year;
end;

function RecoverInterruptedSwap(Dst, Staging, Backup, RequiredFile: String): Boolean;
begin
  Result := False;

  { A previous process may have died after moving live -> previous and before
    moving installing -> live. Restore the only usable copy BEFORE cleanup. }
  if (not DirExists(Dst)) and DirExists(Backup) then
    if not RenameFile(Backup, Dst) then exit;

  if DirExists(Staging) then
    if not DelTree(Staging, True, True, True) then exit;

  if DirExists(Backup) then
  begin
    { A live directory must prove it contains its primary binary before an older
      recoverable copy is discarded. }
    if not FileExists(Dst + '\' + RequiredFile) then exit;
    if not DelTree(Backup, True, True, True) then exit;
  end;
  Result := True;
end;

function DeployServer: Boolean;
var
  Src, Dst, Staging, Backup: String;
  ResultCode: Integer;
begin
  Result := False;
  ServerFailure := '';
  Src := ExpandConstant('{tmp}') + '\HorizunPayload\server';
  Dst := ExpandConstant('{app}') + '\server';
  Staging := ExpandConstant('{app}') + '\server.installing';
  Backup := ExpandConstant('{app}') + '\server.previous';

  if not DirExists(Src) then begin ServerFailure := 'the installer contains no server payload'; exit; end;
  if not RecoverInterruptedSwap(Dst, Staging, Backup, '{#AppExeName}') then
  begin ServerFailure := 'an interrupted server update could not be recovered safely'; exit; end;
  if not ForceDirectories(Staging) then begin ServerFailure := 'the server staging folder could not be created'; exit; end;

  Exec(ExpandConstant('{cmd}'), '/C xcopy "' + Src + '" "' + Staging + '" /E /I /Y /Q',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode <> 0 then
  begin DelTree(Staging, True, True, True); ServerFailure := 'server copy failed, code ' + IntToStr(ResultCode); exit; end;
  if (not FileExists(Staging + '\{#AppExeName}')) or (not FileExists(Staging + '\horizun-mcp.dll')) then
  begin DelTree(Staging, True, True, True); ServerFailure := 'the staged server is incomplete'; exit; end;

  { Claude/Codex legitimately keeps the currently configured stdio server open.
    Revit is already known to be closed, so no Revit-side command can be in
    flight. Stop ONLY processes whose executable path is this exact installed
    server; never stop the client and never use taskkill by image name. }
  if DirExists(Dst) then
  begin
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -ExecutionPolicy Bypass -File "' + Staging + '\client-tools\stop-installed-server.ps1"' +
      ' -ServerPath "' + Dst + '\{#AppExeName}"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    begin
      DelTree(Staging, True, True, True);
      ServerFailure := 'the existing installed server could not be stopped safely';
      exit;
    end;
  end;

  if DirExists(Dst) then
    if not RenameFile(Dst, Backup) then
    begin DelTree(Staging, True, True, True); ServerFailure := 'the existing server is still in use after the bounded stop'; exit; end;
  if not RenameFile(Staging, Dst) then
  begin
    if DirExists(Backup) then RenameFile(Backup, Dst);
    DelTree(Staging, True, True, True);
    ServerFailure := 'the new server could not be swapped in; the previous one was restored';
    exit;
  end;
  if not FileExists(Dst + '\{#AppExeName}') then
  begin
    DelTree(Dst, True, True, True);
    if DirExists(Backup) then RenameFile(Backup, Dst);
    ServerFailure := 'the server vanished after its swap; the previous one was restored';
    exit;
  end;
  if DirExists(Backup) then DelTree(Backup, True, True, True);
  Result := True;
end;

{ ---------------------------------------------------------------------------
  Deploy one year, TRANSACTIONALLY.

  This used to run xcopy and never look at what it returned, then add the year to
  the "installed" list unconditionally. So a copy that failed - a locked file, a
  full disk, a permission - produced a success dialog naming a year that had not
  been installed, and left the previous build half-overwritten. The user then
  reports a bug that was fixed in a version they are not running.

  Now: copy into a staging folder BESIDE the target, verify it, and only then
  swap. If anything fails, the previous install is put back and the year is
  reported as failed. A year is added to InstalledYears only after the swap and a
  post-swap check both succeed.
  --------------------------------------------------------------------------- }
function DeployYear(Year: String): Boolean;
var
  Src, Dst, Staging, Backup: String;
  ResultCode: Integer;
begin
  Result := False;
  Src     := ExpandConstant('{tmp}') + '\HorizunPayload\plugin\' + Year;
  Dst     := AddinsDir(Year) + '\Horizun';
  Staging := AddinsDir(Year) + '\Horizun.installing';
  Backup  := AddinsDir(Year) + '\Horizun.previous';

  if not DirExists(Src) then
  begin
    FailedYears := FailedYears + Year + ' (no payload for this year in the installer), ';
    exit;
  end;

  if not DirExists(AddinsDir(Year)) then
    if not ForceDirectories(AddinsDir(Year)) then
    begin
      FailedYears := FailedYears + Year + ' (could not create the Addins folder), ';
      exit;
    end;

  if not RecoverInterruptedSwap(Dst, Staging, Backup, 'Horizun.Revit.dll') then
  begin
    FailedYears := FailedYears + Year + ' (an interrupted update could not be recovered safely), ';
    exit;
  end;

  if not ForceDirectories(Staging) then
  begin
    FailedYears := FailedYears + Year + ' (could not create the staging folder), ';
    exit;
  end;

  { Copy into staging, and CHECK the result. xcopy returns 0 only when it copied
    everything it was asked to. }
  Exec(ExpandConstant('{cmd}'), '/C xcopy "' + Src + '" "' + Staging + '" /E /I /Y /Q',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode <> 0 then
  begin
    DelTree(Staging, True, True, True);
    FailedYears := FailedYears + Year + ' (copy failed, code ' + IntToStr(ResultCode) + '), ';
    exit;
  end;

  { Existence is not evidence of a copy: xcopy can report success having written
    nothing useful. The one file that must be there is the plugin itself. }
  if not FileExists(Staging + '\Horizun.Revit.dll') then
  begin
    DelTree(Staging, True, True, True);
    FailedYears := FailedYears + Year + ' (the copy reported success but the plugin is not in it), ';
    exit;
  end;

  { The swap. Keep whatever was there until the new one is in place. }
  if DirExists(Dst) then
    if not RenameFile(Dst, Backup) then
    begin
      DelTree(Staging, True, True, True);
      FailedYears := FailedYears + Year + ' (the existing install could not be moved aside - is Revit running?), ';
      exit;
    end;

  if not RenameFile(Staging, Dst) then
  begin
    { Put back exactly what was there. This is the branch that must never be
      skipped: failing here without restoring leaves the year with NO add-in. }
    if DirExists(Backup) then RenameFile(Backup, Dst);
    DelTree(Staging, True, True, True);
    FailedYears := FailedYears + Year + ' (the new build could not be swapped in; the previous one was restored), ';
    exit;
  end;

  if not FileCopy(ExpandConstant('{app}') + '\Horizun.addin', AddinsDir(Year) + '\Horizun.addin', False) then
  begin
    { Without the manifest Revit never loads the DLL, so this is a failed install,
      not a cosmetic problem. Roll the whole year back. }
    DelTree(Dst, True, True, True);
    if DirExists(Backup) then RenameFile(Backup, Dst);
    FailedYears := FailedYears + Year + ' (the .addin manifest could not be written; the previous install was restored), ';
    exit;
  end;

  { Only now is the previous version disposable, and only Horizun''s own folder. }
  if DirExists(Backup) then DelTree(Backup, True, True, True);

  if InstalledYears <> '' then InstalledYears := InstalledYears + ', ';
  InstalledYears := InstalledYears + Year;
  Result := True;
end;

{ Pascal Script has no BoolToStr and no ternary. }
function YesNo(B: Boolean): String;
begin
  if B then Result := 'yes' else Result := 'no';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  I: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    InitYears;
    InstalledYears := '';
    FailedYears := '';
    FoundAny := False;
    ServerInstalled := DeployServer;

    for I := 0 to YearsCount - 1 do
      if RevitInstalled(Years[I]) then
      begin
        FoundAny := True;
        if ServerInstalled then DeployYear(Years[I])
        else FailedYears := FailedYears + Years[I] + ' (server deployment failed; add-in left unchanged), ';
      end;

    { Old installers staged plugin payloads permanently under the application
      directory. It is not live; the new installer extracts to a private temp. }
    if ServerInstalled and DirExists(ExpandConstant('{app}') + '\plugin') then
      DelTree(ExpandConstant('{app}') + '\plugin', True, True, True);

    { A MACHINE-READABLE RESULT, because the dialog is for a person and an
      unattended install has nobody reading it.

      Measured 2026-07-30 by holding a file open inside Addins\2027\Horizun -
      exactly what Revit does - and running /SILENT: 2027 correctly failed and
      was left intact, the other four deployed, and the process still exited
      with code 0. A script reading that exit code would record a partial
      install as a success, which is the same defect as a test printing PASS
      without running.

      Inno Setup returns 0 from any install that completes, and offers no
      supported way to return a different code from ssPostInstall. So the result
      is written where a caller can read it instead, and the limitation is
      stated rather than left to be discovered:

        EXIT CODE 0 DOES NOT MEAN EVERY YEAR DEPLOYED. Read install-result.txt. }
    SaveStringToFile(ExpandConstant('{param:HORIZUNRESULT|' + ExpandConstant('{app}') + '\install-result.txt}'),
      'version=' + '{#AppVersion}' + #13#10 +
      (* WHEN. Added after a silent install FAILED TO INITIALIZE (exit 1, nothing
         deployed) and left the PREVIOUS run's result file sitting in the install
         folder - four hours old, saying fully_installed=yes. A caller that reads
         this file without knowing when it was written cannot tell a successful
         install from a failed one that changed nothing, and the CI gate written
         that same afternoon would have called it a success.

         A reader must check BOTH: the process exit code is 0, and this stamp is
         from the run it just performed.

         Note for whoever edits this comment: it is a star-paren comment because
         an earlier version was a brace comment that quoted an Inno constant by
         name, and a brace comment ends at the first closing brace - so it
         terminated inside its own prose. Do not write either comment delimiter
         in here. Both failures were found by the compiler, four lines apart. *)
      (* LOCAL time, and the field name says so. It was written as installed_utc
         first, and GetDateTimeString returns local: on this machine that is
         UTC-5, so the stamp read five hours in the PAST and the freshness check
         reading it as UTC rejected a perfectly good install as stale. A field
         whose name states a timezone it is not in is worse than one with no
         timezone at all. Inno offers no UTC form, so the name follows the value. *)
      'installed_local=' + GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':') + #13#10 +
      'server_installed=' + YesNo(ServerInstalled) + #13#10 +
      'server_failure=' + ServerFailure + #13#10 +
      'any_revit_found=' + YesNo(FoundAny) + #13#10 +
      'succeeded=' + InstalledYears + #13#10 +
      'failed=' + FailedYears + #13#10 +
      'fully_installed=' + YesNo(ServerInstalled and FoundAny and (FailedYears = '')) + #13#10 +
      'note=READ THIS FILE AND THE EXIT CODE, NOT EITHER ALONE. Setup returns 0 from any install that ' +
      'COMPLETES, including one where a year failed - so 0 does not mean every year deployed, and ' +
      'fully_installed is what says that. But a NON-ZERO code means Setup did not complete, and then ' +
      'this file is whatever the previous run left behind: check installed_local (this machine''s LOCAL ' +
      'time, not UTC) against the time you ran it. A year listed in failed was rolled back to whatever ' +
      'was installed before it.' + #13#10, False);

    { NOBODY IS THERE TO CLICK OK.

      Measured 2026-07-30: with /SILENT /SUPPRESSMSGBOXES these dialogs still
      appeared and the process sat waiting on one. SUPPRESSMSGBOXES answers
      Setup's OWN message boxes; a MsgBox raised from [Code] is not one of them.
      So an unattended deployment - the entire reason a silent switch exists -
      hung on a modal window on a machine with no user in front of it.

      In silent mode the result file above IS the report, and it is written
      before this point precisely so that skipping the dialogs loses nothing. }
    if WizardSilent then exit;

    if not FoundAny then
      MsgBox('No supported Revit installation was found (2023-2027), so the add-in was not deployed.' + #13#10 +
             'The MCP server is installed; run this again after installing Revit.',
             mbInformation, MB_OK)
    else if FailedYears <> '' then
      MsgBox('The add-in was NOT fully installed.' + #13#10#13#10 +
             'Succeeded for: ' + InstalledYears + #13#10 +
             'FAILED for: ' + FailedYears + #13#10#13#10 +
             'Where a year failed, whatever was installed before it has been put back - no Revit has been left ' +
             'without an add-in. The usual cause is Revit still running and holding the files open.' + #13#10#13#10 +
             'Close every Revit and run this installer again.',
             mbError, MB_OK)
    else
      MsgBox('Add-in deployed for Revit: ' + InstalledYears + #13#10#13#10 +
             'Restart Revit to load it.' + #13#10#13#10 +
              'Use the Start-menu shortcut "Configurar Horizun en Codex y Claude" to register both clients safely.' + #13#10 +
              'It keeps timestamped backups and preserves every other MCP entry.' + #13#10#13#10 +
              'Claude CLI alternative:' + #13#10 +
               'claude mcp add --scope user horizun-revit -- "' + ExpandConstant('{app}') + '\server\{#AppExeName}"',
             mbInformation, MB_OK);
  end;
end;

{ Uninstall removes only Horizun's own folders under each Addins\<year>. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  I: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    UninstallFailures := '';
    InitYears;
    for I := 0 to YearsCount - 1 do
    begin
      if DirExists(AddinsDir(Years[I]) + '\Horizun') and
         (not DelTree(AddinsDir(Years[I]) + '\Horizun', True, True, True)) then
        UninstallFailures := UninstallFailures + 'Revit ' + Years[I] + ' add-in folder; ';
      { Leftovers from an interrupted install. Ours, and named unambiguously. }
      DelTree(AddinsDir(Years[I]) + '\Horizun.installing', True, True, True);
      DelTree(AddinsDir(Years[I]) + '\Horizun.previous', True, True, True);
      if FileExists(AddinsDir(Years[I]) + '\Horizun.addin') and
         (not DeleteFile(AddinsDir(Years[I]) + '\Horizun.addin')) then
        UninstallFailures := UninstallFailures + 'Revit ' + Years[I] + ' manifest; ';
    end;
    if DirExists(ExpandConstant('{app}') + '\server') and
       (not DelTree(ExpandConstant('{app}') + '\server', True, True, True)) then
      UninstallFailures := UninstallFailures + 'MCP server; ';
    DelTree(ExpandConstant('{app}') + '\server.installing', True, True, True);
    DelTree(ExpandConstant('{app}') + '\server.previous', True, True, True);
    if UninstallFailures <> '' then
    begin
      Log('Horizun uninstall incomplete: ' + UninstallFailures);
      SuppressibleMsgBox('Horizun could not remove: ' + UninstallFailures + #13#10 +
        'Close any remaining client and run uninstall again. Settings and job history were intentionally preserved.',
        mbError, MB_OK, IDOK);
    end;
  end;
end;
