<div align="center">

# Forge

**De jogador pra jogador.** O otimizador de PC para a comunidade gamer — transparente, reversível e de código aberto.

[![License: MIT](https://img.shields.io/badge/license-MIT-f97316)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/windows-10%20%7C%2011-2a2a30)](#requisitos)
[![Telemetry: none](https://img.shields.io/badge/telemetria-zero-6ee7a8)](SECURITY.md)

</div>

---

Forge substitui uma pilha de ferramentas desconexas (MSI Afterburner, HWiNFO, scripts de debloat) por **um único app em que você pode confiar**. Ele diagnostica seu PC, dá uma nota de 0 a 100, e te mostra exatamente o que melhorar — sempre explicando o que faz e deixando você reverter qualquer mudança.

> Quanto mais quente o medidor, mais perto da forma ideal. Otimizar seu PC é como forjar metal: do estado cru e frio até a temperatura de forja.

## Por que o Forge é diferente

- **Score 0–100 com diagnóstico claro** — chega de planilha de números. "Seu PC: 78/100, 3 ajustes pendentes."
- **Sentinela pós-Windows Update** — o Windows Update reverte suas configs. O Forge detecta e te avisa. Ninguém mais faz isso.
- **Ajuste de GPU nativo** — overclock, undervolt e limite de energia via NVAPI, sem instalar mais nada.
- **Auto Game Profile** — detecta o jogo e aplica o preset certo automaticamente.
- **Linha do tempo + card antes/depois** — veja o ganho real ao longo do tempo. Satisfação de ver o efeito.

## Segurança em primeiro lugar

Forge roda um serviço em segundo plano com privilégios de sistema. Por isso, a segurança não é detalhe — é o projeto:

- **Nada de execução de código arbitrário.** Toda mudança no sistema é uma ação fixa e revisada no código. Nem config, nem presets da comunidade conseguem rodar comandos novos.
- **Tudo reversível e registrado.** Cada ajuste guarda o valor antes/depois e pode ser desfeito.
- **Mostra antes de aplicar.** Você vê exatamente o que muda — sempre.
- **Zero telemetria.** Nenhuma chamada de rede (exceto checagem de driver, opcional e desligada por padrão).
- **Sem driver de kernel.** Ajuste de GPU em espaço de usuário — sem risco de tela azul nem cadeia de assinatura de driver.

Detalhes completos e modelo de ameaças: **[SECURITY.md](SECURITY.md)**.

## O que o Forge pode mudar no seu PC

Transparência total. Estas são todas as categorias de alteração que o app é capaz de fazer:

| Área | Exemplos |
|---|---|
| **Sistema** | Desativar telemetria (DiagTrack), remover bloatware, limpar tarefas agendadas e itens de inicialização |
| **Hardware** | Trocar plano de energia, alertar sobre XMP/RAM e saúde de disco (S.M.A.R.T.) |
| **GPU** | Checar driver, ativar modo máximo desempenho, aplicar offsets de clock/memória/energia |
| **Jogos** | Detectar jogos no HDD, ajustar Game Bar/Game Mode, aplicar perfil ao abrir o jogo |
| **Monitor** | Ler temps, FPS e uso de CPU/GPU/RAM, exibir OSD em jogo |

Nenhuma mudança acontece sem confirmação. Tudo pode ser revertido pelo próprio app.

> **Spike atual (já funciona, reversível + logado):** plano de energia, telemetria DiagTrack, Windows Search, hibernação, aceleração de mouse, atalhos de acessibilidade, Modo Jogo. **Roadmap (em desenvolvimento):** tuning de GPU (NVAPI), aviso de XMP/RAM, S.M.A.R.T., detecção de jogos e auto-profile, OSD, histórico de score. Itens de roadmap aparecem na UI marcados "em breve" — nunca como controle ativo que finge agir.

## Requisitos

- Windows 10 (1903+) ou Windows 11, 64-bit
- GPU NVIDIA para os recursos de ajuste de GPU (suporte AMD planejado para v1.1)

## Arquitetura

```
forge/
├── src/
│   ├── Forge.Core/         # tipos, score engine, config, contrato IPC
│   ├── Forge.System/       # debloat, serviços, telemetria, tarefas
│   ├── Forge.Hardware/     # XMP/RAM, plano de energia, saúde de disco
│   ├── Forge.Gpu/          # NVAPI, NVML, driver, tuning, OSD
│   ├── Forge.Games/        # detecção de jogo, auto profile
│   ├── Forge.Monitoring/   # LibreHardwareMonitor — temps, FPS, uso
│   └── Forge.Sentinel/     # watcher pós-Windows Update
├── apps/
│   ├── Forge.Daemon/       # Windows Service (.NET) — único com privilégio
│   └── Forge.Ui/           # shell WebView2 carregando UI vanilla (usuário comum)
└── installer/              # instala e registra o serviço
```

> **Estado:** árvore-alvo acima. Hoje (spike) o código está flat em `spike-sentinel/` + `forge-ui/` + `design/index.html`. Veja [docs/forge-v1-spec.md](docs/forge-v1-spec.md) §2.

A **UI roda sem privilégio**; só o **daemon** toca no sistema, por uma API HTTP loopback estreita e validada (token CSRF, allowlist de ações). Veja [docs/ADR-001-ui-web-sobre-daemon-nativo.md](docs/ADR-001-ui-web-sobre-daemon-nativo.md).

## Stack

.NET 10 (C#) · shell WebView2 · UI HTML/CSS/JS vanilla · nvidia-smi (NVAPI/NVML planejado) · CIM/WMI (LibreHardwareMonitor planejado) · HTTP loopback + CSRF

## Build (em breve)

```bash
# pré-requisitos: .NET 10 SDK, Node 20+
dotnet build -c Release
cd apps/Forge.Ui && npm install && npm run build
```

> O projeto está em desenvolvimento ativo. Instruções de build e releases assinados virão com a primeira versão.

## Contribuindo

Forge é da comunidade, pra comunidade. Antes de mandar um PR que mexa no daemon ou em código que altera o sistema, leia a seção **For contributors** do [SECURITY.md](SECURITY.md) — as regras de segurança são inegociáveis.

## Licença

MIT — veja [LICENSE](LICENSE).
