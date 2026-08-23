# Publicação de atualizações do TailMsg

> **FONTE DA VERDADE: [`UPDATE.md`](../UPDATE.md)** (raiz do repositório) — procedimento
> completo, comandos literais, verificações, regras e pitfalls. Leia-o antes de
> publicar qualquer versão.

## Resumo

- Os ZIPs são publicados no **Cloudflare R2** (bucket `tailmsg`,
  `https://pub-ce3b9c72c0fa4a2eb8c44a21c6ece860.r2.dev`) — canal principal desde
  `20260823_001`.
- O manifesto permanente e assinado `tailmsg-update.json` aponta para a versão
  mais recente (atualizado no lugar a cada versão).
- **GitHub Releases** (`spigknot/TailMsg-Windows`) é o segundo canal: ZIP full +
  `tailmsg-update.json` assinado + instalador offline `setup_tailmsg_<v>.exe`.
- O app consulta os dois canais a cada abertura e usa a versão mais nova,
  sempre validando assinatura, tamanho e SHA-256 antes de instalar.
- **Google Drive: APOSENTADO** desde `20260823_001`. Não publicar mais lá.

## Numeração

```
20260823_001.zip
20260823_002.zip
```

Nunca apague ou substitua um ZIP publicado. O manifesto é o único arquivo
atualizado no lugar.

## Fluxo de publicação (resumo)

1. Atualize `UpdateConfig.CurrentVersion` (raiz) para a nova versão.
2. `.\release\package-release.ps1 -Version YYYYMMDD_NNN` (build + ZIP + instalador).
3. `.\release\sign-manifest.ps1 -Version YYYYMMDD_NNN -FileId YYYYMMDD_NNN.zip` (manifesto assinado).
4. Suba o ZIP e o `tailmsg-update.json` para o R2 (boto3 — ver UPDATE.md seção 5).
5. `.\release\publish-github-release.ps1 -Version YYYYMMDD_NNN` (release GitHub).
6. Verificações obrigatórias (R2 público + SHA do asset do GitHub) — UPDATE.md seções 5 e 6.

## Chaves

- `update-public-key.xml` é incorporada ao aplicativo.
- `update-private-key.xml` assina os manifestos e nunca entra no ZIP nem no git.
- Se a chave privada for perdida, versões já instaladas não aceitarão
  manifestos assinados por outra chave.
