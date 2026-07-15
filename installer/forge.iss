; Forge — instalador (Inno Setup 6).
;
; Antes de compilar: rode  installer\publish.ps1  para gerar installer\dist.
; Compile este .iss com o Inno Setup (ISCC.exe forge.iss) — gera Output\forge-setup.exe.
;
; O que ele faz:
;   - instala o payload self-contained (daemon + UI + design + profiles) em Program Files\Forge
;   - garante o runtime do WebView2 (Evergreen): se ausente, roda o bootstrapper
;     (bundlado em redist\ se existir, senão baixa o oficial da Microsoft)
;   - cria atalhos no Menu Iniciar e, opcionalmente, na Área de Trabalho
;   - opcionalmente registra a Forja para iniciar com o Windows (HKCU\...\Run)

#define AppName "Forge"
#define AppVersion "1.1.2"
#define AppPublisher "Forge"
#define AppExe "forge-ui.exe"

[Setup]
AppId={{B7F4B3B2-7C2E-4E2A-9C5E-F0A6E2A8C635}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=Output
OutputBaseFilename=forge-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; A Forja toca em energia/serviços (via daemon elevado) e instala em Program Files:
; exige admin.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na Área de Trabalho"; GroupDescription: "Atalhos:"
; Recomendado: deixa o motor da Forja sempre pronto. Sem isso, a primeira abertura
; da Forja em cada sessão pede uma confirmação do Windows (UAC) para subir o motor.
; Consentimento explícito: o texto diz o QUE roda em segundo plano e onde desligar.
Name: "daemontask"; Description: "Iniciar o motor da Forja com o Windows — vigia regressões em segundo plano (recomendado; desligue quando quiser em Ajustes > Inicialização)"; GroupDescription: "Inicialização:"
Name: "startup"; Description: "Abrir a janela da Forja junto com o Windows"; GroupDescription: "Inicialização:"; Flags: unchecked

[InstallDelete]
; Upgrade limpo: o payload self-contained muda de arquivos entre versões — DLL
; órfã da versão anterior não pode sobrar carregável. Só payload mora em {app};
; dados do usuário (baseline, histórico, ajustes) vivem no perfil/%LOCALAPPDATA%.
Type: filesandordirs; Name: "{app}"

[Files]
; Payload gerado por publish.ps1.
Source: "dist\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; Bootstrapper do WebView2 bundlado (opcional). Se você baixar o
; MicrosoftEdgeWebview2Setup.exe e colocar em installer\redist\, ele entra aqui e o
; instalador funciona 100% offline. Sem ele, o setup baixa sob demanda (ver [Code]).
Source: "redist\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: dontcopy skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Autostart opcional (por usuário). A Forja vive na bandeja; sobe o daemon sozinha.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "Forge"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
; Zero-UAC: registra o motor (daemon) como tarefa de logon com privilégio máximo e já
; o inicia. Quando a UI abre, o motor já responde no loopback — sem prompt do Windows.
; A UI continua asInvoker (nunca eleva): quem detém privilégio é só o motor.
Filename: "{sys}\schtasks.exe"; \
  Parameters: "/create /f /tn ""ForgeSentinel"" /tr ""\""{app}\ForgeSentinel.exe\"" serve"" /sc onlogon /rl highest"; \
  Flags: runhidden; Tasks: daemontask
Filename: "{sys}\schtasks.exe"; Parameters: "/run /tn ""ForgeSentinel"""; \
  Flags: runhidden; Tasks: daemontask
Filename: "{app}\{#AppExe}"; Description: "Abrir A Forja agora"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove a tarefa de logon do motor ao desinstalar.
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /f /tn ""ForgeSentinel"""; \
  Flags: runhidden; RunOnceId: "DelForgeTask"

[Code]
const
  // Cliente Evergreen do WebView2 Runtime no EdgeUpdate.
  WV2_CLIENT = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  // Link oficial do bootstrapper (usado só se não houver bundle em redist\).
  WV2_URL = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';

function PvNonEmpty(Root: Integer; Key: string): Boolean;
var
  pv: string;
begin
  Result := RegQueryStringValue(Root, Key, 'pv', pv)
            and (pv <> '') and (pv <> '0.0.0.0');
end;

// Runtime do WebView2 já instalado? Cobre system-wide (HKLM, com e sem WOW6432Node)
// e per-user (HKCU) — qualquer um basta.
function WebView2Installed: Boolean;
begin
  Result :=
    PvNonEmpty(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WV2_CLIENT) or
    PvNonEmpty(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WV2_CLIENT) or
    PvNonEmpty(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\' + WV2_CLIENT);
end;

procedure EnsureWebView2;
var
  exe: string;
  code: Integer;
begin
  if WebView2Installed then
    Exit;

  exe := '';
  // 1) Bundle offline, se presente.
  try
    ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
    exe := ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe');
  except
    exe := '';
  end;

  // 2) Senão, baixa o oficial (precisa de internet no momento da instalação).
  if (exe = '') or (not FileExists(exe)) then
  begin
    exe := ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe');
    try
      DownloadTemporaryFile(WV2_URL, 'MicrosoftEdgeWebview2Setup.exe', '', nil);
    except
      MsgBox('Não consegui obter o runtime do WebView2 automaticamente.' + #13#10 +
             'A Forja só renderiza com ele instalado.' + #13#10 +
             'Instale manualmente em: https://developer.microsoft.com/microsoft-edge/webview2/',
             mbInformation, MB_OK);
      Exit;
    end;
  end;

  // Instalação silenciosa do runtime.
  if FileExists(exe) then
    Exec(exe, '/silent /install', '', SW_SHOW, ewWaitUntilTerminated, code);
end;

// Para os processos da versão instalada antes de copiar por cima: exe travado
// em execução = upgrade quebrado no meio (feedback do beta).
procedure KillRunning;
var
  code: Integer;
begin
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/end /tn "ForgeSentinel"', '',
       SW_HIDE, ewWaitUntilTerminated, code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im ForgeSentinel.exe', '',
       SW_HIDE, ewWaitUntilTerminated, code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im forge-ui.exe', '',
       SW_HIDE, ewWaitUntilTerminated, code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    KillRunning;
  if CurStep = ssPostInstall then
    EnsureWebView2;
end;

// Instalação por cima de versão anterior: avisa que é um upgrade em vez de
// seguir mudo (feedback do beta v1.1.1). A chave de desinstalação usa o AppId
// com sufixo _is1; o modo 64-bit do setup lê a view 64-bit do registro.
function InitializeSetup(): Boolean;
var
  prev: string;
begin
  Result := True;
  if RegQueryStringValue(HKLM,
       'Software\Microsoft\Windows\CurrentVersion\Uninstall\{B7F4B3B2-7C2E-4E2A-9C5E-F0A6E2A8C635}_is1',
       'DisplayVersion', prev)
     and (prev <> '') and (prev <> '{#AppVersion}') then
    MsgBox('Forge v' + prev + ' detectado neste PC.' + #13#10 +
           'Ele será atualizado para a v{#AppVersion} — suas configurações e baseline são mantidos.',
           mbInformation, MB_OK);
end;
