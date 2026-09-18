; PierCam installer — Inno Setup 6/7.
;
; Compile with publish.ps1 -Installer, which publishes the app first and passes the version in:
;   ISCC.exe /DAppVersion=1.0 /DStageDir=..\dist\PierCam-1.0-win-x64 installer\PierCam.iss
;
; Installs per-user by default, into %LocalAppData%\Programs\PierCam, so it raises no UAC
; prompt at all. That is not just convenience: observatory machines are often driven remotely
; by an account without administrator rights, and an installer that cannot complete there is
; worse than a zip. Anyone who does want it machine-wide can pick that in the first dialog.

#ifndef AppVersion
  #define AppVersion "1.0"
#endif
#ifndef StageDir
  #define StageDir "..\dist\PierCam-" + AppVersion + "-win-x64"
#endif

#define AppName "PierCam"
#define AppPublisher "YoruDev-Ryland"
#define AppUrl "https://github.com/YoruDev-Ryland/PierCam"
#define AppExe "PierCam.exe"

[Setup]
; Never change AppId: it is what lets a later version recognise and upgrade this one.
AppId={{8F3C21B4-9D7E-4A16-B0C5-2E6F1A4D8B73}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}.0.0
VersionInfoDescription=PierCam — dusk-to-dawn timelapse recorder

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes

; Per-user unless the user asks otherwise in the privileges dialog.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; .NET 8 needs Windows 10 1809 or later; the ASI SDK is 64-bit only.
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

LicenseFile=..\LICENSE
InfoAfterFile={#StageDir}\READ-ME-FIRST.txt
SetupIconFile=..\piercam.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}

OutputDir=..\dist
OutputBaseFilename=PierCam-{#AppVersion}-Setup
; The payload is a ~250 MB self-contained exe plus ffmpeg, both of which compress well.
; LZMA2 at max costs a minute of build time and saves well over a hundred megabytes.
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Shut a running PierCam down cleanly rather than failing on a locked file.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#StageDir}\{#AppExe}";           DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\tools\ffmpeg.exe";    DestDir: "{app}\tools"; Flags: ignoreversion
Source: "{#StageDir}\READ-ME-FIRST.txt";   DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\THIRD-PARTY.txt";     DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE";                      DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";                  Filename: "{app}\{#AppExe}"
Name: "{group}\Licence and third-party";     Filename: "{app}\THIRD-PARTY.txt"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";            Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // PierCam's "start with Windows" checkbox writes this itself, and treats the registry as
    // the record of truth. Leaving a Run entry pointing at an executable we just deleted would
    // mean a failed launch every logon, so it goes with the app. Settings and the timelapse
    // library are deliberately left alone: the library in particular may be months of nights,
    // and it lives wherever the user pointed it, not in the install directory.
    //
    // (Line comments, not brace comments - Inno's preprocessor treats { } as a comment but a
    //  constant like {app} inside one closes it early and the rest becomes syntax errors.)
    RegDeleteValue(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Run', 'PierCam');
  end;
end;
