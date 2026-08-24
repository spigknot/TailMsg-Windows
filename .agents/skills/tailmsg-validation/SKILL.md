---
name: tailmsg-validation
description: Executa a validação silenciosa do TailMsg após mudanças no app, rede ou updater.
---

# TailMsg Validation

Use esta skill quando uma alteração tocar `TailMsg.cs`, `TailMsgUpdate.cs`,
`TailMsgUpdater.cs`, `UpdateConfig.cs`, `build.ps1`, `tests/` ou o processo de
release.

## Entrada

- Diretório raiz do projeto.
- Opcionalmente, `-Scenario Network`, `-Scenario Update` ou `-Scenario All`.

## Procedimento

1. Compile com `build.ps1` em modo silencioso.
2. Execute `dist\TailMsg.exe --self-test`.
3. Execute `dist\TailMsg.exe --integration-self-test`.
4. Execute `tests\run-smoke.ps1 -Scenario <cenário> -Quiet`.
5. Execute `git diff --check` redirecionando a saída; não use `--quiet`, pois
   esse sinalizador também pode retornar 1 quando existe qualquer diff.

## Saída

Retorne código zero somente quando todas as etapas passarem. Informe o primeiro
comando que falhou e preserve seus artefatos de diagnóstico.

## Falhas e segurança

- Não abra MessageBox nem envie mensagens para peers reais.
- Não publique arquivos, altere credenciais ou modifique configurações do usuário.
- Não registre conteúdo de mensagens ou segredos.
- Uma falha de ambiente deve ser reportada como falha/limitação explícita.
