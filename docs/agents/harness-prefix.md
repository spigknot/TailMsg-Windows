# Contrato do prefixo do harness

Este é um documento de manutenção do harness; não é procedimento operacional
para toda tarefa.

## Camadas

1. **Prefixo estável:** somente `AGENTS.md`, com invariantes e roteamento.
2. **Documentos sob demanda:** skill, validação, release e manutenção do
   harness, carregados conforme o tipo da tarefa.
3. **Capsule volátil:** data, versão, branch, checkout, ambiente e resultados
   atuais; nunca misturado ao prefixo estável.

## Ordem e fingerprint

O fingerprint padrão usa somente `AGENTS.md`, após normalização de LF. Não
depende de `mtime`, GUID, ordem do diretório ou timestamp.

Os documentos abaixo são rotas, não parte do prefixo padrão:

- `.agents/skills/tailmsg-validation/SKILL.md` — gatilho e escolha do cenário;
- `docs/agents/validation.md` — procedimento, pré-requisitos e diagnósticos;
- `UPDATE.md` — publicação, somente em tarefas de release;
- este arquivo — manutenção do próprio harness.

## Regras de estabilidade

- Não colocar data, versão de release, histórico, URL de canal, caminho
  absoluto, estado de ambiente ou resultado de comando em `AGENTS.md`.
- Cada regra normativa deve ter um único proprietário.
- O fingerprint detecta mudança do prefixo; não prova cache hit do provedor.
- Não registrar prompts, mensagens, segredos ou caminhos pessoais.

## Verificação

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-harness-prefix.ps1 -Quiet
```

O verificador confirma o prefixo e a existência das rotas canônicas. O modo
`-RequireTracked` é reservado para o checkout pronto para commit.
