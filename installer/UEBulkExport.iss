#define AppName "UEBulkExport"
#define AppPublisher "UEBulkExport contributors"
#define AppUrl "https://github.com/tavridabless/UEBulkExport"

#ifndef AppVersion
  #define AppVersion "2.1.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\staging\UEBulkExport"
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

[Setup]
AppId={{6D4C1463-C986-4B7A-A2EB-61C9FF840F2C}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
SetupIconFile=..\src\UEBulkExport\Assets\app.ico
; Installer artwork, generated from installer/branding by installer/branding/generator (run it
; before compiling; the release workflow does). Each wildcard lists one image per display scale;
; Setup picks the best fit, so the artwork stays sharp from 100 % to 250 %.
WizardImageFile=branding\wizard-image-*.png
WizardSmallImageFile=branding\wizard-small-*.png
WizardBackImageFile=branding\wizard-back-*.png
UninstallDisplayIcon={app}\UEBulkExport.exe
OutputDir={#OutputDir}
OutputBaseFilename=UEBulkExport-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Types]
Name: "full"; Description: "{cm:FullInstallation}"
Name: "compact"; Description: "{cm:CompactInstallation}"
Name: "custom"; Description: "{cm:CustomInstallation}"; Flags: iscustom

[Components]
Name: "core"; Description: "{cm:ComponentCore}"; Types: full compact custom; Flags: fixed
Name: "native"; Description: "{cm:ComponentNative}"; Types: full custom
Name: "native\animations"; Description: "{cm:ComponentAnimations}"; Types: full custom
Name: "native\textures"; Description: "{cm:ComponentTextures}"; Types: full custom
Name: "native\compression"; Description: "{cm:ComponentCompression}"; Types: full custom
Name: "documentation"; Description: "{cm:ComponentDocumentation}"; Types: full custom
Name: "tools"; Description: "{cm:ComponentTools}"; Types: full custom

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Artwork used by Setup itself: extracted to a temporary folder, never installed. Listed first
; because solid compression makes later entries slower to extract.
Source: "branding\wizard-install-*.png"; Flags: dontcopy noencryption
Source: "branding\wizard-back-*.png"; Flags: dontcopy noencryption

; The GUI, CLI, managed runtime and all libraries required to start the application.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "CUE4Parse-Natives.dll,Detex.dll,oodle-data-shared.dll,zlib-ng2.dll,README.md,README.ru.md,LICENSE,NOTICE,THIRD-PARTY-NOTICES.md,CHANGELOG.md,docs\*,tools\*"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: core

; Optional native feature providers. They are selected by the default Full installation.
Source: "{#SourceDir}\CUE4Parse-Natives.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist; Components: native\animations
Source: "{#SourceDir}\Detex.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist; Components: native\textures
Source: "{#SourceDir}\oodle-data-shared.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist; Components: native\compression
Source: "{#SourceDir}\zlib-ng2.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist; Components: native\compression

; Offline reference material and the UE4SS mappings helper.
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist; Components: documentation
Source: "{#SourceDir}\README.ru.md"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist; Components: documentation
Source: "{#SourceDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist; Components: documentation
Source: "{#SourceDir}\NOTICE"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist; Components: documentation
Source: "{#SourceDir}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist; Components: documentation
Source: "{#SourceDir}\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist; Components: documentation
Source: "{#SourceDir}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Components: documentation
Source: "{#SourceDir}\tools\*"; DestDir: "{app}\tools"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Components: tools

[Icons]
Name: "{group}\UEBulkExport"; Filename: "{app}\UEBulkExport.exe"; WorkingDir: "{app}"
Name: "{group}\UEBulkExport CLI"; Filename: "{app}\UEBulkExport.Cli.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\UEBulkExport"; Filename: "{app}\UEBulkExport.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\UEBulkExport.exe"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[CustomMessages]
english.FullInstallation=Full installation (recommended)
english.CompactInstallation=Core application only
english.CustomInstallation=Custom installation
english.ComponentCore=UEBulkExport GUI, CLI and required .NET runtime
english.ComponentNative=Native export dependencies
english.ComponentAnimations=ACL animation decompression
english.ComponentTextures=Additional texture decoding
english.ComponentCompression=Oodle and zlib-ng container decompression
english.ComponentDocumentation=Offline documentation and licences
english.ComponentTools=UE4SS mappings helper tools

russian.FullInstallation=Полная установка (рекомендуется)
russian.CompactInstallation=Только основное приложение
russian.CustomInstallation=Выборочная установка
russian.ComponentCore=Интерфейс UEBulkExport, CLI и обязательная среда .NET
russian.ComponentNative=Нативные зависимости экспорта
russian.ComponentAnimations=Распаковка ACL-анимаций
russian.ComponentTextures=Дополнительное декодирование текстур
russian.ComponentCompression=Распаковка контейнеров Oodle и zlib-ng
russian.ComponentDocumentation=Офлайн-документация и лицензии
russian.ComponentTools=Инструменты UE4SS для получения mappings

[Code]
// While files are copied the wizard shows the hero artwork; every other page keeps the regular
// background. WizardSetBackImage swaps the image at runtime and picks the best size for the DPI.
var
  InstallArt: TArrayOfGraphic;
  PageArt: TArrayOfGraphic;

procedure LoadArt(const Pattern: String; var Images: TArrayOfGraphic);
var
  FindRec: TFindRec;
  Count: Integer;
begin
  ExtractTemporaryFiles('{tmp}\' + Pattern);
  Count := 0;
  if FindFirst(ExpandConstant('{tmp}\' + Pattern), FindRec) then
  try
    repeat
      SetLength(Images, Count + 1);
      Images[Count] := TPngImage.Create;
      Images[Count].LoadFromFile(ExpandConstant('{tmp}\') + FindRec.Name);
      Count := Count + 1;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

procedure FreeArt(var Images: TArrayOfGraphic);
var
  I: Integer;
begin
  for I := 0 to GetArrayLength(Images) - 1 do
    Images[I].Free;
  SetLength(Images, 0);
end;

procedure InitializeWizard;
begin
  LoadArt('wizard-install-*.png', InstallArt);
  LoadArt('wizard-back-*.png', PageArt);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpInstalling then
    WizardSetBackImage(InstallArt, True, True, 255)
  else if CurPageID = wpFinished then
    WizardSetBackImage(PageArt, True, True, 255);
end;

procedure DeinitializeSetup;
begin
  FreeArt(InstallArt);
  FreeArt(PageArt);
end;
