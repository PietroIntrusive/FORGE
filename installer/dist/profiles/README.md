# Forge — Módulo Jogos (perfis de detecção + otimização)

## O problema e a solução

Tabelar **config por jogo × por hardware** explode: N jogos × M combos de PC.
Solução: **nunca enumerar combos.** Separar o conhecimento em dois eixos
independentes e cruzar em runtime com um solver.

```
Conhecimento do JOGO          Conhecimento do HARDWARE
(GameProfile, este dir)        (HardwareTier, do ScoreEngine/snapshot)
hardware-agnóstico             jogo-agnóstico
pesquisa 1x por jogo           modelado 1x por PC
        \                         /
         \                       /
          v                     v
              S O L V E R   (runtime)
        alvo de fps + tier + custo do jogo
                     |
                     v
            config concreto, reversível
```

Custo de trabalho: **N + M**, não N × M. Pesquisa de jogo é linear.

## GameProfile (`game-profile.schema.json`)

Cada `games/*.json` descreve UM jogo, sem hardware:
- **detect** — processo/exe, steamAppId
- **config** — caminho (com env vars), formato (`json`/`ini`/`keyvalues`), flags
  (`regeneratesOnLaunch`, `cloudSynced`, `writeRequiresGameClosed`, `backup`)
- **settings[]** — pra cada setting: `values` ordenados barato→caro +
  pesos `costGpu/costCpu/vram/visual` (modelo relativo 0..1) + `competitiveSafeMax`
- **anchors[]** — 1-3 pontos de benchmark público pra calibrar o solver

`verify:true` numa setting = a key exata ainda não foi confirmada contra um
arquivo real. **O apply DEVE validar a existência da key antes de escrever.**
Não se inventa key de config — corrompe o arquivo do usuário.

### Piloto (valida o schema em 3 formatos)
| Jogo | Formato | Keys confirmadas |
|---|---|---|
| `valorant.json` | INI (`sg.*` UE) | ✅ sim |
| `cyberpunk2077.json` | JSON | ⚠️ verify (confirmar em UserSettings.json real) |
| `cs2.json` | KeyValues | ⚠️ verify (confirmar em cs2_video.txt real) |

## Hardware tier (a construir — reaproveita o snapshot)

Bucketiza o PC em ~5 tiers (em vez de contínuo, mata a variância):
`GPU class · CPU class · VRAM · RAM · resolução/refresh` → orçamento de frametime.
Máquina de calibração-base: **RTX 3060 Ti / R5 3600 / 1080p 179Hz / 16GB**.

## Solver (a construir)

```
alvo_fps   = refresh do monitor (default) | preset | slider
budget     = anchor mais próximo (mesmo gpuClass) escalado pro tier
enquanto  fps_previsto < alvo_fps:
    escolhe a setting de MAIOR (custo / visual) ainda acima do mínimo
    respeitando competitiveSafeMax no preset Competitivo
    baixa um nível
    recalcula fps_previsto pelo modelo de custo
devolve config concreto
```
Não precisa ser frame-perfect — precisa **bater o default**. As anchors calibram;
buckets de tier seguram a variância.

## Apply / revert

Mesma ética do `RestoreEngine`: **snapshot do config do jogo antes de escrever,
reversível.** Tratar `regeneratesOnLaunch` (CS2: jogo fechado + `.bak`) e
`cloudSynced` (Valorant: só gráficos são locais).

## Próximos passos
1. `HardwareTier` a partir do snapshot existente
2. Solver (C#) consumindo os profiles
3. Apply/revert por formato (json/ini/keyvalues) com validação de `verify`
4. Confirmar keys `verify` de Cyberpunk/CS2 contra arquivos reais
5. Escalar curadoria além do piloto
