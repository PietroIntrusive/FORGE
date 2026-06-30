# Plano — Mock → Produção + Reconciliação de Docs

> **Para workers:** passos com checkbox `- [ ]`. Verificação neste repo = daemon vivo + browser (não há suíte de teste unitário no protótipo HTML/JS; o "teste" é o estado real renderizado).

**Meta:** matar todo dado falso da UI do Forge (regra de ouro: endpoint real ou vazio honesto, zero mock em produção) e alinhar spec×README×código.

**Arquitetura:** daemon C# (`spike-sentinel`) expõe HTTP em `127.0.0.1:5172`; `design/index.html` é a UI servida com token CSRF. UI faz polling dos endpoints `/api/{status,gpu,ram,monitor}`. Telas que ninguém plugou ainda mostram número chumbado.

**Stack:** .NET 10 (HttpListener + CIM/nvidia-smi), HTML/CSS/JS vanilla, Chrome MCP p/ verificação.

---

## Mapa de arquivos

| Arquivo | Responsabilidade | Toca nesta sessão |
|---|---|---|
| `design/index.html` | UI inteira (markup + CSS + JS) | A, C |
| `spike-sentinel/ApiServer.cs` | endpoints HTTP + `MonitorScript` | A2 (backend opcional) |
| `docs/forge-v1-spec.md` | spec técnico | B |
| `README.md` | pitch + arquitetura | B |
| `docs/ADR-002-*.md` (novo) | registrar drift named-pipe→HTTP, React→HTML | B |

---

# PARTE A — Tela Monitor: mock → live

**Diagnóstico (systematic-debugging):** `paintSys`/`paintGpu` já cabeiam disco, processos, RAM, e o rail GPU. NÃO cabeados (mock estático): os 3 arc-gauges (FPS, GPU temp, CPU temp), CPU-por-núcleo, SSD leitura/escrita, e o card de alerta "GPU 87°C". O "Sem dados" visto foi latência do `/api/monitor` (CIM lento), não bug.

**Princípio:** o que tem fonte real → ligar; o que não tem (CPU temp sem LibreHardwareMonitor, FPS sem PDH, SSD throughput) → `—` honesto + rótulo "sensor indisponível", nunca número inventado.

### A1 — Frontend-only (sem mexer no daemon) — RESOLVER AGORA
**Files:** `design/index.html`

- [ ] **A1.1** Dar `id` aos readouts dos 3 arc-gauges e às barras de núcleo / SSD R/W (hoje são texto chumbado).
- [ ] **A1.2** GPU temp gauge → live de `/api/gpu` (temp + arco proporcional; sub = `util% · core MHz`). Em `paintGpu`, recalcular `stroke-dasharray` do arco GPU = `temp/100*163.4`.
- [ ] **A1.3** FPS gauge e CPU temp gauge → `—` + sub "sensor indisponível" (sem fonte honesta hoje). Arco fica em estado frio (aço).
- [ ] **A1.4** CPU-por-núcleo → substituir 6 barras fixas por **uma barra de carga total** (de `/api/monitor` `cpu.load`) até A2 trazer per-core. Rótulo honesto "CPU total".
- [ ] **A1.5** RAM·Disco → RAM usada/% live (já temos em `paintSys`, espelhar no painel do Monitor); SSD leitura/escrita → `—` "requer contador de E/S" até A2.
- [ ] **A1.6** Alerta de sensor → **data-driven**: só renderiza o card vermelho quando `gpu.temp_c >= limite` (85°C). Abaixo disso: estado "Nenhum alerta · sensores dentro do limite". Remover botão "Forçar ventoinhas" (sem backend) → trocar por texto honesto ou esconder.
- [ ] **A1.7** Distinguir loading de vazio: enquanto `/api/monitor` não respondeu a 1ª vez, mostrar "Lendo…" (já existe placeholder), não "Sem dados".
- [ ] **A1.8** Verificar no Chrome: subir daemon, abrir Monitor, **aguardar 4s**, confirmar GPU temp = valor real (~35°C), FPS/CPU "—", sem alerta 87°C, processos populados.

### A2 — Backend (próxima leva, opcional) — ANOTAR, não obrigatório agora
**Files:** `spike-sentinel/ApiServer.cs`

- [ ] **A2.1** `MonitorScript`: adicionar load **por núcleo** (`Win32_PerfFormattedData_PerfOS_Processor` já expõe `Name=0,1,2…`).
- [ ] **A2.2** Adicionar SSD throughput (`Win32_PerfFormattedData_PerfDisk_PhysicalDisk` DiskReadBytes/sec, DiskWriteBytes/sec).
- [ ] **A2.3** CPU temp: avaliar LibreHardwareMonitor (spec já prevê) — decisão de dependência.
- [ ] **A2.4** Frontend: ligar per-core, SSD R/W e CPU temp reais; reverter os `—` do A1.

---

# PARTE B — Reconciliação spec × README × código

**Problema:** doc descreve arquitetura que não existe (named pipe, React/Tailwind, árvore `src/Forge.*`), e cores/labels de tier divergem do código. Projeto é open-source → drift confunde contribuidor.

**Files:** `docs/forge-v1-spec.md`, `README.md`, `docs/ADR-002-http-e-ui-vanilla.md` (novo)

- [ ] **B.1** Criar `ADR-002` registrando as 2 decisões já tomadas de fato: (a) IPC = HTTP loopback 127.0.0.1:5172 (não named pipe), (b) UI = HTML/JS vanilla servido pelo daemon + shell WinForms/WebView2 (não React/Tailwind/Recharts). Justificativa: spike provou; menos superfície, menos deps.
- [ ] **B.2** Spec §2 (Architecture): trocar bloco "named pipe / Named pipe `\\.\pipe\forge`" por HTTP loopback + token CSRF; remover árvore `src/Forge.Core…`/`apps/` ou marcá-la como **alvo futuro** explicitamente (hoje é `spike-sentinel` flat).
- [ ] **B.3** Spec §8 (IPC Protocol): substituir o protocolo de pipe pelos endpoints REST reais (`/api/status`, `/api/apply`, `/api/quick`, `/api/restore`, `/api/gamemode`, `/api/baseline`, `/api/gpu`, `/api/ram`, `/api/monitor`).
- [ ] **B.4** Spec §3 (tiers): alinhar com o código — escala térmica (`#fcd34d/#f97316/#b8442e/#3b4a6b`) + labels PT (Optimal/Bom/Atenção/Crítico), não verde/EN.
- [ ] **B.5** Spec §3 (score): documentar a renormalização por peso ativo (55%) E a regra honesta: enquanto GPU/Jogos/Monitor não pontuam, rotular o global como **parcial** (ex.: "95 · parcial — 3 de 6 módulos"). Não cravar "Optimal" com 45% cego.
- [ ] **B.6** README "Stack" e "Arquitetura": refletir realidade (HTTP, vanilla) ou marcar claramente "meta v1, hoje em spike".
- [ ] **B.7** README "O que o Forge pode mudar": remover linhas de features sem backend (HAGS, USB Suspend, Nagle, PBO, Core Parking, ReBAR, Shader Cache, GPU tuning) OU mover p/ seção "Roadmap" — não listar como capacidade atual.

---

# PARTE C — design-taste-frontend: telas mock → produção

**Quando:** depois de A e B, com a skill `design-taste-frontend`, levar as telas que ainda são protótipo a acabamento de produção — mantendo a identidade (escala térmica de forja, Geist/mono, dark).

**Files:** `design/index.html`

- [ ] **C.1** Auditoria de design (audit-first, padrão da skill): inventário das telas Monitor/Histórico/Jogos/Hardware pós-fix, listar inconsistências visuais (estados vazios, loading, `—`, cores de severidade).
- [ ] **C.2** Estados vazios e de carregamento com tratamento de primeira-classe (hoje "Sem dados" parece erro). Skeleton/loading consistente.
- [ ] **C.3** Histórico: redesenhar como "exemplo/preview" honesto OU esperar `score_history` real (decidir em B.5). Sem legenda "leitura real" sobre mock.
- [ ] **C.4** Hardware: toggles sem backend → estado visual "em breve" (desabilitado, tooltip), não toggles ativos que mentem.
- [ ] **C.5** Pré-flight da skill: contraste, espaçamento, hierarquia, responsividade do shell.

---

## Self-review (cobertura do spec de auditoria)
- Painel ajustes (crítico nº1) → ✅ já resolvido sessão anterior.
- Monitor mock (crítico) → Parte A.
- Histórico "leitura real" (crítico) → B.5 decisão + C.3 execução.
- Score 95 vs 91 (crítico) → some quando Histórico deixar de ser mock (C.3) + B.5.
- Toggles falsos / GPU tuning (alto) → B.7 (doc) + C.4 (visual "em breve"); implementação real fica fora deste plano (precisa NVAPI/endpoints — backlog).
- Disco/procs "sem dados" (médio) → A1.7 (loading honesto); não era bug.
- Drift de doc (azul) → Parte B inteira.

## Ordem de execução
1. **A1** (agora) → 2. **B** (docs) → 3. **C** (design-taste) → 4. **A2** (backend) quando decidir deps.
