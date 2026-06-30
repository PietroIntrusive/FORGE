# Forge — Instalador

Empacota a Forja (daemon + UI + dashboard + perfis) num único `forge-setup.exe`
para Windows x64, com o runtime do **WebView2** garantido na instalação.

## Pré-requisitos (máquina de build)

- **.NET SDK 10** (`dotnet --version` ≥ 10) — já usado para compilar o projeto.
- **Inno Setup 6** — https://jrsoftware.org/isdl.php (fornece o `ISCC.exe`).

## Build em dois passos

```powershell
# 1. Empacota o payload self-contained em installer\dist
pwsh installer\publish.ps1

# 2. Compila o instalador → installer\Output\forge-setup.exe
&  "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"  installer\forge.iss
```

`publish.ps1` publica **self-contained win-x64**: o usuário final **não** precisa ter
.NET instalado. Os dois executáveis (`forge-ui.exe` e `ForgeSentinel.exe`) ficam na
mesma pasta de propósito — a UI encontra o daemon ao seu lado.

## WebView2 (runtime de renderização)

O dashboard roda dentro do WebView2. O instalador detecta o runtime Evergreen no
registro (`EdgeUpdate\Clients`) e, se faltar:

1. usa o bootstrapper **bundlado** em `installer\redist\MicrosoftEdgeWebview2Setup.exe`
   (instalação 100% offline), se você o colocar lá; **ou**
2. **baixa** o oficial da Microsoft no momento da instalação (precisa de internet).

Para um instalador offline, baixe o
[Evergreen Bootstrapper](https://developer.microsoft.com/microsoft-edge/webview2/)
e salve em `installer\redist\MicrosoftEdgeWebview2Setup.exe` antes do passo 2.

## O que o setup faz

| Etapa | Detalhe |
|---|---|
| Instala em | `C:\Program Files\Forge` (exige admin — a Forja mexe em energia/serviços) |
| Atalhos | Menu Iniciar sempre; Área de Trabalho opcional |
| Autostart | Opcional — `HKCU\…\Run` (a Forja vive na bandeja e sobe o daemon sozinha) |
| WebView2 | Garante o runtime conforme acima |
| Desinstalar | Remove tudo; some o autostart |

## Autostart do motor (zero-UAC)

A tarefa **"Manter a Forja pronta"** (marcada por padrão) registra o daemon como
**tarefa de logon com privilégio máximo** (`schtasks /sc onlogon /rl highest`) e já o
inicia no fim da instalação. Efeito: o motor sobe elevado no logon **sem pedir UAC**, e
quando a UI abre o daemon já responde no loopback — abertura sem nenhum prompt.

Se o usuário desmarcar essa tarefa, nada quebra: na primeira vez que a Forja abre em
cada sessão, a própria UI sobe o motor com **um** aviso do Windows (UAC). A UI segue
`asInvoker` — nunca eleva; quem detém privilégio é só o motor.

## Notas / próximos passos

- Evoluir a tarefa de logon para um **serviço do Windows** gerenciado (start no boot,
  antes do login, com recuperação automática) é o degrau seguinte — exige o daemon
  responder ao SCM (`ServiceBase`/`UseWindowsService`).
- Publicação é self-contained (não-AOT). Ligar NativeAOT no daemon reduz tamanho,
  mas WebView2+WinForms na UI não casam com AOT — manter a UI como JIT.
- Assinatura de código (Authenticode) não está no fluxo; sem ela o SmartScreen
  avisa na primeira execução. Adicionar `SignTool` é recomendado antes de distribuir.
