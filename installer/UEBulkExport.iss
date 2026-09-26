#ifndef AppName
  #define AppName "UEBulkExport"
#endif
#define AppPublisher "UEBulkExport contributors"
#define AppUrl "https://github.com/tavridabless/UEBulkExport"
; Never change for releases: Windows and Setup recognise an existing installation by it. Test
; builds pass their own (/DAppGuid=...) so they never touch a real installation.
#ifndef AppGuid
  #define AppGuid "6D4C1463-C986-4B7A-A2EB-61C9FF840F2C"
#endif
; Created by UEBulkExport.exe and UEBulkExport.Cli.exe while they run (see RunningMarker.cs).
#define AppMutexName "UEBulkExport.Running"

#ifndef AppVersion
  #define AppVersion "2.1.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\staging\UEBulkExport"
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

; Every file that ships in the application folder itself. An update removes program files from
; an older version that are no longer on this list, so stale libraries never linger.
#define SourceRoot (Pos(":", SourceDir) > 0 ? SourceDir : AddBackslash(SourcePath) + SourceDir)
#define ShippedFiles "|"
#define FindHandle
#define FindResult
#sub AddShippedFile
  #expr ShippedFiles = ShippedFiles + LowerCase(FindGetFileName(FindHandle)) + "|"
#endsub
#for {FindHandle = FindResult = FindFirst(AddBackslash(SourceRoot) + "*", 0); FindResult; FindResult = FindNext(FindHandle)} AddShippedFile
#if FindHandle
  #expr FindClose(FindHandle)
#endif
#if ShippedFiles == "|"
  #error No files found in the source folder. Publish the application into SourceDir first.
#endif

; Optional native providers are offered only when the build actually contains them; the ones it
; lacks are downloaded by the application on first use, so an empty checkbox would only mislead.
#define HasAcl FileExists(AddBackslash(SourceRoot) + "CUE4Parse-Natives.dll")
#define HasDetex FileExists(AddBackslash(SourceRoot) + "Detex.dll")
#define HasCompression FileExists(AddBackslash(SourceRoot) + "oodle-data-shared.dll") || FileExists(AddBackslash(SourceRoot) + "zlib-ng2.dll")
#define HasNative HasAcl || HasDetex || HasCompression

[Setup]
AppId={{{#AppGuid}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppMutex={#AppMutexName},Global\{#AppMutexName}
; Program Files for everyone, or the user's own programs folder without administrator rights.
; An existing installation keeps its mode, so an update never creates a second copy.
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
UsePreviousPrivileges=yes
DisableWelcomePage=no
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
; .NET 10 runs on Windows 10 1607 and later, and on Windows Server 2012 and later. Setup refuses
; older servers here and older desktop Windows in InitializeSetup.
MinVersion=6.2
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
; The optional "add to PATH" task edits the environment.
ChangesEnvironment=yes
; %TEMP%\Setup Log <date>.txt and the uninstaller's log, for when something needs explaining.
SetupLogging=yes
UninstallLogging=yes
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
#ifdef SignInstaller
; The release workflow defines a "signtool" command (ISCC /S) when a certificate is configured.
SignTool=signtool
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Types]
Name: "full"; Description: "{cm:FullInstallation}"
Name: "compact"; Description: "{cm:CompactInstallation}"
Name: "custom"; Description: "{cm:CustomInstallation}"; Flags: iscustom

[Components]
Name: "core"; Description: "{cm:ComponentCore}"; Types: full compact custom; Flags: fixed
#if HasNative
Name: "native"; Description: "{cm:ComponentNative}"; Types: full custom
#endif
#if HasAcl
Name: "native\animations"; Description: "{cm:ComponentAnimations}"; Types: full custom
#endif
#if HasDetex
Name: "native\textures"; Description: "{cm:ComponentTextures}"; Types: full custom
#endif
#if HasCompression
Name: "native\compression"; Description: "{cm:ComponentCompression}"; Types: full custom
#endif
Name: "documentation"; Description: "{cm:ComponentDocumentation}"; Types: full custom
Name: "tools"; Description: "{cm:ComponentTools}"; Types: full custom

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "addtopath"; Description: "{cm:AddToPath}"; GroupDescription: "{cm:CommandLineGroup}"; Flags: unchecked

[InstallDelete]
; Setup never removes what a component installed earlier when that component is later deselected;
; these entries do, so the folder always matches the current choice.
#if HasAcl
Type: files; Name: "{app}\CUE4Parse-Natives.dll"; Components: not native\animations
#endif
#if HasDetex
Type: files; Name: "{app}\Detex.dll"; Components: not native\textures
#endif
#if HasCompression
Type: files; Name: "{app}\oodle-data-shared.dll"; Components: not native\compression
Type: files; Name: "{app}\zlib-ng2.dll"; Components: not native\compression
#endif
Type: files; Name: "{app}\README.md"; Components: not documentation
Type: files; Name: "{app}\README.ru.md"; Components: not documentation
Type: files; Name: "{app}\LICENSE"; Components: not documentation
Type: files; Name: "{app}\NOTICE"; Components: not documentation
Type: files; Name: "{app}\THIRD-PARTY-NOTICES.md"; Components: not documentation
Type: files; Name: "{app}\CHANGELOG.md"; Components: not documentation
Type: filesandordirs; Name: "{app}\docs"; Components: not documentation
Type: filesandordirs; Name: "{app}\tools"; Components: not tools

[Files]
; Artwork used by Setup itself: extracted to a temporary folder, never installed. Listed first
; because solid compression makes later entries slower to extract.
Source: "branding\wizard-install-*.png"; Flags: dontcopy noencryption
Source: "branding\wizard-back-*.png"; Flags: dontcopy noencryption

; The GUI, CLI, managed runtime and all libraries required to start the application.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "CUE4Parse-Natives.dll,Detex.dll,oodle-data-shared.dll,zlib-ng2.dll,README.md,README.ru.md,LICENSE,NOTICE,THIRD-PARTY-NOTICES.md,CHANGELOG.md,\docs,\tools"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: core

; Optional native feature providers. They are selected by the default Full installation.
#if HasAcl
Source: "{#SourceDir}\CUE4Parse-Natives.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist; Components: native\animations
#endif
#if HasDetex
Source: "{#SourceDir}\Detex.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist; Components: native\textures
#endif
#if HasCompression
Source: "{#SourceDir}\oodle-data-shared.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist; Components: native\compression
Source: "{#SourceDir}\zlib-ng2.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist; Components: native\compression
#endif

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
Name: "{group}\UEBulkExport"; Filename: "{app}\UEBulkExport.exe"; WorkingDir: "{app}"; Comment: "{cm:GuiShortcutComment}"
; A console that stays open with the help on screen and is ready for the next command, instead of
; a window that can only be read and closed.
Name: "{group}\UEBulkExport CLI"; Filename: "{cmd}"; Parameters: "/k ""{app}\UEBulkExport.Cli.exe"" --help"; WorkingDir: "{userdocs}"; IconFilename: "{app}\UEBulkExport.Cli.exe"; Comment: "{cm:CliShortcutComment}"
Name: "{autodesktop}\UEBulkExport"; Filename: "{app}\UEBulkExport.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\UEBulkExport.exe"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Written by Setup after the files were copied, so the uninstaller does not know it otherwise.
Type: files; Name: "{app}\installer.json"

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
english.CommandLineGroup=Command line:
english.AddToPath=Add UEBulkExport CLI to the PATH (run UEBulkExport.Cli from any console)
english.GuiShortcutComment=Export Unreal Engine containers
english.CliShortcutComment=Command prompt with UEBulkExport CLI help
english.TitleUpdate=Update - %1
english.WelcomeUpdateTitle=Update %1
english.WelcomeUpdate=%1 %2 is installed on this computer. Setup will update it to version %3.%n%nYour settings, recent games and downloaded helpers are kept. The installation folder, components and shortcuts stay as they are.%n%nClose %1 before you continue.
english.WelcomeReinstallTitle=Reinstall %1
english.WelcomeReinstall=%1 %2 is already installed. Setup will reinstall it, restoring any missing or damaged files.%n%nOn the next pages you can also change the components and shortcuts. Your settings are kept.
english.WelcomeDowngradeTitle=Replace with an older version
english.WelcomeDowngrade=A newer version, %1 %2, is installed. Setup will replace it with the older version %3.%n%nYour settings are kept, but options added in the newer version will be ignored.
english.DowngradeQuestion=%1 %2 is installed, which is newer than the version in this setup (%3).%n%nReplace it with the older version?
english.ReadyUpdate=Update: %1 → %2
english.ReadyReinstall=Reinstall version %1
english.ReadyDowngrade=Replace version %1 with the older version %2
english.ReadyUpdateHeading=Ready to Update
english.ReadyUpdateDescription=Setup is now ready to update %1 to version %2.
english.ReadyUpdateLabel=Click Update to continue, or click Back if you want to review.
english.ReadyReinstallHeading=Ready to Reinstall
english.ReadyReinstallDescription=Setup is now ready to reinstall %1 %2.
english.ReadyReinstallLabel=Click Reinstall to continue, or click Back if you want to review or change any settings.
english.InstallingUpdateHeading=Updating
english.InstallingUpdateDescription=Please wait while Setup updates %1 on your computer.
english.FinishedUpdateHeading=%1 is up to date
english.ButtonUpdate=&Update
english.ButtonReinstall=&Reinstall
english.FinishedUpdate=%1 has been updated to version %2.
english.OldWindows=UEBulkExport needs Windows 10 version 1607 or later. This computer runs an older version of Windows.
english.RemoveUserData=Also delete your UEBulkExport settings, recent games and downloaded helper libraries?%n%n%1%n%nChoose No to keep them for a later installation.

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
russian.CommandLineGroup=Командная строка:
russian.AddToPath=Добавить UEBulkExport CLI в PATH (запуск UEBulkExport.Cli из любой консоли)
russian.GuiShortcutComment=Экспорт контейнеров Unreal Engine
russian.CliShortcutComment=Командная строка со справкой UEBulkExport CLI
russian.TitleUpdate=Обновление — %1
russian.WelcomeUpdateTitle=Обновление %1
russian.WelcomeUpdate=На компьютере установлен %1 %2. Программа установки обновит его до версии %3.%n%nНастройки, недавние игры и скачанные вспомогательные программы сохранятся. Папка установки, компоненты и ярлыки останутся прежними.%n%nПеред продолжением закройте %1.
russian.WelcomeReinstallTitle=Переустановка %1
russian.WelcomeReinstall=%1 %2 уже установлен. Программа установки переустановит его и восстановит отсутствующие или повреждённые файлы.%n%nНа следующих страницах можно также изменить компоненты и ярлыки. Настройки сохранятся.
russian.WelcomeDowngradeTitle=Замена более старой версией
russian.WelcomeDowngrade=Установлена более новая версия — %1 %2. Программа установки заменит её более старой версией %3.%n%nНастройки сохранятся, но параметры, появившиеся в новой версии, будут проигнорированы.
russian.DowngradeQuestion=Установлен %1 %2 — это новее, чем версия в этом установщике (%3).%n%nЗаменить установленную версию более старой?
russian.ReadyUpdate=Обновление: %1 → %2
russian.ReadyReinstall=Переустановка версии %1
russian.ReadyDowngrade=Замена версии %1 более старой версией %2
russian.ReadyUpdateHeading=Всё готово к обновлению
russian.ReadyUpdateDescription=Программа установки готова обновить %1 до версии %2.
russian.ReadyUpdateLabel=Нажмите «Обновить», чтобы продолжить, или «Назад», если хотите что-то проверить.
russian.ReadyReinstallHeading=Всё готово к переустановке
russian.ReadyReinstallDescription=Программа установки готова переустановить %1 %2.
russian.ReadyReinstallLabel=Нажмите «Установить», чтобы переустановить программу, или «Назад», если хотите проверить или изменить параметры.
russian.InstallingUpdateHeading=Обновление
russian.InstallingUpdateDescription=Пожалуйста, подождите, пока программа установки обновляет %1 на вашем компьютере.
russian.FinishedUpdateHeading=%1 обновлён
russian.ButtonUpdate=&Обновить
russian.ButtonReinstall=&Установить
russian.FinishedUpdate=%1 обновлён до версии %2.
russian.OldWindows=Для UEBulkExport нужна Windows 10 версии 1607 или новее. На этом компьютере установлена более старая версия Windows.
russian.RemoveUserData=Удалить также настройки UEBulkExport, список недавних игр и скачанные вспомогательные библиотеки?%n%n%1%n%nВыберите «Нет», чтобы сохранить их для следующей установки.

[Code]
type
  TInstallKind = (ikFresh, ikUpdate, ikReinstall, ikDowngrade);

const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{#AppGuid}}_is1';
  ShippedFiles = '{#ShippedFiles}';
  MachineEnvironmentKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';
  UserEnvironmentKey = 'Environment';

var
  InstallKind: TInstallKind;
  PreviousVersion: String;
  InstallArt: TArrayOfGraphic;
  PageArt: TArrayOfGraphic;

// ---------------------------------------------------------------- previous installation

// The uninstall entry lives under HKLM for an installation for all users and under HKCU for one
// installed by the user alone; UsePreviousPrivileges has already put Setup in the matching mode.
function UninstallRoot: Integer;
begin
  if IsAdminInstallMode then
    Result := HKLM
  else
    Result := HKCU;
end;

function HasCommandLineSwitch(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

procedure DetectPreviousInstallation;
var
  Installed, Current: Int64;
  Difference: Integer;
begin
  InstallKind := ikFresh;
  PreviousVersion := '';
  if not RegQueryStringValue(UninstallRoot, UninstallKey, 'DisplayVersion', PreviousVersion) or
     (PreviousVersion = '') then
    Exit;

  // A version that cannot be compared (a pre-release tag, say) is treated as an update.
  InstallKind := ikUpdate;
  if StrToVersion(PreviousVersion, Installed) and StrToVersion('{#AppVersion}', Current) then
  begin
    Difference := ComparePackedVersion(Current, Installed);
    if Difference = 0 then
      InstallKind := ikReinstall
    else if Difference < 0 then
      InstallKind := ikDowngrade;
  end;

  Log('Installed version: ' + PreviousVersion + '; this setup: {#AppVersion}; kind: ' +
    IntToStr(Ord(InstallKind)));
end;

// ---------------------------------------------------------------- start-up checks

function IsSupportedWindows: Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  // Servers from 2012 on are allowed by MinVersion; desktop Windows needs 10 1607 (build 14393).
  Result := (Version.ProductType <> VER_NT_WORKSTATION) or (Version.Major > 10) or
    ((Version.Major = 10) and (Version.Build >= 14393));
end;

function InitializeSetup: Boolean;
begin
  Result := True;

  if not IsSupportedWindows then
  begin
    SuppressibleMsgBox(CustomMessage('OldWindows'), mbCriticalError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;

  DetectPreviousInstallation;

  // Going back to an older version is allowed, but never by accident. Unattended installs need
  // /ALLOWDOWNGRADE to do it.
  if (InstallKind = ikDowngrade) and not HasCommandLineSwitch('/ALLOWDOWNGRADE') then
    if SuppressibleMsgBox(FmtMessage(CustomMessage('DowngradeQuestion'), ['{#AppName}', PreviousVersion, '{#AppVersion}']),
         mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) <> IDYES then
    begin
      Log('Downgrade declined.');
      Result := False;
    end;
end;

// ---------------------------------------------------------------- wizard

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

  // The welcome page says what is about to happen to the copy that is already there.
  case InstallKind of
    ikUpdate:
      begin
        WizardForm.Caption := FmtMessage(CustomMessage('TitleUpdate'), ['{#AppName}']);
        WizardForm.WelcomeLabel1.Caption := FmtMessage(CustomMessage('WelcomeUpdateTitle'), ['{#AppName}']);
        WizardForm.WelcomeLabel2.Caption := FmtMessage(CustomMessage('WelcomeUpdate'), ['{#AppName}', PreviousVersion, '{#AppVersion}']);
      end;
    ikReinstall:
      begin
        WizardForm.WelcomeLabel1.Caption := FmtMessage(CustomMessage('WelcomeReinstallTitle'), ['{#AppName}']);
        WizardForm.WelcomeLabel2.Caption := FmtMessage(CustomMessage('WelcomeReinstall'), ['{#AppName}', PreviousVersion]);
      end;
    ikDowngrade:
      begin
        WizardForm.WelcomeLabel1.Caption := CustomMessage('WelcomeDowngradeTitle');
        WizardForm.WelcomeLabel2.Caption := FmtMessage(CustomMessage('WelcomeDowngrade'), ['{#AppName}', PreviousVersion, '{#AppVersion}']);
      end;
  end;
end;

// An update takes everything from the installed copy: the licence was accepted then, and the
// folder, components and shortcuts are reused. A reinstall is where they can be changed.
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if InstallKind = ikFresh then
    Exit;

  case PageID of
    wpLicense, wpSelectDir:
      Result := True;
    wpSelectComponents, wpSelectTasks:
      Result := InstallKind <> ikReinstall;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  Parts: TArrayOfString;
  I: Integer;
begin
  Result := '';
  case InstallKind of
    ikUpdate: Result := FmtMessage(CustomMessage('ReadyUpdate'), [PreviousVersion, '{#AppVersion}']);
    ikReinstall: Result := FmtMessage(CustomMessage('ReadyReinstall'), ['{#AppVersion}']);
    ikDowngrade: Result := FmtMessage(CustomMessage('ReadyDowngrade'), [PreviousVersion, '{#AppVersion}']);
  end;

  // The folder page was skipped, so its summary is missing; the folder is still worth showing.
  if (InstallKind <> ikFresh) and (MemoDirInfo = '') then
    MemoDirInfo := SetupMessage(msgReadyMemoDir) + NewLine + Space + ExpandConstant('{app}');

  SetArrayLength(Parts, 6);
  Parts[0] := MemoUserInfoInfo;
  Parts[1] := MemoDirInfo;
  Parts[2] := MemoTypeInfo;
  Parts[3] := MemoComponentsInfo;
  Parts[4] := MemoGroupInfo;
  Parts[5] := MemoTasksInfo;
  for I := 0 to GetArrayLength(Parts) - 1 do
    if Parts[I] <> '' then
    begin
      if Result <> '' then
        Result := Result + NewLine + NewLine;
      Result := Result + Parts[I];
    end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  // While files are copied the wizard shows the hero artwork; every other page keeps the regular
  // background. WizardSetBackImage swaps the image at runtime and picks the best size for the DPI.
  if CurPageID = wpInstalling then
    WizardSetBackImage(InstallArt, True, True, 255)
  else if CurPageID = wpFinished then
    WizardSetBackImage(PageArt, True, True, 255);

  // Every page of an update or reinstall speaks of that, not of a first installation.
  if CurPageID = wpReady then
    case InstallKind of
      ikUpdate:
        begin
          WizardForm.PageNameLabel.Caption := CustomMessage('ReadyUpdateHeading');
          WizardForm.PageDescriptionLabel.Caption := FmtMessage(CustomMessage('ReadyUpdateDescription'), ['{#AppName}', '{#AppVersion}']);
          WizardForm.ReadyLabel.Caption := CustomMessage('ReadyUpdateLabel');
          WizardForm.NextButton.Caption := CustomMessage('ButtonUpdate');
        end;
      ikReinstall:
        begin
          WizardForm.PageNameLabel.Caption := CustomMessage('ReadyReinstallHeading');
          WizardForm.PageDescriptionLabel.Caption := FmtMessage(CustomMessage('ReadyReinstallDescription'), ['{#AppName}', '{#AppVersion}']);
          WizardForm.ReadyLabel.Caption := CustomMessage('ReadyReinstallLabel');
          WizardForm.NextButton.Caption := CustomMessage('ButtonReinstall');
        end;
    end;

  if (CurPageID = wpInstalling) and (InstallKind = ikUpdate) then
  begin
    WizardForm.PageNameLabel.Caption := CustomMessage('InstallingUpdateHeading');
    WizardForm.PageDescriptionLabel.Caption := FmtMessage(CustomMessage('InstallingUpdateDescription'), ['{#AppName}']);
  end;

  if (CurPageID = wpFinished) and (InstallKind = ikUpdate) then
  begin
    WizardForm.FinishedHeadingLabel.Caption := FmtMessage(CustomMessage('FinishedUpdateHeading'), ['{#AppName}']);
    WizardForm.FinishedLabel.Caption := FmtMessage(CustomMessage('FinishedUpdate'), ['{#AppName}', '{#AppVersion}']);
  end;
end;

// ---------------------------------------------------------------- files

// Removes program files an older version installed that this one no longer ships. Only the
// kinds of files a .NET publish produces are considered, never the uninstaller or anything the
// user might have put next to the program.
procedure RemoveObsoleteProgramFiles;
var
  FindRec: TFindRec;
  AppDir, Name, Extension: String;
begin
  AppDir := ExpandConstant('{app}');
  if not FindFirst(AddBackslash(AppDir) + '*', FindRec) then
    Exit;
  try
    repeat
      if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY = 0 then
      begin
        Name := Lowercase(FindRec.Name);
        Extension := ExtractFileExt(Name);
        if ((Extension = '.dll') or (Extension = '.exe') or (Extension = '.json') or (Extension = '.pdb')) and
           (Pos('unins', Name) <> 1) and (Name <> 'installer.json') and
           (Pos('|' + Name + '|', ShippedFiles) = 0) then
        begin
          if DeleteFile(AddBackslash(AppDir) + FindRec.Name) then
            Log('Removed obsolete file: ' + FindRec.Name)
          else
            Log('Could not remove obsolete file: ' + FindRec.Name);
        end;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

// Tells the application which language the wizard ran in. The application uses it only until
// the user picks a language in its own Settings page.
procedure WriteInstallerDefaults;
var
  Language: String;
begin
  if ActiveLanguage = 'russian' then
    Language := 'ru'
  else
    Language := 'en';
  SaveStringToFile(ExpandConstant('{app}\installer.json'),
    '{ "Language": "' + Language + '" }' + #13#10, False);
end;

// ---------------------------------------------------------------- PATH

procedure EnvironmentKey(var Root: Integer; var Key: String);
begin
  if IsAdminInstallMode then
  begin
    Root := HKLM;
    Key := MachineEnvironmentKey;
  end
  else
  begin
    Root := HKCU;
    Key := UserEnvironmentKey;
  end;
end;

function SamePath(const A, B: String): Boolean;
begin
  Result := CompareText(RemoveBackslashUnlessRoot(Trim(A)), RemoveBackslashUnlessRoot(Trim(B))) = 0;
end;

// Adds or removes the application folder in PATH, leaving every other entry exactly as it was.
procedure UpdatePath(const Add: Boolean);
var
  Root: Integer;
  Key, Path, Entry, Rest, Rebuilt, AppDir: String;
  Found: Boolean;
  Separator: Integer;
begin
  EnvironmentKey(Root, Key);
  AppDir := ExpandConstant('{app}');
  if not RegQueryStringValue(Root, Key, 'Path', Path) then
    Path := '';

  Rebuilt := '';
  Found := False;
  Rest := Path;
  while Rest <> '' do
  begin
    Separator := Pos(';', Rest);
    if Separator = 0 then
    begin
      Entry := Rest;
      Rest := '';
    end
    else
    begin
      Entry := Copy(Rest, 1, Separator - 1);
      Rest := Copy(Rest, Separator + 1, Length(Rest));
    end;

    if Trim(Entry) = '' then
      Continue;
    if SamePath(Entry, AppDir) then
    begin
      Found := True;
      if not Add then
        Continue;
    end;
    if Rebuilt <> '' then
      Rebuilt := Rebuilt + ';';
    Rebuilt := Rebuilt + Entry;
  end;

  if Add and not Found then
  begin
    if Rebuilt <> '' then
      Rebuilt := Rebuilt + ';';
    Rebuilt := Rebuilt + AppDir;
  end;

  if (Add and Found) or (not Add and not Found) then
    Exit;

  if RegWriteExpandStringValue(Root, Key, 'Path', Rebuilt) then
  begin
    if Add then
      Log('Added to PATH: ' + AppDir)
    else
      Log('Removed from PATH: ' + AppDir);
  end
  else
    Log('Could not update PATH.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  case CurStep of
    ssInstall:
      if InstallKind <> ikFresh then
        RemoveObsoleteProgramFiles;
    ssPostInstall:
      begin
        WriteInstallerDefaults;
        // Also removes the entry when the task was deselected in a reinstall.
        UpdatePath(WizardIsTaskSelected('addtopath'));
      end;
  end;
end;

procedure DeinitializeSetup;
begin
  FreeArt(InstallArt);
  FreeArt(PageArt);
end;

// ---------------------------------------------------------------- uninstall

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UserData: String;
begin
  case CurUninstallStep of
    usUninstall:
      UpdatePath(False);
    usPostUninstall:
      begin
        // Settings and downloaded helpers belong to the user and survive by default; removing
        // them is offered, never assumed.
        UserData := ExpandConstant('{localappdata}\UEBulkExport');
        if DirExists(UserData) and not UninstallSilent then
          if MsgBox(FmtMessage(CustomMessage('RemoveUserData'), [UserData]),
               mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
          begin
            if DelTree(UserData, True, True, True) then
              Log('Removed user data: ' + UserData)
            else
              Log('Could not remove all user data: ' + UserData);
          end;
      end;
  end;
end;
