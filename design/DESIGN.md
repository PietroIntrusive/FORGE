# Forge — Design System

## Como ver

Abra `index.html` no navegador. Clique na sidebar pra navegar entre **Painel**, **Sistema** e **GPU** (telas construídas). Hardware, Jogos, Monitor e Linha do tempo são placeholders por enquanto.

## Conceito

**Instrumento de precisão encontra a forja.** Otimizar o PC = levar metal cru e frio até a temperatura de forja. A cor é a linguagem: score baixo = aço frio azulado, score alto = laranja-âmbar incandescente. A cor *codifica significado* (a nota) e *é a marca* ao mesmo tempo.

## Tokens

### Cores
| Token | Hex | Uso |
|---|---|---|
| void | `#0a0a0b` | fundo |
| anvil | `#141416` | superfície |
| steel | `#1c1c20` | superfície elevada |
| edge | `#2a2a30` | bordas |
| ash | `#8b8b94` | texto secundário |
| chrome | `#f4f4f5` | texto primário |
| cold | `#3b4a6b` | escala térmica — frio (ruim) |
| ember | `#b8442e` | escala térmica — brasa |
| forge | `#f97316` | accent primário |
| hot | `#fcd34d` | escala térmica — incandescente (ótimo) |
| danger | `#d4543a` | só avisos destrutivos/críticos |

### Tipografia
- **Space Grotesk** — display, títulos, score (mecânico, técnico)
- **Inter** — UI, labels, descrições
- **JetBrains Mono** — toda telemetria: temps, FPS, clocks, valores (tabular)

### Signature
O **medidor térmico de score** — arco que esquenta conforme a nota sobe, com brilho (bloom). Reusado como linguagem nas barras de categoria e na linha do tempo.

## Decisões
- Glassmorphism só na barra de topo (logo centralizado) — resto é sólido e disciplinado.
- Sem emojis na UI — iconografia via SVG inline (estilo Lucide).
- Elementos de confiança visíveis: badge "código aberto · zero telemetria" na sidebar, banner de transparência e diff antes/depois na tela Sistema.
- Risco estético assumido: inverter o vermelho=ruim/verde=bom para a escala térmica frio→quente, coerente com a metáfora da forja. Vermelho (`danger`) reservado só pra ações destrutivas, onde ainda lê como perigo.

## Pendências de design (próxima sessão)
- Telas Hardware, Jogos, Monitor, Linha do tempo, Settings
- Card antes/depois exportável (1200×630) pra compartilhar
- OSD em jogo (overlay)
- Tela de boas-vindas / wizard do primeiro uso
- Estados vazios e de erro
