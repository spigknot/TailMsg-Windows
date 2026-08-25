# UPDATE.md — Procedimento completo de geração de nova versão (TailMsg Windows)

> Este documento é a FONTE DA VERDADE para gerar e publicar uma nova versão do TailMsg Windows.
> Siga EXATAMENTE esta ordem. Cada passo tem comandos literais, verificações e os
> pitfalls já vividos. Se um passo falhar, NÃO pule — resolva conforme a seção
> "Pitfalls e resolução".

---

## 0. Visão geral do fluxo

```
bump da versão → package-release (build + ZIP + instalador) → manifest assinado → R2 (ZIP + manifesto) → GitHub (release full) → commit
```

- **Canais de atualização do app** (consultados a cada abertura, escolhe a mais nova):
  1. **Cloudflare R2** (canal principal desde `20260823_001`): `https://pub-ce3b9c72c0fa4a2eb8c44a21c6ece860.r2.dev` — o app lê o manifesto permanente `tailmsg-update.json` e baixa o ZIP apontado por ele.
  2. **GitHub Releases** (`spigknot/TailMsg-Windows`, `releases/latest`): ZIP full + `tailmsg-update.json` assinado + instalador offline.
- **Google Drive**: APOSENTADO desde `20260823_001`. Não publicar mais lá. (Os arquivos antigos continuam no Drive por um tempo — as versões instaladas antes da migração atualizam pelo canal GitHub, que já existia.)
- O app **valida SEMPRE** assinatura do manifesto (RSA/SHA256), tamanho e SHA-256 do ZIP antes de instalar. Um pacote adulterado em qualquer canal é rejeitado.
- O `GetDownloadUrl` do app só aceita origens confiáveis: `github.com`, `objects.githubusercontent.com` e hosts `*.r2.dev`.

## 0.1 Contexto essencial

- **Repositório**: `D:\Projetos\TailMsg` (Windows; o terminal é bash/MSYS; os scripts de release são PowerShell — rodar com `powershell.exe -NoProfile -ExecutionPolicy Bypass -File ...`).
- **Versão nova**: a versão atual está em `UpdateConfig.cs` → `CurrentVersion`, mas a data da nova versão **NUNCA é herdada automaticamente da versão anterior**. O campo `YYYYMMDD` deve ser sempre a data local do dia em que o novo pacote está sendo gerado. No primeiro release daquele dia use `_001`; em releases adicionais no mesmo dia, use o próximo número livre (`_002`, `_003` etc.). Antes do bump, confira os pacotes locais e as releases/objetos publicados para não reutilizar uma versão. Exemplo obrigatório: se a última versão for `20260823_005` e o pacote for gerado em `2026-08-24`, a nova versão será `20260824_001`; uma segunda versão gerada em `2026-08-24` será `20260824_002`.
- **Arquivos do projeto**:
  - `TailMsg.cs` — app principal (WinForms).
  - `TailMsgUpdate.cs` — cliente de atualização (consulta R2 + GitHub, valida, baixa e dispara o updater contido no próprio ZIP).
  - `TailMsgUpdater.cs` — atualizador independente (instala o ZIP no diretório do app).
  - `UpdateConfig.cs` — `CurrentVersion`, `R2PublicBase`, `ManifestFileName`, `GitHubRepository`.
  - `release/update-private-key.xml` — assina os manifestos (NUNCA commitar; NUNCA entrar no ZIP).
  - `release/update-public-key.xml` — incorporada ao executável pelo `build.ps1`.
- **R2**: o destino deste projeto é SEMPRE o bucket `tailmsg`, com URL pública `https://pub-ce3b9c72c0fa4a2eb8c44a21c6ece860.r2.dev`.
  As credenciais S3 ficam em `release/r2_config.json` local (arquivo ignorado;
  veja `release/r2_config.example.json`). Se forem copiadas do projeto SIG
  Windows, copie apenas `endpoint`, `access_key_id` e `secret_access_key`.
  NÃO reutilize `bucket` ou `public_base` do JSON do SIG: eles podem apontar
  para `sig`. O upload deste projeto força `bucket = tailmsg`. O token da API
  Cloudflare (`cfat...`) não é necessário para o upload S3 e nunca deve ser
  documentado ou commitado.

## 0.2 Regras obrigatórias (não negociáveis)

1. **NUNCA sobrescrever um ZIP publicado** — cada versão é um arquivo `YYYYMMDD_NNN.zip` novo. O único arquivo atualizado no lugar é o manifesto `tailmsg-update.json` (permanente, aponta para a versão mais nova).
2. **A data da versão é a data atual da geração** — nunca copie a data de `CurrentVersion` anterior. No primeiro release do dia, comece em `_001`; incremente somente para outro release do mesmo dia. Se a data ou o número já existir em qualquer canal, pare e escolha o próximo identificador livre.
3. **Bump do `CurrentVersion` em `UpdateConfig.cs` ANTES do package-release** (o script valida e falha se não corresponder).
4. **`--package`/`-FileId` do manifesto = nome do arquivo no R2** (`YYYYMMDD_NNN.zip`), não um ID do Drive.
5. **Publicar nos DOIS canais sempre**: R2 (ZIP + manifesto) E GitHub (ZIP + instalador + manifesto). O fallback só funciona se os dois estiverem íntegros.
6. **Verificar o download público pelo R2.dev** (curl) e conferir SHA-256/tamanho contra o manifesto ANTES de anunciar.
7. **Verificar o SHA-256 do asset da release do GitHub** contra o ZIP local (lição do SIG Android: nunca confiar em data/aparência do GitHub).
8. **NUNCA commitar**: `update-private-key.xml`, `r2_config.json`, `dist/`, `release/packages/*.zip`, `release/generated/`, arquivos temporários de build.
9. Não inventar resultados nem números: tudo que for reportado deve vir da saída real dos comandos.
10. Se QUALQUER etapa falhar: PARE imediatamente e reporte o erro exato (mensagem + o comando que falhou), sem tentar contornar por conta própria fora deste documento.
11. Ao terminar, revise e atualize este documento se algo divergiu (seção 9 — Manutenção do documento).

12. Antes de gerar pacote, execute o gate silencioso de validação:
    `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\run-smoke.ps1 -Scenario All -Quiet`.
    O gate deve validar build, protocolo, integração UDP/TCP, ACK e o journal do updater.

## 1. Pré-requisitos (antes de começar)

1. **`gh` autenticado** como `spigknot`: `gh auth status` (se falhar: `gh auth login`).
2. **PowerShell + compilador .NET Framework 4** (o `build.ps1` usa `csc.exe` do Windows; já está disponível na máquina).
3. **Chave privada presente**: `release/update-private-key.xml` (sem ela não há manifesto assinado).
4. **Credenciais R2**: manter o `release/r2_config.json` local deste projeto
   (copiar somente `endpoint`, `access_key_id` e `secret_access_key` da chave
   dedicada ao bucket `tailmsg`). O arquivo é ignorado pelo Git. Se o token não
   tiver acesso ao bucket `tailmsg`, editar a chave no painel da Cloudflare.
5. **Python com boto3** para o upload R2 (o venv do Hermes tem; usar o mesmo comando dos exemplos abaixo).

## 2. Bump da versão

1. Descubra a data local atual no formato `YYYYMMDD`. Essa será obrigatoriamente a
   parte inicial da versão nova.
2. Liste os identificadores já usados nessa mesma data em `release/packages/`, nas
   releases do GitHub e nos objetos publicados no R2. Se não houver nenhum, use
   `_001`; caso já existam, use o próximo número sequencial livre.
3. Nunca mantenha a data da versão anterior apenas porque o número sequencial
   ainda não foi esgotado. A virada do calendário reinicia a sequência em `_001`.
4. Se o identificador escolhido já existir em qualquer canal, pare sem sobrescrever
   nada e escolha o próximo identificador livre.

Editar `UpdateConfig.cs`:

```csharp
public const string CurrentVersion = "YYYYMMDD_NNN";   // ex.: 20260824_001
```

- Formato obrigatório: `\d{8}_\d{3}` (o `package-release.ps1` valida).
- A versão DEVE ser a mesma em: `CurrentVersion`, o nome do ZIP no R2, o manifesto assinado e a tag da release do GitHub.

## 3. Pacote (build + ZIP + instalador offline)

```bash
cd "D:/Projetos/TailMsg"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File release/package-release.ps1 -Version YYYYMMDD_NNN
```

- Gera: `release/packages/YYYYMMDD_NNN.zip` (full) + `release/generated/YYYYMMDD_NNN/setup_tailmsg_YYYYMMDD_NNN.exe` (instalador offline Inno).
- Valida que `UpdateConfig.CurrentVersion` corresponde à versão pedida; falha se o pacote já existir (não sobrescreve).
- O ZIP contém: `TailMsg.exe`, `TailMsgUpdater.exe`, `install.ps1`, `firewall.ps1`, `README.md`, `version.txt`.

## 4. Manifesto assinado

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File release/sign-manifest.ps1 -Version YYYYMMDD_NNN -FileId YYYYMMDD_NNN.zip
```

- `-FileId` = o NOME DO ARQUIVO NO R2 (a versão + `.zip`).
- Gera `release/tailmsg-update.json` assinado (versão, fileId, sha256, size, signature).
- ⚠️ A assinatura cobre `versão\nfileId\nSHA256\nsize` — NÃO alterar o JSON depois de assinado.

## 5. Publicar no Cloudflare R2 (canal principal)

```bash
cd "D:/Projetos/TailMsg"
python -c "
import json, boto3
from botocore.config import Config
cfg = json.load(open('release/r2_config.json', encoding='utf-8'))
client = boto3.client('s3', endpoint_url=cfg['endpoint'], aws_access_key_id=cfg['access_key_id'], aws_secret_access_key=cfg['secret_access_key'], config=Config(signature_version='s3v4'), region_name='auto')
bucket = 'tailmsg'  # NÃO usar cfg.get('bucket'): a config do SIG pode apontar para sig
client.upload_file('release/packages/YYYYMMDD_NNN.zip', bucket, 'YYYYMMDD_NNN.zip', ExtraArgs={'ContentType': 'application/zip'})
client.upload_file('release/tailmsg-update.json', bucket, 'tailmsg-update.json', ExtraArgs={'ContentType': 'application/json'})
print('subiu ZIP + manifesto')
"
```

- O manifesto `tailmsg-update.json` é ATUALIZADO NO LUGAR (objeto permanente) — aponta para a versão nova.
- **Verificação pós-upload (obrigatória)**:

```bash
curl -sL "https://pub-ce3b9c72c0fa4a2eb8c44a21c6ece860.r2.dev/tailmsg-update.json" -o build/r2-check/manifest.json
curl -sL "https://pub-ce3b9c72c0fa4a2eb8c44a21c6ece860.r2.dev/YYYYMMDD_NNN.zip" -o build/r2-check/pkg.zip
python -c "
import hashlib, os, json
h = hashlib.sha256(open('build/r2-check/pkg.zip','rb').read()).hexdigest()
m = json.load(open('build/r2-check/manifest.json'))
print('manifesto:', m['version'], m['fileId'])
print('OK' if h == m['sha256'] and os.path.getsize('build/r2-check/pkg.zip') == m['size'] else 'DIVERGE!')
"
```

## 6. Publicar no GitHub (release full — canal de fallback)

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File release/publish-github-release.ps1 -Version YYYYMMDD_NNN
```

- Cria a release `YYYYMMDD_NNN` em `spigknot/TailMsg-Windows` com 3 assets: `YYYYMMDD_NNN.zip`, `setup_tailmsg_YYYYMMDD_NNN.exe`, `tailmsg-update.json`.
- O script falha se a release já existir (não sobrescreve).
- **Verificação pós-publicação (obrigatória)** — conferir o SHA-256 do ZIP do asset contra o local:

```bash
gh release view YYYYMMDD_NNN --repo spigknot/TailMsg-Windows --json assets --jq '.assets[].name'
python -c "
import urllib.request, hashlib
url = 'https://github.com/spigknot/TailMsg-Windows/releases/download/YYYYMMDD_NNN/YYYYMMDD_NNN.zip'
remote = hashlib.sha256(urllib.request.urlopen(url).read()).hexdigest()
local = hashlib.sha256(open('release/packages/YYYYMMDD_NNN.zip','rb').read()).hexdigest()
print('OK' if remote == local else 'DIVERGE!')
"
```

- ⚠️ No TailMsg NÃO se deleta a release anterior (diferente do SIG): o histórico de releases permanece; o app consulta `releases/latest`.

## 7. Commit e push

```bash
cd "D:/Projetos/TailMsg"
git add -A
git commit -m "Versao YYYYMMDD_NNN: <descrição curta>"
git push origin main
```

- NUNCA commitar: `release/update-private-key.xml`, `release/packages/*.zip`, `release/generated/`, `dist/`, `build/`. Conferir `git status` antes do `git add -A` se houver dúvida.

## 8. Entrega (relatório final obrigatório)

Ao concluir, reportar APENAS valores reais das saídas dos comandos:

1. A versão publicada (`YYYYMMDD_NNN`).
2. SHA-256 e tamanho do ZIP (local).
3. Resultado da verificação do R2 (manifesto público + ZIP: `OK`).
4. Resultado da verificação do GitHub (assets + SHA: `OK`).
5. O link da release do GitHub.
6. O hash do commit (`git rev-parse HEAD`).

Se QUALQUER etapa falhar: PARE imediatamente e reporte o erro exato (mensagem + o comando que falhou), sem tentar contornar por conta própria fora deste documento.

## 9. Manutenção do documento (obrigatório)

Ao terminar, revise este `UPDATE.md`: se QUALQUER passo divergir do que foi
documentado, ou se você encontrou um pitfall novo (erro, atalho, detalhe de
ambiente), ATUALIZE este documento para refletir a realidade e inclua o
pitfall na tabela de resolução — no mesmo commit da versão. Este documento é
a fonte da verdade e deve evoluir com a prática.

---

## Pitfalls e resolução (já vividos — não repetir)

| Sintoma | Causa | Resolução |
|---|---|---|
| `RequestTimeTooSkewed` no R2 | relógio do Windows dessincronizado (w32time parado; >15 min de diferença) | `powershell -c "Start-Service w32time; w32tm /resync"` (elevação); conferir `date -u` vs `curl -sI https://api.cloudflare.com \| grep -i ^date:` |
| `AccessDenied` no bucket `tailmsg` | token R2 com escopo restrito a outro bucket | editar o token no painel da Cloudflare: escopo "todos os buckets" (o token é o MESMO dos projetos SIG e TailMsg) |
| Manifesto rejeitado pelo app ("não foi assinada pelo responsável") | JSON editado DEPOIS de assinado (a assinatura cobre version/fileId/sha256/size) | re-rodar o `sign-manifest.ps1` e re-subir o manifesto (nunca editar o JSON à mão) |
| App antigo (antes da migração) não vê a versão nova | versões antigas consultam Drive + GitHub | o GitHub já existia como canal — manter a release do GitHub publicada; o Drive fica congelado na última versão publicada lá |
| `no matches found for C:/...` no `gh` | shell MSYS trata `C:/` como glob | `cd` no diretório e usar caminhos relativos |
| Upload R2 com URL pública 404 | objeto não subiu ou nome divergente | listar o bucket (`list_objects_v2`) e conferir o nome exato; verificar se o R2.dev subdomain está habilitado no bucket |
| `Upload aparece no bucket sig, mas não no TailMsg` | foi reutilizado `cfg['bucket']` do JSON compartilhado do SIG | copiar somente as credenciais S3 e forçar `bucket = tailmsg`; conferir também a URL pública `pub-ce3b9c72...r2.dev` |
| `gh release create` falha e release fica em draft | upload interrompido | `gh release edit YYYYMMDD_NNN --repo spigknot/TailMsg-Windows --draft=false` |
| ZIP publicado no R2 com SHA divergente do manifesto | subiu arquivo errado (ex.: rezip local) | SEMPRE usar o ZIP de `release/packages/` gerado pelo `package-release.ps1`; conferir SHA antes do upload |
| Atualização demora cerca de 15–20 s antes de abrir | o updater aguardava as portas antigas por até 30 s, embora o novo app já possua retry próprio de bind | limitar a espera do updater a 1 s; depois disso, instalar e deixar o `NetworkService` do novo app concluir a liberação com retry |
| Nova versão inicia minimizada após a atualização | o updater sempre passava `--background` ao reiniciar o executável | passar `--background` somente no início automático, rollback e testes isolados; reinício de atualização real deve abrir a janela |
| Nova instância falha com erro de endereço de soquete durante a atualização | o updater legado iniciou o pacote enquanto os sockets da instância anterior ainda estavam sendo liberados e aguardava `app-confirmed` enquanto mantinha sondas abertas | confirmar o processo antes do bind durante a atualização, manter o encerramento explícito do `NetworkService`, o retry de inicialização de 25 s e fazer o cliente extrair o `TailMsgUpdater.exe` do ZIP validado |

---

## Contexto da transição (histórico)

- Até `20260818_005`: canal principal = Google Drive (manifesto permanente de ID `1XNoZq_vnVP0FGfYKnmF1cv4Rn1HcBKn4` apontando para o ZIP mais novo no Drive) + GitHub como segundo canal (desde o commit `6cd30bb`).
- `20260823_001`: **migração para o Cloudflare R2** (bucket `tailmsg`, URL pública `https://pub-ce3b9c72c0fa4a2eb8c44a21c6ece860.r2.dev`). O Drive é APOSENTADO (arquivos antigos permanecem lá, sem novas publicações). O `UpdateConfig.cs` troca `ManifestFileId` (Drive) por `R2PublicBase` + `ManifestFileName`; o `TailMsgUpdate.cs` passa a consultar R2 + GitHub.
- O Drive pode ser removido de vez quando não houver mais instalações com versão anterior à `20260823_001` (as anteriores atualizam pelo GitHub, que mantém o histórico).
