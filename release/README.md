# Publicação de atualizações do TailMsg

Os ZIPs são mantidos na pasta do Google Drive:

```text
https://drive.google.com/drive/folders/1_eHfJVvb9a1F3D3Fd54u_ZRSfOA5aCK4
```

O arquivo `tailmsg-update.json` possui ID permanente:

```text
1XNoZq_vnVP0FGfYKnmF1cv4Rn1HcBKn4
```

## Numeração

Liste os ZIPs existentes no Drive e escolha o próximo número do dia:

```text
20260730_001.zip
20260730_002.zip
20260731_001.zip
```

Nunca apague ou substitua um ZIP publicado. O manifesto é o único arquivo
atualizado no lugar.

## Fluxo de publicação

1. Atualize `UpdateConfig.CurrentVersion` para a nova versão.
2. Crie o pacote com compactação rápida:

   ```powershell
   .\release\package-release.ps1 -Version 20260730_002
   ```

3. Envie o ZIP criado em `release\packages` à pasta do Drive, preservando o
   nome, e anote o ID retornado.
4. Gere o manifesto assinado:

   ```powershell
   .\release\sign-manifest.ps1 `
       -Version 20260730_002 `
       -FileId ID_DO_ZIP_NO_DRIVE
   ```

5. Atualize no Drive o arquivo de ID
   `1XNoZq_vnVP0FGfYKnmF1cv4Rn1HcBKn4` com o novo
   `release\tailmsg-update.json`.
6. Baixe o manifesto e o ZIP pelos URLs públicos, confira tamanho, SHA-256 e
   assinatura antes de anunciar a versão.

## Chaves

- `update-public-key.xml` é incorporada ao aplicativo.
- `update-private-key.xml` assina os manifestos e nunca entra no ZIP.
- Se a chave privada for perdida, versões já instaladas não aceitarão
  manifestos assinados por outra chave.
