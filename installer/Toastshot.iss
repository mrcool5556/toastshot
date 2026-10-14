; Toastshot's installer: one ToastshotSetup.exe, per user (no admin), into %LOCALAPPDATA%\Programs\Toastshot.
; Built by installer\build.ps1, which passes the version and the published Toastshot.exe:
;   ISCC.exe /DAppVersion=0.4.0 /DSourceExe=..\dist\publish\Toastshot.exe installer\Toastshot.iss
; The in-app updater runs it with /VERYSILENT; it closes the running copy and starts the new one.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\dist\publish\Toastshot.exe"
#endif

[Setup]
; Never change the AppId: it is how Windows knows a new setup is an update of the installed Toastshot.
AppId={{D7F9AE48-D6E4-4B1A-A430-2B2E554AFF42}
AppName=Toastshot
AppVersion={#AppVersion}
AppVerName=Toastshot {#AppVersion}
AppPublisher=Toastshot
AppPublisherURL=https://github.com/mrcool5556/toastshot
AppSupportURL=https://github.com/mrcool5556/toastshot/issues
AppUpdatesURL=https://github.com/mrcool5556/toastshot/releases
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Toastshot setup
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Toastshot
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=ToastshotSetup
SetupIconFile=..\src\Toastshot\Toastshot.ico
UninstallDisplayIcon={app}\Toastshot.exe
UninstallDisplayName=Toastshot
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Toastshot keeps running in the notification area, so setup closes it itself (see [Code]).
CloseApplications=no

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Toastshot"; Filename: "{app}\Toastshot.exe"

[Registry]
; Toastshot writes its own "Start with Windows" entry; uninstalling removes it. Settings stay in %LOCALAPPDATA%\Toastshot.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Toastshot"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Toastshot.exe"; Description: "Start Toastshot"; Flags: nowait postinstall skipifsilent
; An update (silent) starts the new version straight away.
Filename: "{app}\Toastshot.exe"; Flags: nowait; Check: WizardSilent

[Code]
procedure CloseToastshot();
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Toastshot.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(300);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseToastshot();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Versions before 0.4.0 installed with PowerShell scripts: drop their Apps entry and uninstall script.
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\Toastshot');
    DeleteFile(ExpandConstant('{app}\uninstall.ps1'));
  end;
end;

function InitializeUninstall(): Boolean;
begin
  CloseToastshot();
  Result := True;
end;
