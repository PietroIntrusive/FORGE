---
tags: [forge, adr, arquitetura]
status: aceito
data: 2026-06-25
nota: "Espelhar no vault (4. projetos/FORGE/) quando o Google Drive voltar a aceitar escrita — hoje C: estava 100% cheio e bloqueou o sync."
---

# ADR-001 · UI web sobre daemon nativo

> [!abstract] Decisão
> UI em HTML/CSS/JS vanilla servida pelo daemon C# em `127.0.0.1:5172`, renderizada em shell WebView2 nativo (`forge-ui.exe`, com janela + bandeja + ícone). O daemon — processo separado e privilegiado — é o único que toca o sistema; a UI é **sem privilégio**.

## Contexto
O Forge já precisa de um núcleo privilegiado (serviço no boot, elevação pra mexer em serviços/energia/registro). Dado isso, a única questão era **como desenhar a interface** que fala com ele.

## Por que web venceu
- **Visual**: medidor térmico c/ bloom, glassmorphism, SVG, animações — CSS trivial; WinForms tosco, WPF verboso.
- **Iteração**: salvou → recarregou, sem recompilar.
- **Autonomia do Pietro**: ele domina HTML/CSS/JS (stack da INTRUSIVE); UI nativa o trancaria fora do código.
- **Segurança**: UI sandboxed no WebView; toda mutação passa pelo daemon (HTTP auditado + CSRF). A fronteira de menor-privilégio é **forçada**, não convencionada.
- **Um código, dois hosts**: mesmo `index.html` no browser (dev) e no shell (produto).

## Custos assumidos
- Runtime WebView2 + bundle self-contained ~145MB (trimável).
- Não é feel 100% nativo; controles recriados em CSS.
- Shell dirige o WebView via `ExecuteScript` — `PostWebMessageAsJson` + validação de origem seria hardening mais limpo.
- Duas peças (daemon + UI) vs uma.

## Percepção de usuário (risco aberto)
Concorrentes (Razer Cortex, Process Lasso, Wise) são nativos → viés "nativo = sério". Mitigação: o shell WebView2 **é** nativo (sem barra de URL), mesmo padrão de Discord/Spotify/VS Code/Figma — o usuário não sabe nem liga. A confiança do Forge vem do **modelo de segurança** (open source, reversível, daemon auditado, zero telemetria, UI sandboxed); a separação web/daemon é **mais** confiável, não menos. Decisão de produto: default sempre no shell nativo, nunca "abrir no navegador".

## Status
🟢 Aceito. Revisitar só se o bundle virar problema de distribuição, ou o hardening exigir trocar o bridge ExecuteScript→PostWebMessage.
