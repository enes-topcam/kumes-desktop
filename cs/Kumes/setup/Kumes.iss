; Kümes kurulum dosyası (Inno Setup 6). Yönetici izni istemez: kullanıcının kendi klasörüne kurar.
; Derleme (yayın paketi klasörü hazırlandıktan sonra):
;   ISCC.exe /DAppVer=1.2 /DSrc=<Kumes.exe, LICENSE.txt ve tools\vrf\ olan klasör> /DOut=<çıktı klasörü> Kumes.iss
; Çıktı: Kumes-<sürüm>-setup.exe

#ifndef AppVer
  #error "AppVer verilmeli: /DAppVer=1.2"
#endif
#ifndef Src
  #error "Src verilmeli: /DSrc=<paket klasörü>"
#endif
#ifndef Out
  #define Out "."
#endif

[Setup]
; AppId değişmemeli: yeni sürüm aynı kurulumun üzerine kurulur (veriler %APPDATA%\KumesCS'te, dokunulmaz)
AppId={{6B0C7E52-3F1A-4C8E-9D2B-7A4E1C5F8B30}
AppName=Kümes
AppVersion={#AppVer}
AppVerName=Kümes {#AppVer}
AppPublisher=enes-topcam
AppPublisherURL=https://github.com/enes-topcam/kumes-desktop
AppSupportURL=https://github.com/enes-topcam/kumes-desktop/issues
AppUpdatesURL=https://github.com/enes-topcam/kumes-desktop/releases/latest
DefaultDirName={localappdata}\Programs\Kumes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#Out}
OutputBaseFilename=Kumes-{#AppVer}-setup
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\Kumes.exe
UninstallDisplayName=Kümes
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ShowLanguageDialog=auto

[Languages]
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
tr.DesktopIcon=Masaüstü kısayolu oluştur
en.DesktopIcon=Create a desktop shortcut
tr.LaunchKumes=Kümes'i şimdi aç
en.LaunchKumes=Open Kümes now
tr.DeleteData=Tavuk verileri de silinsin mi?%n%nSteam oturumu, ayarlar ve hazırlanmış tavuk modelleri (%APPDATA%\KumesCS). Hayır dersen yeniden kurunca kaldığın yerden devam eder.
en.DeleteData=Also delete chicken data?%n%nSteam session, settings and prepared chicken models (%APPDATA%\KumesCS). If you choose No, reinstalling continues where you left off.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked

[Files]
Source: "{#Src}\Kumes.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Src}\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Src}\tools\vrf\*"; DestDir: "{app}\tools\vrf"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\Kümes"; Filename: "{app}\Kumes.exe"
Name: "{autodesktop}\Kümes"; Filename: "{app}\Kumes.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Kumes.exe"; Description: "{cm:LaunchKumes}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunName = 'Kumes';

// Açık Kümes dosyaları kilitler: kurmadan / kaldırmadan önce kapat (tek kopya çalışır, adı Kumes.exe)
procedure CloseKumes();
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Kumes.exe /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseKumes();
  Result := '';
end;

// "Windows açılınca başlat" açıksa (ör. önce zip sürümü kullanıldıysa) yeni kurulan exe'yi göstersin
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and RegValueExists(HKCU, RunKey, RunName) then
    RegWriteStringValue(HKCU, RunKey, RunName, '"' + ExpandConstant('{app}\Kumes.exe') + '" --autostart');
end;

function InitializeUninstall(): Boolean;
begin
  CloseKumes();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  RegDeleteValue(HKCU, RunKey, RunName);
  if UninstallSilent then Exit;
  if MsgBox(CustomMessage('DeleteData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(ExpandConstant('{userappdata}\KumesCS'), True, True, True);
end;
