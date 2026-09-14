# Histórico em tabela (4 colunas)

## Objetivo

Substituir a caixa de histórico (linhas livres, alinhadas à esquerda/direita)
por uma **tabela de 4 colunas**, com uma mensagem por linha:

```
+---------+----------------------------+----------------------------+---------+
| 21:57   |  LUIZ -> [áudio 0:03] ▶    |                            |         |
+---------+----------------------------+----------------------------+---------+
|         |                            |  🖼 [imagem 29 KB] -> LUIZ  |  22:10  |
+---------+----------------------------+----------------------------+---------+
| 21:58   |  LUIZ -> mensagem de texto |                            |         |
+---------+----------------------------+----------------------------+---------+
```

| Coluna | Conteúdo | Largura |
| --- | --- | --- |
| 1 | horário de **recebimento** (hh:mm) | mínima (só o horário) |
| 2 | mensagem **recebida** (sem o horário) | metade do restante |
| 3 | mensagem **enviada** (sem o horário) | metade do restante |
| 4 | horário de **envio** (hh:mm) | mínima (só o horário) |

- Linha **recebida**: colunas 3 e 4 vazias.
- Linha **enviada**: colunas 1 e 2 vazias.
- Texto **centralizado** dentro de cada célula.
- **Todos os contornos** visíveis (bordas de célula).
- **REMETENTE** e **DESTINATÁRIO** em **negrito** (parte do texto da célula).

## Implementação

- `InboxRowBase` deixa de posicionar conteúdo livre e passa a montar a grade:
  `TableLayoutPanel` com 4 colunas (`CellBorderStyle = Single` resolve os
  contornos), sendo as colunas 1 e 4 `AutoSize` e as colunas 2 e 3 a 50% cada.
- Cada subclasse (texto, imagem, áudio) continua dona do seu conteúdo, mas o
  adiciona na **célula da sua direção**: `ContentCell` (coluna 2 para recebida,
  coluna 3 para enviada). A troca de direção (`Sent`) move os controles entre as
  células em vez de reposicioná-los.
- Os formatos passam a ter uma variante **sem horário**:
  `LUIZ -> [áudio 0:03]` (recebida) e `[imagem 29 KB] -> LUIZ` (enviada).
- Os horários (colunas 1 e 4) são preenchidos com o `Time` da entrada.
- Nomes em negrito: a célula monta o texto em partes (nome com fonte em negrito,
  resto normal) usando um `FlowLayoutPanel` com `WrapContents = false`.
- O histórico persistido (tsv) já traz tudo o que a tabela precisa (tipo,
  horário, remetente, destino, tamanho, duração); nenhum formato novo é
  necessário — a montagem é feita na hora de renderizar.

## Estado

- [ ] `InboxRowBase`: grade de 4 colunas + `ContentCell` + horários.
- [ ] `InboxTextRow`: texto na célula + negrito no nome.
- [ ] `InboxImageRow`: miniatura/clipe + texto + negrito.
- [ ] `InboxAudioRow`: player + texto + negrito.
- [ ] `InboxPanel`: layout das linhas (altura/rolagem) com a grade.
- [ ] Formato sem horário nos textos.
