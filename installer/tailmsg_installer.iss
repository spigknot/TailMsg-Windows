; TailMsg Windows — instalador offline (Inno Setup 6)
; O pacote full é extraído pelo build-installer.ps1 antes da compilação.
#ifndef AppVersion
  #define AppVersion "dev"
#endif

[Setup]
AppId={{D4A0F3E7-2F96-4E55-9B1F-7A4E0A8D6C31}
AppName=TailMsg
AppVersion={#AppVersion}
AppPublisher=TailMsg
DefaultDirName={autopf}\TailMsg
DefaultGroupName=TailMsg
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x86compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=6.1
SetupIconFile=..\assets\ninja.ico
OutputDir=..\release\generated\{#AppVersion}
OutputBaseFilename=setup_tailmsg_{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\TailMsg.exe
SetupLogging=yes

[Files]
; Árvore completa do pacote full validado.
Source: "..\release\generated\{#AppVersion}\package\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Dirs]
; O updater precisa substituir os arquivos na pasta instalada sem exigir
; elevação a cada atualização automática.
Name: "{app}"; Permissions: users-modify

[Registry]
; Mantém a inicialização em segundo plano também para uma instalação nova.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TailMsg"; ValueData: "{app}\TailMsg.exe --background"; Flags: uninsdeletevalue

[Icons]
Name: "{autodesktop}\TailMsg"; Filename: "{app}\TailMsg.exe"; WorkingDir: "{app}"; Comment: "Mensageiro interno TailMsg"
Name: "{autoprograms}\TailMsg\TailMsg"; Filename: "{app}\TailMsg.exe"; WorkingDir: "{app}"; Comment: "Mensageiro interno TailMsg"
Name: "{autodesktop}\TailMsgUpdater"; Filename: "{app}\TailMsgUpdater.exe"; WorkingDir: "{app}"; Comment: "Atualizar ou reparar o TailMsg"
Name: "{autoprograms}\TailMsg\TailMsgUpdater"; Filename: "{app}\TailMsgUpdater.exe"; WorkingDir: "{app}"; Comment: "Atualizar ou reparar o TailMsg"
Name: "{autoprograms}\TailMsg\Desinstalar TailMsg"; Filename: "{uninstallexe}"

[Run]
; O instalador já é elevado; o script configura as portas TCP/UDP do app.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\firewall.ps1"" -ProgramPath ""{app}\TailMsg.exe"""; StatusMsg: "Configurando o firewall do TailMsg..."; Flags: runhidden waituntilterminated
Filename: "{app}\TailMsg.exe"; Description: "Abrir o TailMsg agora"; Flags: nowait postinstall skipifsilent
