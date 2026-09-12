# Contrato de validação do agente

Este é o proprietário único do procedimento detalhado de validação do TailMsg.
`AGENTS.md` e a skill `tailmsg-validation` apenas apontam para este documento;
`UPDATE.md` acrescenta somente o contexto de publicação.

Os blocos `powershell` abaixo são para Windows PowerShell. O bloco `bash` da
seção Wine só deve ser executado em um host Linux/Wine com os helpers indicados;
não misture os dois ambientes.

## Entradas e segurança

- Código alterado, `build.ps1`, testes e configuração do updater.
- Ambiente Windows com o compilador .NET Framework 4 disponível.
- Endpoints isolados criados pelo teste; nunca peers reais.
- Para Wine/Tailscale, host separado com helper disponível; o teste não envia
  mensagens reais.
- Nenhum log pode conter texto de mensagem, API key ou segredo.

Depois de clonar, ative o hook versionado uma vez:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-hooks.ps1 -Quiet
```

O arquivo `.githooks/pre-commit` é um dispatcher versionado. Ele localiza a
raiz do repositório, escolhe `powershell.exe` ou `pwsh` e chama o validador
central. A configuração `core.hooksPath` fica em `.git/config`, portanto não
é transportada pelo clone e precisa ser ativada uma vez em cada clone novo.

## Gate de commit

O hook executa, em ordem, `scripts/validate-agent-harness.ps1` no modo de
commit:

1. `scripts/check-staged-snapshot.ps1` bloqueia alterações relevantes fora do
   índice staged. Essa é a estratégia adotada para garantir que o build e os
   testes não validem uma versão diferente da que entrará no commit.
2. `validate-harness-prefix.ps1 -RequireTracked` confirma as rotas canônicas.
3. `lint.ps1 -Quiet` valida a sintaxe PowerShell, o whitespace e campos privados de C# sem atribuição.
4. `build.ps1 -Quiet` compila em diretório temporário fora do repositório.
5. `TailMsg.exe --self-test` exercita o binário compilado.

O sucesso do hook não imprime linhas. A falha retorna o primeiro exit code
real da etapa que falhou e mostra somente a etapa, o código e a localização da
evidência compacta em `build-validation/pre-commit.json`. Os logs temporários
são mascarados para remover tokens, chaves, senhas e headers de autorização.

Para executar o mesmo validador manualmente:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-agent-harness.ps1 -Quiet
```

Para automação, `-Json` emite um único objeto JSON compacto. `-EvidencePath`
só aceita um caminho externo ao repositório ou um diretório ignorado, como
`build-validation/`. O modo `-Full` delega ao gate completo de release e é a
rota usada pelo CI. Em Windows nativo, o cenário `All` registra a subetapa
Wine como `NOT_APPLICABLE` e permite o release sem alegar que Wine foi
validado. O CI nativo continua passando `-SkipWine` para separar
explicitamente as coberturas. O cenário focado Wine continua exigindo host
Linux/Wine, helper e peers apropriados.

O validador aceita -DiagnosticsPath somente em diretório externo ou ignorado
(build-validation/). Quando informado, preserva ali somente os logs já
mascarados e os resultados JSON das etapas; não copia stdout bruto, argumentos
ou segredos. O CI usa essa opção e publica build-validation/** como evidência
de diagnóstico.

O mecanismo possui testes próprios:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\test-validation-harness.ps1 -Quiet
```

Eles cobrem sucesso silencioso, falha e JSON, divergência staged/working tree
incluindo diretórios ocultos e deleção de artefato de release, mascaramento de
segredos, instalação idempotente e ativação do hook no clone de teste. O gate
completo também executa essa suíte, além de
`tests\test-update-journal.ps1 -Quiet` para validar a redação do journal.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\test-update-journal.ps1 -Quiet
```

`git commit --no-verify` pode contornar hooks locais. Por isso, o workflow
`.github/workflows/validate.yml` executa o gate `-Full -SkipWine` em pull requests,
pushes na `main` e execução manual. O CI é a segunda barreira; ausência de
Wine no cenário focado, ou ausência de helper/peer em um host Wine, continua
sendo `UNVERIFIED` e não `PASS`.

## Sequência silenciosa

| Cenário | Quando usar | Ambiente | Resultado ausente |
| --- | --- | --- | --- |
| `Network` | protocolo, descoberta local e ACK | Windows isolado | falha |
| `Update` | updater, confirmação e rollback | Windows isolado | falha |
| `Wine` | helper e peers Tailscale | host Wine/Tailscale | `UNVERIFIED` |
| `All` | gate de release local | Windows nativo ou Windows + host Wine | Wine nativo = `NOT_APPLICABLE`; helper/peer ausente em Wine = `UNVERIFIED` |
| `All -SkipWine` | gate nativo do CI | Windows isolado | Wine fica `UNVERIFIED` fora deste gate |

Para a validação de regras do harness:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-harness-prefix.ps1 -Quiet
```

O modo normal confirma a existência das rotas canônicas. O hook de commit usa
`-RequireTracked` para impedir que uma rota necessária fique fora do Git; por
isso, novos arquivos do harness precisam ser incluídos no mesmo commit que os
introduz.

Para o lint determinístico, que não depende de ferramentas externas:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\lint.ps1 -Quiet
```

Para o gate único de release:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-release.ps1 -Quiet
```

O gate chama o verificador do prefixo, o lint, a redação do journal, o teste do
harness, o build, os self-tests, o smoke All e a verificação de whitespace,
sempre em sequência.
Cada etapa captura
stdout/stderr em um log temporário e emite zero linhas quando passa no modo
`-Quiet`; em caso de erro, preserva o log completo e informa somente a etapa,
o código de saída e o caminho do diagnóstico. Para guardar o ledger compacto
de uma execução, use `-LedgerPath` explicitamente:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-release.ps1 `
  -Quiet -LedgerPath .\build-validation\last-validation.json
```

O ledger contém apenas etapa, status, código de saída, duração e tamanhos dos
logs; não contém stdout/stderr bruto nem argumentos de comandos.

As flags têm escopos distintos:

- `build.ps1 -Quiet` remove somente as mensagens de sucesso; warnings e erros
  continuam disponíveis para o log.
- `tests\run-smoke.ps1 -Quiet` remove status de sucesso; processos filhos têm
  stdout/stderr capturados e só são preservados em caso de falha.
- `scripts\lint.ps1 -Quiet` valida sintaxe PowerShell e whitespace sem saída
  detalhada em caso de sucesso. O mesmo script roda `scripts\check-unassigned-fields.ps1`, que
  falha quando um campo privado de `.cs` nunca recebe atribuição — campo assim
  compila e só quebra em tempo de execução (`NullReferenceException`). Se um
  campo for preenchido de forma que a checagem não reconheça (por exemplo
  reflexão), marque a declaração com `// lint:allow-unassigned`.
- `scripts\validate-release.ps1 -Quiet` é o único gate para release e retorna
  `0` em sucesso, `1` em falha e `2` quando o ambiente requerido está
  `UNVERIFIED`.

Não execute dois smoke tests com build ao mesmo tempo: ambos usam `dist/`.
Quando o build é solicitado pelo smoke, um bloqueio temporário serializa essa
etapa; o gate já compila uma vez e usa `-SkipBuild` no smoke.

A sequência detalhada para diagnóstico é:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& .\dist\TailMsg.exe --self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$operationId = [Guid]::NewGuid().ToString("N")
& .\dist\TailMsg.exe --integration-self-test $operationId
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\run-smoke.ps1 -Scenario All -Quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
git diff --check 2>$null
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

O script deve falhar fechado: qualquer resultado diferente de zero interrompe
a cadeia. No cenário focado `Wine`, o smoke não pode converter ausência de
Wine, helper ou peer remoto em `SKIP` ou `PASS`; deve retornar `UNVERIFIED`.
No cenário `All` executado em Windows nativo, a subetapa Wine é
`NOT_APPLICABLE`, sem alegar validação Wine e sem bloquear o release.

Para desenvolvimento, `Network`, `Update` e `Wine` são cenários focados. O
cenário `Update` deve validar `service-ready` seguido de `app-confirmed`, a
forma de argumentos do updater legado e o rollback por falha de prontidão.

O cenário `Network` cobre, além da descoberta UDP, do envio TCP e do ACK de
texto, a transferência de imagem em blocos. O log da operação precisa conter
`image_header_sent`, `image_chunks_sent`, `image_received`, `image_ack_sent`,
`image_ack_received`, `source:network-image` e a recusa `peer-sem-suporte`
(peer que não anuncia a capacidade de imagem).

## Validação manual de interface

Os cenários automatizados não abrem janelas. Quando a mudança exigir teste
visual, execute o binário de `dist/` com a instalação fechada: o mutex de
instância única (`Local\TailMsg-8E47A034`) e a porta 38257 pertencem à
instalação em `C:\Program Files\TailMsg`. `--test-instance <nome>` troca
apenas o mutex, não as portas, portanto não permite duas instâncias com rede.

`StartupRegistration.EnsureRegistered` grava
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\TailMsg` apontando para o
executável em execução. Depois de testar o binário de `dist/`, restaure esse
valor para a instalação e reabra o app instalado; comprove o estado final com
o processo da instalação ativo e a porta 38257 em `Listen`.

## Diagnóstico Wine/Tailscale

Quando a alteração tocar descoberta, execute em host apropriado:

```bash
TAILMSG_TAILSCALE_BIN=/usr/bin/tailscale \
TAILMSG_WINE_BASH=/usr/bin/bash \
wine "<diretorio-de-instalacao>/TailMsg.exe" --diagnose
```

O relatório deve diferenciar helper ausente de lista vazia e registrar a fonte
dos peers remotos. Falhas de firewall ou da rede real são limitações
ambientais explícitas, nunca sucesso silencioso.

## Saída

Retorne código zero somente quando todas as etapas aplicáveis passarem.
Informe o primeiro comando que falhar e preserve seus artefatos diagnósticos.
