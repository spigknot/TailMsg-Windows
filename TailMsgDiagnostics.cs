using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TailMsg
{
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
