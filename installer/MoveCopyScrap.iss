; ============================================================================
;  MoveCopyScrap - Inno Setup script
;
;  Compiled by build.ps1 -Installer, which passes the payload and output
;  folders in, so nothing here has to be kept in step with CMake by hand:
;
;      ISCC /DSourceDir=<dist folder> /DOutputDir=<build\installer> MoveCopyScrap.iss
;
;  Both have defaults, so opening this file in the Inno Setup IDE and pressing
;  F9 works too, as long as the app has been built and installed first.
;
;  Requires Inno Setup 6.3 or newer for the x64compatible architecture
;  identifier. build.ps1 installs the current release via winget.
; ============================================================================

#define AppName        "MoveCopyScrap"
#define AppPublisher   "MoveCopyScrap"
#define AppExeName     "MoveCopyScrap.exe"

; Where the built application is. Default matches CMAKE_INSTALL_PREFIX in
; CMakePresets.json, i.e. `cmake --install build` with the flat layout.
#ifndef SourceDir
  #define SourceDir "..\dist"
#endif

#ifndef OutputDir
  #define OutputDir "..\build\installer"
#endif

#define IconFile "..\assets\MoveCopyScrap.ico"

; Fail early and clearly rather than producing an installer with no payload.
#if !FileExists(AddBackslash(SourceDir) + AppExeName)
  #error Build the application first: run build.bat, or pass /DSourceDir=<folder containing MoveCopyScrap.exe>
#endif

; Single source of truth for the version: the .exe, which CMake stamps from
; project(VERSION). GetVersionNumbersString yields four parts ("1.0.0.0"); the
; file name reads better with three, so the last one is trimmed off.
; (GetVersionNumbersString is the current name; GetFileVersion still works but
; warns.)
#define FullVersion  GetVersionNumbersString(AddBackslash(SourceDir) + AppExeName)
#define ShortVersion Copy(FullVersion, 1, RPos(".", FullVersion) - 1)

[Setup]
; Never change AppId: it is what lets an upgrade replace the previous install
; and what Add/Remove Programs keys off.
AppId={{0D2E3A86-8385-462B-9E88-FB26AD94FB9B}
AppName={#AppName}
AppVersion={#ShortVersion}
AppVerName={#AppName} {#ShortVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#FullVersion}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; Per-user by default, so a normal install raises no UAC prompt at all and
; lands in %LOCALAPPDATA%\Programs. The dialog still offers all-users for
; anyone who wants it, and {autopf} follows whichever they pick.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; The app is a 64-bit self-contained .NET publish; without this, setup runs
; 32-bit and {autopf} would resolve to "Program Files (x86)" for an all-users
; install.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; ~180 MB of .NET and Windows App SDK binaries, which compress well - expect
; the installer to come out around a third of that. Solid compression helps a
; lot here because so many of the DLLs resemble each other.
Compression=lzma2/max
SolidCompression=yes

; Uses the Restart Manager to offer to close a running copy instead of failing
; on locked files - which matters here, since MoveCopyScrap.exe and every DLL
; beside it are held open while the app runs.
CloseApplications=yes
RestartApplications=no

SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} {#ShortVersion}
WizardStyle=modern

OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#ShortVersion}-Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole published folder. recursesubdirs/createallsubdirs matter because a
; self-contained publish is not flat - it carries runtime subfolders.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// Marks live outside the install folder, in %LOCALAPPDATA%\MoveCopyScrap, so
// they survive an uninstall by default - which is right for an upgrade, and
// wrong if someone is really finished with the app. So ask, once, and default
// to keeping them. SuppressibleMsgBox takes the default under /SILENT.
// NOTE: no line below may *begin* with '#'. ISPP scans every line for a
// preprocessor directive before Pascal ever sees it, so a continuation line
// starting with #13#10 is read as the directive "13" and aborts the compile
// with "Unknown preprocessor directive". Hence the separate Prompt variable
// and the breaks placed after the '+'.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir, Prompt: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\{#AppName}');
    if DirExists(DataDir) then
    begin
      Prompt := 'Also delete your saved marks?' + #13#10 + #13#10 +
        DataDir + #13#10 + #13#10 +
        'Choose No if you are reinstalling or upgrading.';
      if SuppressibleMsgBox(Prompt, mbConfirmation, MB_YESNO, IDNO) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
