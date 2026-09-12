using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TailMsg
{
    // Testes sem rede do transporte de imagem: cabeçalho, blocos, hash,
    // capacidades de descoberta, política de colagem e miniatura.
    internal static class ImageSelfTests
    {
        public static string ProtocolFailure()
        {
            // Descoberta: peer antigo (sem capacidades) e peer novo convivem.
            string legacyLine = TailMsgProtocol.BuildDiscoveryResponse(
                "Sala",
                "10.0.0.2",
                38257);
            string name;
            string address;
            int port;
            int capabilities;
            if (!TailMsgProtocol.TryParseDiscoveryResponse(
                legacyLine,
                out name,
                out address,
                out port,
                out capabilities))
            {
                return "a resposta de descoberta v1 sem capacidades não foi aceita";
            }
            if (capabilities != 0)
            {
                return "peer antigo não deveria anunciar capacidades";
            }
            if (name != "Sala" || address != "10.0.0.2" || port != 38257)
            {
                return "campos da descoberta v1 divergiram";
            }

            string capableLine = TailMsgProtocol.BuildDiscoveryResponse(
                "Sala",
                "10.0.0.2",
                38257,
                TailMsgProtocol.CapabilityImage);
            if (!TailMsgProtocol.TryParseDiscoveryResponse(
                capableLine,
                out name,
                out address,
                out port,
                out capabilities))
            {
                return "a resposta de descoberta com capacidades não foi aceita";
            }
            if (!TailMsgProtocol.SupportsImages(capabilities))
            {
                return "a capacidade de imagem não foi reconhecida";
            }
            if (TailMsgProtocol.SupportsImages(0))
            {
                return "peer sem capacidade foi tratado como compatível";
            }
            if (TailMsgProtocol.TryParseDiscoveryResponse(
                "TAILMSG_HERE|1|QQ|10.0.0.2|0|1",
                out name,
                out address,
                out port,
                out capabilities))
            {
                return "porta inválida na descoberta foi aceita";
            }

            // Cabeçalho de imagem: ida e volta com metadados completos.
            byte[] payload = new byte[(TailMsgProtocol.ImageChunkBytes * 2) + 7];
            for (int index = 0; index < payload.Length; index++)
            {
                payload[index] = (byte)(index % 251);
            }
            string sha256 = TailMsgProtocol.ComputeSha256Hex(payload);
            string headerLine = TailMsgProtocol.BuildImageHeader(
                "Sala",
                TailMsgProtocol.ImageFormatPng,
                320,
                200,
                payload.Length,
                sha256,
                "op-imagem-1");

            ImageHeader header;
            if (!TailMsgProtocol.TryParseImageHeader(headerLine, out header))
            {
                return "o cabeçalho de imagem válido foi recusado";
            }
            if (header.Width != 320 || header.Height != 200 ||
                header.ByteCount != payload.Length)
            {
                return "os metadados do cabeçalho divergiram";
            }
            if (header.Sha256 != sha256 || header.SenderName != "Sala" ||
                header.OperationId != "op-imagem-1")
            {
                return "os campos do cabeçalho divergiram";
            }
            if (TailMsgProtocol.CountImageChunks(header.ByteCount) != 3)
            {
                return "a contagem de blocos divergiu";
            }
            if (TailMsgProtocol.CountImageChunks(0) != 0)
            {
                return "contagem de blocos de payload vazio divergiu";
            }

            string[] invalidHeaders = new string[]
            {
                headerLine.Replace("|png|", "|jpg|"),
                headerLine.Replace("|320|", "|0|"),
                headerLine.Replace("|200|", "|abc|"),
                headerLine.Replace(sha256, new string('z', 64)),
                headerLine.Replace("TAILMSG_IMAGE|1|", "TAILMSG_IMAGE|2|"),
                "TAILMSG_IMAGE|1|QQ|png|320"
            };
            foreach (string candidate in invalidHeaders)
            {
                ImageHeader rejected;
                if (TailMsgProtocol.TryParseImageHeader(candidate, out rejected))
                {
                    return "cabeçalho de imagem inválido foi aceito: " + candidate;
                }
            }

            // Blocos: roundtrip e recusas.
            int index2;
            string base64;
            if (!TailMsgProtocol.TryParseImageChunk(
                TailMsgProtocol.BuildImageChunk(2, "QUJD"),
                out index2,
                out base64) ||
                index2 != 2 ||
                base64 != "QUJD")
            {
                return "o bloco não sobreviveu ao roundtrip";
            }

            string[] invalidChunks = new string[]
            {
                TailMsgProtocol.BuildImageChunk(-1, "QUJD"),
                "TAILMSG_CHUNK|1|2|",
                "TAILMSG_CHUNK|1|2|QUJD|extra",
                "TAILMSG_CHUNK|2|0|QUJD",
                "TAILMSG_CHUNK|1|x|QUJD"
            };
            foreach (string candidate in invalidChunks)
            {
                int rejectedIndex;
                string rejectedBase64;
                if (TailMsgProtocol.TryParseImageChunk(
                    candidate,
                    out rejectedIndex,
                    out rejectedBase64))
                {
                    return "bloco inválido foi aceito: " + candidate;
                }
            }

            // Encerramento e confirmações.
            string endSha;
            if (!TailMsgProtocol.TryParseImageEnd(
                TailMsgProtocol.BuildImageEnd(sha256),
                out endSha) ||
                endSha != sha256)
            {
                return "o encerramento da imagem não sobreviveu ao roundtrip";
            }
            string invalidEnd;
            if (TailMsgProtocol.TryParseImageEnd("TAILMSG_IMAGE_END|1|xyz", out invalidEnd))
            {
                return "encerramento com hash inválido foi aceito";
            }

            bool accepted;
            if (!TailMsgProtocol.TryParseAcknowledgement(
                TailMsgProtocol.AcknowledgementOk,
                out accepted) || !accepted)
            {
                return "a confirmação de sucesso não foi reconhecida";
            }
            if (!TailMsgProtocol.TryParseAcknowledgement(
                TailMsgProtocol.AcknowledgementRejected,
                out accepted) || accepted)
            {
                return "a recusa do destinatário não foi reconhecida";
            }
            if (TailMsgProtocol.TryParseAcknowledgement("TAILMSG_ACK|1|?", out accepted))
            {
                return "confirmação desconhecida foi aceita";
            }

            // Assinatura PNG e limite de linha do transporte.
            if (NetworkService.IsPngSignature(new byte[8]))
            {
                return "assinatura PNG vazia foi aceita";
            }
            byte[] signature = new byte[]
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A
            };
            if (!NetworkService.IsPngSignature(signature))
            {
                return "assinatura PNG válida foi recusada";
            }
            long encodedChunkBytes = ((TailMsgProtocol.ImageChunkBytes + 2L) / 3L) * 4L;
            if (TailMsgProtocol.MaximumImageLineBytes <= encodedChunkBytes)
            {
                return "o limite de linha é menor que um bloco codificado";
            }

            return "";
        }

        // Decisão do usuário: imagem junto com texto cola os dois; o
        // destinatário recebe cada um como mensagem separada.
        public static string PastePolicyFailure()
        {
            if (ClipboardPastePolicy.Decide(false, false) != PasteDecision.None)
            {
                return "clipboard sem imagem deveria liberar a colagem nativa";
            }
            if (ClipboardPastePolicy.Decide(false, true) != PasteDecision.None)
            {
                return "texto puro foi tratado como imagem";
            }
            if (ClipboardPastePolicy.Decide(true, false) != PasteDecision.ImageOnly)
            {
                return "imagem sozinha não foi reconhecida";
            }
            if (ClipboardPastePolicy.Decide(true, true) != PasteDecision.ImageAndText)
            {
                return "imagem com texto não foi reconhecida";
            }
            if (!ClipboardPastePolicy.ShouldConsume(PasteDecision.ImageOnly))
            {
                return "imagem sozinha deveria consumir o Ctrl+V";
            }
            if (ClipboardPastePolicy.ShouldConsume(PasteDecision.ImageAndText))
            {
                return "imagem com texto não pode bloquear a colagem do texto";
            }
            if (ClipboardPastePolicy.ShouldConsume(PasteDecision.None))
            {
                return "clipboard sem imagem não pode consumir o Ctrl+V";
            }
            return "";
        }

        public static string ThumbnailFailure()
        {
            byte[] pngBytes;
            using (Bitmap source = new Bitmap(40, 20))
            {
                using (Graphics graphics = Graphics.FromImage(source))
                {
                    graphics.Clear(Color.CornflowerBlue);
                }
                using (MemoryStream buffer = new MemoryStream())
                {
                    source.Save(buffer, ImageFormat.Png);
                    pngBytes = buffer.ToArray();
                }
            }

            if (!NetworkService.IsPngSignature(pngBytes))
            {
                return "o PNG gerado não tem assinatura válida";
            }
            if (!TailMsgProtocol.IsSafeSha256(
                TailMsgProtocol.ComputeSha256Hex(pngBytes)))
            {
                return "o hash do PNG gerado não é hexadecimal de 64 caracteres";
            }

            string error;
            using (Bitmap thumbnail = ImageTransfer.CreateThumbnail(pngBytes, 10, 10, out error))
            {
                if (thumbnail == null)
                {
                    return "a miniatura não foi criada: " + error;
                }
                if (thumbnail.Width != 10 || thumbnail.Height != 5)
                {
                    return "a miniatura perdeu a proporção";
                }
            }
            using (Bitmap enlarged = ImageTransfer.CreateThumbnail(pngBytes, 200, 200, out error))
            {
                if (enlarged == null || enlarged.Width != 40 || enlarged.Height != 20)
                {
                    return "a miniatura ampliou uma imagem pequena";
                }
            }
            if (ImageTransfer.CreateThumbnail(new byte[] { 1, 2, 3 }, 10, 10, out error) != null)
            {
                return "bytes que não são imagem geraram miniatura";
            }
            if (String.IsNullOrEmpty(error))
            {
                return "bytes inválidos não produziram diagnóstico";
            }
            if (ImageTransfer.DescribeDimensions(1920, 1080) != "1920x1080")
            {
                return "a descrição de dimensões divergiu";
            }
            if (!ImageTransfer.IsLarge(TailMsgProtocol.LargeImageWarningBytes + 1))
            {
                return "o aviso de arquivo grande não disparou";
            }
            if (ImageTransfer.IsLarge(1024))
            {
                return "o aviso de arquivo grande disparou cedo demais";
            }
            return "";
        }
    }

    // Testes sem rede do transporte de áudio: cabeçalho, WAV, blocos e a
    // leitura da resposta JSON do serviço de transcrição.
    internal static class AudioSelfTests
    {
        public static string ProtocolFailure()
        {
            // WAV sintético de 2 s em 16 kHz mono 16 bits.
            byte[] wavBytes = BuildTestWav(2);
            int durationMilliseconds;
            if (!TailMsgProtocol.TryReadWavDurationMilliseconds(
                wavBytes,
                out durationMilliseconds))
            {
                return "o WAV sintético não teve duração legível";
            }
            if (durationMilliseconds < 1900 || durationMilliseconds > 2100)
            {
                return "a duração lida do WAV divergiu: " + durationMilliseconds;
            }

            int dataBytes;
            int sampleRate;
            short channels;
            short bits;
            if (!TailMsgProtocol.TryReadWavFormat(
                wavBytes,
                out dataBytes,
                out sampleRate,
                out channels,
                out bits))
            {
                return "o formato do WAV sintético não foi lido";
            }
            if (sampleRate != 16000 || channels != 1 || bits != 16)
            {
                return "o formato lido do WAV divergiu";
            }
            if (TailMsgProtocol.TryReadWavFormat(
                new byte[] { 1, 2, 3 },
                out dataBytes,
                out sampleRate,
                out channels,
                out bits))
            {
                return "bytes que não são WAV foram aceitos";
            }

            string sha256 = TailMsgProtocol.ComputeSha256Hex(wavBytes);
            string headerLine = TailMsgProtocol.BuildAudioHeader(
                "Sala",
                durationMilliseconds,
                TailMsgProtocol.AudioSampleRate,
                TailMsgProtocol.AudioChannels,
                TailMsgProtocol.AudioBitsPerSample,
                wavBytes.Length,
                sha256,
                "op-audio-1");

            AudioHeader header;
            if (!TailMsgProtocol.TryParseAudioHeader(headerLine, out header))
            {
                return "o cabeçalho de áudio válido foi recusado";
            }
            if (header.DurationMilliseconds != durationMilliseconds ||
                header.ByteCount != wavBytes.Length ||
                header.SampleRate != 16000 ||
                header.Channels != 1 ||
                header.BitsPerSample != 16)
            {
                return "os metadados do cabeçalho de áudio divergiram";
            }
            if (header.Sha256 != sha256 || header.SenderName != "Sala" ||
                header.OperationId != "op-audio-1")
            {
                return "os campos do cabeçalho de áudio divergiram";
            }

            string[] invalidHeaders = new string[]
            {
                headerLine.Replace("|wav|", "|mp3|"),
                headerLine.Replace("|16000|", "|8000|"),
                headerLine.Replace("|16000|1|16|", "|16000|2|16|"),
                headerLine.Replace("|1|16|", "|1|8|"),
                headerLine.Replace("|" + durationMilliseconds + "|", "|0|"),
                headerLine.Replace(sha256, new string('z', 64)),
                headerLine.Replace("TAILMSG_AUDIO|1|", "TAILMSG_AUDIO|2|"),
                "TAILMSG_AUDIO|1|QQ|wav|2000"
            };
            foreach (string candidate in invalidHeaders)
            {
                // Cada candidato precisa ser diferente do cabeçalho válido:
                // um replace que não casa produziria um falso positivo.
                if (candidate == headerLine)
                {
                    return "fixture inválida idêntica ao cabeçalho válido";
                }
                AudioHeader rejected;
                if (TailMsgProtocol.TryParseAudioHeader(candidate, out rejected))
                {
                    return "cabeçalho de áudio inválido foi aceito: " + candidate;
                }
            }

            string endSha256;
            if (!TailMsgProtocol.TryParseAudioEnd(
                TailMsgProtocol.BuildAudioEnd(sha256),
                out endSha256) ||
                endSha256 != sha256)
            {
                return "o encerramento de áudio não sobreviveu ao roundtrip";
            }
            if (TailMsgProtocol.TryParseAudioEnd("TAILMSG_AUDIO_END|1|abc", out endSha256))
            {
                return "encerramento de áudio com hash inválido foi aceito";
            }

            // Um áudio de 2 s precisa de mais de um bloco de 48 KB.
            if (TailMsgProtocol.CountImageChunks(wavBytes.Length) < 2)
            {
                return "a fixture de áudio não exercita múltiplos blocos";
            }
            if (!TailMsgProtocol.SupportsAudio(TailMsgProtocol.CapabilityAudio))
            {
                return "a capacidade de áudio não foi reconhecida";
            }
            if (TailMsgProtocol.SupportsAudio(TailMsgProtocol.CapabilityImage))
            {
                return "imagem foi confundida com áudio";
            }

            return "";
        }

        // Leitura da resposta do serviço de transcrição, inclusive com a
        // fixture real devolvida pelo Granite.
        public static string TranscriptionFailure()
        {
            const string realResponse =
                "{\"model\":\"ibm-granite/granite-speech-4.1-2b-nar\"," +
                "\"results\":[{\"filename\":\"audio.wav\"," +
                "\"text\":\"bom dia, senhora cristiane.\"," +
                "\"transcription\":\"bom dia, senhora cristiane.\"," +
                "\"duration_seconds\":30.016,\"chunks\":1}]," +
                "\"batch_size\":1,\"vad\":\"off\"}";

            string text;
            if (!TranscriptionJson.TryExtractText(realResponse, out text))
            {
                return "a resposta real do Granite não produziu texto";
            }
            if (text != "bom dia, senhora cristiane.")
            {
                return "o texto extraído divergiu: " + text;
            }

            const string escaped =
                "{\"results\":[{\"transcription\":\"linha 1\\nlinha 2 " +
                "com \\\"aspas\\\" e acento \\u00e7\"}]}";
            if (!TranscriptionJson.TryExtractText(escaped, out text))
            {
                return "a resposta com escapes não produziu texto";
            }
            if (text != "linha 1\nlinha 2 com \"aspas\" e acento ç")
            {
                return "os escapes do JSON foram mal interpretados: " + text;
            }

            // Sem texto: erro do serviço não pode virar transcrição.
            const string errorResponse =
                "{\"results\":[{\"text\":\"\",\"transcription\":\"\"}]," +
                "\"detail\":\"modelo indisponível\"}";
            if (TranscriptionJson.TryExtractText(errorResponse, out text))
            {
                return "resposta sem texto foi aceita como transcrição";
            }
            if (TranscriptionJson.TryExtractText("", out text) ||
                TranscriptionJson.TryExtractText("nao e json", out text))
            {
                return "conteúdo inválido foi aceito como transcrição";
            }

            if (!TailMsgProtocol.IsSafeSha256(
                TailMsgProtocol.ComputeSha256Hex(BuildTestWav(1))))
            {
                return "o hash do WAV de teste não é hexadecimal";
            }
            return "";
        }

        // WAV PCM 16 kHz mono 16 bits com um tom audível.
        public static byte[] BuildTestWav(int seconds)
        {
            int sampleRate = TailMsgProtocol.AudioSampleRate;
            int sampleCount = sampleRate * Math.Max(1, seconds);
            byte[] samples = new byte[sampleCount * 2];
            for (int index = 0; index < sampleCount; index++)
            {
                short value = (short)(6000 * Math.Sin(2 * Math.PI * 440 * index / sampleRate));
                samples[index * 2] = (byte)(value & 0xFF);
                samples[(index * 2) + 1] = (byte)((value >> 8) & 0xFF);
            }
            return TailMsgProtocol.BuildWavContainer(samples, sampleRate, 1, 16);
        }
    }

    internal static class IntegrationSelfTest
    {
        public static bool TryRun(out string failure)
        {
            return TryRun(null, out failure);
        }

        public static bool TryRun(
            string requestedOperationId,
            out string failure)
        {
            failure = "";
            NetworkService receiver = null;
            NetworkService sender = null;
            ManualResetEvent received = new ManualResetEvent(false);
            MessageReceivedEventArgs receivedArgs = null;
            ManualResetEvent imageReceived = new ManualResetEvent(false);
            ImageReceivedEventArgs imageArgs = null;
            ManualResetEvent audioReceived = new ManualResetEvent(false);
            AudioReceivedEventArgs audioArgs = null;
            string operationId = TailMsgDiagnostics.IsSafeOperationId(
                requestedOperationId)
                ? requestedOperationId
                : TailMsgDiagnostics.CreateOperationId();
            try
            {
                receiver = new NetworkService(
                    "TailMsg-Integration-Receiver",
                    0,
                    0,
                    true);
                sender = new NetworkService(
                    "TailMsg-Integration-Sender",
                    0,
                    0,
                    true);
                receiver.MessageReceived += delegate(
                    object source,
                    MessageReceivedEventArgs args)
                {
                    receivedArgs = args;
                    received.Set();
                };
                receiver.ImageReceived += delegate(
                    object source,
                    ImageReceivedEventArgs args)
                {
                    imageArgs = args;
                    imageReceived.Set();
                };
                receiver.AudioReceived += delegate(
                    object source,
                    AudioReceivedEventArgs args)
                {
                    audioArgs = args;
                    audioReceived.Set();
                };

                receiver.Start();
                sender.Start();
                sender.SendDiscoveryRequestForTest(
                    IPAddress.Loopback,
                    receiver.ListeningDiscoveryPort,
                    operationId);

                PeerInfo peer = null;
                DateTime discoveryDeadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < discoveryDeadline && peer == null)
                {
                    foreach (PeerInfo candidate in sender.GetPeersSnapshot())
                    {
                        if (String.Equals(
                            candidate.Name,
                            "TailMsg-Integration-Receiver",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            peer = candidate;
                            break;
                        }
                    }
                    if (peer == null) Thread.Sleep(20);
                }

                if (peer == null)
                {
                    failure = "a descoberta UDP de loopback não retornou um peer";
                    return false;
                }

                const string message = "TailMsg integration smoke";
                MessageSendResult result = MessageSender.Send(
                    peer,
                    "TailMsg-Integration-Sender",
                    message,
                    operationId);
                if (!result.Success)
                {
                    failure = result.ErrorMessage;
                    return false;
                }
                if (!received.WaitOne(3000))
                {
                    failure = "o receptor não recebeu a mensagem";
                    return false;
                }
                if (receivedArgs == null || receivedArgs.Message != message)
                {
                    failure = "o conteúdo recebido não corresponde ao enviado";
                    return false;
                }
                if (!String.Equals(
                    receivedArgs.OperationId,
                    operationId,
                    StringComparison.Ordinal))
                {
                    failure = "a correlação da tentativa não atravessou o receptor";
                    return false;
                }

                // Transferência de imagem: mesma descoberta, mesmo peer.
                if (!peer.SupportsImages)
                {
                    failure = "o peer de teste não anunciou suporte a imagens";
                    return false;
                }

                ImagePayload payload = CreateTestImage();
                if (payload == null)
                {
                    failure = "não foi possível gerar a imagem sintética";
                    return false;
                }
                int chunks = TailMsgProtocol.CountImageChunks(payload.ByteCount);
                if (chunks < 2)
                {
                    failure = "a fixture de imagem não exercita múltiplos blocos";
                    return false;
                }

                MessageSendResult imageResult = MessageSender.SendImage(
                    peer,
                    "TailMsg-Integration-Sender",
                    payload,
                    operationId);
                if (!imageResult.Success)
                {
                    failure = imageResult.ErrorMessage;
                    return false;
                }
                if (!imageReceived.WaitOne(5000))
                {
                    failure = "o receptor não recebeu a imagem";
                    return false;
                }
                if (imageArgs == null || imageArgs.ImageBytes == null ||
                    imageArgs.ImageBytes.Length == 0)
                {
                    failure = "a imagem recebida está vazia";
                    return false;
                }
                if (imageArgs.ImageBytes.Length != payload.PngBytes.Length)
                {
                    failure = "o tamanho da imagem recebida divergiu";
                    return false;
                }
                if (TailMsgProtocol.ComputeSha256Hex(imageArgs.ImageBytes) !=
                    TailMsgProtocol.ComputeSha256Hex(payload.PngBytes))
                {
                    failure = "o conteúdo da imagem recebida divergiu";
                    return false;
                }
                if (imageArgs.Width != payload.Width ||
                    imageArgs.Height != payload.Height)
                {
                    failure = "as dimensões da imagem recebida divergiram";
                    return false;
                }
                if (!String.Equals(
                    imageArgs.OperationId,
                    operationId,
                    StringComparison.Ordinal))
                {
                    failure = "a correlação da imagem não atravessou o receptor";
                    return false;
                }
                if (!String.Equals(imageArgs.Sha256,
                    TailMsgProtocol.ComputeSha256Hex(payload.PngBytes),
                    StringComparison.Ordinal))
                {
                    failure = "o hash anunciado da imagem divergiu";
                    return false;
                }

                // Áudio: mesmo transporte, com o formato fixo do TailMsg.
                byte[] wavBytes = AudioSelfTests.BuildTestWav(2);
                AudioPayload audio = new AudioPayload();
                audio.WavBytes = wavBytes;
                audio.DurationMilliseconds = 0;   // exercita a leitura do WAV

                MessageSendResult audioResult = MessageSender.SendAudio(
                    peer,
                    "TailMsg-Integration-Sender",
                    audio,
                    operationId);
                if (!audioResult.Success)
                {
                    failure = audioResult.ErrorMessage;
                    return false;
                }
                if (!audioReceived.WaitOne(5000))
                {
                    failure = "o receptor não recebeu o áudio";
                    return false;
                }
                if (audioArgs == null || audioArgs.AudioBytes == null ||
                    audioArgs.AudioBytes.Length != wavBytes.Length)
                {
                    failure = "o tamanho do áudio recebido divergiu";
                    return false;
                }
                if (TailMsgProtocol.ComputeSha256Hex(audioArgs.AudioBytes) !=
                    TailMsgProtocol.ComputeSha256Hex(wavBytes))
                {
                    failure = "o conteúdo do áudio recebido divergiu";
                    return false;
                }
                if (audioArgs.DurationMilliseconds < 1900 ||
                    audioArgs.DurationMilliseconds > 2100)
                {
                    failure = "a duração do áudio recebido divergiu";
                    return false;
                }
                if (!String.Equals(audioArgs.OperationId, operationId, StringComparison.Ordinal))
                {
                    failure = "a correlação do áudio não atravessou o receptor";
                    return false;
                }

                // Peer que aceita imagem mas não áudio: recusa antes da conexão.
                PeerInfo imageOnlyPeer = new PeerInfo();
                imageOnlyPeer.Name = peer.Name;
                imageOnlyPeer.Address = peer.Address;
                imageOnlyPeer.Port = peer.Port;
                imageOnlyPeer.Capabilities = TailMsgProtocol.CapabilityImage;
                if (imageOnlyPeer.SupportsAudio)
                {
                    failure = "peer sem áudio foi tratado como compatível";
                    return false;
                }
                if (MessageSender.SendAudio(
                    imageOnlyPeer,
                    "TailMsg-Integration-Sender",
                    audio,
                    operationId).Success)
                {
                    failure = "o envio de áudio para peer sem suporte não foi recusado";
                    return false;
                }

                // Áudio vazio: recusado antes de qualquer conexão.
                AudioPayload emptyAudio = new AudioPayload();
                emptyAudio.WavBytes = new byte[0];
                if (MessageSender.SendAudio(
                    peer,
                    "TailMsg-Integration-Sender",
                    emptyAudio,
                    operationId).Success)
                {
                    failure = "áudio vazio foi enviado";
                    return false;
                }

                // Peer antigo (sem capacidade): recusa antes de abrir conexão.
                PeerInfo legacyPeer = new PeerInfo();
                legacyPeer.Name = peer.Name;
                legacyPeer.Address = peer.Address;
                legacyPeer.Port = peer.Port;
                legacyPeer.Capabilities = 0;
                if (legacyPeer.SupportsImages)
                {
                    failure = "peer sem capacidade foi tratado como compatível";
                    return false;
                }
                MessageSendResult unsupported = MessageSender.SendImage(
                    legacyPeer,
                    "TailMsg-Integration-Sender",
                    payload,
                    operationId);
                if (unsupported.Success)
                {
                    failure = "o envio para peer sem suporte não foi recusado";
                    return false;
                }

                // Imagem vazia: recusada antes de qualquer conexão.
                ImagePayload emptyPayload = new ImagePayload();
                emptyPayload.PngBytes = new byte[0];
                emptyPayload.Width = 1;
                emptyPayload.Height = 1;
                if (MessageSender.SendImage(
                    peer,
                    "TailMsg-Integration-Sender",
                    emptyPayload,
                    operationId).Success)
                {
                    failure = "imagem vazia foi enviada";
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                failure = exception.Message;
                return false;
            }
            finally
            {
                if (sender != null) sender.Stop();
                if (receiver != null) receiver.Stop();
                received.Close();
                imageReceived.Close();
                audioReceived.Close();
            }
        }

        // PNG sintético com ruído determinístico: garante vários blocos sem
        // depender de arquivos externos.
        private static ImagePayload CreateTestImage()
        {
            try
            {
                const int width = 220;
                const int height = 220;
                Random random = new Random(12345);
                using (Bitmap image = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                {
                    Rectangle area = new Rectangle(0, 0, width, height);
                    BitmapData data = image.LockBits(
                        area,
                        ImageLockMode.WriteOnly,
                        PixelFormat.Format24bppRgb);
                    int rawBytes = Math.Abs(data.Stride) * height;
                    byte[] raw = new byte[rawBytes];
                    random.NextBytes(raw);
                    Marshal.Copy(raw, 0, data.Scan0, rawBytes);
                    image.UnlockBits(data);

                    ImagePayload payload = new ImagePayload();
                    payload.Width = width;
                    payload.Height = height;
                    using (MemoryStream buffer = new MemoryStream())
                    {
                        image.Save(buffer, ImageFormat.Png);
                        payload.PngBytes = buffer.ToArray();
                    }
                    return payload;
                }
            }
            catch
            {
                return null;
            }
        }
    }

    internal static class TailMsgDiagnostics
    {
        private const long MaximumLogBytes = 1024L * 1024L;
        private const int MaximumDetailCharacters = 400;
        private static readonly object Sync = new object();

        public static string LogPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "TailMsg",
                    "tailmsg-events.log");
            }
        }

        public static string CreateOperationId()
        {
            return Guid.NewGuid().ToString("N");
        }

        public static bool IsSafeOperationId(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 64)
                return false;
            foreach (char item in value)
            {
                if (!((item >= 'a' && item <= 'z') ||
                      (item >= 'A' && item <= 'Z') ||
                      (item >= '0' && item <= '9') || item == '-'))
                {
                    return false;
                }
            }
            return true;
        }

        public static string ComputeFingerprint(string senderName, string message)
        {
            string value = (senderName ?? "") + "\n" + (message ?? "");
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                StringBuilder result = new StringBuilder(digest.Length * 2);
                foreach (byte item in digest)
                {
                    result.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                }
                return result.ToString();
            }
        }

        public static void WriteMessageEvent(
            string operationId,
            string stage,
            string result,
            string peer,
            string address,
            string fingerprint,
            long elapsedMilliseconds,
            string detail)
        {
            if (String.IsNullOrEmpty(operationId))
            {
                operationId = CreateOperationId();
            }

            string line =
                "utc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) +
                ";operation=" + Safe(operationId) +
                ";stage=" + Safe(stage) +
                ";result=" + Safe(result) +
                ";peer=" + Safe(peer) +
                ";address=" + Safe(address) +
                ";fingerprint=" + Safe(fingerprint) +
                ";elapsed_ms=" + elapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
                ";detail=" + Safe(detail);

            try
            {
                lock (Sync)
                {
                    string path = LogPath;
                    string directory = Path.GetDirectoryName(path);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    RotateIfNeeded(path);
                    using (FileStream stream = new FileStream(
                        path,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read))
                    using (StreamWriter writer = new StreamWriter(
                        stream,
                        new UTF8Encoding(false)))
                    {
                        writer.WriteLine(line);
                    }
                }
            }
            catch
            {
                // Diagnóstico nunca pode derrubar o mensageiro.
            }
        }

        private static void RotateIfNeeded(string path)
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length < MaximumLogBytes)
            {
                return;
            }

            string previous = path + ".1";
            try
            {
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(path, previous);
            }
            catch
            {
                try { File.Delete(path); } catch { }
            }
        }

        private static string Safe(string value)
        {
            string result = (value ?? "").Replace("\r", " ").Replace("\n", " ");
            result = result.Replace(";", ",").Replace("=", ":").Trim();
            if (result.Length > MaximumDetailCharacters)
            {
                result = result.Substring(0, MaximumDetailCharacters);
            }
            return result;
        }
    }
}
