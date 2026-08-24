# Invariantes do projeto TailMsg

## Escopo

- Preservar Windows 7 32-bit, Windows 10/11 e execução sob Wine.
- Manter a descoberta nas faixas 10.x.x.x e 100.64.0.0/10.
- Preservar o protocolo de rede v1 e as portas de produção TCP 38257 e UDP 38258.
- Não alterar autenticação ou criptografia de mensagens sem solicitação explícita.

## Build e validação

- O build oficial é `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`.
- Antes de uma release, executar `TailMsg.exe --self-test`,
  `TailMsg.exe --integration-self-test` e `tests\run-smoke.ps1 -Scenario All`.
- `--self-test` valida protocolo e filtros; `--diagnose` valida o ambiente de rede.
- O smoke test deve ser silencioso, não abrir janelas e retornar código de saída útil.
- Para o gate de whitespace, redirecionar `git diff --check` para manter a saída silenciosa;
  não combinar `--quiet`, pois o Git pode tratá-lo como "há diff".
- Mudanças no `.cs` ou no `build.ps1` precisam atualizar as listas de fontes do compilador.

## Atualizações

- `UPDATE.md` é a fonte da verdade para pacote, manifesto, R2 e GitHub.
- `UpdateConfig.CurrentVersion` é a única versão de produto; o updater deve compilá-la.
- O updater só confirma sucesso depois que a nova instância inicia e grava o journal.
- Nunca sobrescrever pacotes publicados nem commitar chaves, `r2_config.json`, `dist/`,
  `release/packages/`, `release/generated/` ou artefatos temporários.

## Segurança operacional

- Logs de diagnóstico não podem conter texto de mensagem, API keys ou segredos.
- Não enviar mensagens reais durante smoke tests; usar endpoints isolados ou loopback.
- Ao relatar uma validação, informar o comando e o resultado real.
