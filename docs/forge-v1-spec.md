# Forge — Technical Specification v1.0

**Status:** Draft  
**Date:** 2026-06-20  
**Author:** Pietro  

---

## 1. Overview

Forge is an open-source Windows PC optimizer built for the Brazilian gaming community. It replaces a stack of disconnected tools (MSI Afterburner, HWiNFO, CCleaner, debloat scripts) with a single, trusted, transparent application.

**Tagline:** De jogador pra jogador.

**Target users:**
- PC fraco — needs guided optimization
- PC bom com configs pendentes — detects what's missing
- Veterano — confirms everything is correct + history tracking

---

## 2. Architecture

### Process Model
Two binaries, one installer:

```
forge-daemon.exe    Windows Service / processo elevado, expõe a API local
forge-ui.exe        Shell WebView2 (sem privilégio), carrega a UI servida pelo daemon
```

### IPC
> **Decidido em [ADR-001](ADR-001-ui-web-sobre-daemon-nativo.md).** O spike adotou **HTTP loopback** em vez de named pipe — destrava sem elevação no Win7+, serve a mesma UI no browser (dev) e no shell (produto), e endurece via token.

HTTP loopback: `http://127.0.0.1:5172`
Protocol: JSON request/response (REST). Defesas em camadas: bind só em 127.0.0.1, allowlist de Host+Origin, CORS só same-origin, token CSRF por processo em toda mutação.
Latência: < 1ms local.

### Solution Layout

> **Estado atual (spike):** o código vive **flat** em `spike-sentinel/` (daemon: API, score, snapshot, restore, GPU/RAM/monitor readers) + `forge-ui/` (shell WinForms/WebView2) + `design/index.html` (UI vanilla servida pelo daemon). A árvore modular abaixo é o **alvo de refactor**, não o que está no disco hoje.

**Alvo (futuro):**
```
forge/
├── src/
│   ├── Forge.Core/         # shared types, score engine, config, API contract
│   ├── Forge.System/       # debloat, services, telemetry, scheduled tasks
│   ├── Forge.Hardware/     # XMP/RAM, power plan, disk health
│   ├── Forge.Gpu/          # NVAPI, NVML, driver check, tuning, OSD
│   ├── Forge.Games/        # game detection, auto profile, game settings
│   ├── Forge.Monitoring/   # real-time temps, FPS, CPU/GPU/RAM usage
│   └── Forge.Sentinel/     # Windows Update watcher, regression detection
├── apps/
│   ├── Forge.Daemon/       # Windows Service binary
│   └── Forge.Ui/           # shell WebView2 (carrega design/index.html vanilla)
└── installer/              # WiX or NSIS, installs both + registers service
```

### Technology Stack
| Layer | Technology |
|---|---|
| UI shell | WebView2 (host WinForms) |
| Frontend | HTML/CSS/JS **vanilla** (sem framework) — ver ADR-001 |
| Styling | CSS puro (escala térmica de forja) |
| Charts | SVG inline (sem Recharts) |
| Backend language | C# / .NET 10 |
| Build | .NET 10 (self-contained); NativeAOT como alvo |
| GPU (Nvidia) | nvidia-smi (leitura) hoje; NVAPI/NVML (tuning) planejado |
| Hardware monitoring | CIM/WMI via PowerShell out-of-process; LibreHardwareMonitor planejado p/ temps |
| Windows APIs | WMI/CIM, powercfg, Win32 via P/Invoke |
| IPC | HTTP loopback 127.0.0.1:5172 + token CSRF, System.Text.Json — ver ADR-001 |
| Persistence | hoje: JSON cifrado (baseline, fix_log). Alvo: SQLite (score history, settings, achievements) |
| Installer | Inno Setup (`installer/forge.iss`) |

---

## 3. Score Engine

### Formula
Each category returns a score 0–100. Global score is weighted average.

| Category | Weight |
|---|---|
| System | 25% |
| Hardware | 20% |
| GPU | 20% |
| Games | 15% |
| Monitoring | 10% |
| Sentinel health | 10% |

### Score Tiers
Escala **térmica de forja** (não verde→vermelho). Vermelho fica reservado só pra perigo real de hardware. Bate com `ScoreEngine.GetTierColor` e com a UI.

| Score | Label | Color |
|---|---|---|
| 90–100 | Optimal | #fcd34d (incandescente) |
| 70–89 | Bom | #f97316 (forja) |
| 50–69 | Atenção | #b8442e (brasa) |
| 0–49 | Crítico | #3b4a6b (aço frio) |

### Score parcial (regra de honestidade)
Hoje só **Sistema (25%) + Hardware (20%) + Sentinel (10%) = 55%** pontuam; GPU/Jogos/Monitor ainda não. O global **renormaliza pelo peso ativo** (`global = Σ(score·peso)/55`). Enquanto a superfície não estiver completa, o global é **parcial** — a UI deve rotular ("parcial · 3 de 6 módulos") e **não cravar "Optimal"** com 45% cego. Um otimizador não diz "ótimo" sem olhar overclock/temperatura.

### Score History
- Baseline snapshot stored on first run
- Snapshot taken: daily (daemon), on every manual scan, post-fix
- Stored in SQLite: `score_history(timestamp, global_score, system, hardware, gpu, games, monitoring)`
- Retention: 1 year

---

## 4. Modules

### 4.1 forge-system

**Checks:**
- Windows bloatware (list of known bloat apps) installed
- Telemetry services enabled (DiagTrack, dmwappushservice, etc.)
- Unnecessary scheduled tasks active
- Unnecessary startup entries
- Windows Search indexing on non-SSD
- Hibernation enabled unnecessarily
- Pagefile misconfigured

**Actions:** disable service, remove scheduled task, remove startup entry, registry edit  
**Safety:** all changes logged to SQLite, revertible via Forge

---

### 4.2 forge-hardware

**Checks:**
- XMP/DOCP enabled in BIOS (detected via DMI/WMI — actual RAM speed vs rated speed)
- Power plan set to High Performance or Balanced (never Power Saver on gaming PC)
- HDD/SSD health via S.M.A.R.T. (reallocated sectors, pending sectors, uncorrectable)
- SSD wear level
- CPU base clock vs expected (thermal throttling detection)

**Actions:** set power plan (WMI), warn on XMP (requires BIOS — explains how), warn on disk health

---

### 4.3 forge-gpu (Nvidia only, v1.0)

**Checks:**
- Driver version vs latest (scrape Nvidia release page)
- Hardware-accelerated GPU scheduling enabled (Windows 10 2004+)
- Variable Refresh Rate enabled
- Power management mode set to Prefer Maximum Performance (NVAPI)
- GPU temperature under load (via NVML)
- GPU memory clock offset (NVAPI)

**GPU Tuning (differentiator):**
- Core clock offset slider (NVAPI)
- Memory clock offset slider (NVAPI)
- Power limit % (NVAPI)
- Fan curve: target temp + fan % pairs (NVAPI) — simple 3-point curve
- Voltage/frequency curve via NVAPI (shows current P-state table)
- Apply / Reset to default buttons
- Live preview: clock, temp, fan % while tuning

**OSD:**
- Borderless transparent overlay window, always-on-top, corner-configurable
- Shows: GPU temp, GPU usage %, VRAM usage, FPS (via PDH), CPU usage, RAM usage
- Font: monospace, small, white with shadow — no decorations
- Toggle: hotkey (default: Ctrl+Shift+O) or via tray menu

---

### 4.4 forge-games

**Checks:**
- Detected installed games (Steam, Epic, GOG paths) — check if on HDD vs SSD
- Xbox Game Bar enabled (overhead, can be disabled)
- Game Mode enabled

**Auto Game Profile:**
- forge-daemon watches process list every 2s
- On known game launch: apply preset (process priority High, GPU max performance mode, disable Xbox Game Bar overlay, cap frame rate to monitor refresh rate)
- Presets: hardcoded JSON in forge-core, updated via app updates
- On game exit: restore previous settings

**Game Database (v1.0):** curated JSON — top 20 games by Steam BR data (Warzone, CS2, Valorant, Fortnite, etc.)

---

### 4.5 forge-monitoring

**Real-time data (daemon → UI via pipe, 500ms interval):**
- CPU: usage %, per-core %, temperature (LibreHardwareMonitor)
- GPU: usage %, VRAM used/total, temperature, fan speed, clock (NVML)
- RAM: used/total GB, usage %
- FPS: PDH counter for DirectX present rate (current foreground game)
- Disk: read/write MB/s

**OSD:** see forge-gpu section — monitoring data feeds OSD

---

### 4.6 forge-sentinel

**Windows Update watcher:**
- On boot: compare current system state snapshot vs last known good snapshot
- Detects regressions: power plan reverted, services re-enabled, scheduled tasks re-added, registry values changed
- On regression detected: push notification via Windows toast + tray icon badge
- Notification text: "Windows Update reverteu [X] configurações. Seu score caiu [N] pontos. Corrigir agora?"
- User can fix with one click or dismiss

**Snapshot:** stored in SQLite, full diff on each check

---

## 5. Retention System

### 5.1 Score Timeline
- Line chart in Dashboard
- X-axis: dates, Y-axis: score 0–100
- Per-category sub-lines (toggleable)
- Hover: tooltip with exact scores + what changed

### 5.2 Before/After Card
- Generated on demand ("Export card")
- Shows: score on first run vs today, per-category delta
- Exported as PNG (1200x630 — optimal for social sharing)
- No emojis — clean typography

### 5.3 Achievement System
Achievements stored in SQLite, displayed in Dashboard.

| ID | Name | Condition |
|---|---|---|
| first_forge | First Forge | Complete first full scan + fix |
| zero_bloat | Zero Bloat | System score 100/100 |
| thermal_master | Thermal Master | GPU temp < 75C under full load |
| sentinel_guard | Sentinel Guard | 30 days without unresolved regression |
| perfect_score | Perfect Score | Global 100/100 |
| historian | Historian | Score history 90+ days |
| gpu_tuner | GPU Tuner | Applied a custom GPU profile |

### 5.4 Regression Loss Aversion Alert
Windows toast notification with score delta in red.  
Psychologically: losing points hurts more than gaining feels good. Every regression is a re-engagement trigger.

---

## 6. UI Screens

### Layout
- Left sidebar: logo + nav links (no icons-only, always show labels)
- Main content area
- Top bar: global score chip + sentinel status indicator

### Screens
1. **Dashboard** — global score gauge, score timeline chart, top issues list, quick-fix button, achievements strip
2. **System** — checklist of system issues, fix/revert per item, explanation of each
3. **Hardware** — RAM speed status, power plan selector, disk health table
4. **GPU** — driver status, tuning sliders, live metrics panel, OSD toggle
5. **Games** — detected games list, SSD/HDD indicator, auto profile toggle per game
6. **Monitor** — real-time graphs CPU/GPU/RAM/FPS, OSD settings
7. **History** — score timeline full view, before/after card export, achievements list
8. **Settings** — startup behavior, OSD position, hotkeys, sentinel toggle, language (pt-BR / en)

### Design System
- Background: #0f0f0f (near-black)
- Surface: #1a1a1a
- Surface elevated: #242424
- Border: #2e2e2e
- Text primary: #f5f5f5
- Text secondary: #9a9a9a
- Accent: #f97316 (orange-500)
- Success: #22c55e
- Warning: #f59e0b
- Error: #ef4444
- Font: Geist (UI) + Geist Mono (metrics/numbers)
- Radius: 8px cards, 4px inputs
- No emojis in UI — iconography via Lucide icons only

---

## 7. Data Model (SQLite)

```sql
CREATE TABLE score_history (
    id INTEGER PRIMARY KEY,
    timestamp INTEGER NOT NULL,
    global_score INTEGER,
    system_score INTEGER,
    hardware_score INTEGER,
    gpu_score INTEGER,
    games_score INTEGER,
    monitoring_score INTEGER
);

CREATE TABLE system_snapshot (
    id INTEGER PRIMARY KEY,
    timestamp INTEGER NOT NULL,
    data TEXT NOT NULL  -- JSON blob of full system state
);

CREATE TABLE achievements (
    id TEXT PRIMARY KEY,
    unlocked_at INTEGER
);

CREATE TABLE settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE fix_log (
    id INTEGER PRIMARY KEY,
    timestamp INTEGER NOT NULL,
    module TEXT NOT NULL,
    action TEXT NOT NULL,
    before_value TEXT,
    after_value TEXT,
    reverted INTEGER DEFAULT 0
);
```

---

## 8. API Protocol (HTTP loopback)

> Substituiu o named-pipe push do desenho original (ver ADR-001). UI faz **polling** REST; sem canal de eventos push no v1 (a UI repinta a cada poll). Toda mutação carrega `Security-Token` (CSRF) e exige admin.

### Endpoints atuais (`spike-sentinel/ApiServer.cs`)
| Método | Rota | Faz |
|---|---|---|
| GET | `/` | serve a UI (`design/index.html`) com token injetado |
| GET | `/api/status` | score global+categorias, regressões, plano de energia, `tasks` (ajustes com `needed`), game mode |
| GET | `/api/gpu` | telemetria GPU via nvidia-smi (temp/clock/util/vram/power/fan) |
| GET | `/api/ram` | módulos RAM, clock real vs rated (XMP ativo?) |
| GET | `/api/monitor` | CPU (nome/cores/threads/load), RAM, discos (S.M.A.R.T.), top processos |
| POST | `/api/apply` | aplica UMA ação do allowlist (id fixo → kind/target hardcoded) |
| POST | `/api/quick` | "Sopro da Forja": aplica o conjunto seguro inteiro, atômico (1 restore point + 1 batch) |
| POST | `/api/restore` | restaura ao baseline (Sentinel) |
| POST | `/api/gamemode` | liga/desliga Modo Jogo |
| POST | `/api/baseline` | recaptura baseline |

### Segurança da API
- Bind só em `127.0.0.1` (sem `+`/`*` → sem urlacl/admin pra escutar).
- Allowlist de `Host` e `Origin`; CORS só same-origin.
- Token CSRF de 256 bits por processo, em `Security-Token`, exigido em toda rota mutante.
- Mutações exigem admin; senão 403.
- Princípio 1: nenhum valor do request vira escrita — o `id` resolve pra `(kind,target)` fixo no servidor.

### Planejado (não no v1 atual)
- Canal de eventos push (regressão do Sentinel, jogo aberto/fechado) — hoje é detectado e notificado via toast no daemon, não via API.
- `gpu.apply_profile` (tuning NVAPI), `history.get` (quando houver SQLite).

---

## 9. Distribution

- Single `.exe` installer (NSIS)
- Installs `forge-ui.exe` + `forge-daemon.exe`
- Registers daemon as Windows Service: `ForgeDaemon`, auto-start, runs as LocalSystem
- Requires: Windows 10 1903+ (x64), Nvidia GPU for GPU tuning features
- Size target: < 30MB installer

---

## 10. MVP Scope (v1.0)

**In:**
- All 5 modules (System, Hardware, GPU, Monitoring, Games)
- Score engine + history + before/after card
- Achievement system (7 achievements)
- Sentinel (regression detection + alert)
- GPU tuning via NVAPI (Nvidia only)
- OSD
- Auto Game Profile (top 20 games, hardcoded presets)
- pt-BR + en-US

**Out (v2+):**
- Community features (backend, Supabase)
- AMD GPU support
- Fan curve (requires signed driver)
- Forge Wrapped annual report
- Plugin system

---

## 11. Security & Threat Model

Forge runs `forge-daemon` as a privileged Windows Service and modifies services, scheduled tasks, registry, power settings, and GPU state. Security is a first-class design constraint. Full policy: `SECURITY.md`.

### Non-negotiable principles
1. **No arbitrary code execution.** Every system change maps to a hardcoded, reviewed C# action. Config and (v2) community presets are declarative data that select from a fixed allowlist — they can never introduce a new command, path, or script. This makes "malicious preset runs as SYSTEM" structurally impossible.
2. **Least privilege.** UI runs as the normal user. Only the daemon holds privilege and only the daemon touches the system.
3. **Hardened IPC.** Named pipe `\\.\pipe\forge` created with an ACL limited to authenticated local users. Every message validated against a strict schema; all inputs bounds-checked and allowlisted before any action.
4. **Reversible + logged.** Every change writes a `fix_log` row (before/after) and is revertible. Nothing changes silently; UI shows before/after prior to applying.
5. **No kernel driver.** GPU tuning via NVAPI in user space — removes privilege-escalation/BSOD class and the driver-signing chain.
6. **No telemetry.** Zero network calls except an opt-in NVIDIA driver-version check (off by default, no user data).

### Threat model
| Threat | Mitigation |
|---|---|
| Malicious community preset runs code as SYSTEM | Declarative presets, allowlisted actions only (P1) |
| Local non-admin process abuses daemon via pipe | Pipe ACL + schema validation + allowlists (P3) |
| Tampered/fake Forge binary | Authenticode signing + published SHA-256 checksums |
| Compromised dependency | NuGet vulnerability audit + pinned lockfile + Dependabot |
| Stolen CI signing key | Encrypted secrets, least-privilege tokens, rotation |
| Silent harmful change | Logged, reversible, shown before apply (P4) |
| Data exfiltration | No telemetry; opt-in driver check only (P6) |

### Repository / supply chain
- Signed commits + branch protection on `main`; PR review required
- CodeQL on every PR; `dotnet list package --vulnerable` / `gitleaks` in CI
- Code-signed release binaries + published checksums; reproducible builds target
- Least-privilege `GITHUB_TOKEN`; signing certs in encrypted secrets, never in tree
- Installer ships only the two signed binaries; downloads no executable code at install time

---

## 12. Open Source

- License: MIT
- Repository: github.com/[username]/forge
- Contributing: CONTRIBUTING.md with module contribution guide + SECURITY.md "For contributors" gate
- Transparency: README explicitly lists every system change the app can make
- No telemetry: zero network calls in v1.0 (except driver update check, opt-in)
