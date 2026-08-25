# TailMsg — regras permanentes

Este arquivo é o único prefixo estável carregado por padrão. Procedimentos
detalhados pertencem aos documentos roteados abaixo.

## Compatibilidade

- Preservar Windows 7 32-bit, Windows 10/11 e execução sob Wine.
- Preservar descoberta nas faixas 10.x.x.x e 100.64.0.0/10.
- Preservar protocolo de rede v1 e portas TCP 38257 / UDP 38258.
- Não alterar autenticação ou criptografia sem solicitação explícita.

## Segurança

- Nunca registrar mensagens, API keys ou outros segredos.
- Usar somente fixtures isoladas; nunca enviar mensagens reais na validação.
- Não commitar credenciais, `dist/`, `build/`, pacotes, logs ou artefatos
  temporários.

## Roteamento progressivo

- Mudança em `.cs`, `build.ps1`, `tests/`, updater, rede ou regras do harness:
  leia `.agents/skills/tailmsg-validation/SKILL.md`.
- Comandos, pré-requisitos, códigos de saída e diagnósticos:
  leia `docs/agents/validation.md`.
- Publicação de versão: leia `UPDATE.md` somente nessa tarefa.
- Manutenção do prefixo/cache: leia `docs/agents/harness-prefix.md`.
- Hooks/harness: use `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-hooks.ps1 -Quiet`;
  o dispatcher chama `scripts/validate-agent-harness.ps1`; não copie o
  procedimento para este arquivo.

## Gate mínimo

- Desenvolvimento: execute o cenário focado (`Network`, `Update` ou `Wine`).
- Mudança no harness: execute `scripts\validate-agent-harness.ps1 -Quiet`.
- Release: execute
  `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-release.ps1 -Quiet`.
- No release em Windows nativo, a ausência de Wine é `NOT_APPLICABLE` e não
  bloqueia o gate; isso não representa validação sob Wine. No cenário focado
  `Wine`, ausência de Wine, helper ou peer remoto continua `UNVERIFIED`.
- Relate o primeiro comando que falhar e preserve o diagnóstico.

## Build

- As listas de fontes C# ficam em `build.ps1`; atualize-as ao adicionar ou
  remover qualquer arquivo `.cs`.
