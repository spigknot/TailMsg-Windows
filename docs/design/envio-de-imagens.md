# Design — imagens (Ctrl+V) e áudio (gravação) no TailMsg

Status: implementado e validado. Decisões do usuário aplicadas.

Escopo: colagem de imagem por `Ctrl+V`, envio/recebimento com miniatura e
botão `Copiar`, anexo na resposta do popup, e gravação de áudio com timeline e
transcrição (seção 6).

## 1. Comportamento entregue

1. `Ctrl+V` na caixa de mensagem do TailMsg anexa a imagem da área de
   transferência, como em Word/Paint — sem passar por "anexar arquivo".
2. Texto e imagem no mesmo envio viram **duas mensagens separadas**; o
   destinatário usa o botão `Copiar` na mensagem que quiser.
3. No envio e no recebimento aparece apenas uma **miniatura**; o botão
   `Copiar` da mensagem de imagem entrega a **imagem original**, sem
   recompressão e sem redimensionamento (PNG sem perda).
4. O popup de imagem recebida tem a miniatura e a caixa `Responder:`, e ela
   **também aceita `Ctrl+V` de imagem**: texto e imagem saem como mensagens
   separadas, exatamente como no envio principal.
5. Ao responder, o popup **fecha automaticamente** quando a resposta é
   entregue; o resultado aparece na barra de status da janela principal
   ("Resposta enviada a X."). Falha mantém o popup aberto, com o erro — e
   somente o que já foi entregue é limpo.
6. O popup de imagem **espelha** o popup de texto: mesma altura (248), botão
   `Copiar` com o mesmo rótulo e na mesma posição, e **sem** linha de
   metadados (dimensões/tamanho não aparecem na tela).

Decisões registradas pelo usuário:

| Pergunta | Decisão |
| --- | --- |
| Clipboard com texto **e** imagem | Anexa a imagem **e** cola o texto (destinatário recebe as duas mensagens) |
| Limite de tamanho | Sem limite prático; aviso na pré-visualização acima de 50 MB |
| Imagem acima do limite | Não há recompressão nem recusa: a imagem original é enviada |

## 2. Protocolo (aditivo — v1 preservado)

Regra permanente do repositório: o protocolo v1 e as portas não mudam. O
transporte de imagem usa tipos novos, e a capacidade do peer viaja como campo
**extra no fim** de `TAILMSG_HERE` — peers antigos ignoram o campo e continuam
funcionando, sem bifurcar a descoberta.

```
TAILMSG_HERE|1|<nomeB64>|<endereco>|<porta>|<capacidades>   (bit 1 = img)
TAILMSG_IMAGE|1|<nomeB64>|<formato>|<largura>|<altura>|<bytes>|<sha256>|<opid>
TAILMSG_ACK|1|OK        (cabeçalho aceito; REJECT = recusa antecipada)
TAILMSG_AUDIO|1|<nomeB64>|wav|<duracaoMs>|<taxa>|<canais>|<bits>|<bytes>|<sha256>|<opid>
TAILMSG_CHUNK|1|<indice>|<base64 de 48 KB>   (o mesmo bloco serve imagem e áudio)
TAILMSG_IMAGE_END|1|<sha256>
TAILMSG_ACK|1|OK        (só depois de validar tamanho, hash e assinatura PNG)
```

- Bloco de 48 KB decodificados (64 KB em base64) — abaixo do limite de linha.
- O receptor valida: ordem e limite dos blocos, tamanho declarado, SHA-256 do
  conjunto e assinatura PNG antes de confirmar.
- Peer sem capacidade anunciada: o envio é recusado com mensagem clara, sem
  abrir conexão.
- Diagnóstico registra apenas origem, bytes, número de blocos, dimensões e
  hash — nunca o conteúdo da imagem nem da mensagem.

Limites técnicos: o cabeçalho rejeita `bytes > Int32.MaxValue` (teto de array
do .NET) e o PNG é montado em memória; uma imagem grande demais para o
processo (Win7 32-bit) falha com erro exibido ao usuário, sem corromper o
estado.

## 3. Onde está no código

| Ponto | Referência |
| --- | --- |
| Constantes, cabeçalho, blocos, ACK | `TailMsg.cs` `TailMsgProtocol` |
| Recebimento em blocos com validação | `TailMsg.cs` `NetworkService.HandleImageTransfer` |
| Envio em blocos | `TailMsg.cs` `MessageSender.SendImage` |
| Colagem (política testável) | `TailMsg.cs` `ClipboardPastePolicy`, `ClipboardImageReader`, `MessageTextBox` |
| Miniatura e cópia da original | `TailMsg.cs` `ImageTransfer` |
| Pré-visualização e envio separado | `TailMsg.cs` `MainForm.SetPendingImage`, `SendMessage` |
| Anexo na resposta do popup | `TailMsg.cs` `ReceivedMessageForm.SetReplyAttachment`, `ApplyReplyAttachmentLayout` |
| Capacidade do remetente | `TailMsg.cs` `NetworkService.FindPeerCapabilities` |
| Popup com miniatura | `TailMsg.cs` `ReceivedMessageForm` (construtor `ImageReceivedEventArgs`) |
| Gravação e reprodução | `TailMsgAudio.cs` `WaveRecorder`, `WavePlayer` |
| Botão com ícone vetorial | `TailMsgAudio.cs` `IconButton`, `IconGlyph` |
| Timeline do áudio | `TailMsgAudio.cs` `AudioTrackPanel` |
| Transcrição (multipart + JSON) | `TailMsgAudio.cs` `TranscriptionClient`, `TranscriptionJson`, `TranscriptionSettings` |
| Testes | `TailMsg.cs` `Program.RunSelfTest`, `TailMsgDiagnostics.cs` `ImageSelfTests`/`AudioSelfTests`/`IntegrationSelfTest`, `tests/run-smoke.ps1` |

## 4. Evidências de validação (executadas)

### 4.1 Spike de colagem (máquina do usuário, antes de codificar)

Ctrl+V real injetado com `SendInput` e foco verificado:

| Caso | Resultado |
| --- | --- |
| Imagem no clipboard, `ProcessCmdKey` consome | `cmdkey consumed=1`, `wm_paste=0`, `wm_char=0`, caixa vazia |
| Texto puro | `wm_paste=1`, caixa com o texto colado |
| Imagem sem política | `wm_paste=1`, caixa permanece vazia (nada de lixo) |

A rota `WndProc`/`WM_KEYDOWN` foi descartada pelo spike: deixava um "v" na
caixa. Ficou `ProcessCmdKey` como caminho primário, com `WM_PASTE` apenas como
defesa (menu de contexto e `Shift+Insert`).

### 4.2 `--self-test`

`OK - protocolo, filtros de endereço, política de colagem e transporte de
imagem funcionando.` (código 0). Cobre descoberta com e sem capacidade,
cabeçalho válido/inválido, blocos, hash, ACK OK/REJECT, assinatura PNG,
política de colagem e miniatura (proporção preservada, sem ampliar).

### 4.3 `--integration-self-test` (loopback, sem peer real)

Imagem sintética de 145.689 bytes (220x220) em **3 blocos**: enviada,
recebida, hash e dimensões conferidos; peer sem capacidade recusado;
imagem vazia recusada. Estágios registrados: `image_header_sent`,
`image_chunks_sent`, `image_received`, `image_ack_sent`, `image_ack_received`.

### 4.4 Smoke `Network`

`tests/run-smoke.ps1 -Scenario Network -Quiet` → PASS. O cenário agora exige
os estágios de imagem, a origem `source:network-image` e a recusa
`peer-sem-suporte` no log de eventos.

### 4.5 Interface real (envio para o próprio computador)

Automação de UI (UIAutomation) + `SendInput` na janela do app novo, com o
peer `GUSTAVO (10.77.163.167) [você]` selecionado:

| Verificação | Resultado |
| --- | --- |
| Ctrl+V de imagem na caixa | Pré-visualização "Imagem anexada: 640x400 — 26 KB" + botão Remover |
| Envio só de imagem | Popup de 420x248 com a miniatura no lugar da caixa de texto, **sem** linha de metadados e com o botão `Copiar` (mesmo rótulo e mesma posição do popup de texto) |
| `Copiar` da mensagem de imagem | Clipboard passou de texto para imagem 640x400, `pixel_hash` idêntico ao original |
| Texto digitado + Ctrl+V de imagem | Texto preservado ("teste texto e imagem juntos"), **sem** caractere fantasma, e imagem anexada |
| Envio de texto + imagem | Dois popups distintos (texto e imagem) e duas operações no log |
| `Copiar` da mensagem de texto | Clipboard recebeu o texto daquela mensagem |
| Inbox | Três linhas, incluindo `[imagem 640x400, 26 KB]` |
| Clipboard misto (texto + imagem) | Texto colado na caixa **e** imagem anexada |
| Botão `Remover` (painel principal) | Anexo descartado da pré-visualização |
| Ctrl+V de imagem na caixa de resposta | Anexo "Anexo: 300x200 — 7 KB" e o popup cresce de 248 para 310, empilhando corretamente |
| Resposta só com imagem | Nova mensagem de imagem entregue; o `Copiar` do popup recebido devolveu 300x200 com `pixel_hash` idêntico ao anexado |
| Botão `Remover` (popup) | Popup voltou a 248 px |
| Resposta digitada e enviada | O popup **fechou automaticamente**; a resposta chegou como nova mensagem e o log registrou `payload_sent`, `received` e `ui_shown` |

Nenhuma mensagem foi enviada a outro computador: o destinatário foi o próprio
host (loopback pelo IP local).

## 5. Limitações conhecidas

- Colar um **arquivo** de imagem copiado no Explorer (`FileDrop`) não anexa:
  o clipboard não expõe bitmap nesse caso.
- Um anexo por envio: colar outra imagem substitui a anterior (vale para o
  painel principal e para a resposta do popup).
- Imagens não são persistidas: fechado o popup, a imagem só continua
  acessível pelo clipboard, se o usuário tiver usado `Copiar`.
- A resposta com imagem só é oferecida quando o remetente aceita imagens: quem
  enviou uma imagem comprovadamente aceita; nos outros casos vale a capacidade
  anunciada na descoberta (peer antigo é avisado, sem tentativa de envio).

## 6. Áudio (gravação, timeline e transcrição)

### 6.1 Comportamento

1. Botão quadrado de microfone à esquerda do `Enviar`, com **a mesma altura**
   do botão Enviar (os dois ocupam a altura útil do rodapé). É um controle com
   ícone vetorial desenhado em GDI+ (cápsula + arco + haste), antialiased e
   proporcional ao tamanho do botão — não depende de fontes de símbolos, o que
   importa no Windows 7 e no Wine.
2. Clique grava (o botão vira vermelho com ícone de parada e o status mostra
   `Gravando 0:07`); o segundo clique para e **anexa** o áudio, com timeline de
   pré-visualização (play/pause, barra clicável, tempo) e botão `Remover`.
3. Formato único gravado e aceito: **WAV PCM 16 kHz, mono, 16 bits**. Qualquer
   outra combinação é recusada no cabeçalho. Teto de segurança de 30 minutos.
4. Envio: texto, imagem e áudio viajam como **mensagens separadas**, nesta
   ordem. `Enviar` fica bloqueado enquanto há gravação em andamento.
5. No recebimento, o popup mostra a **timeline com botão de play**: play vira
   pause e vice-versa, a barra acompanha a posição e aceita clique para
   reposicionar. Logo abaixo fica a **transcrição**, buscada do serviço em
   segundo plano (o popup abre na hora). Em caso de falha aparece
   `Tentar de novo`.

### 6.2 Transcrição

- `POST multipart/form-data` no mesmo formato que o SIG Windows usa: campo
  `files`, `filename`, `Content-Type: audio/wav`, `accept: application/json`.
- Endereço: `HKCU\Software\TailMsg\TranscriptionEndpoint` quando definido;
  senão `http://servidor:8100/transcribe` e, como reserva,
  `http://servidor.local:8100/transcribe`. **`localhost` não é usado**: o
  serviço escuta apenas no IPv6 do host `servidor` — `127.0.0.1` e `localhost`
  recusam conexão (medido).
- Cada endereço é tentado duas vezes (o IPv6 link-local falha de forma
  intermitente). Texto lido de `text`, `transcription` ou `transcript`, em
  qualquer profundidade do JSON, sem dependências externas.
- Nada do conteúdo é registrado: o log guarda apenas sucesso/falha, endereço,
  tempo e quantidade de caracteres.

### 6.3 Evidências de áudio

| Verificação | Resultado |
| --- | --- |
| Captura winmm (sonda isolada) | 2 dispositivos; 2 s = 64.000 bytes exatos; pico 1729 |
| `--self-test` | cabeçalho de áudio (8 cabeçalhos inválidos recusados), WAV, capacidade, e leitura do JSON do Granite (fixture real + escapes + resposta vazia) |
| `--integration-self-test` | áudio de 2 s em 3 blocos com hash/duração conferidos; peer só-imagem recusado; áudio vazio recusado |
| Gravação pela UI | `Gravando 0:06` → parar → painel com `Reproduzir` e `0:00 / 0:08` |
| Play/pause/pausa/retomada | 2 s tocados = `0:03 / 0:07`; congelado em 3 s de pausa; retomou até `0:06 / 0:07` |
| Áudio real de fala (16 kHz mono) enviado ao app | popup com timeline e transcrição `testando 1, 2,3. meu nome é gustavo e eu trabalho em tagaguaí.` |
| Áudio de fala maior | transcrição `registro de ocorrência número 457 do dia 10 de setembro...` |
| Áudio do microfone sem fala | `O serviço respondeu sem texto (o áudio pode não ter fala audível).` + `Tentar de novo` |

Defeitos encontrados e corrigidos durante a validação:

1. O `Stop()` do gravador descartava o áudio por corrida com o `waveInReset`
   (agora espera os buffers ficarem prontos, sem duplicar blocos).
2. A posição da timeline usava `waveOutGetPosition`, que conta amostras na
   taxa do dispositivo (48 kHz) e marcava o fim cedo — agora vem dos buffers
   já reproduzidos.
3. **A captura parava depois de 8 segundos** (4 buffers de 2 s): ao reciclar um
   buffer eu zerava `dwFlags`, apagando o bit `WHDR_PREPARED` marcado pelo
   driver; o `waveInAddBuffer` passou a falhar com `WAVERR_UNPREPARED` (34) e
   nenhum buffer voltava para a fila. Agora só o bit `WHDR_DONE` é limpo, com
   re-preparação de reserva. Provado em teste isolado com o mesmo código:
   16 s contínuos, 513 KB, zero falhas de reciclagem (antes travava em 8 s).
4. No fechamento do mic vermelho eu descartava o gravador antes de copiar a
   janela em aberto, então o commit final não tinha bytes e a caixa ficava em
   "Transcrevendo...".

Para investigar o item 3 ficou um diagnóstico barato: o log registra
`audio_capture progress` a cada ~2,5 s com `capturado_ms`, `relogio_ms`,
`reciclados`, `falhas` e `erro` — foi ele que mostrou a captura estagnada, e a
mesma trilha serve para o próximo chamado.

### 6.4 Os três botões (microfone branco, pausa e microfone vermelho)

Ícones e comportamento copiados do SIG Windows (`sig_app.py`,
`_draw_normal_live_mic_button`, `_draw_live_mic_button`,
`_draw_live_pause_button` — canvas 44x44):

| Botão | Parado | Gravando |
| --- | --- | --- |
| Branco (esquerda) | **imagem** `assets/mic_branco.png` | círculo vinho `#3d1515`, contorno `#5a2424`, **check verde `#3ddc66`** |
| Pausa (meio) | sem desenho (slot fixo) | **imagem** `assets/pause.png`; pausado vira círculo amarelo com o triângulo do play |
| Vermelho (direita) | **imagem** `assets/mic_vermelho.png` | mesmo círculo escuro com **check verde** |

Os ícones vieram da pasta `D:\icones` (originais 1254x1254) e foram reduzidos
para **256x256 com transparência preservada** em `assets/`, sendo embutidos no
executável (`/resource:` no `build.ps1`) — o app continua um único arquivo. O
desenho é escalado com `HighQualityBicubic` e mantido em cache por tamanho
(44 px no painel principal, 22 px nos popups). Os ícones de check verde (gravando)
e do play (retomar) continuam vetoriais, porque não há arquivo para eles.

- Os três ficam quadrados, com **a mesma altura do botão Enviar**, e o slot do
  meio é fixo: fora da gravação ele apenas não desenha nada, então os vizinhos
  não se deslocam (igual às colunas do SIG).
- O botão do meio aparece durante a gravação de **qualquer** um dos
  microfones, alterna pausar/retomar a captura e desaparece quando o
  microfone ativo é encerrado.
- `Ctrl+V` de imagem na resposta, anexos, etc. seguem como antes.

### 6.5 Envio durante a gravação

Clicar em `Enviar` enquanto grava **encerra a captura automaticamente**, o
áudio entra como anexo e a mensagem segue — sem aviso pedindo para parar.

### 6.6 Transcrição no remetente (antes de enviar)

- **Microfone branco**: ao encerrar, o áudio é anexado e a transcrição aparece
  na faixa do áudio, abaixo da timeline.
- **Microfone vermelho**: janela deslizante igual ao SIG — a cada **1 segundo**
  envia a janela em aberto (o rascunho **substitui** o anterior) e a cada
  **10 segundos** confirma o que já foi transcrito, recomeçando a janela do
  ponto atual (`committed` + rascunho da janela). Rascunhos obsoletos são
  descartados por geração; a caixa fica visível durante a gravação desde o
  primeiro trecho.
  Ritmo medido: primeiros trechos em 1–2 s ("i" → "thank" → "thank you."),
  requisições a cada ~1 s.
- Ao encerrar, a janela em aberto é transcrita como confirmação final (os
  bytes são copiados **antes** de fechar o gravador — era esse o defeito que
  deixava a caixa presa em "Transcrevendo...").
- A transcrição é guardada por operação (`TranscriptionCache`), então o popup
  de recebimento reaproveita o mesmo texto sem chamar o serviço de novo.

### 6.7 Histórico de mensagens recebidas

A caixa de texto do histórico virou um painel de linhas (`InboxPanel`), porque
`TextBox` não aceita controles filhos. Cada linha de áudio fica assim:

```
[23:29:11] GUSTAVO (10.77.163.1xx): [áudio 0:06] (play) - "transcrição do áudio"
```

- O botão de play é o mesmo círculo amarelo do SIG, em miniatura; clicar toca
  o áudio e o ícone vira pausa (ao iniciar outro, o anterior para).
- A transcrição é buscada ao receber o áudio e vai para o cache; a linha é
  atualizada quando o texto chega.
- Linhas de texto e de imagem continuam sendo linhas simples.

### 6.8 Áudio na caixa de resposta (popup)

Os três botões (microfone branco, pausa e microfone vermelho) também existem no
popup de mensagem recebida, à esquerda do `Enviar`, com **a mesma altura dele
(22 px)**. O comportamento é o mesmo do painel principal:

- microfone branco: grava e, ao encerrar, anexa o áudio e transcreve;
- microfone vermelho: grava com transcrição incremental (rascunho a cada 1 s,
  confirmação a cada 10 s);
- pausa: alterna pausar/retomar (ícone amarelo de pausa ↔ triângulo de play);
- o áudio anexado aparece na faixa do popup com a transcrição e o botão
  `Remover`, e a resposta envia texto, imagem e áudio como mensagens separadas;
- clicar em `Enviar` durante a gravação encerra a captura e envia, igual ao
  painel principal.

### 6.9 Limitações de áudio

- A transcrição depende do serviço local: sem ele, o popup mostra o motivo e o
  botão de nova tentativa (o áudio continua tocando normalmente).
- Áudio sem fala audível volta sem texto — isso é registrado como falha de
  transcrição, nunca como texto inventado.
- Um anexo de áudio por envio: gravar de novo substitui o anterior.

### 6.9 Evidências das melhorias (UI real, áudio de fala do usuário)

| Verificação | Resultado |
| --- | --- |
| Enviar durante a gravação | clique em `Enviar` com a captura ativa: o log registrou o envio do áudio (`audio_chunks_sent`, `audio_ack_received`) sem aviso de bloqueio |
| Transcrição no remetente (branco) | após encerrar: `des de acontecimentos, essa sucessão de f...` na faixa do áudio |
| Mic vermelho | `started(modo=live)` no log; caixa visível durante a gravação com o primeiro trecho (`thank you.`) |
| Pausa / retomada | log `paused` → `resumed`; botão alternou `Pausar gravação` ↔ `Retomar gravação` |
| Encerramento do mic vermelho | log `stopped(modo=live)`; timeline anexada (`0:00 / 0:08`) e transcrição mantida |
| Histórico | linha `[23:29:11] TailMsg-Test-Sender (127.0.0.1): [áudio 0:06]` + botão de play + `- "testando 1, 2,3. meu nome é gustavo e eu trabalho em tagaguaí."` |
| Play do histórico | clique transformou o botão em `Pausar áudio` |

### 6.10 Evidências dos ícones e do popup

| Verificação | Resultado |
| --- | --- |
| Ícones em imagem no painel principal | captura do rodapé mostra mic branco → pausa amarela (com as duas barras) → check verde durante a gravação, todos nítidos |
| Botões no popup | `invoke` da UIA lista `Gravar áudio na resposta`, o mic vermelho e o `Enviar` na mesma linha (22 px), sem sobreposição |
| Transcrição no popup | áudio injetado mostrou `testando 1, 2,3. meu nome é gustavo e eu trabalho em tagaguaí.` na área de transcrição |
| Acessibilidade/automação | o `IconButton` agora implementa `DoDefaultAction`, então a ação de pressionar funciona por leitor de tela e por automação |

## 7. Histórico de imagens recebidas

O histórico mostra `[HH:mm:ss] Remetente (ip): [imagem NN KB]` seguido de um
botão do mesmo tamanho do play dos áudios (22 px). O botão leva o ícone
`assets/imagem.png` (embutido como recurso `TailMsg.IconImage`) e, ao ser
clicado, grava os bytes em `%TEMP%\TailMsg\imagem-<hash>.png` e entrega o
caminho ao shell (`UseShellExecute`), que abre no aplicativo padrão do usuário.
As dimensões não aparecem mais no resumo; a imagem fiel continua disponível no
popup (botão Copiar) e no clique do ícone.

Evidência do teste com o loopback: o histórico passou a exibir
`[09:38:59] GUSTAVO (10.77.163.167): [imagem 26 KB]`, a UIA listou o botão
`Abrir imagem` e o clique criou `%TEMP%\TailMsg\imagem-d77b1b9d6fb419aa.png`
(26.481 bytes, os mesmos bytes enviados) e abriu o visualizador padrão do
Windows.
