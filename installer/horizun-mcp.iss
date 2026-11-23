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
#define HorizunAddInId "b8e5a2f0-3c1d-4e6a-9f2b-7a4c8d1e5f30"

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

[CustomMessages]
es.IcoFolder=Horizun Revit MCP (carpeta)
en.IcoFolder=Horizun Revit MCP (folder)
es.IcoComplete=Horizun - completar instalacion
en.IcoComplete=Horizun - complete installation
es.FolderName=Horizun-Revit-MCP
en.FolderName=Horizun-Revit-MCP
es.TaskOpenHub=Ver Horizun Hub - las herramientas y flujos construidos sobre este puente
en.TaskOpenHub=Visit Horizun Hub - the tools and workflows built on this bridge
es.RunOpenHub=Abrir Horizun Hub
en.RunOpenHub=Open Horizun Hub

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
; Identity and add-in manifests participate in the same post-install transaction
; as the server and DLLs. Copying either directly to {app} before that transaction
; completes can make a rolled-back install describe bytes that never landed.
Source: "..\dist\stage\manifest.json"; DestDir: "{tmp}\HorizunPayload"; Flags: ignoreversion deleteafterinstall
Source: "..\dist\stage\Horizun.addin"; DestDir: "{tmp}\HorizunPayload"; Flags: ignoreversion deleteafterinstall

[Icons]
Name: "{group}\{cm:IcoFolder}"; Filename: "{app}"
Name: "{group}\Horizun Hub"; Filename: "{#AppHubUrl}"
; The visible way to finish registration later: nothing waits in the background
; for a client to close (no Run entry, no hidden worker), so this is where the
; user goes after closing Claude Code or Codex.
Name: "{group}\{cm:IcoComplete}"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy RemoteSigned -File ""{app}\server\client-tools\complete-install.ps1"" -Pause"; \
  WorkingDir: "{app}"

[Tasks]
; OPT-IN, and unchecked by default. An installer that opens a browser nobody
; asked for is the kind of thing people warn each other about, and this one is
; going to be installed by people who were told it is safe.
Name: "openhub"; Description: "{cm:TaskOpenHub}"; \
  Flags: unchecked

[Run]
Filename: "{#AppHubUrl}"; Description: "{cm:RunOpenHub}"; \
  Flags: shellexec nowait postinstall skipifsilent; Tasks: openhub
; The client-side helpers are NOT started from here. A [Run] entry without the
; postinstall flag executes during "Finishing installation", which Inno performs
; BEFORE CurStepChanged(ssPostInstall) - and ssPostInstall is where this installer
; swaps server.installing into place. Starting them here raced that swap and
; failed against a folder that did not exist yet. They are launched from
; StartClientHelpers, after the server is really on disk.

[UninstallRun]
; A pending first-start verification must not survive removal with a command that
; points at files the uninstaller is about to delete.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy RemoteSigned -File ""{app}\server\client-tools\complete-install.ps1"" -CancelPending"; \
  Flags: runhidden waituntilterminated skipifdoesntexist

[Code]

const
  { Supported Revit years. There is no runtime column any more: the payload folder
    IS the year, because each year is compiled against its own RevitAPI. Sharing a
    binary between years with the same target framework was the old scheme, and it
    shipped the 2024 build to Revit 2023 and the 2026 build to Revit 2025 - which
    fails at the first API call, not at load. 2022 is gone: it is not supported. }
  YearsCount = 5;
  { Why the manifest preflight refused a year. A duplicate AddInId and a file the
    preflight could not read are different problems with different fixes, and
    reporting both as "already exists" sent a clean machine looking for a Horizun
    install that was never there. }
  ConflictNone = 0;
  ConflictDuplicate = 1;
  ConflictUnreadable = 2;
  ConflictEnumeration = 3;
type
  TSystemTime = record
    wYear: Word;
    wMonth: Word;
    wDayOfWeek: Word;
    wDay: Word;
    wHour: Word;
    wMinute: Word;
    wSecond: Word;
    wMilliseconds: Word;
  end;
var
  Years: array[0..4] of String;
  InstalledYears: String;
  FailedYears: String;
  FoundAny: Boolean;
  ServerInstalled: Boolean;
  ServerFailure: String;
  { The server COPY itself failed (xcopy non-zero, or files missing right after it).
    That is disk space or an antivirus, not Revit: Revit cannot hold the server. }
  ServerCopyFailed: Boolean;
  ServerCopyCode: String;
  UninstallFailures: String;
  YearDeployed: array[0..4] of Boolean;
  YearManifestWritten: array[0..4] of Boolean;
  InstallManifestWritten: Boolean;
  { 4.0 tool profile page. IsUpgrade: an earlier Horizun install or settings file exists. }
  ProfilePage: TInputOptionWizardPage;
  IsUpgrade: Boolean;
  ExistingProfile: String;
  ProfileResult: String;
  ClientCompletion: String;

{ ONE LANGUAGE PER INSTALL, INCLUDING THE PARTS WRITTEN HERE.

  Setup offers Spanish and English, and Inno translates its own wizard - but a
  MsgBox raised from [Code] carries whatever text is compiled into it. Every one
  of them was English, and they named Spanish shortcuts, so somebody who chose
  Spanish was told in English to run something whose name did not match any
  shortcut on their Start menu. L() picks the text for the language the user
  actually chose. }
function L(const Es, En: String): String;
begin
  if ActiveLanguage() = 'es' then Result := Es else Result := En;
end;

function ShouldCompleteInstall(): Boolean;
begin
  { Asked for AND actually present. A helper that did not land is a packaging
    fault, and Setup saying so beats Windows raising CreateProcess at the user. }
  Result := (CompareText(ExpandConstant('{param:HORIZUNCLIENT|Both}'), 'None') <> 0) and
            FileExists(ExpandConstant('{app}\server\client-tools\complete-install.ps1'));
end;

function DesktopHelperPresent(): Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\server\client-tools\install-claude-desktop-extension.ps1'));
end;

{ Started once the server folder is really in place. It runs one pass and Setup
  waits for it: every durable result is written to
  %LOCALAPPDATA%\Horizun\install-status.json, and it never writes underneath a
  live client. Nothing is left running or scheduled after Setup exits. }
function DesktopFolder(): String;
begin
  Result := ExpandConstant('{userdocs}') + '\' + ExpandConstant('{cm:FolderName}');
end;

{ Put the extension and its printed instructions where a person can reach them,
  and OPEN that folder. The package used to sit under AppData\Local\Programs,
  which Explorer hides and Claude Desktop's file picker opens nowhere near - so
  the one step left to the user began with hunting for a file. }
function HandOverDesktopPackage(var Folder: String): Boolean;
var
  Source, Sheet, Language: String;
  Code: Integer;
begin
  Result := False;
  Folder := DesktopFolder();
  Source := ExpandConstant('{app}\server\integrations\claude-desktop\horizun-revit-{#AppVersion}.mcpb');
  if not FileExists(Source) then exit;
  if not ForceDirectories(Folder) then exit;
  if not FileCopy(Source, Folder + '\horizun-revit-{#AppVersion}.mcpb', False) then exit;

  if ActiveLanguage() = 'es' then Language := 'es' else Language := 'en';
  Sheet := ExpandConstant('{app}\server\integrations\claude-desktop\Instalar en Claude Desktop.') + Language + '.pdf';
  if FileExists(Sheet) then
    FileCopy(Sheet, Folder + '\Instalar en Claude Desktop.pdf', False);

  { Selected, not merely listed: the next thing the user does is drag it.
    Only when somebody is watching - a silent install still gets the files,
    because they are part of installing, but it does not get a window. }
  if not WizardSilent then
    Exec(ExpandConstant('{sys}\..\explorer.exe'), '/select,"' + Folder + '\horizun-revit-{#AppVersion}.mcpb"',
         '', SW_SHOWNORMAL, ewNoWait, Code);
  Result := True;
end;

procedure StartClientHelpers;
var
  Shell, Args: String;
  Code: Integer;
begin
  // The plain system constant, from a 32-bit Setup, is redirected to SysWOW64
  // when it is USED, so it would start the 32-bit PowerShell - and these helpers
  // reach for tools that exist only in the native System32. The sysnative
  // constant is the real one, and it falls back for a 32-bit Windows.
  Shell := ExpandConstant('{sysnative}\WindowsPowerShell\v1.0\powershell.exe');
  if not FileExists(Shell) then Shell := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  if ShouldCompleteInstall() then
  begin
    Args := '-NoProfile -ExecutionPolicy RemoteSigned -File "' +
            ExpandConstant('{app}\server\client-tools\complete-install.ps1') + '"' +
            ' -ServerPath "' + ExpandConstant('{app}\server\{#AppExeName}') + '"' +
            ' -Client ' + ExpandConstant('{param:HORIZUNCLIENT|Both}') + ' ' +
            ExpandConstant('{param:HORIZUNNOLIVE|}');
    { Waited on, and asked again while a client is open (exit 3): the old
      design armed a logon Run entry and a hidden PowerShell that waited up to a
      day, and an antivirus behaviour engine read that as malware (Norton
      IDP.HEUR.26, 2026-11-03). Silent installs get one pass; the Start menu
      entry finishes it later. }
    repeat
      if not Exec(Shell, Args, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) then Code := -1;
      if (Code <> 3) or WizardSilent then break;
    until MsgBox(L('Para registrar Horizun en Claude Code y Codex hay que cerrarlos primero (no se edita la configuracion de un cliente abierto).' + #13#10#13#10 +
                   'Cierra Claude Code y Codex y pulsa Reintentar. Si prefieres hacerlo despues, pulsa Cancelar y usa ' +
                   '"' + ExpandConstant('{cm:IcoComplete}') + '" en el menu Inicio.',
                   'To register Horizun with Claude Code and Codex they must be closed first (an open client''s configuration is never edited).' + #13#10#13#10 +
                   'Close Claude Code and Codex and press Retry. To do it later, press Cancel and use ' +
                   '"' + ExpandConstant('{cm:IcoComplete}') + '" in the Start menu.'),
                 mbInformation, MB_RETRYCANCEL) <> IDRETRY;
    case Code of
      0: ClientCompletion := 'completed';
      2: ClientCompletion := 'no_client';
      3: ClientCompletion := 'pending_client_open';
    else
      ClientCompletion := 'failed (exit ' + IntToStr(Code) + ')';
    end;
  end
  else
    ClientCompletion := 'skipped';
  // Claude Desktop is NOT configured from here any more. Its extension is
  // installed from inside the app, by the person using it, and Setup's job is to
  // put the file and its instructions somewhere they can be found.
end;

function McpbPath(): String;
begin
  Result := ExpandConstant('{app}') + '\server\integrations\claude-desktop\horizun-revit-{#AppVersion}.mcpb';
end;

procedure InitYears;
var
  I: Integer;
begin
  Years[0] := '2023';
  Years[1] := '2024';
  Years[2] := '2025';
  Years[3] := '2026';
  Years[4] := '2027';
  for I := 0 to YearsCount - 1 do
  begin
    YearDeployed[I] := False;
    YearManifestWritten[I] := False;
  end;
  InstallManifestWritten := False;
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

function Win32GetLastError(): LongWord;
  external 'GetLastError@kernel32.dll stdcall';

function Win32GetFileAttributes(FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

procedure Win32GetSystemTime(var SystemTime: TSystemTime);
  external 'GetSystemTime@kernel32.dll stdcall';

procedure Win32ExitProcess(ExitCode: LongWord);
  external 'ExitProcess@kernel32.dll stdcall';

function IsHexDigit(Value: Char): Boolean;
begin
  Result := ((Value >= '0') and (Value <= '9')) or
            ((Value >= 'a') and (Value <= 'f')) or
            ((Value >= 'A') and (Value <= 'F'));
end;

function TryNormalizeGuid(Value: String; var Normalized: String): Boolean;
var
  I: Integer;
  S: String;
begin
  Result := False;
  Normalized := '';
  S := Trim(Value);

  { Revit/.NET accepts the standard D, N, B and P GUID spellings. Reduce all
    four to the same 32 lowercase hex digits, but reject every malformed value;
    deleting punctuation from arbitrary text would not be GUID parsing. }
  if (Length(S) = 38) and
     (((S[1] = '{') and (S[38] = '}')) or
      ((S[1] = '(') and (S[38] = ')'))) then
    S := Copy(S, 2, 36);

  if Length(S) = 36 then
  begin
    if (S[9] <> '-') or (S[14] <> '-') or (S[19] <> '-') or (S[24] <> '-') then exit;
    Delete(S, 24, 1);
    Delete(S, 19, 1);
    Delete(S, 14, 1);
    Delete(S, 9, 1);
  end;

  if Length(S) <> 32 then exit;
  for I := 1 to Length(S) do
    if not IsHexDigit(S[I]) then exit;

  Normalized := Lowercase(S);
  Result := True;
end;

procedure AddEvidence(var Evidence: String; Item: String);
begin
  if Evidence <> '' then Evidence := Evidence + #13#10;
  Evidence := Evidence + '  ' + Item;
end;

function GuidPatternInRawBytes(const Pattern: String; const RawBytes: AnsiString): Boolean;
var
  I: Integer;
  Utf16LE, Utf16BE: AnsiString;
begin
  Result := Pos(AnsiString(Pattern), RawBytes) > 0;
  if Result then exit;
  Utf16LE := '';
  Utf16BE := '';
  for I := 1 to Length(Pattern) do
  begin
    Utf16LE := Utf16LE + Pattern[I] + #0;
    Utf16BE := Utf16BE + #0 + Pattern[I];
  end;
  Result := (Pos(Utf16LE, RawBytes) > 0) or (Pos(Utf16BE, RawBytes) > 0);
end;

function RawBytesContainHorizunGuid(const RawBytes: AnsiString): Boolean;
var
  Normalized, Pattern, Digits: String;
  Style, LetterCase: Integer;
begin
  Result := False;
  if not TryNormalizeGuid('{#HorizunAddInId}', Normalized) then exit;
  Digits := Normalized;
  Insert('-', Digits, 21);
  Insert('-', Digits, 17);
  Insert('-', Digits, 13);
  Insert('-', Digits, 9);
  for Style := 0 to 3 do
  begin
    case Style of
      0: Pattern := Digits;
      1: Pattern := Normalized;
      2: Pattern := '{' + Digits + '}';
      3: Pattern := '(' + Digits + ')';
    end;
    for LetterCase := 0 to 1 do
    begin
      if LetterCase = 0 then Pattern := Lowercase(Pattern)
      else Pattern := Uppercase(Pattern);
      if GuidPatternInRawBytes(Pattern, RawBytes) then
      begin
        Result := True;
        exit;
      end;
      { Restore the original pattern before selecting the other letter case. }
      case Style of
        0: Pattern := Digits;
        1: Pattern := Normalized;
        2: Pattern := '{' + Digits + '}';
        3: Pattern := '(' + Digits + ')';
      end;
    end;
  end;
end;

function JsonEscape(const Value: String): String;
var
  I: Integer;
begin
  Result := '"';
  for I := 1 to Length(Value) do
    case Ord(Value[I]) of
      34: Result := Result + '\"';
      92: Result := Result + '\\';
      8: Result := Result + '\b';
      9: Result := Result + '\t';
      10: Result := Result + '\n';
      12: Result := Result + '\f';
      13: Result := Result + '\r';
    else
      if Ord(Value[I]) < 32 then Result := Result + ' ' else Result := Result + Value[I];
    end;
  Result := Result + '"';
end;

function Pad2(Value: Integer): String;
begin
  Result := IntToStr(Value);
  if Length(Result) < 2 then Result := '0' + Result;
end;

function UtcTimestamp: String;
var
  SystemTime: TSystemTime;
begin
  Win32GetSystemTime(SystemTime);
  Result := IntToStr(SystemTime.wYear) + '-' + Pad2(SystemTime.wMonth) + '-' +
    Pad2(SystemTime.wDay) + 'T' + Pad2(SystemTime.wHour) + ':' +
    Pad2(SystemTime.wMinute) + ':' + Pad2(SystemTime.wSecond) + 'Z';
end;

procedure WritePrecheck(const Cause: String; ExitCode: Integer; const Evidence: String);
var
  Path, Json: String;
begin
  Path := ExpandConstant('{localappdata}') + '\Horizun\install-precheck.json';
  if not ForceDirectories(ExtractFileDir(Path)) then
  begin
    Log('Preflight could not create report directory: ' + ExtractFileDir(Path));
    exit;
  end;
  Json := '{"version":"{#AppVersion}","cause":' + JsonEscape(Cause) +
    ',"exit_code":' + IntToStr(ExitCode) + ',"evidence":[' + JsonEscape(Evidence) +
    '],"utc":' + JsonEscape(UtcTimestamp) + '}';
  if not SaveStringToFile(Path, Utf8Encode(Json), False) then
    Log('Preflight could not write JSON report: ' + Path)
  else
    Log('Preflight JSON report: ' + Path);
end;

procedure RefusePreflight(const Cause, MessageText, Evidence: String; ExitCode: Integer);
begin
  Log('Preflight refused: cause=' + Cause + ' exit_code=' + IntToStr(ExitCode) + ' evidence=' + Evidence);
  WritePrecheck(Cause, ExitCode, Evidence);
  SuppressibleMsgBox(MessageText + #13#10#13#10 + L('No se ha cambiado nada.', 'Nothing has been changed.'),
    mbError, MB_OK, IDOK);
  Win32ExitProcess(ExitCode);
end;

function HasHorizunAddInIdConflict(AddinsRoot, Year, AllowedManifest: String;
  var Kind: Integer; var Evidence: String): Boolean;
var
  FindRec: TFindRec;
  Candidate, Needle, CandidateId, YearRoot, Unreadable, ParseError: String;
  RawBytes: AnsiString;
  I: Integer;
  EnumerationError, YearRootAttributes: LongWord;
  FoundManifest, EnumerationCompleted: Boolean;
  XmlDocument, AddInIdNodes: Variant;
begin
  { Parse failures are inspected as raw bytes before they block: a foreign
    manifest with no Horizun GUID is logged and skipped. If the bytes contain
    the GUID, or the file cannot be read, the preflight still refuses. }
  Result := True;
  Kind := ConflictUnreadable;
  YearRoot := AddinsRoot + '\' + Year;
  Evidence := '';
  Unreadable := '';
  if not TryNormalizeGuid('{#HorizunAddInId}', Needle) then
  begin
    AddEvidence(Evidence, 'Horizun AddInId {#HorizunAddInId}: not a GUID');
    exit;
  end;
  YearRootAttributes := Win32GetFileAttributes(YearRoot);
  if YearRootAttributes = $FFFFFFFF then
  begin
    EnumerationError := Win32GetLastError();
    { A genuinely absent year directory is harmless. Access denied, a sharing
      violation and every other failure cannot establish absence. }
    if (EnumerationError = 2) or (EnumerationError = 3) then Result := False;
    if Result then AddEvidence(Evidence, YearRoot + ' (Windows error ' + IntToStr(EnumerationError) + ')')
    else Kind := ConflictNone;
    exit;
  end;
  if (YearRootAttributes and $10) = 0 then
  begin
    AddEvidence(Evidence, YearRoot + ' (' + L('no es una carpeta', 'not a folder') + ')');
    exit;
  end;
  FoundManifest := FindFirst(YearRoot + '\*.addin', FindRec);
  EnumerationError := Win32GetLastError();
  if FoundManifest then
  begin
    EnumerationCompleted := False;
    try
      repeat
        Candidate := AddinsRoot + '\' + Year + '\' + FindRec.Name;
        if (AllowedManifest = '') or (CompareText(Candidate, AllowedManifest) <> 0) then
        begin
          try
            XmlDocument := CreateOleObject('Msxml2.DOMDocument.6.0');
            XmlDocument.async := False;
            XmlDocument.validateOnParse := False;
            XmlDocument.resolveExternals := False;
            XmlDocument.setProperty('ProhibitDTD', True);
            XmlDocument.setProperty('SelectionLanguage', 'XPath');
            if XmlDocument.load(Candidate) then
            begin
              AddInIdNodes := XmlDocument.selectNodes('//*[local-name()="AddInId"]');
              for I := 0 to AddInIdNodes.length - 1 do
                if TryNormalizeGuid(String(AddInIdNodes.item[I].text), CandidateId) and
                   (CandidateId = Needle) then
                begin
                  Kind := ConflictDuplicate;
                  Evidence := '';
                  AddEvidence(Evidence, Candidate);
                  exit;
                end;
            end
            else
            begin
              ParseError := Trim(String(XmlDocument.parseError.reason)) + ' (' +
                L('linea ', 'line ') + IntToStr(XmlDocument.parseError.line) + ')';
              if not LoadStringFromFile(Candidate, RawBytes) then
              begin
                AddEvidence(Unreadable, Candidate + ' - ' + ParseError + '; ' +
                  L('no se pudieron leer los bytes', 'raw bytes could not be read'));
              end
              else if RawBytesContainHorizunGuid(RawBytes) then
              begin
                Kind := ConflictDuplicate;
                Evidence := '';
                AddEvidence(Evidence, Candidate + ' - ' + L('GUID de Horizun encontrado en bytes sin parsear',
                  'Horizun GUID found in unparsed bytes'));
                exit;
              end
              else
                Log('Manifest preflight ignored unreadable foreign manifest: ' + Candidate + ' - ' + ParseError);
            end;
          except
            ParseError := GetExceptionMessage;
            try
              if LoadStringFromFile(Candidate, RawBytes) then
              begin
                if RawBytesContainHorizunGuid(RawBytes) then
                begin
                  Kind := ConflictDuplicate;
                  Evidence := '';
                  AddEvidence(Evidence, Candidate + ' - ' + L('GUID de Horizun encontrado en bytes sin parsear',
                    'Horizun GUID found in unparsed bytes'));
                  exit;
                end
                else
                begin
                  Log('Manifest preflight ignored unreadable foreign manifest: ' + Candidate + ' - ' + ParseError);
                end;
              end
              else
                AddEvidence(Unreadable, Candidate + ' - ' + ParseError + '; ' +
                  L('no se pudieron leer los bytes', 'raw bytes could not be read'));
            except
              AddEvidence(Unreadable, Candidate + ' - ' + ParseError + '; ' +
                L('fallo al leer los bytes', 'raw byte read failed'));
            end;
          end;
        end;
        FoundManifest := FindNext(FindRec);
        if not FoundManifest then
        begin
          EnumerationError := Win32GetLastError();
          EnumerationCompleted := (EnumerationError = 0) or (EnumerationError = 18);
        end;
      until not FoundManifest;
    finally
      FindClose(FindRec);
    end;
    Evidence := Unreadable;
    if not EnumerationCompleted then
    begin
      if EnumerationError = 5 then Kind := ConflictUnreadable
      else Kind := ConflictEnumeration;
      AddEvidence(Evidence, YearRoot + ' (Windows error ' + IntToStr(EnumerationError) + ')');
      exit;
    end;
    if Unreadable <> '' then
    begin
      Kind := ConflictUnreadable;
      exit;
    end;
  end;
  if not FoundManifest then
  begin
    { FindFirst returns False both for an empty match and for access/I/O errors.
      Inno Setup's wrapper also reports ERROR_SUCCESS (0) for a clean directory
      with no matching *.addin files on some Windows 10 systems. That is not an
      enumeration failure: treating it as one caused the false "manifest already
      exists" block reported in discussion #37. Keep access, sharing and every
      unknown non-success error fail-closed, but accept 0 and the documented
      no-match results as a clean directory. }
    if (EnumerationError <> 0) and (EnumerationError <> 2) and (EnumerationError <> 18) then
    begin
      if EnumerationError = 5 then Kind := ConflictUnreadable
      else Kind := ConflictEnumeration;
      AddEvidence(Evidence, YearRoot + ' (Windows error ' + IntToStr(EnumerationError) + ')');
      exit;
    end;
  end;
  Kind := ConflictNone;
  Result := False;
end;

procedure RecordAddInIdConflict(AddinsRoot, Year, AllowedManifest: String;
  var Duplicates, Unreadables, EnumerationErrors: String);
var
  Kind: Integer;
  Evidence: String;
begin
  if not HasHorizunAddInIdConflict(AddinsRoot, Year, AllowedManifest, Kind, Evidence) then exit;
  if Kind = ConflictDuplicate then
  begin
    if Duplicates <> '' then Duplicates := Duplicates + #13#10;
    Duplicates := Duplicates + 'Revit ' + Year + ':' + #13#10 + Evidence;
  end
  else if Kind = ConflictEnumeration then
  begin
    if EnumerationErrors <> '' then EnumerationErrors := EnumerationErrors + #13#10;
    EnumerationErrors := EnumerationErrors + 'Revit ' + Year + ':' + #13#10 + Evidence;
  end
  else
  begin
    if Unreadables <> '' then Unreadables := Unreadables + #13#10;
    Unreadables := Unreadables + 'Revit ' + Year + ':' + #13#10 + Evidence;
  end;
end;

function RevitProcessEvidence: String;
var
  Path: String;
  Code: Integer;
  RawOutput: AnsiString;
begin
  Path := ExpandConstant('{tmp}\revit-processes.csv');
  Result := 'Revit.exe detected; process details unavailable';
  if Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq Revit.exe" /FO CSV /NH > "' + Path + '"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) then
    if LoadStringFromFile(Path, RawOutput) and (Trim(String(RawOutput)) <> '') then
      Result := Trim(String(RawOutput));
  DeleteFile(Path);
end;

function InitializeSetup: Boolean;
var
  I: Integer;
  ProgramFiles64, Duplicates, Unreadables, EnumerationErrors, UserRoot, MachineRoot,
  ExpectedUserManifest, Evidence: String;
begin
  Result := True;
  if RevitIsRunning then
  begin
    Evidence := RevitProcessEvidence;
    RefusePreflight('revit_running',
      L('Revit está abierto (' + Evidence + '). Cierra todos los procesos Revit.exe y vuelve a ejecutar el instalador.',
        'Revit is running (' + Evidence + '). Close every Revit.exe process and run Setup again.'), Evidence, 10);
  end;

  { Revit reads both ProgramData and AppData. Installing the same AddInId in both
    scopes is not an update: it is two competing add-ins. Refuse before Setup
    writes anything. }
  InitYears;
  ProgramFiles64 := GetEnv('ProgramW6432');
  if ProgramFiles64 = '' then ProgramFiles64 := ExpandConstant('{commonpf}');
  UserRoot := ExpandConstant('{userappdata}') + '\Autodesk\Revit\Addins';
  MachineRoot := ExpandConstant('{commonappdata}') + '\Autodesk\Revit\Addins';
  Duplicates := '';
  Unreadables := '';
  EnumerationErrors := '';
  for I := 0 to YearsCount - 1 do
    if FileExists(ProgramFiles64 + '\Autodesk\Revit ' + Years[I] + '\Revit.exe') then
    begin
      ExpectedUserManifest := UserRoot + '\' + Years[I] + '\Horizun.addin';
      RecordAddInIdConflict(MachineRoot, Years[I], '', Duplicates, Unreadables, EnumerationErrors);
      RecordAddInIdConflict(UserRoot, Years[I], ExpectedUserManifest, Duplicates, Unreadables, EnumerationErrors);
    end;
  if Duplicates <> '' then
  begin
    RefusePreflight('duplicate_addin_id',
      L('Ya existe otro manifiesto con el AddInId de Horizun. Revisa estas rutas y quita o migra la instalación duplicada antes de continuar:' + #13#10 + Duplicates,
        'Another manifest with the Horizun AddInId already exists. Review these paths and remove or migrate the duplicate installation before continuing:' + #13#10 + Duplicates), Duplicates, 11);
  end;
  if Unreadables <> '' then
  begin
    RefusePreflight('unreadable_manifest_or_folder',
      L('No se pudo leer un manifiesto o carpeta Addins que podría registrar Horizun. Corrige sus permisos o retira temporalmente el archivo indicado y vuelve a ejecutar el instalador:' + #13#10 + Unreadables,
        'A manifest or Addins folder could not be read and may register Horizun. Fix its permissions or temporarily move the listed file, then run Setup again:' + #13#10 + Unreadables), Unreadables, 12);
  end;
  if EnumerationErrors <> '' then
  begin
    RefusePreflight('enumeration_error',
      L('Windows no pudo terminar de enumerar una carpeta Addins. Revisa los permisos y el estado de la carpeta indicada; luego vuelve a ejecutar el instalador:' + #13#10 + EnumerationErrors,
        'Windows could not finish enumerating an Addins folder. Check the listed folder permissions and state, then run Setup again:' + #13#10 + EnumerationErrors), EnumerationErrors, 13);
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  if RevitIsRunning or BridgeIsRunning then
  begin
    SuppressibleMsgBox(L('Cierra todas las ventanas de Revit y todos los clientes Codex/Claude que usen Horizun antes de desinstalar.' + #13#10#13#10 +
                         'Esos procesos mantienen abiertos los archivos instalados. No se quito nada.',
                         'Close every Revit window and every Codex/Claude client using Horizun before uninstalling.' + #13#10#13#10 +
                         'Those processes hold the installed files open. Nothing was removed.'), mbError, MB_OK, IDOK);
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

function QuarantineServerBackup(Backup: String): Boolean;
var
  Candidate: String;
  I: Integer;
begin
  Result := not DirExists(Backup);
  if Result then exit;
  for I := 0 to 999 do
  begin
    Candidate := AddBackslash(ExtractFileDir(Backup)) + '.legacy-backup-' +
      GetDateTimeString('yyyymmddhhnnss', '-', ':') + '-' + IntToStr(I);
    if not DirExists(Candidate) then
    begin
      Result := RenameFile(Backup, Candidate);
      exit;
    end;
  end;
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
    if RequiredFile = '{#AppExeName}' then
    begin
      if not QuarantineServerBackup(Backup) then exit;
    end
    else if not DelTree(Backup, True, True, True) then exit;
  end;
  Result := True;
end;

function StopAndMoveInstalledServer(Staging, Dst, Backup: String): Boolean;
var
  Attempt, ResultCode: Integer;
begin
  Result := False;
  { MCP clients may immediately respawn their configured stdio server after it
    exits. Keep the stop and directory move in one bounded retry loop so a
    respawn cannot turn the tiny gap between those operations into a permanent
    update failure. The helper still kills only this exact executable path. }
  for Attempt := 1 to 8 do
  begin
    if not DirExists(Dst) then begin Result := True; exit; end;
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -ExecutionPolicy RemoteSigned -File ' +
      AddQuotes(AddBackslash(Staging) + 'client-tools\stop-installed-server.ps1') +
      ' -ServerPath ' + AddQuotes(AddBackslash(Dst) + '{#AppExeName}'),
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      exit;
    if RenameFile(Dst, Backup) then begin Result := True; exit; end;
    Sleep(125);
  end;
end;

function DeployServer: Boolean;
var
  Src, Dst, Staging, Backup: String;
  ResultCode: Integer;
begin
  Result := False;
  ServerFailure := '';
  ServerCopyFailed := False;
  ServerCopyCode := '';
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
  begin
    DelTree(Staging, True, True, True);
    ServerCopyFailed := True; ServerCopyCode := 'xcopy ' + IntToStr(ResultCode);
    ServerFailure := 'server copy failed, code ' + IntToStr(ResultCode); exit;
  end;
  if (not FileExists(Staging + '\{#AppExeName}')) or (not FileExists(Staging + '\horizun-mcp.dll')) then
  begin
    DelTree(Staging, True, True, True);
    ServerCopyFailed := True; ServerCopyCode := 'xcopy 0, files missing after the copy';
    ServerFailure := 'the staged server is incomplete (xcopy returned 0 but files are missing)'; exit;
  end;

  { Claude/Codex legitimately keeps the currently configured stdio server open.
    Revit is already known to be closed, so no Revit-side command can be in
    flight. Stop ONLY processes whose executable path is this exact installed
    server; never stop the client and never use taskkill by image name. }
  if DirExists(Dst) then
  begin
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -ExecutionPolicy RemoteSigned -File "' + Staging + '\client-tools\stop-installed-server.ps1"' +
      ' -ServerPath "' + Dst + '\{#AppExeName}"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    begin
      DelTree(Staging, True, True, True);
      ServerFailure := 'the existing installed server could not be stopped safely';
      exit;
    end;
  end;

  if DirExists(Dst) then
    if not StopAndMoveInstalledServer(Staging, Dst, Backup) then
    begin DelTree(Staging, True, True, True); ServerFailure := 'the existing server is still in use after bounded stop-and-swap retries'; exit; end;
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
  { Keep Backup until every installed Revit year and both manifests succeed. }
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

  { Keep Backup and defer the .addin manifest until the whole deployment is
    known good. A server/add-in contract may never be partially promoted. }

  if InstalledYears <> '' then InstalledYears := InstalledYears + ', ';
  InstalledYears := InstalledYears + Year;
  Result := True;
end;

procedure RollbackDeployment;
var
  I: Integer;
  Dst, Backup, Addin, AddinBackup, ServerDst, ServerBackup, ProductManifest, ProductManifestBackup: String;
begin
  for I := YearsCount - 1 downto 0 do
  begin
    Dst := AddinsDir(Years[I]) + '\Horizun';
    Backup := AddinsDir(Years[I]) + '\Horizun.previous';
    Addin := AddinsDir(Years[I]) + '\Horizun.addin';
    AddinBackup := AddinsDir(Years[I]) + '\Horizun.addin.previous';
    if YearManifestWritten[I] then
    begin
      if FileExists(AddinBackup) then
      begin
        DeleteFile(Addin);
        RenameFile(AddinBackup, Addin);
      end
      else
        DeleteFile(Addin);
    end;
    if YearDeployed[I] then
    begin
      if DirExists(Dst) then DelTree(Dst, True, True, True);
      if DirExists(Backup) then RenameFile(Backup, Dst);
    end;
  end;

  ProductManifest := ExpandConstant('{app}') + '\manifest.json';
  ProductManifestBackup := ExpandConstant('{app}') + '\manifest.previous.json';
  if InstallManifestWritten then
  begin
    DeleteFile(ProductManifest);
    if FileExists(ProductManifestBackup) then RenameFile(ProductManifestBackup, ProductManifest);
  end;

  if ServerInstalled then
  begin
    ServerDst := ExpandConstant('{app}') + '\server';
    ServerBackup := ExpandConstant('{app}') + '\server.previous';
    if DirExists(ServerDst) then DelTree(ServerDst, True, True, True);
    if DirExists(ServerBackup) then RenameFile(ServerBackup, ServerDst);
  end;
  ServerInstalled := False;
  InstalledYears := '';
end;

function WriteDeploymentManifests: Boolean;
var
  I: Integer;
  SourceAddin, Addin, AddinBackup, ProductManifest, ProductManifestBackup: String;
begin
  Result := False;
  SourceAddin := ExpandConstant('{tmp}') + '\HorizunPayload\Horizun.addin';
  for I := 0 to YearsCount - 1 do
    if YearDeployed[I] then
    begin
      Addin := AddinsDir(Years[I]) + '\Horizun.addin';
      AddinBackup := AddinsDir(Years[I]) + '\Horizun.addin.previous';
      if FileExists(AddinBackup) then DeleteFile(AddinBackup);
      if FileExists(Addin) and (not FileCopy(Addin, AddinBackup, False)) then
      begin
        FailedYears := FailedYears + Years[I] + ' (could not back up the existing .addin manifest), ';
        exit;
      end;
      YearManifestWritten[I] := True;
      if not FileCopy(SourceAddin, Addin, False) then
      begin
        FailedYears := FailedYears + Years[I] + ' (could not write the .addin manifest), ';
        exit;
      end;
    end;

  ProductManifest := ExpandConstant('{app}') + '\manifest.json';
  ProductManifestBackup := ExpandConstant('{app}') + '\manifest.previous.json';
  if FileExists(ProductManifestBackup) then DeleteFile(ProductManifestBackup);
  if FileExists(ProductManifest) and (not FileCopy(ProductManifest, ProductManifestBackup, False)) then
  begin
    ServerFailure := 'the installed identity manifest could not be backed up';
    exit;
  end;
  InstallManifestWritten := True;
  if not FileCopy(ExpandConstant('{tmp}') + '\HorizunPayload\manifest.json', ProductManifest, False) then
  begin
    ServerFailure := 'the installed identity manifest could not be written';
    exit;
  end;
  Result := True;
end;

function LegacyVerifyInstalledPayload: Boolean;
var
  ResultCode: Integer;
begin
  { The manifest and every rollback image still exist at this point. Verify the
    complete server/plugin inventories plus each real .addin before Commit removes
    those images; an existence check is not a transaction postcondition. }
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy RemoteSigned -File "' +
    ExpandConstant('{app}') + '\server\client-tools\verify-install.ps1"' +
    ' -Client None -ServerPath "' + ExpandConstant('{app}') + '\server\{#AppExeName}" -SkipLive',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if not Result then
    ServerFailure := 'exact installed-payload verification failed; the previous installation was restored';
end;

function VerifyInstalledPayload: Boolean;
var
  ResultCode: Integer;
  ReportPath, VerifyArgs: String;
begin
  { Pascal strings are single-quoted. A double quote therefore must be passed
    directly; prefixing it with a backslash makes that backslash part of the
    Windows command line and PowerShell rejects the -File path as illegal. }
  ReportPath := AddBackslash(ExpandConstant('{localappdata}')) + 'Horizun\install-verification.json';
  ForceDirectories(ExtractFileDir(ReportPath));
  VerifyArgs := '-NoProfile -ExecutionPolicy RemoteSigned -File ' +
    AddQuotes(AddBackslash(ExpandConstant('{app}')) + 'server\client-tools\verify-install.ps1') +
    ' -Client None -ServerPath ' +
    AddQuotes(AddBackslash(ExpandConstant('{app}')) + 'server\{#AppExeName}') +
    ' -SkipLive -Json ' + AddQuotes(ReportPath);
  Log('Installed payload verifier arguments: ' + VerifyArgs);
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    VerifyArgs,
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if not Result then
    ServerFailure := 'exact installed-payload verification failed (verifier exit ' +
      IntToStr(ResultCode) + '); the previous installation was restored';
end;

procedure CommitDeployment;
var
  I, CleanupResult: Integer;
begin
  if not QuarantineServerBackup(ExpandConstant('{app}') + '\server.previous') then
    Log('Server backup could not be quarantined; it is preserved for a later run');
  if FileExists(ExpandConstant('{app}') + '\manifest.previous.json') then
    DeleteFile(ExpandConstant('{app}') + '\manifest.previous.json');
  for I := 0 to YearsCount - 1 do
  begin
    if DirExists(AddinsDir(Years[I]) + '\Horizun.previous') then
      DelTree(AddinsDir(Years[I]) + '\Horizun.previous', True, True, True);
    if FileExists(AddinsDir(Years[I]) + '\Horizun.addin.previous') then
      DeleteFile(AddinsDir(Years[I]) + '\Horizun.addin.previous');
  end;
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy RemoteSigned -File ' +
    AddQuotes(ExpandConstant('{app}\server\client-tools\cleanup-backups.ps1')) +
    ' -Root ' + AddQuotes(ExpandConstant('{app}')) + ' -PreservePrevious',
    '', SW_HIDE, ewWaitUntilTerminated, CleanupResult) then
    Log('Backup retention could not start')
  else if CleanupResult <> 0 then
    Log('Backup retention deferred or failed: ' + IntToStr(CleanupResult));
end;

{ ---------------------------------------------------------------------------
  TOOL PROFILE (4.0). Which tool groups MCP clients see: core + the profile.
  It is visibility only - permissions, the Python grant and every other setting
  are untouched - and any tool stays callable through horizun_call_tool.

  NEW INSTALL: the chosen profile is written to settings.json (tool_profile).
  The page pre-selects "modelador" (complete modeller) because modelling is
  where most measured use is; the person can pick any other.

  UPGRADE: the first option, "keep what I have", is pre-selected and writes
  NOTHING. A machine without tool_profile keeps the 3.x behaviour - every tool
  listed - unless the person chooses a profile here.

  Silent installs: /PROFILE=<name> chooses; without it a new install writes the
  pre-selected profile and an upgrade keeps what it has.
  --------------------------------------------------------------------------- }
const
  ProfileCount = 10;

function ProfileId(Index: Integer): String;
begin
  case Index of
    0: Result := 'nucleo';
    1: Result := 'auditor';
    2: Result := 'modelador_arquitectura';
    3: Result := 'modelador_estructura';
    4: Result := 'modelador_mep';
    5: Result := 'modelador';
    6: Result := 'documentador';
    7: Result := 'coordinador';
    8: Result := 'analista_datos';
    9: Result := 'completo';
  else
    Result := '';
  end;
end;

function ProfileLabel(Index: Integer): String;
begin
  case Index of
    0: Result := L('Solo nucleo (19 herramientas, ~12-15 mil tokens): el resto se activa por grupos cuando haga falta',
                   'Core only (19 tools, ~12-15k tokens): everything else is switched on by group when needed');
    1: Result := L('Auditor: lectura, auditoria y documentacion', 'Auditor: reading, audits and documentation');
    2: Result := L('Modelador de arquitectura', 'Architecture modeller');
    3: Result := L('Modelador de estructura y refuerzo', 'Structure and reinforcement modeller');
    4: Result := L('Modelador MEP', 'MEP modeller');
    5: Result := L('Modelador completo: arquitectura, estructura, MEP, CAD/IFC y familias (recomendado)',
                   'Complete modeller: architecture, structure, MEP, CAD/IFC and families (recommended)');
    6: Result := L('Documentador: vistas, planos, anotacion, exportacion y tablas',
                   'Documenter: views, sheets, annotation, export and schedules');
    7: Result := L('Coordinador: interferencias, vinculos, CDE, CAD/IFC y documentacion',
                   'Coordinator: clash, links, CDE, CAD/IFC and documentation');
    8: Result := L('Analista de datos: cantidades, tablas, Excel, Power BI y familias',
                   'Data analyst: quantities, schedules, Excel, Power BI and families');
    9: Result := L('Completo: todas las herramientas, como en 3.x (~120-150 mil tokens)',
                   'Full: every tool, as in 3.x (~120-150k tokens)');
  else
    Result := '';
  end;
end;

function SettingsFile(): String;
var
  Root: String;
begin
  Root := GetEnv('HORIZUN_DATA_ROOT');
  if Root = '' then Root := ExpandConstant('{%USERPROFILE}') + '\.horizun';
  Result := Root + '\settings.json';
end;

{ The current tool_profile value, read crudely: settings.json is the owner's file and
  the helper parses it properly; this is only for the label of "keep". }
function ReadExistingProfile(): String;
var
  Text: AnsiString;
  S: String;
  P, Q: Integer;
begin
  Result := '';
  if not LoadStringFromFile(SettingsFile(), Text) then exit;
  S := String(Text);
  P := Pos('"tool_profile"', S);
  if P = 0 then exit;
  S := Copy(S, P + 14, 200);
  P := Pos('"', S);
  if P = 0 then exit;
  S := Copy(S, P + 1, 200);
  Q := Pos('"', S);
  if Q = 0 then exit;
  Result := Copy(S, 1, Q - 1);
end;

procedure InitializeWizard;
var
  I: Integer;
  KeepLabel: String;
begin
  { A 3.x install made with install.ps1 has no uninstall entry and may have no
    settings.json yet: its server folder or install status still marks an upgrade. }
  IsUpgrade := RegKeyExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F3B6A21-9E44-4E77-A0C5-6C1D2E9A7B10}_is1') or
               FileExists(SettingsFile()) or
               DirExists(ExpandConstant('{localappdata}') + '\Programs\Horizun\MCP\server') or
               FileExists(ExpandConstant('{localappdata}') + '\Horizun\install-status.json');
  ExistingProfile := ReadExistingProfile();
  ProfilePage := CreateInputOptionPage(wpSelectTasks,
    L('Perfil de herramientas', 'Tool profile'),
    L('Que herramientas veran Claude, Codex y los demas clientes al empezar.',
      'Which tools Claude, Codex and the other clients see when a session starts.'),
    L('Menos herramientas en la lista = menos tokens por sesion. Es solo visibilidad: los permisos no cambian, ' +
      'cualquier grupo se activa en la sesion con horizun_toolsets y toda herramienta se puede llamar con ' +
      'horizun_call_tool. Se puede cambiar despues (settings.json, tool_profile, o HORIZUN_PROFILE en el cliente).',
      'Fewer tools in the list = fewer tokens per session. It is visibility only: permissions do not change, ' +
      'any group can be switched on in the session with horizun_toolsets and every tool can be called through ' +
      'horizun_call_tool. It can be changed later (settings.json tool_profile, or HORIZUN_PROFILE in the client).'),
    True, False);
  if IsUpgrade then
  begin
    if ExistingProfile <> '' then
      KeepLabel := L('Mantener lo que tengo (perfil actual: ', 'Keep what I have (current profile: ') + ExistingProfile + ')'
    else
      KeepLabel := L('Mantener lo que tengo (actualizacion: todas las herramientas, como en 3.x)',
                     'Keep what I have (upgrade: every tool, as in 3.x)');
    ProfilePage.Add(KeepLabel);
  end;
  for I := 0 to ProfileCount - 1 do ProfilePage.Add(ProfileLabel(I));
  if IsUpgrade then ProfilePage.SelectedValueIndex := 0
  else ProfilePage.SelectedValueIndex := 5;  { modelador }
end;

{ The profile id the person chose, or 'keep'. /PROFILE= wins in a silent install. }
function ChosenProfile(): String;
var
  Index: Integer;
begin
  Result := Lowercase(Trim(ExpandConstant('{param:PROFILE|}')));
  if Result <> '' then exit;
  Index := ProfilePage.SelectedValueIndex;
  if IsUpgrade then
  begin
    if Index <= 0 then begin Result := 'keep'; exit; end;
    Index := Index - 1;
  end;
  Result := ProfileId(Index);
  if Result = '' then
  begin
    if IsUpgrade then Result := 'keep' else Result := 'modelador';
  end;
end;

{ Runs the installed helper, which validates the name, preserves every other key and
  reads the file back. A failure is recorded, never fatal: the bridge works either way. }
procedure ApplyToolProfile;
var
  Shell, Args, Mode, OutFile: String;
  Code: Integer;
  Lines: AnsiString;
begin
  if IsUpgrade then Mode := 'upgrade' else Mode := 'new';
  Shell := ExpandConstant('{sysnative}\WindowsPowerShell\v1.0\powershell.exe');
  if not FileExists(Shell) then Shell := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  OutFile := ExpandConstant('{tmp}\tool-profile-result.txt');
  Args := '-NoProfile -ExecutionPolicy RemoteSigned -File "' +
          ExpandConstant('{app}\server\client-tools\set-tool-profile.ps1') + '"' +
          ' -ToolProfile ' + ChosenProfile() + ' -Mode ' + Mode + ' -ResultPath "' + OutFile + '"';
  if Exec(Shell, Args, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) and
     LoadStringFromFile(OutFile, Lines) then
    ProfileResult := Trim(String(Lines))
  else
    ProfileResult := 'not applied (exit ' + IntToStr(Code) + ')';
  Log('Tool profile: ' + ProfileResult);
end;

{ Pascal Script has no BoolToStr and no ternary. }
function YesNo(B: Boolean): String;
begin
  if B then Result := 'yes' else Result := 'no';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  I: Integer;
  HandoverFolder: String;
  HandedOver: Boolean;
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
        if ServerInstalled then
        begin
          YearDeployed[I] := DeployYear(Years[I]);
        end
        else FailedYears := FailedYears + Years[I] + ' (server deployment failed; add-in left unchanged), ';
      end;

    if ServerInstalled and ((not FoundAny) or (FailedYears = '')) then
    begin
      if WriteDeploymentManifests then
      begin
        if VerifyInstalledPayload then CommitDeployment
        else RollbackDeployment;
      end
      else RollbackDeployment;
    end
    else if ServerInstalled then
      RollbackDeployment;

    { The server is on disk, verified and committed now - not a moment earlier.
      These helpers register it with Claude and Codex; started before the
      verification, they registered a payload that a failed verify then rolled
      back. RollbackDeployment clears ServerInstalled, so a rolled-back install
      starts nothing. }
    { The tool profile goes in after the payload is committed, so a rolled-back install
      writes no setting, and before the client helpers start, so their first session
      already reads it. }
    if ServerInstalled then ApplyToolProfile;
    if ServerInstalled then StartClientHelpers;

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
      'server_copy_code=' + ServerCopyCode + #13#10 +
      'any_revit_found=' + YesNo(FoundAny) + #13#10 +
      'succeeded=' + InstalledYears + #13#10 +
      'failed=' + FailedYears + #13#10 +
      'fully_installed=' + YesNo(ServerInstalled and FoundAny and (FailedYears = '')) + #13#10 +
      'tool_profile=' + ProfileResult + #13#10 +
      'client_completion=' + ClientCompletion + #13#10 +
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
    { Before the silent-mode exit: handing the file over is installing, not
      reporting. A quiet install that skipped it would leave the one manual
      step with nothing to do it from. }
    HandedOver := HandOverDesktopPackage(HandoverFolder);
    if WizardSilent then exit;

    if (not FoundAny) and (not ServerCopyFailed) then
      MsgBox(L('No se encontro ninguna instalacion de Revit compatible (2023-2027), asi que el complemento no se instalo.' + #13#10 +
               'El servidor MCP si quedo instalado; vuelve a ejecutar esto despues de instalar Revit.',
               'No supported Revit installation was found (2023-2027), so the add-in was not deployed.' + #13#10 +
               'The MCP server is installed; run this again after installing Revit.'),
             mbInformation, MB_OK)
    else if ServerCopyFailed then
      MsgBox(L('El servidor MCP NO se pudo copiar, asi que no se instalo nada nuevo y quedo lo que habia antes.' + #13#10#13#10 +
               'Codigo de la copia: ' + ServerCopyCode + #13#10#13#10 +
               'Revisa que haya espacio libre en el disco y que el antivirus no este bloqueando ni poniendo en cuarentena ' +
               'los archivos de Horizun, y vuelve a ejecutar este instalador.',
               'The MCP server could NOT be copied, so nothing new was installed and the previous installation was kept.' + #13#10#13#10 +
               'Copy code: ' + ServerCopyCode + #13#10#13#10 +
               'Check that the disk has free space and that no antivirus is blocking or quarantining the Horizun files, ' +
               'then run this installer again.'),
             mbError, MB_OK)
    else if FailedYears <> '' then
      MsgBox(L('El complemento NO quedo instalado del todo.' + #13#10#13#10 +
               'Correcto en: ' + InstalledYears + #13#10 +
               'FALLO en: ' + FailedYears + #13#10#13#10 +
               'Donde fallo un ano se restauro lo que hubiera antes: ningun Revit se quedo sin complemento. ' +
               'La causa habitual es que Revit siga abierto reteniendo los archivos.' + #13#10#13#10 +
               'Cierra todos los Revit y vuelve a ejecutar este instalador.',
               'The add-in was NOT fully installed.' + #13#10#13#10 +
               'Succeeded for: ' + InstalledYears + #13#10 +
               'FAILED for: ' + FailedYears + #13#10#13#10 +
               'Where a year failed, whatever was installed before it has been put back - no Revit has been left ' +
               'without an add-in. The usual cause is Revit still running and holding the files open.' + #13#10#13#10 +
               'Close every Revit and run this installer again.'),
             mbError, MB_OK)
    else
      { THE NEXT STEP DEPENDS ON WHICH CLIENT YOU USE, and the shortcut names here
        are the names the Start menu really carries. The old text named one that
        did not exist, so following it exactly led nowhere. }
      { The one manual step left is Claude Desktop, because its extension is
        installed from inside the app and there is no documented command for it.
        So the file and the sheet that explains it are put in a folder of the
        user's own and Explorer is already open on them when this is read. }
      if HandedOver then
        MsgBox(L('Complemento instalado para Revit: ' + InstalledYears + #13#10#13#10 +
                 'Reinicia Revit para cargarlo.' + #13#10#13#10 +
                 'Claude Code y Codex quedaron configurados solos. No tienes que ejecutar nada para ellos.' + #13#10#13#10 +
                 'ChatGPT Work NO queda conectado con esta instalacion: necesita un tunel, una clave y una app en tu cuenta de OpenAI, ' +
                 'y el cliente completo de OpenAI (tunnel-client-v<version>-windows-amd64.zip, o -arm64.zip en equipos ARM, sin "runtime" en el nombre). ' +
                 'El asistente client-tools\chatgpt-tunnel.ps1 -Status te dice cada paso.' + #13#10#13#10 +
                 'Falta un paso, y es solo para Claude Desktop: su extension se instala desde dentro de la propia app.' + #13#10#13#10 +
                 'Acabo de abrirte esta carpeta con el archivo y las instrucciones en PDF:' + #13#10 +
                 '      ' + HandoverFolder + #13#10#13#10 +
                 'Abre el PDF y sigue los seis pasos. Son tres minutos.',
                 'Add-in deployed for Revit: ' + InstalledYears + #13#10#13#10 +
                 'Restart Revit to load it.' + #13#10#13#10 +
                 'Claude Code and Codex were configured for you. There is nothing to run for them.' + #13#10#13#10 +
                 'ChatGPT Work is NOT connected by this installation: it needs a tunnel, a key and an app in your OpenAI account, ' +
                 'and OpenAI''s full client (tunnel-client-v<version>-windows-amd64.zip, or -arm64.zip on ARM machines, with no "runtime" in the name). ' +
                 'The helper client-tools\chatgpt-tunnel.ps1 -Status names every step.' + #13#10#13#10 +
                 'One step is left, and only for Claude Desktop: its extension is installed from inside the app itself.' + #13#10#13#10 +
                 'This folder is now open, with the file and the instructions as a PDF:' + #13#10 +
                 '      ' + HandoverFolder + #13#10#13#10 +
                 'Open the PDF and follow the six steps. It takes three minutes.'),
               mbInformation, MB_OK)
      else
        MsgBox(L('Complemento instalado para Revit: ' + InstalledYears + #13#10#13#10 +
                 'Reinicia Revit para cargarlo.' + #13#10#13#10 +
                 'Claude Code y Codex quedaron configurados solos.' + #13#10#13#10 +
                 'ChatGPT Work NO queda conectado con esta instalacion: necesita un tunel, una clave y una app en tu cuenta de OpenAI, ' +
                 'y el cliente completo de OpenAI (tunnel-client-v<version>-windows-amd64.zip, o -arm64.zip en equipos ARM, sin "runtime" en el nombre). ' +
                 'El asistente client-tools\chatgpt-tunnel.ps1 -Status te dice cada paso.' + #13#10#13#10 +
                 'NO pude dejarte la extension de Claude Desktop en Documentos. La tienes aqui:' + #13#10 +
                 '      ' + ExpandConstant('{app}\server\integrations\claude-desktop') + #13#10#13#10 +
                 'Esa carpeta esta oculta para el Explorador: copiala a Documentos antes de instalarla en Claude Desktop.',
                 'Add-in deployed for Revit: ' + InstalledYears + #13#10#13#10 +
                 'Restart Revit to load it.' + #13#10#13#10 +
                 'Claude Code and Codex were configured for you.' + #13#10#13#10 +
                 'ChatGPT Work is NOT connected by this installation: it needs a tunnel, a key and an app in your OpenAI account, ' +
                 'and OpenAI''s full client (tunnel-client-v<version>-windows-amd64.zip, or -arm64.zip on ARM machines, with no "runtime" in the name). ' +
                 'The helper client-tools\chatgpt-tunnel.ps1 -Status names every step.' + #13#10#13#10 +
                 'The Claude Desktop extension could NOT be placed in Documents. It is here:' + #13#10 +
                 '      ' + ExpandConstant('{app}\server\integrations\claude-desktop') + #13#10#13#10 +
                 'Explorer hides that folder: copy the file out to Documents before installing it in Claude Desktop.'),
               mbError, MB_OK);
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
