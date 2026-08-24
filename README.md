# TailMsg

Este repositório contém o aplicativo Windows. Existe também uma implementação
Linux compatível, mantida separadamente, que usa o mesmo protocolo e as mesmas
portas.

Mensageiro Windows próprio para computadores da delegacia. O TailMsg não usa
`msg.exe`, RPC, SMB ou permissões de sessão do Windows.

## O que já funciona

- Descoberta automática de outros TailMsg por UDP nas interfaces `10.x.x.x`.
- Descoberta de peers Tailscale pelos endereços `100.64.0.0/10` presentes nas
  interfaces e pelo comando `tailscale status --json`, quando instalado.
- Comunicação direta por TCP, com confirmação de entrega.
- Envio permitido também para o próprio computador, útil para testes e
  lembretes locais.
- Filtros independentes para mostrar a rede `10.x.x.x`, o Tailscale
  `100.64.0.0/10` ou ambas.
- Lista atualizada automaticamente a cada 5 segundos e filtrada imediatamente
  ao marcar ou desmarcar uma interface.
- Recebimento em segundo plano, histórico na janela e notificação na bandeja
  do Windows.
- Inicialização automática com o Windows; fechar a janela mantém o serviço
  ativo na bandeja.
- Abertura pela bandeja com clique esquerdo, clique duplo ou menu
  **Abrir TailMsg**, com restauração forçada da janela.
- Consulta automática de atualização ao iniciar. Quando houver uma versão
  mais recente, aparece o botão **Atualização disponível** no cabeçalho.
- Atualização em um clique, com manifesto assinado, verificação SHA-256,
  substituição dos arquivos e reinicialização automática.
- Janela de mensagem recebida com remetente, conteúdo selecionável e botão
  **Copiar**.
- Ícone próprio de ninja no executável, nas janelas e na bandeja do Windows.
- Envio por linha de comando com prioridade para `10.x.x.x` quando o mesmo
  computador também for encontrado no Tailscale.
- Mensagens em UTF-8: até 100.000 caracteres pela interface e até 7.000
  caracteres pelo texto direto da linha de comando.
- Executável `x86` por padrão, adequado para Windows 7 32-bit, Windows 10 e
  Windows 11.

## Instalação

1. Execute o PowerShell na pasta do programa e rode:

   ```powershell
   .\install.ps1
   ```

   O instalador pedirá elevação, copiará o programa para
   `%LOCALAPPDATA%\TailMsg`, adicionará o comando `tailmsg` ao PATH do usuário,
   configurará a inicialização automática e abrirá apenas as portas TCP
   `38257` e UDP `38258` para `10.0.0.0/8` e `100.64.0.0/10`.
2. Abra o TailMsg pelo ícone da bandeja quando quiser consultar o histórico.
3. Selecione um computador, escreva a mensagem e
   clique em **Enviar**. `Ctrl+Enter` também envia.

O Windows pode pedir autorização para o aplicativo na primeira execução. A
regra de firewall é necessária para receber mensagens e responder à descoberta.

A execução inicial de `install.ps1` também instala o helper
`TailMsgUpdater.exe`. Depois disso, versões futuras podem ser instaladas pelo
botão **Atualização disponível**, sem executar o PowerShell novamente.
O `TailMsgUpdater.exe` também pode ser aberto diretamente, sem parâmetros,
para baixar o pacote full mais recente do GitHub e reparar ou recriar a pasta
de instalação mesmo quando o `TailMsg.exe` não estiver mais disponível.
As releases do GitHub também incluem um instalador offline versionado, que
instala o pacote completo em `C:\Program Files\TailMsg` e cria atalhos para o
TailMsg e para o updater.

## Atualizações

Os pacotes seguem o formato `AAAAMMDD_NNN.zip`, por exemplo
`20260730_001.zip`. O canal principal é o Cloudflare R2, com as releases do
GitHub como alternativa. O aplicativo consulta os dois canais e escolhe a
versão mais nova com manifesto assinado. O Google Drive conserva apenas
versões históricas da fase anterior.

O aplicativo ignora manifestos sem assinatura válida e confere o tamanho e o
SHA-256 do ZIP antes de fechar para instalar a atualização.

## Linha de comando

Abra um novo Prompt de Comando após a instalação e use:

```text
tailmsg NOME_DO_PC "Olá, bom dia!"
```

O TailMsg procura o nome nas duas redes. Se encontrar o mesmo computador em
mais de uma interface, usa primeiro o endereço `10.x.x.x`. Ao terminar, exibe
uma janela informando sucesso e o IP usado, ou o erro encontrado.

Para automação sem janela, use `--quiet` ou `--silent` antes do destinatário;
o processo retorna código zero somente quando a entrega foi confirmada:

```text
tailmsg --quiet NOME_DO_PC "Olá, bom dia!"
```

## Validação e diagnóstico

Execute os comandos abaixo a partir da pasta que contém o executável. Eles não
alteram a configuração de rede nem enviam mensagens para outros computadores.

```powershell
TailMsg.exe --self-test
TailMsg.exe --integration-self-test
TailMsg.exe --diagnose
TailMsg.exe --diagnose 10.x.x.x
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\run-smoke.ps1 -Scenario All -Quiet
```

`--self-test` valida o protocolo, UTF-8, o limite da interface e os filtros de
endereço. `--integration-self-test` usa endpoints UDP/TCP isolados em loopback
para exercitar descoberta, envio, recebimento e ACK. `--diagnose` examina as
interfaces 10.x/100.x, peers Tailscale, UDP, multicast e, quando informado,
uma porta TCP remota. O diagnóstico é salvo em
`%LOCALAPPDATA%\TailMsg\tailmsg-diagnose.txt`.

Em Wine, quando o Tailscale está instalado no Linux, podem ser informados:

```bash
TAILMSG_TAILSCALE_BIN=/usr/bin/tailscale \
TAILMSG_WINE_BASH=/usr/bin/bash \
wine "C:\\Program Files\\TailMsg\\TailMsg.exe" --diagnose
```

O diagnóstico não prova que uma mensagem foi entregue; para isso use um envio
CLI controlado ou o smoke test versionado em `tests\run-smoke.ps1`.

O cenário `Wine` do smoke registra `SKIP` em um host Windows nativo. Executado
sob Wine, ele exige que o diagnóstico encontre peers Tailscale, evitando que a
lista de destinatários remotos desapareça sem sinalizar erro.

## Tailscale

O TailMsg não tenta varrer cegamente toda a faixa `100.64.0.0/10`, pois isso
representaria mais de quatro milhões de endereços. Ele usa o IP Tailscale da
própria máquina, os peers retornados por `tailscale status --json` e multicast
quando permitido pela rede. Assim, basta o Tailscale estar conectado e o
TailMsg estar aberto nos computadores participantes.

## Compilação

No PowerShell:

```powershell
.\build.ps1
```

O padrão é `x86`, que funciona também em Windows 64-bit. Para gerar outras
arquiteturas:

```powershell
.\build.ps1 -Architecture x64
.\build.ps1 -Architecture AnyCPU
```

É necessário o compilador e o runtime do .NET Framework 4.5 ou posterior. Em
Windows 7, o .NET Framework 4.5 precisa estar instalado.
