---
name: tailmsg-validation
description: Roteia a validação do TailMsg após mudanças no app, rede, updater, testes, release ou harness.
---

# TailMsg Validation

Leia `docs/agents/validation.md` antes de executar comandos. Esta skill só
escolhe a rota; não duplica o procedimento.

- `Network`: protocolo, descoberta local e ACK; `Update`: updater, confirmação
  e rollback; `Wine`: helpers, Tailscale e peers remotos no host apropriado.
- `build.ps1`, lista de fontes, testes ou harness: execute o gate central, que
  também valida a redação segura do journal.
- `All`: gate completo antes de release. Em Windows nativo, a subetapa Wine é
  `NOT_APPLICABLE` para o release e não bloqueia; isso não transforma o
  cenário focado Wine em PASS. `-SkipWine` continua reservado ao CI nativo.
- Mudança Tailscale/Wine: execute também `--diagnose` no host apropriado.
- No cenário focado Wine, ausência de helper, peer ou host Wine é
  `UNVERIFIED`, nunca `PASS`.
- Não envie mensagens reais, abra janelas, publique arquivos ou altere
  credenciais.
