# Contrato de validação do agente

Este documento define a validação mínima após alterações no TailMsg. Ele é
complementar ao `AGENTS.md` e ao procedimento de release em `UPDATE.md`.

## Entradas

- Código alterado, `build.ps1` e arquivos de configuração do updater.
- Ambiente Windows com o compilador .NET Framework 4 disponível.
- Para o cenário de rede, apenas endpoints isolados criados pelo teste.

## Sequência silenciosa

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 | Out-Null
& .\dist\TailMsg.exe --self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& .\dist\TailMsg.exe --integration-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\run-smoke.ps1 -Scenario All -Quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
git diff --check *> $null
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

O script deve falhar fechado: qualquer build, self-test, smoke test ou
verificação de whitespace com resultado diferente de zero interrompe a cadeia.

## Evidência esperada

- `self-test`: protocolo e filtros de endereço aprovados.
- `integration-self-test`: descoberta UDP, envio TCP, recebimento e ACK aprovados.
- `run-smoke.ps1`: cenários Network e Update aprovados, incluindo rollback.
- journal de atualização contendo confirmação pós-reinício ou rollback explícito.
- log de mensagem sem conteúdo textual da mensagem.

## Limites

O smoke test não substitui o teste de matriz em Windows 7, Windows 10/11 e
Wine/Tailscale. Falhas dependentes de firewall ou da rede real devem ser
registradas como limitação ambiental, nunca convertidas silenciosamente em
sucesso.
