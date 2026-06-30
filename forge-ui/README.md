# forge-ui — shell desktop

A casca nativa (janela + bandeja) que hospeda o dashboard do Forge. **Não-privilegiada**:
roda como usuário comum e fala com o daemon só pela IPC auditada (HTTP loopback + CSRF).
O daemon é quem toca no sistema — esta casca nunca escreve na máquina.

## Arquitetura

```
forge-ui.exe  (WinForms + WebView2, asInvoker)
   │  hospeda em WebView2:
   ▼
http://127.0.0.1:5172/   ← servido pelo ForgeSentinel (daemon) com token CSRF injetado
```

O HTML é o mesmo `design/index.html`, servido pelo daemon. A casca **não** tem cópia
da UI — aponta o WebView2 pro daemon. Trocar a UI no futuro não toca nesta casca.

## Rodar (dev)

```bash
# 1. daemon (terminal elevado, pras ações de aplicar funcionarem)
cd ../spike-sentinel && dotnet run -- serve

# 2. shell
cd ../forge-ui && dotnet run
```

Se o daemon estiver offline quando a casca abre, ela tenta subir o `ForgeSentinel.exe serve`
(best-effort, layout de dev). Em produção o daemon é serviço registrado no boot.

## Bandeja (system tray)

| Item | Estado |
|---|---|
| Abrir A Forja | ✅ restaura a janela |
| Sopro da Forja (otimização rápida) | ⚠️ stub — abre a janela. Falta endpoint `/api/quick` real |
| Modo Jogo Automático | ⚠️ inerte — módulo Forge.Games não existe ainda |
| Sentinel: status | ✅ vivo — poll de `/api/status` a cada 3s (score + regressões) |
| Encerrar Forja | ✅ saída real, libera ícone |

Fechar a janela → esconde na bandeja (app segue rodando). Saída real só pelo "Encerrar".

## Pendências

- **Distribuição:** WebView2 precisa do Evergreen Runtime (já vem no Win11/maioria Win10).
  O instalador deve checar/bootstrapar. ~10 linhas.
- Endpoint real de quick-optimize pro "Sopro da Forja".
- Modo Jogo quando `Forge.Games` existir.
- Ícone branded (hoje é desenhado em código — `ForgeIcon`).
- Empacotar daemon+ui+instalador num único setup assinado.
