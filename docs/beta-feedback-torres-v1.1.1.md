# Beta v1.1.1 — Feedback Torres (2026-07-15)

Primeiro teste externo real. 13 itens triados por prioridade.

## Status v1.1.2 (mesmo dia)

- ✅ Item 1 CONFIRMADO: Torres estava na v1.0 antiga (terminal + página web eram o spike).
- ✅ Item 2 RESOLVIDO: telemetria in-process (SystemTelemetry/PDH) — CPU = métrica do
  Gerenciador, RAM sem lag, seção nunca some (keep-last na UI), zero powershell.exe
  no poll (82 zumbis achados e mortos na máquina do Pietro — mesmo bug, ao vivo).
- ✅ P2 RESOLVIDO: instalador avisa upgrade, mata processos antes de copiar,
  limpa payload antigo ([InstallDelete]).
- ✅ NOVO (confiança): autostart do motor agora é escolha visível — texto de
  consentimento no instalador + toggle "Iniciar com o Windows" em Ajustes
  (GET/POST /api/autostart, gate token+admin). Sobre corrigido (licença
  proprietária · beta, repo real).
- 🔴 NOVO (aguardando dados): "uso da GPU errado em jogo" — precisa: GPU do
  Torres (NVIDIA?), valor no Forge vs Afterburner/Gerenciador no mesmo momento.
  Hipótese: se for AMD/Intel, o app mostra dado inválido em vez de explicar.
- 🔴 NOVO (retela GPU, junto com power limiter): sem placa NVIDIA, a tela GPU
  precisa EXPLICAR a limitação — "telemetria e tuning cobrem só NVIDIA (via
  oficial do driver); não mexemos em GPU sem caminho seguro" — em vez de
  gauges vazios/valores errados. Detectar o modelo real (Win32_VideoController)
  e nomear a placa no aviso.
- ⏳ Pendentes: power limiter (knob + confirmação + tutorial), loop de feedback
  visual pós-ação, reverter descobrível, caçador de drivers.

## P0 — Bugs

### 1. "Ainda abre terminal" + "tem que abrir página na web"
**Suspeita forte: Torres está rodando build/fluxo ANTIGO (v1.0).**
Evidência: binário shipado na release v1.1.1 verificado GUI (PE subsystem 2 —
janela de console impossível); instalador roda tudo `runhidden`. O fluxo
"daemon num terminal + browser em localhost:5172" é exatamente o spike v1.0.
- [ ] **Pedir ao Torres: versão exibida na tela Ajustes/Sobre** (deve ler
      "Forge v1.1.1 · daemon local"). Se v1.0 → reinstalar da release.
- [ ] Se confirmar v1.1.1 mesmo assim → reabrir investigação com print.

### 2. Monitor sob carga de jogo — CPU errada, RAM equivocada, seções sumindo
**Raiz única: telemetria via subprocess PowerShell a cada poll.**
Três sintomas, uma causa:
- **CPU % baixa demais em jogo**: `PercentProcessorTime` é time-based; o
  Gerenciador de Tarefas usa *Processor Utility* (ponderado por frequência/
  boost). Em jogo, TM mostra 100% e nosso contador mostra 60-80%. Trocar para
  `Win32_PerfFormattedData_Counters_ProcessorInformation.PercentProcessorUtility`
  (total e per-core, clamp em 100 pra exibição).
- **RAM "errada"**: valores chegam defasados — spawn de powershell.exe com CPU
  a 100% leva segundos; leitura fica velha. Conferir também conversão KB→GB e
  se a UI mostra "em uso" = total − FreePhysicalMemory (available), igual TM.
- **Seções (SSD) desaparecem**: pass do PowerShell falha/timeout parcial sob
  carga → payload sem `io` → UI esconde a seção.
**Fix definitivo (promovido da Fase 2 → agora):**
- [ ] Telemetria in-process no daemon: `System.Diagnostics.PerformanceCounter`
      (PDH) — zero subprocess, leitura em ms, aguenta jogo a 100% de CPU.
- [ ] Regra de UI: seção NUNCA desaparece — sem dado = "—" (padrão de
      honestidade já adotado no resto do app).

### 3. Power limiter (tela GPU)
- [ ] Interação do knob bugada + arrasto "porco" — refazer controle (steps
      discretos, teclado, ARIA).
- [ ] Sem confirmação visual pós-"Aplicar" — estado aplicado/pendente/falhou
      + valor atual lido de volta do nvidia-smi (fonte da verdade).
- [ ] Tutorial inline: o que é power limit, o que ganha (temp/ruído), o que
      não perde (estabilidade), auto-revert 15s explicado.

### 4. "Desempenho máximo" bugado ao lado da versão
String não existe no index.html — provavelmente é o nome do plano de energia
(`power_plan` do /api/status) renderizado em algum canto, ou tela antiga (item 1).
- [ ] Pedir print ao Torres. Reavaliar depois de confirmar a versão.

## P1 — UX: loop de feedback do usuário (tema unificado)

> Padrão do feedback: usuário age e o app não responde. "O que ganhei? Aplicou
> mesmo? Como desfaço?"

- [ ] **Confirmação visual em TODA ação**: toast/estado no card — pendente →
      aplicado (verde, com o valor novo) → ou erro claro.
- [ ] **Mostrar o ganho**: delta estimado por ajuste (pontos de score, °C, W)
      antes e depois de aplicar.
- [ ] **Reverter descobrível**: CTA "Desfazer/Restaurar" visível na tela onde a
      mudança foi feita + tela "Histórico de mudanças" (FixLog já grava tudo —
      falta expor).

## P2 — Instalador

- [ ] Detectar instalação anterior (uninstall key `DisplayVersion`) e mostrar
      "Atualizando da vX para a vY" em vez de instalar mudo por cima.

## P3 — Features novas (backlog)

- [ ] Caçador de drivers: comparar driver GPU local vs último disponível,
      avisar e linkar download oficial. (Sem auto-instalar — risco.)
- [ ] "RAM separada do SSD e informações" (item confuso — esclarecer com Torres
      o que ele quis dizer).
- [ ] "Algumas coisas não funcionam direito" — pedir lista específica
      (tela + ação + o que esperava).

## Perguntas abertas pro Torres

1. Versão no Ajustes/Sobre? (v1.0.0 ou v1.1.1)
2. Print do "Desempenho máximo" bugado.
3. Lista do que "não funciona direito" (tela + ação).
4. O que você quis dizer com "RAM separada do SSD"?
