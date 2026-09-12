using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Reflection;
using Microsoft.Win32;

namespace TailMsg
{
    internal sealed class UpdateStartupInfo
    {
        public readonly string OperationId;
        public readonly string ExpectedVersion;
        public readonly bool VersionMatches;
        public readonly bool AppStartedRecorded;

        public UpdateStartupInfo(
            string operationId,
            string expectedVersion,
            bool versionMatches,
            bool appStartedRecorded)
        {
            OperationId = operationId;
            ExpectedVersion = expectedVersion;
            VersionMatches = versionMatches;
            AppStartedRecorded = appStartedRecorded;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--self-test")
            {
                RunSelfTest();
                return;
            }

            if (args.Length >= 1 && args[0] == "--integration-self-test")
            {
                RunIntegrationSelfTest(args.Length >= 2 ? args[1] : null);
                return;
            }

            if (args.Length >= 1 && args[0] == "--diagnose")
            {
                RunDiagnostics(args.Length >= 2 ? args[1] : null);
                return;
            }

            UpdateStartupInfo updateStartup = PrepareUpdateStartup(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool quietCommand = args.Length > 0 &&
                (String.Equals(args[0], "--quiet", StringComparison.OrdinalIgnoreCase) ||
                 String.Equals(args[0], "--silent", StringComparison.OrdinalIgnoreCase));
            int commandStart = quietCommand ? 1 : 0;
            if (args.Length - commandStart >= 2 &&
                !args[commandStart].StartsWith("--", StringComparison.Ordinal))
            {
                string[] messageParts = new string[args.Length - commandStart - 1];
                Array.Copy(args, commandStart + 1, messageParts, 0, messageParts.Length);
                CommandLineMode.Send(
                    args[commandStart],
                    String.Join(" ", messageParts),
                    quietCommand);
                return;
            }

            bool startHidden = HasArgument(args, "--background") &&
                (updateStartup == null ||
                 HasArgument(args, "--test-exit-after-confirm"));
            bool disableNetwork = HasArgument(args, "--test-no-network");
            string testInstance = GetArgumentValue(args, "--test-instance");
            string mutexName = String.IsNullOrEmpty(testInstance)
                ? @"Local\TailMsg-8E47A034"
                : @"Local\TailMsg-Test-" + SanitizeMutexPart(testInstance);
            bool createdNew;
            using (Mutex instanceMutex = new Mutex(true, mutexName, out createdNew))
            {
                if (!createdNew)
                {
                    if (!startHidden)
                    {
                        SingleInstanceChannel.SendShowCommand();
                    }
                    return;
                }

                if (!disableNetwork) StartupRegistration.EnsureRegistered();
                Application.Run(new MainForm(
                    startHidden,
                    disableNetwork,
                    updateStartup,
                    HasArgument(args, "--test-exit-after-confirm"),
                    HasArgument(args, "--test-force-service-failure")));
            }
        }

        private static string SanitizeMutexPart(string value)
        {
            StringBuilder result = new StringBuilder();
            foreach (char item in value ?? "")
            {
                if ((item >= 'a' && item <= 'z') ||
                    (item >= 'A' && item <= 'Z') ||
                    (item >= '0' && item <= '9') || item == '-')
                    result.Append(item);
            }
            return result.Length == 0 ? "instance" : result.ToString();
        }

        private static UpdateStartupInfo PrepareUpdateStartup(string[] args)
        {
            string operationId = GetArgumentValue(args, "--update-operation-id");
            if (String.IsNullOrEmpty(operationId)) return null;
            string expectedVersion = GetArgumentValue(args, "--update-expected-version");
            bool versionMatches = String.Equals(
                expectedVersion,
                UpdateConfig.CurrentVersion,
                StringComparison.Ordinal);
            bool appStartedRecorded;
            if (versionMatches)
            {
                appStartedRecorded = UpdateJournal.WriteState(
                    operationId,
                    "app-started",
                    expectedVersion,
                    Application.StartupPath,
                    "process-started");
            }
            else
            {
                appStartedRecorded = UpdateJournal.WriteState(
                    operationId,
                    "app-version-mismatch",
                    expectedVersion,
                    Application.StartupPath,
                    "expected=" + expectedVersion + ";actual=" + UpdateConfig.CurrentVersion);
            }

            return new UpdateStartupInfo(
                operationId,
                expectedVersion,
                versionMatches,
                appStartedRecorded);
        }

        internal static bool CompleteUpdateStartup(
            UpdateStartupInfo updateStartup,
            bool serviceReady,
            string detail)
        {
            if (updateStartup == null || !updateStartup.VersionMatches)
                return false;
            if (!updateStartup.AppStartedRecorded)
            {
                UpdateJournal.WriteState(
                    updateStartup.OperationId,
                    "app-journal-failed",
                    updateStartup.ExpectedVersion,
                    Application.StartupPath,
                    "app-started-not-recorded");
                return false;
            }
            if (!serviceReady)
            {
                UpdateJournal.WriteState(
                    updateStartup.OperationId,
                    "app-service-failed",
                    updateStartup.ExpectedVersion,
                    Application.StartupPath,
                    String.IsNullOrEmpty(detail)
                        ? "network-service-not-ready"
                        : detail);
                return false;
            }

            bool serviceRecorded = UpdateJournal.WriteState(
                updateStartup.OperationId,
                "service-ready",
                updateStartup.ExpectedVersion,
                Application.StartupPath,
                String.IsNullOrEmpty(detail)
                    ? "network-service-ready"
                    : detail);
            if (!serviceRecorded)
            {
                UpdateJournal.WriteState(
                    updateStartup.OperationId,
                    "app-journal-failed",
                    updateStartup.ExpectedVersion,
                    Application.StartupPath,
                    "service-ready-not-recorded");
                return false;
            }

            return UpdateJournal.WriteState(
                updateStartup.OperationId,
                "app-confirmed",
                updateStartup.ExpectedVersion,
                Application.StartupPath,
                String.IsNullOrEmpty(detail)
                    ? "service-ready"
                    : detail);
        }

        internal static bool ReleaseUpdateHandoff(
            UpdateStartupInfo updateStartup)
        {
            if (updateStartup == null || !updateStartup.VersionMatches)
                return false;
            if (!updateStartup.AppStartedRecorded)
            {
                UpdateJournal.WriteState(
                    updateStartup.OperationId,
                    "app-journal-failed",
                    updateStartup.ExpectedVersion,
                    Application.StartupPath,
                    "app-started-not-recorded");
                return false;
            }

            // Compatibilidade com o updater antigo: este marcador libera
            // sondas de porta antigas, mas não representa prontidão do
            // serviço. O updater atual exige service-ready seguido de um
            // app-confirmed posterior antes de concluir.
            return UpdateJournal.WriteState(
                updateStartup.OperationId,
                "app-confirmed",
                updateStartup.ExpectedVersion,
                Application.StartupPath,
                "legacy-handoff-released");
        }

        private static bool HasArgument(string[] args, string expected)
        {
            if (args == null) return false;
            foreach (string arg in args)
            {
                if (String.Equals(arg, expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string GetArgumentValue(string[] args, string name)
        {
            if (args == null) return "";
            for (int index = 0; index + 1 < args.Length; index++)
            {
                if (String.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                    return args[index + 1];
            }
            return "";
        }

        private static void RunSelfTest()
        {
            bool valid10 = NetworkDiscovery.IsTailMsgAddress(IPAddress.Parse("10.12.3.4"));
            bool validTailscale = NetworkDiscovery.IsTailMsgAddress(IPAddress.Parse("100.100.10.20"));
            bool invalidTailscale = NetworkDiscovery.IsTailMsgAddress(IPAddress.Parse("100.63.10.20"));
            string encoded = TailMsgProtocol.Encode("Olá | TailMsg");
            string decoded = TailMsgProtocol.Decode(encoded);
            List<IPAddress> tailscaleFixture = TailscaleDiscovery.ParsePeerAddressesForTest(
                "{\"PeerA\":{\"TailscaleIPs\":[\"100.110.211.23\"]}," +
                "\"PeerB\":{\"TailscaleIPs\":[\"100.127.255.254\",\"100.63.1.2\"]}}" );
            bool tailscaleParserValid = tailscaleFixture.Count == 2 &&
                tailscaleFixture.Contains(IPAddress.Parse("100.110.211.23")) &&
                tailscaleFixture.Contains(IPAddress.Parse("100.127.255.254"));
            string largeMessage = new string(
                'X',
                TailMsgProtocol.MaxGuiMessageCharacters);
            bool largeMessageValid =
                TailMsgProtocol.Decode(TailMsgProtocol.Encode(largeMessage)) ==
                largeMessage;

            string imageFailure = ImageSelfTests.ProtocolFailure();
            if (imageFailure.Length == 0)
            {
                imageFailure = ImageSelfTests.PastePolicyFailure();
            }
            if (imageFailure.Length == 0)
            {
                imageFailure = ImageSelfTests.ThumbnailFailure();
            }
            if (imageFailure.Length == 0)
            {
                imageFailure = AudioSelfTests.ProtocolFailure();
            }
            if (imageFailure.Length == 0)
            {
                imageFailure = AudioSelfTests.TranscriptionFailure();
            }

            if (!valid10 || !validTailscale || invalidTailscale ||
                decoded != "Olá | TailMsg" || !largeMessageValid ||
                !tailscaleParserValid || imageFailure.Length > 0)
            {
                Console.Error.WriteLine(
                    "Falha no teste do protocolo de rede." +
                    (imageFailure.Length > 0
                        ? " Imagem: " + imageFailure
                        : ""));
                Environment.ExitCode = 1;
                return;
            }

            Console.WriteLine(
                "OK - protocolo, filtros de endereço, política de colagem, " +
                "imagem, áudio e leitura de transcrição funcionando.");
        }

        private static void RunIntegrationSelfTest(string operationId)
        {
            string failure;
            if (IntegrationSelfTest.TryRun(operationId, out failure))
            {
                Console.WriteLine(
                    "OK - descoberta UDP, envio TCP, ACK, imagem e áudio em " +
                    "blocos funcionando.");
                return;
            }

            Console.Error.WriteLine("Falha no teste integrado: " + failure);
            Environment.ExitCode = 1;
        }

        private static void RunDiagnostics(string remoteAddressArg)
        {
            bool consoleAvailable = true;
            try
            {
                Console.Write("");
            }
            catch
            {
                consoleAvailable = false;
            }

            StringBuilder report = new StringBuilder();
            report.AppendLine("TailMsg --diagnose (Windows/" +
                Environment.OSVersion.VersionString + ")");
            report.AppendLine("Wine detectado: " + (WineEnvironment.IsWine ? "SIM" : "nao"));
            report.AppendLine();

            List<NetworkEndpoint> endpoints = NetworkDiscovery.GetEndpoints();
            report.AppendLine("[1] Interfaces 10.x/100.x encontradas: " + endpoints.Count);
            foreach (NetworkEndpoint endpoint in endpoints)
            {
                report.AppendLine("    local=" + endpoint.LocalAddress +
                    " broadcast=" + (endpoint.BroadcastAddress ?? "-") +
                    " mask=" + (endpoint.Mask ?? "-"));
            }
            if (endpoints.Count == 0)
            {
                report.AppendLine("    AVISO: nenhuma interface elegivel -> a descoberta nao" +
                    " envia pacotes e a lista fica vazia.");
            }

            List<IPAddress> tailscalePeers = TailscaleDiscovery.FindPeerAddresses();
            report.AppendLine("[2] Peers Tailscale via 'tailscale status': " + tailscalePeers.Count +
                (tailscalePeers.Count > 0
                    ? " (fonte: " + TailscaleDiscovery.LastSuccessSource + ")"
                    : ""));
            if (WineEnvironment.IsWine)
            {
                report.AppendLine("    Tentativa Wine: " +
                    (TailscaleDiscovery.LastAttemptSummary ?? "-"));
            }
            foreach (IPAddress peer in tailscalePeers)
            {
                report.AppendLine("    " + peer);
            }
            if (tailscalePeers.Count == 0 && WineEnvironment.IsWine)
            {
                report.AppendLine("    DICA: se o Tailscale estiver instalado no Linux," +
                    " execute novamente com --diagnose e verifique 'which tailscale'" +
                    " e a permissao da LocalAPI no Linux.");
            }

            bool udpLoopback = false;
            string udpError = "";
            try
            {
                using (UdpClient listener = new UdpClient(0))
                {
                    IPEndPoint bound = (IPEndPoint)listener.Client.LocalEndPoint;
                    listener.Client.ReceiveTimeout = 1500;
                    byte[] payload = Encoding.UTF8.GetBytes("tailmsg-diag");
                    listener.EnableBroadcast = true;
                    listener.Send(payload, payload.Length, "127.0.0.1", bound.Port);
                    IPEndPoint source = new IPEndPoint(IPAddress.Any, 0);
                    byte[] received = listener.Receive(ref source);
                    udpLoopback = Encoding.UTF8.GetString(received) == "tailmsg-diag";
                }
            }
            catch (Exception exception)
            {
                udpError = exception.Message;
            }
            report.AppendLine("[3] UDP loopback (bind/send/receive): " +
                (udpLoopback ? "OK" : "FALHOU" +
                    (udpError.Length > 0 ? " - " + udpError : "")));

            bool multicastJoin = false;
            string multicastError = "";
            try
            {
                using (UdpClient multicast = new UdpClient(0))
                {
                    IPAddress joinAddress = IPAddress.Parse(NetworkService.DiscoveryMulticast);
                    if (endpoints.Count > 0)
                    {
                        multicast.JoinMulticastGroup(
                            joinAddress,
                            IPAddress.Parse(endpoints[0].LocalAddress));
                    }
                    else
                    {
                        multicast.JoinMulticastGroup(joinAddress);
                    }
                    multicastJoin = true;
                }
            }
            catch (Exception exception)
            {
                multicastError = exception.Message;
            }
            report.AppendLine("[4] Multicast join " + NetworkService.DiscoveryMulticast + ": " +
                (multicastJoin ? "OK" : "FALHOU - " + multicastError));

            report.AppendLine("[5] Endpoints de descoberta usados pela GUI:");
            foreach (IPAddress target in NetworkDiscovery.GetDiscoveryTargets())
            {
                report.AppendLine("    " + target);
            }

            if (!String.IsNullOrEmpty(remoteAddressArg))
            {
                IPAddress remote;
                if (IPAddress.TryParse(remoteAddressArg, out remote))
                {
                    string tcpError = "";
                    bool tcpOk = false;
                    try
                    {
                        using (TcpClient probe = new TcpClient())
                        {
                            IAsyncResult connection = probe.BeginConnect(remote, NetworkService.TcpPort, null, null);
                            tcpOk = connection.AsyncWaitHandle.WaitOne(3000);
                            if (tcpOk)
                            {
                                probe.EndConnect(connection);
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        tcpError = exception.Message;
                    }
                    report.AppendLine("[6] TCP " + remoteAddressArg + ":" + NetworkService.TcpPort +
                        " (porta de mensagem): " + (tcpOk ? "ABERTA" : "SEM RESPOSTA" +
                        (tcpError.Length > 0 ? " - " + tcpError : "")));
                }
                else
                {
                    report.AppendLine("[6] Endereco invalido para o teste TCP: " + remoteAddressArg);
                }
            }
            else
            {
                report.AppendLine("[6] Teste TCP pulado. Para testar, execute:" +
                    " TailMsg.exe --diagnose 10.x.x.x");
            }

            string reportText = report.ToString();
            if (consoleAvailable)
            {
                Console.Write(reportText);
            }

            try
            {
                string logDirectory = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "TailMsg");
                Directory.CreateDirectory(logDirectory);
                string logPath = Path.Combine(
                    logDirectory,
                    "tailmsg-diagnose.txt");
                File.WriteAllText(logPath, reportText, Encoding.UTF8);
                if (consoleAvailable)
                {
                    Console.WriteLine("Relatorio salvo em: " + logPath);
                }
            }
            catch { }
        }
    }

    internal static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static void EnsureRegistered()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    key.SetValue(
                        "TailMsg",
                        "\"" + Application.ExecutablePath + "\" --background",
                        RegistryValueKind.String);
                }
            }
            catch
            {
                // O mensageiro continua funcional mesmo se a política do
                // Windows impedir alterações na inicialização do usuário.
            }
        }
    }

    internal static class NotificationSettings
    {
        private const string SettingsKey = @"Software\TailMsg";
        private const string TransparencyValue = "NotificationTransparencyPercent";
        public const int DefaultTransparencyPercent = 30;
        public const int MinimumTransparencyPercent = 0;
        public const int MaximumTransparencyPercent = 80;

        public static int TransparencyPercent
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey))
                    {
                        if (key != null)
                        {
                            object value = key.GetValue(TransparencyValue);
                            int parsed = Convert.ToInt32(value);
                            return Normalize(parsed);
                        }
                    }
                }
                catch { }
                return DefaultTransparencyPercent;
            }
        }

        public static double WindowOpacity
        {
            get { return 1.0D - (TransparencyPercent / 100.0D); }
        }

        public static void SetTransparencyPercent(int percent)
        {
            percent = Normalize(percent);
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    if (key != null)
                    {
                        key.SetValue(
                            TransparencyValue,
                            percent,
                            RegistryValueKind.DWord);
                    }
                }
            }
            catch { }
        }

        private static int Normalize(int percent)
        {
            if (percent < MinimumTransparencyPercent)
                return MinimumTransparencyPercent;
            if (percent > MaximumTransparencyPercent)
                return MaximumTransparencyPercent;
            return percent;
        }
    }

    internal static class AppResources
    {
        private static Icon applicationIcon;

        public static Icon ApplicationIcon
        {
            get
            {
                if (applicationIcon == null)
                {
                    applicationIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                }
                return applicationIcon;
            }
        }

        public static Bitmap LoadAboutImage()
        {
            Assembly assembly = typeof(AppResources).Assembly;
            using (Stream resource = assembly.GetManifestResourceStream("TailMsg.AppWinImage"))
            {
                if (resource != null)
                {
                    using (Image source = Image.FromStream(resource))
                    {
                        return new Bitmap(source);
                    }
                }
            }

            string developmentPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "assets",
                "appwin.png");
            if (File.Exists(developmentPath))
            {
                using (Image source = Image.FromFile(developmentPath))
                {
                    return new Bitmap(source);
                }
            }

            return null;
        }

        // Ícones de áudio embutidos no executável (assets\*.png, 256x256 com
        // transparência). Devolve null se o recurso não existir, para o botão
        // cair no desenho vetorial.
        public static Image LoadAudioIcon(string resourceName)
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream resource = assembly.GetManifestResourceStream(resourceName))
                {
                    if (resource == null) return null;
                    using (Image source = Image.FromStream(resource))
                    {
                        return new Bitmap(source);
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        public static Image AudioIconWhiteMicrophone()
        {
            return LoadAudioIcon("TailMsg.IconMicWhite");
        }

        public static Image AudioIconRedMicrophone()
        {
            return LoadAudioIcon("TailMsg.IconMicRed");
        }

        public static Image AudioIconImage()
        {
            return LoadAudioIcon("TailMsg.IconImage");
        }

        public static Image AudioIconPause()
        {
            return LoadAudioIcon("TailMsg.IconPause");
        }
    }

    internal sealed class AboutForm : Form
    {
        private readonly Panel canvas;
        private Bitmap aboutImage;

        public AboutForm(Form owner)
        {
            Text = "Sobre";
            ClientSize = new Size(420, 638);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.Black;
            ShowInTaskbar = false;
            Owner = owner;

            canvas = new Panel();
            canvas.Dock = DockStyle.Fill;
            canvas.BackColor = Color.Black;
            Controls.Add(canvas);

            PictureBox picture = new PictureBox();
            picture.Location = new Point(2, 0);
            picture.Size = new Size(415, 556);
            picture.SizeMode = PictureBoxSizeMode.StretchImage;
            picture.BackColor = Color.FromArgb(20, 32, 31);
            try
            {
                aboutImage = AppResources.LoadAboutImage();
                picture.Image = aboutImage;
            }
            catch
            {
                picture.Image = null;
            }
            canvas.Controls.Add(picture);

            Label institution = new Label();
            institution.AutoSize = false;
            institution.Text = "Delegacia de Taguaí";
            institution.TextAlign = ContentAlignment.MiddleCenter;
            institution.ForeColor = Color.White;
            institution.Font = new Font("Segoe UI Semibold", 13F);
            institution.Location = new Point(0, 574);
            institution.Size = new Size(420, 24);
            canvas.Controls.Add(institution);

            Label department = new Label();
            department.AutoSize = false;
            department.Text = "Setor de Investigações Gerais";
            department.TextAlign = ContentAlignment.MiddleCenter;
            department.ForeColor = Color.FromArgb(225, 240, 239);
            department.Font = new Font("Segoe UI", 10F);
            department.Location = new Point(0, 603);
            department.Size = new Size(420, 22);
            canvas.Controls.Add(department);

            Label version = new Label();
            version.AutoSize = false;
            version.Text = "Versão: " + UpdateConfig.CurrentVersion;
            version.TextAlign = ContentAlignment.MiddleCenter;
            version.ForeColor = Color.FromArgb(225, 240, 239);
            version.Font = new Font("Segoe UI", 9F);
            version.Location = new Point(0, 626);
            version.Size = new Size(420, 12);
            canvas.Controls.Add(version);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && aboutImage != null)
            {
                aboutImage.Dispose();
                aboutImage = null;
            }
            base.Dispose(disposing);
        }
    }

    internal static class SingleInstanceChannel
    {
        private static readonly IntPtr HwndBroadcast = new IntPtr(0xffff);
        public static readonly int ShowMessage = RegisterWindowMessage(
            "TailMsg.ShowWindow.8E47A034");

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(
            IntPtr windowHandle,
            int message,
            IntPtr wordParameter,
            IntPtr longParameter);

        public static void SendShowCommand()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                PostMessage(HwndBroadcast, ShowMessage, IntPtr.Zero, IntPtr.Zero);
                if (attempt < 2) Thread.Sleep(120);
            }
        }
    }

    internal static class CommandLineMode
    {
        public static void Send(
            string computerName,
            string message,
            bool quiet)
        {
            computerName = (computerName ?? "").Trim();
            message = (message ?? "").Trim();

            if (computerName.Length == 0 || message.Length == 0)
            {
                Report(
                    quiet,
                    false,
                    "Uso:\r\n\r\ntailmsg [--quiet|--silent] NOME_DO_PC \"Mensagem\"",
                    "TailMsg - linha de comando",
                    MessageBoxIcon.Information);
                Environment.ExitCode = 1;
                return;
            }

            if (message.Length > TailMsgProtocol.MaxCommandLineMessageCharacters)
            {
                Report(
                    quiet,
                    false,
                    "A mensagem excede o limite de " +
                    TailMsgProtocol.MaxCommandLineMessageCharacters +
                    " caracteres da linha de comando.",
                    "TailMsg - erro",
                    MessageBoxIcon.Error);
                Environment.ExitCode = 1;
                return;
            }

            string operationId = TailMsgDiagnostics.CreateOperationId();
            string fingerprint = TailMsgDiagnostics.ComputeFingerprint(
                Environment.MachineName,
                message);
            TailMsgDiagnostics.WriteMessageEvent(
                operationId,
                "attempt_started",
                "pending",
                computerName,
                "",
                fingerprint,
                0,
                "source=cli");
            PeerInfo peer;
            string discoveryError;
            if (!CommandLineDiscovery.TryFindComputer(
                computerName,
                4000,
                operationId,
                out peer,
                out discoveryError))
            {
                Report(
                    quiet,
                    false,
                    discoveryError,
                    "TailMsg - computador não encontrado",
                    MessageBoxIcon.Error);
                Environment.ExitCode = 1;
                return;
            }

            MessageSendResult result = MessageSender.Send(
                peer,
                Environment.MachineName,
                message,
                operationId);
            if (result.Success)
            {
                Report(
                    quiet,
                    true,
                    "Mensagem entregue a " + peer.Name + ".\r\n\r\nEndereço usado: " + peer.Address,
                    "TailMsg - mensagem enviada",
                    MessageBoxIcon.Information);
            }
            else
            {
                Report(
                    quiet,
                    false,
                    "Falha ao enviar para " + peer.Name + " (" + peer.Address + ").\r\n\r\n" +
                    result.ErrorMessage,
                    "TailMsg - erro no envio",
                    MessageBoxIcon.Error);
                Environment.ExitCode = 1;
            }
        }

        private static void Report(
            bool quiet,
            bool success,
            string text,
            string title,
            MessageBoxIcon icon)
        {
            if (quiet)
            {
                string oneLine = text.Replace("\r", " ").Replace("\n", " ");
                if (success) Console.WriteLine("OK: " + oneLine);
                else Console.Error.WriteLine("ERRO: " + oneLine);
                return;
            }

            MessageBox.Show(
                text,
                title,
                MessageBoxButtons.OK,
                icon);
        }
    }

    internal static class CommandLineDiscovery
    {
        public static bool TryFindComputer(
            string computerName,
            int waitMilliseconds,
            string operationId,
            out PeerInfo selectedPeer,
            out string error)
        {
            selectedPeer = null;
            error = "";
            List<PeerInfo> matches = new List<PeerInfo>();
            DateTime started = DateTime.UtcNow;
            TailMsgDiagnostics.WriteMessageEvent(
                operationId,
                "discovery_started",
                "pending",
                computerName,
                "",
                "",
                0,
                "targets=" + NetworkDiscovery.GetDiscoveryTargets().Count);

            try
            {
                using (UdpClient client = new UdpClient(0))
                {
                    client.EnableBroadcast = true;
                    client.Client.ReceiveTimeout = 250;
                    byte[] request = Encoding.UTF8.GetBytes(
                        TailMsgProtocol.BuildDiscoveryRequest(Environment.MachineName));

                    foreach (IPAddress address in NetworkDiscovery.GetDiscoveryTargets())
                    {
                        try
                        {
                            IPEndPoint target = new IPEndPoint(address, NetworkService.DiscoveryPort);
                            client.Send(request, request.Length, target);
                        }
                        catch { }
                    }

                    DateTime deadline = DateTime.UtcNow.AddMilliseconds(waitMilliseconds);
                    DateTime fallbackDeadline = deadline;
                    bool fallbackFound = false;

                    while (DateTime.UtcNow < deadline &&
                        (!fallbackFound || DateTime.UtcNow < fallbackDeadline))
                    {
                        try
                        {
                            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                            byte[] response = client.Receive(ref remote);
                            ReadResponse(response, remote, computerName, matches);

                            PeerInfo preferred = FindDelegaciaPeer(matches);
                            if (preferred != null)
                            {
                                selectedPeer = preferred;
                                TailMsgDiagnostics.WriteMessageEvent(
                                    operationId,
                                    "discovery_completed",
                                    "success",
                                    selectedPeer.Name,
                                    selectedPeer.Address,
                                    "",
                                    ElapsedMilliseconds(started),
                                    "priority=delegacia");
                                return true;
                            }

                            if (matches.Count > 0 && !fallbackFound)
                            {
                                // Aguarda somente uma janela curta para um
                                // possível resultado 10.x, que tem prioridade.
                                fallbackFound = true;
                                fallbackDeadline = DateTime.UtcNow.AddMilliseconds(10);
                                client.Client.ReceiveTimeout = 10;
                            }
                        }
                        catch (SocketException exception)
                        {
                            if (exception.SocketErrorCode != SocketError.TimedOut &&
                                exception.SocketErrorCode != SocketError.ConnectionReset &&
                                exception.SocketErrorCode != SocketError.HostUnreachable &&
                                exception.SocketErrorCode != SocketError.NetworkUnreachable)
                            {
                                throw;
                            }
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                error = "A descoberta de rede falhou: " + exception.Message;
                TailMsgDiagnostics.WriteMessageEvent(
                    operationId,
                    "discovery_completed",
                    "failed",
                    computerName,
                    "",
                    "",
                    ElapsedMilliseconds(started),
                    exception.Message);
                return false;
            }

            if (matches.Count == 0)
            {
                error = "Nenhum TailMsg ativo com o nome \"" + computerName + "\" foi encontrado.\r\n\r\n" +
                    "Verifique se o computador está ligado, se o TailMsg está em segundo plano e se o firewall permite UDP 38258.";
                TailMsgDiagnostics.WriteMessageEvent(
                    operationId,
                    "discovery_completed",
                    "not_found",
                    computerName,
                    "",
                    "",
                    ElapsedMilliseconds(started),
                    "matches=0");
                return false;
            }

            matches.Sort(delegate(PeerInfo left, PeerInfo right)
            {
                bool leftDelegacia = NetworkDiscovery.IsDelegaciaAddress(IPAddress.Parse(left.Address));
                bool rightDelegacia = NetworkDiscovery.IsDelegaciaAddress(IPAddress.Parse(right.Address));
                if (leftDelegacia != rightDelegacia) return leftDelegacia ? -1 : 1;
                return StringComparer.OrdinalIgnoreCase.Compare(left.Address, right.Address);
            });

            selectedPeer = matches[0];
            TailMsgDiagnostics.WriteMessageEvent(
                operationId,
                "discovery_completed",
                "success",
                selectedPeer.Name,
                selectedPeer.Address,
                "",
                ElapsedMilliseconds(started),
                "matches=" + matches.Count);
            return true;
        }

        private static long ElapsedMilliseconds(DateTime started)
        {
            return (long)(DateTime.UtcNow - started).TotalMilliseconds;
        }

        private static PeerInfo FindDelegaciaPeer(List<PeerInfo> peers)
        {
            foreach (PeerInfo peer in peers)
            {
                IPAddress address;
                if (IPAddress.TryParse(peer.Address, out address) &&
                    NetworkDiscovery.IsDelegaciaAddress(address))
                {
                    return peer;
                }
            }
            return null;
        }

        private static void ReadResponse(
            byte[] response,
            IPEndPoint remote,
            string wantedName,
            List<PeerInfo> matches)
        {
            string[] pieces = Encoding.UTF8.GetString(response).Split('|');
            if (pieces.Length < 5 ||
                pieces[0] != TailMsgProtocol.DiscoveryResponse ||
                pieces[1] != "1")
            {
                return;
            }

            string name;
            int port;
            try
            {
                name = TailMsgProtocol.Decode(pieces[2]);
            }
            catch
            {
                return;
            }

            if (!String.Equals(name, wantedName, StringComparison.OrdinalIgnoreCase) ||
                !Int32.TryParse(pieces[4], out port) ||
                port <= 0 || port >= 65536 ||
                !NetworkDiscovery.IsTailMsgAddress(remote.Address))
            {
                return;
            }

            string address = remote.Address.ToString();
            foreach (PeerInfo match in matches)
            {
                if (match.Address == address && match.Port == port) return;
            }

            PeerInfo peer = new PeerInfo();
            peer.Name = name;
            peer.Address = address;
            peer.Port = port;
            peer.IsLocal = NetworkDiscovery.IsLocalAddress(address);
            matches.Add(peer);
        }
    }

    // Decisão de colagem: imagem sem texto é consumida pela política (o
    // controle nativo não vê o Ctrl+V); imagem junto com texto anexa a imagem
    // e deixa o texto ser colado normalmente.
    internal enum PasteDecision
    {
        None = 0,
        ImageOnly = 1,
        ImageAndText = 2
    }

    internal static class ClipboardPastePolicy
    {
        public static PasteDecision Decide(bool hasImage, bool hasText)
        {
            if (!hasImage) return PasteDecision.None;
            return hasText ? PasteDecision.ImageAndText : PasteDecision.ImageOnly;
        }

        public static bool ShouldConsume(PasteDecision decision)
        {
            return decision == PasteDecision.ImageOnly;
        }
    }

    internal static class ClipboardImageReader
    {
        private const int ClipboardAttempts = 3;

        public static bool TryProbe(out bool hasImage, out bool hasText)
        {
            hasImage = false;
            hasText = false;
            for (int attempt = 0; attempt < ClipboardAttempts; attempt++)
            {
                try
                {
                    hasImage = Clipboard.ContainsImage();
                    hasText = Clipboard.ContainsText() ||
                        Clipboard.ContainsData(DataFormats.UnicodeText);
                    return true;
                }
                catch (ExternalException)
                {
                    // Outro processo pode estar com a área de transferência
                    // aberta; espera curta antes de desistir.
                    Thread.Sleep(40);
                }
            }
            return false;
        }

        // Converte a imagem da área de transferência para PNG (sem perda) e
        // devolve as dimensões originais.
        public static bool TryReadImage(out ImagePayload payload, out string error)
        {
            payload = null;
            error = "";
            for (int attempt = 0; attempt < ClipboardAttempts; attempt++)
            {
                try
                {
                    if (!Clipboard.ContainsImage())
                    {
                        return false;
                    }

                    using (Image source = Clipboard.GetImage())
                    {
                        if (source == null)
                        {
                            return false;
                        }

                        int width = source.Width;
                        int height = source.Height;
                        if (width <= 0 || height <= 0)
                        {
                            error = "A imagem copiada não tem dimensões válidas.";
                            return false;
                        }

                        using (MemoryStream buffer = new MemoryStream())
                        {
                            source.Save(buffer, ImageFormat.Png);
                            ImagePayload result = new ImagePayload();
                            result.PngBytes = buffer.ToArray();
                            result.Width = width;
                            result.Height = height;
                            payload = result;
                            return true;
                        }
                    }
                }
                catch (ExternalException)
                {
                    Thread.Sleep(40);
                }
                catch (Exception exception)
                {
                    error = "Não foi possível ler a imagem copiada: " +
                        exception.Message;
                    return false;
                }
            }

            error = "A área de transferência está em uso por outro programa. Tente colar novamente.";
            return false;
        }
    }

    // Caixa de mensagem que transforma Ctrl+V de imagem em anexo. O texto
    // continua sendo colado pelo controle nativo quando houver texto junto.
    internal sealed class MessageTextBox : TextBox
    {
        private const int WmKeyDown = 0x0100;
        private const int WmPaste = 0x0302;

        public event EventHandler<ImagePastedEventArgs> ImagePasted;
        public event EventHandler<ImagePasteFailedEventArgs> ImagePasteFailed;

        private bool captureEnabled = true;
        private bool suppressEditNotification;

        // Disparado quando o USUÁRIO mexe no texto (não quando o app escreve a
        // transcrição do áudio).
        public event EventHandler UserEdited;

        internal bool CaptureEnabled
        {
            get { return captureEnabled; }
            set { captureEnabled = value; }
        }

        internal bool SuppressEditNotification
        {
            get { return suppressEditNotification; }
            set { suppressEditNotification = value; }
        }

        // Escreve a transcrição do áudio sem marcar como edição do usuário.
        internal void SetProgrammaticText(string text)
        {
            suppressEditNotification = true;
            try
            {
                Text = text == null ? "" : text;
                SelectionStart = TextLength;
                ScrollToCaret();
            }
            finally
            {
                suppressEditNotification = false;
            }
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            if (suppressEditNotification) return;
            EventHandler handler = UserEdited;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (IsPasteShortcut(keyData) || IsShiftInsert(keyData))
            {
                bool consume;
                if (TryCaptureImage(out consume) && consume)
                {
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmPaste)
            {
                bool consume;
                if (TryCaptureImage(out consume) && consume)
                {
                    return;
                }
            }
            else if (m.Msg == WmKeyDown && IsShiftInsertKeyDown(m))
            {
                bool consume;
                if (TryCaptureImage(out consume) && consume)
                {
                    return;
                }
            }
            base.WndProc(ref m);
        }

        private static bool IsPasteShortcut(Keys keyData)
        {
            return (keyData & Keys.KeyCode) == Keys.V &&
                (keyData & Keys.Control) == Keys.Control;
        }

        private static bool IsShiftInsert(Keys keyData)
        {
            return (keyData & Keys.KeyCode) == Keys.Insert &&
                (keyData & Keys.Shift) == Keys.Shift;
        }

        private static bool IsShiftInsertKeyDown(Message m)
        {
            int virtualKey = m.WParam.ToInt32() & 0xFFFF;
            if (virtualKey != 0x2D) return false;   // VK_INSERT
            return (ModifierKeys & Keys.Shift) == Keys.Shift;
        }

        // Tenta capturar a imagem do clipboard. Devolve true quando a política
        // anexou a imagem; consume indica se o evento nativo deve ser abortado.
        internal bool TryCaptureImage(out bool consume)
        {
            consume = false;
            if (!captureEnabled) return false;

            bool hasImage;
            bool hasText;
            if (!ClipboardImageReader.TryProbe(out hasImage, out hasText))
            {
                return false;
            }

            PasteDecision decision = ClipboardPastePolicy.Decide(hasImage, hasText);
            if (decision == PasteDecision.None)
            {
                return false;
            }

            ImagePayload payload;
            string error;
            if (!ClipboardImageReader.TryReadImage(out payload, out error))
            {
                if (!String.IsNullOrEmpty(error))
                {
                    ImagePasteFailedEventArgs failure = new ImagePasteFailedEventArgs();
                    failure.ErrorMessage = error;
                    EventHandler<ImagePasteFailedEventArgs> failedHandler = ImagePasteFailed;
                    if (failedHandler != null) failedHandler(this, failure);
                }
                return false;
            }

            ImagePastedEventArgs args = new ImagePastedEventArgs();
            args.Image = payload;
            EventHandler<ImagePastedEventArgs> handler = ImagePasted;
            if (handler != null) handler(this, args);

            consume = ClipboardPastePolicy.ShouldConsume(decision);
            return true;
        }
    }

    internal sealed class ImagePastedEventArgs : EventArgs
    {
        public ImagePayload Image;
    }

    internal sealed class ImagePasteFailedEventArgs : EventArgs
    {
        public string ErrorMessage;
    }

    internal static class ImageTransfer
    {
        public static Bitmap CreateThumbnail(
            byte[] pngBytes,
            int maximumWidth,
            int maximumHeight,
            out string error)
        {
            error = "";
            try
            {
                using (MemoryStream buffer = new MemoryStream(pngBytes))
                using (Image decoded = Image.FromStream(buffer))
                {
                    double scale = Math.Min(
                        (double)maximumWidth / decoded.Width,
                        (double)maximumHeight / decoded.Height);
                    if (scale > 1.0) scale = 1.0;
                    int width = Math.Max(1, (int)Math.Round(decoded.Width * scale));
                    int height = Math.Max(1, (int)Math.Round(decoded.Height * scale));
                    Bitmap thumbnail = new Bitmap(width, height);
                    using (Graphics graphics = Graphics.FromImage(thumbnail))
                    {
                        graphics.InterpolationMode =
                            System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode =
                            System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        graphics.DrawImage(decoded, 0, 0, width, height);
                    }
                    return thumbnail;
                }
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return null;
            }
        }

        public static string DescribeDimensions(int width, int height)
        {
            return width.ToString(CultureInfo.InvariantCulture) + "x" +
                height.ToString(CultureInfo.InvariantCulture);
        }

        public static string DescribeBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L)
            {
                return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            }
            if (bytes >= 1024L)
            {
                return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
            }
            return bytes.ToString(CultureInfo.InvariantCulture) + " bytes";
        }

        public static bool IsLarge(long bytes)
        {
            return bytes > TailMsgProtocol.LargeImageWarningBytes;
        }

        // Decodifica a imagem original para colocar na área de transferência.
        public static bool TryCopyToClipboard(byte[] pngBytes, out string error)
        {
            error = "";
            try
            {
                using (MemoryStream buffer = new MemoryStream(pngBytes))
                using (Image decoded = Image.FromStream(buffer))
                using (Bitmap copy = new Bitmap(decoded))
                {
                    Clipboard.SetImage(copy);
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private const int SwRestore = 9;
        // Diâmetro dos botões redondos de microfone (igual à altura do Enviar).
        private const int MicButtonSize = 44;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr windowHandle);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr windowHandle, int command);

        private readonly TableLayoutPanel computerList;
        private readonly ToolTip peerTooltip;
        private readonly MessageTextBox messageBox;
        private VScrollBar peerScroll;
        private Panel peerViewport;
        private Panel peerClip;
        private Panel peerListBorder;
        private readonly InboxPanel inboxBox;
        private readonly Button sendButton;
        private readonly Button updateButton;
        private readonly CheckBox delegaciaCheckBox;
        private readonly CheckBox tailscaleCheckBox;
        private readonly Label statusLabel;
        private readonly Label countLabel;
        private readonly string localComputerName;
        private readonly NetworkService networkService;
        private readonly NotifyIcon notifyIcon;
        private readonly System.Windows.Forms.Timer discoveryTimer;
        private readonly System.Windows.Forms.Timer restoreTimer;
        private readonly bool startHidden;
        private readonly bool disableNetwork;
        private readonly UpdateStartupInfo updateStartup;
        private readonly bool exitAfterUpdateConfirmation;
        private readonly bool forceServiceFailure;
        private AboutForm aboutForm;
        private readonly List<ReceivedMessageForm> receivedNotifications =
            new List<ReceivedMessageForm>();
        private Panel imagePreviewPanel;
        private Panel imagePreviewBorder;
        private PictureBox imagePreviewBox;
        private Label imagePreviewLabel;
        private TableLayoutPanel contentLayout;
        private ImagePayload pendingImage;
        private Bitmap pendingImageThumbnail;
        private AudioTrackPanel audioPreviewPanel;
        private IconButton recordButton;          // microfone branco
        private IconButton liveMicButton;         // microfone vermelho
        private IconButton pauseButton;           // pausa/play do meio
        private WaveRecorder recorder;
        private System.Windows.Forms.Timer recordingTimer;
        private AudioPayload pendingAudio;
        // Quando verdadeiro, o texto da caixa de mensagem é a transcrição do
        // áudio gravado (apenas informativa: só o áudio é enviado).
        private bool messageBoxIsTranscription;
        private bool isRecordingAudio;
        private bool recordingLive;
        private bool isPausedAudio;
        private int diagnosticTicks;
        // Transcrição incremental do microfone vermelho: texto confirmado
        // (janelas de 30 s) e rascunho da janela atual.
        private string liveCommittedText = "";
        private string liveDraftText = "";
        private int liveCommitOffset;
        private int liveLastDraftBytes;
        private int liveGeneration;
        private bool liveCommitBusy;
        private List<PeerInfo> latestPeers = new List<PeerInfo>();
        private bool isRefreshing;
        private bool isSending;
        private bool updateInProgress;
        private bool allowExit;
        private UpdateManifest availableUpdate;

        public MainForm(bool startInBackground)
            : this(startInBackground, false, null, false, false)
        {
        }

        public MainForm(
            bool startInBackground,
            bool disableNetwork)
            : this(startInBackground, disableNetwork, null, false, false)
        {
        }

        public MainForm(
            bool startInBackground,
            bool disableNetwork,
            UpdateStartupInfo updateStartup,
            bool exitAfterUpdateConfirmation,
            bool forceServiceFailure)
        {
            startHidden = startInBackground;
            this.disableNetwork = disableNetwork;
            this.updateStartup = updateStartup;
            this.exitAfterUpdateConfirmation = exitAfterUpdateConfirmation;
            this.forceServiceFailure = forceServiceFailure;
            localComputerName = Environment.MachineName;
            networkService = new NetworkService(localComputerName);
            networkService.MessageReceived += NetworkServiceMessageReceived;
            networkService.ImageReceived += NetworkServiceImageReceived;
            networkService.AudioReceived += NetworkServiceAudioReceived;
            networkService.MessageDeleted += NetworkServiceMessageDeleted;

            Text = "TailMsg";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(560, 620);
            Size = new Size(680, 720);
            BackColor = Color.FromArgb(245, 247, 250);
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Icon = AppResources.ApplicationIcon;
            ShowInTaskbar = !startHidden;
            WindowState = FormWindowState.Normal;

            MenuStrip mainMenu = new MenuStrip();
            mainMenu.Dock = DockStyle.Top;
            mainMenu.BackColor = Color.FromArgb(245, 247, 250);
            mainMenu.ForeColor = Color.FromArgb(31, 41, 55);
            mainMenu.Font = new Font("Segoe UI", 9F);
            mainMenu.Padding = new Padding(8, 2, 0, 2);
            mainMenu.Items.Add(new ToolStripMenuItem("Verificar Atualizações", null, delegate { CheckForUpdates(true); }));
            mainMenu.Items.Add(new ToolStripMenuItem("Sobre", null, delegate { OpenAbout(); }));
            Controls.Add(mainMenu);
            mainMenu.BringToFront();

            Panel footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = 72;
            footer.Padding = new Padding(24, 14, 24, 14);
            footer.BackColor = Color.White;
            Controls.Add(footer);

            // Área de ações: microfone branco, pausa (só durante a gravação),
            // microfone vermelho e Enviar — os três botões redondos ficam com a
            // mesma altura do botão Enviar.
            Panel actionArea = new Panel();
            actionArea.Dock = DockStyle.Right;
            actionArea.Width = (MicButtonSize * 3) + 24 + 132 + 12;
            actionArea.BackColor = Color.White;
            footer.Controls.Add(actionArea);

            sendButton = new Button();
            sendButton.Dock = DockStyle.Right;
            sendButton.Width = 132;
            sendButton.Text = "Enviar";
            sendButton.Font = new Font("Segoe UI Semibold", 10F);
            sendButton.FlatStyle = FlatStyle.Flat;
            sendButton.FlatAppearance.BorderSize = 0;
            sendButton.BackColor = Color.FromArgb(37, 99, 235);
            sendButton.ForeColor = Color.White;
            sendButton.Cursor = Cursors.Hand;
            sendButton.Enabled = false;
            sendButton.Click += SendButtonClick;
            actionArea.Controls.Add(sendButton);

            // Microfone branco: grava e, ao encerrar, anexa e transcreve.
            recordButton = new IconButton();
            recordButton.Width = MicButtonSize;
            recordButton.AccessibleName = "Gravar áudio";
            recordButton.Enabled = false;
            recordButton.Click += delegate { ToggleWhiteMicrophone(); };
            actionArea.Controls.Add(recordButton);

            // Pausa do meio: aparece somente enquanto algum microfone grava.
            pauseButton = new IconButton();
            pauseButton.Width = MicButtonSize;
            pauseButton.CircleColor = Color.FromArgb(242, 207, 55);
            pauseButton.CircleOutline = Color.FromArgb(196, 157, 0);
            // O botão fica sempre no layout (slot fixo, como no SIG); fora da
            // gravação ele não desenha nada.
            pauseButton.Glyph = IconGlyph.None;
            pauseButton.AccessibleName = "Pausar gravação";
            pauseButton.Click += delegate { ToggleAudioPause(); };
            actionArea.Controls.Add(pauseButton);

            // Microfone vermelho: grava em janelas e transcreve quase em tempo
            // real, como o microfone vermelho do SIG.
            liveMicButton = new IconButton();
            liveMicButton.Width = MicButtonSize;
            liveMicButton.AccessibleName = "Gravar com transcrição ao vivo";
            liveMicButton.Enabled = false;
            liveMicButton.Click += delegate { ToggleLiveMicrophone(); };
            actionArea.Controls.Add(liveMicButton);

            bool adjustingMicButtons = false;
            EventHandler resizeMicButtons = delegate
            {
                // Ordem fixa da esquerda para a direita: microfone branco,
                // pausa e microfone vermelho — todos quadrados, com a altura do
                // botão Enviar.
                if (adjustingMicButtons) return;
                adjustingMicButtons = true;
                int size = sendButton.Height;
                if (size > 0)
                {
                    int gap = 8;
                    recordButton.Size = new Size(size, size);
                    pauseButton.Size = new Size(size, size);
                    liveMicButton.Size = new Size(size, size);
                    recordButton.Location = new Point(0, 0);
                    pauseButton.Location = new Point(size + gap, 0);
                    liveMicButton.Location = new Point((size + gap) * 2, 0);
                    actionArea.Width = (size * 3) + (gap * 2) + 12 +
                        sendButton.Width;
                }
                adjustingMicButtons = false;
            };
            actionArea.Resize += resizeMicButtons;
            sendButton.Resize += resizeMicButtons;
            resizeMicButtons(null, EventArgs.Empty);
            UpdateRecordButtons();

            statusLabel = new Label();
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.ForeColor = Color.FromArgb(75, 85, 99);
            statusLabel.Text = "Iniciando serviço TailMsg...";
            footer.Controls.Add(statusLabel);

            Panel content = new Panel();
            content.Dock = DockStyle.Fill;
            content.Padding = new Padding(24, 20, 24, 18);
            Controls.Add(content);
            content.BringToFront();

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 10;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 29F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 59F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            // Linhas dos anexos: altura zero enquanto não houver imagem/áudio.
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 12F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0F));
            content.Controls.Add(layout);
            contentLayout = layout;

            updateButton = new Button();
            updateButton.Text = "Atualização disponível";
            updateButton.Size = new Size(190, 36);
            updateButton.Location = new Point(content.ClientSize.Width - updateButton.Width - 24, 2);
            updateButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            updateButton.Font = new Font("Segoe UI Semibold", 9.5F);
            updateButton.FlatStyle = FlatStyle.Flat;
            updateButton.FlatAppearance.BorderSize = 0;
            updateButton.BackColor = Color.FromArgb(16, 185, 129);
            updateButton.ForeColor = Color.White;
            updateButton.Cursor = Cursors.Hand;
            updateButton.Visible = false;
            updateButton.Click += UpdateButtonClick;
            content.Controls.Add(updateButton);
            updateButton.BringToFront();

            TableLayoutPanel destinationArea = new TableLayoutPanel();
            destinationArea.Dock = DockStyle.Fill;
            destinationArea.ColumnCount = 2;
            destinationArea.RowCount = 1;
            destinationArea.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172F));
            destinationArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            destinationArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            destinationArea.Margin = new Padding(0);
            layout.Controls.Add(destinationArea, 0, 0);
            layout.SetRowSpan(destinationArea, 2);

            Panel interfacePanel = new Panel();
            interfacePanel.Dock = DockStyle.Fill;
            interfacePanel.Margin = new Padding(0, 0, 14, 0);
            destinationArea.Controls.Add(interfacePanel, 0, 0);

            Label interfaceLabel = new Label();
            interfaceLabel.AutoSize = true;
            interfaceLabel.Text = "Interfaces exibidas";
            interfaceLabel.Font = new Font("Segoe UI Semibold", 10F);
            interfaceLabel.Location = new Point(0, 5);
            interfacePanel.Controls.Add(interfaceLabel);

            TableLayoutPanel computerArea = new TableLayoutPanel();
            computerArea.Dock = DockStyle.Fill;
            computerArea.ColumnCount = 1;
            computerArea.RowCount = 2;
            computerArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            computerArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            computerArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            computerArea.Margin = new Padding(0);
            destinationArea.Controls.Add(computerArea, 1, 0);

            Panel computerHeader = new Panel();
            computerHeader.Dock = DockStyle.Fill;
            computerArea.Controls.Add(computerHeader, 0, 0);

            Label computerLabel = new Label();
            computerLabel.AutoSize = true;
            computerLabel.Text = "Computador de destino";
            computerLabel.Font = new Font("Segoe UI Semibold", 10F);
            computerLabel.Location = new Point(0, 5);
            computerHeader.Controls.Add(computerLabel);

            countLabel = new Label();
            countLabel.AutoSize = true;
            countLabel.ForeColor = Color.FromArgb(107, 114, 128);
            countLabel.Location = new Point(175, 7);
            computerHeader.Controls.Add(countLabel);

            delegaciaCheckBox = new CheckBox();
            delegaciaCheckBox.AutoSize = true;
            delegaciaCheckBox.Checked = true;
            delegaciaCheckBox.Text = "Rede 10.x.x.x";
            delegaciaCheckBox.Location = new Point(0, 36);
            delegaciaCheckBox.CheckedChanged += InterfaceFilterChanged;
            interfacePanel.Controls.Add(delegaciaCheckBox);

            tailscaleCheckBox = new CheckBox();
            tailscaleCheckBox.AutoSize = true;
            tailscaleCheckBox.Checked = true;
            tailscaleCheckBox.Text = "Tailscale 100.x.x.x";
            tailscaleCheckBox.Location = new Point(0, 66);
            tailscaleCheckBox.CheckedChanged += InterfaceFilterChanged;
            interfacePanel.Controls.Add(tailscaleCheckBox);


            Panel listBorder = new Panel();
            listBorder.Dock = DockStyle.Fill;
            listBorder.Padding = new Padding(0);
            listBorder.BackColor = Color.White;
            computerArea.Controls.Add(listBorder, 0, 1);
            peerListBorder = listBorder;

            peerViewport = new Panel();
            peerViewport.Dock = DockStyle.Fill;
            // Mesmo contorno do histórico: o fundo do viewport aparece na margem
            // de 1 px e a barra de rolagem fica FORA do retângulo.
            peerViewport.BackColor = BoxBorder.LineColor;
            peerViewport.MouseWheel += PeerListMouseWheel;
            listBorder.Controls.Add(peerViewport);

            // Recorte interno: as linhas de destinatários nunca cobrem a margem.
            peerClip = new Panel();
            peerClip.Location = new Point(1, 1);
            peerClip.BackColor = Color.White;
            peerViewport.Controls.Add(peerClip);

            // Faixa sempre reservada, com a barra dentro: o contorno dos
            // destinatários fica na mesma coluna dos outros, com ou sem barra.
            Panel peerStrip = new Panel();
            peerStrip.Dock = DockStyle.Right;
            peerStrip.Width = SystemInformation.VerticalScrollBarWidth;
            peerStrip.BackColor = Color.White;
            listBorder.Controls.Add(peerStrip);

            peerScroll = new VScrollBar();
            peerScroll.Dock = DockStyle.Fill;
            peerScroll.Width = SystemInformation.VerticalScrollBarWidth;
            peerScroll.SmallChange = 24;
            peerScroll.Visible = false;
            peerScroll.Scroll += delegate { LayoutPeerList(); };
            peerStrip.Controls.Add(peerScroll);

            peerTooltip = new ToolTip();
            // O balãozinho com o IP aparece depois de 1 segundo com o mouse parado.
            peerTooltip.InitialDelay = 1000;
            peerTooltip.ReshowDelay = 400;
            peerTooltip.AutoPopDelay = 6000;

            // Uma coluna por interface marcada; cada célula guarda os
            // destinatários daquela interface.
            computerList = new TableLayoutPanel();
            computerList.AutoSize = true;
            computerList.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            computerList.Padding = new Padding(10, 8, 10, 8);
            computerList.BackColor = Color.White;
            computerList.Location = new Point(0, 0);
            computerList.MouseWheel += PeerListMouseWheel;
            // Um único caminho de layout: mudanças na lista ou no container
            // pedem o recálculo, e a guarda impede a recursão entre eles.
            computerList.ControlAdded += delegate { LayoutPeerList(); };
            computerList.ControlRemoved += delegate { LayoutPeerList(); };
            peerClip.Controls.Add(computerList);
            listBorder.SizeChanged += delegate { LayoutPeerList(); };

            Label inboxLabel = new Label();
            inboxLabel.Dock = DockStyle.Fill;
            inboxLabel.Text = "Histórico";
            inboxLabel.Font = new Font("Segoe UI Semibold", 10F);
            inboxLabel.TextAlign = ContentAlignment.BottomLeft;
            layout.Controls.Add(inboxLabel, 0, 3);

            inboxBox = new InboxPanel();
            inboxBox.Dock = DockStyle.Fill;
            inboxBox.Font = new Font("Segoe UI", 9.5F);
            layout.Controls.Add(inboxBox, 0, 4);
            // Só agora o painel existe para receber o pedido de deleção.
            inboxBox.DeleteRequested += InboxDeleteRequested;
            LoadHistoryIntoInbox();

            Label messageLabel = new Label();
            messageLabel.Dock = DockStyle.Fill;
            messageLabel.Text = "Mensagem";
            messageLabel.Font = new Font("Segoe UI Semibold", 10F);
            messageLabel.TextAlign = ContentAlignment.BottomLeft;
            layout.Controls.Add(messageLabel, 0, 5);

            messageBox = new MessageTextBox();
            messageBox.Dock = DockStyle.Fill;
            messageBox.Multiline = true;
            messageBox.ScrollBars = ScrollBars.Vertical;
            messageBox.Font = new Font("Segoe UI", 11F);
            messageBox.BorderStyle = BorderStyle.FixedSingle;
            messageBox.MaxLength = TailMsgProtocol.MaxGuiMessageCharacters;
            messageBox.KeyDown += MessageBoxKeyDown;
            messageBox.UserEdited += delegate { messageBoxIsTranscription = false; };
            messageBox.ImagePasted += MessageImagePasted;
            messageBox.ImagePasteFailed += MessageImagePasteFailed;
            layout.Controls.Add(messageBox, 0, 8);

            Panel previewBorder = new Panel();
            previewBorder.Dock = DockStyle.Fill;
            previewBorder.Padding = new Padding(1);
            previewBorder.BackColor = Color.FromArgb(209, 213, 219);
            previewBorder.Visible = false;
            layout.Controls.Add(previewBorder, 0, 6);
            imagePreviewBorder = previewBorder;

            imagePreviewPanel = new Panel();
            imagePreviewPanel.Dock = DockStyle.Fill;
            imagePreviewPanel.BackColor = Color.White;
            previewBorder.Controls.Add(imagePreviewPanel);

            imagePreviewBox = new PictureBox();
            imagePreviewBox.Size = new Size(56, 56);
            imagePreviewBox.Location = new Point(3, 3);
            imagePreviewBox.SizeMode = PictureBoxSizeMode.Zoom;
            imagePreviewBox.BackColor = Color.FromArgb(243, 244, 246);
            imagePreviewPanel.Controls.Add(imagePreviewBox);

            imagePreviewLabel = new Label();
            imagePreviewLabel.AutoSize = false;
            imagePreviewLabel.Location = new Point(66, 3);
            imagePreviewLabel.Size = new Size(imagePreviewPanel.Width - 150, 56);
            imagePreviewLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            imagePreviewLabel.TextAlign = ContentAlignment.MiddleLeft;
            imagePreviewLabel.ForeColor = Color.FromArgb(55, 65, 81);
            imagePreviewPanel.Controls.Add(imagePreviewLabel);

            Button removeImageButton = new Button();
            removeImageButton.Text = "Remover";
            removeImageButton.Size = new Size(78, 24);
            removeImageButton.Location = new Point(300, 19);
            removeImageButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            removeImageButton.BackColor = Color.FromArgb(55, 65, 81);
            removeImageButton.ForeColor = Color.White;
            removeImageButton.FlatStyle = FlatStyle.Flat;
            removeImageButton.FlatAppearance.BorderSize = 0;
            removeImageButton.Cursor = Cursors.Hand;
            removeImageButton.Click += delegate { ClearPendingImage(); };
            imagePreviewPanel.Controls.Add(removeImageButton);
            imagePreviewPanel.Resize += delegate
            {
                removeImageButton.Location = new Point(
                    Math.Max(70, imagePreviewPanel.ClientSize.Width - removeImageButton.Width - 8),
                    Math.Max(1, (imagePreviewPanel.ClientSize.Height - removeImageButton.Height) / 2));
                imagePreviewLabel.Size = new Size(
                    Math.Max(40, removeImageButton.Left - imagePreviewLabel.Left - 8),
                    Math.Max(20, imagePreviewPanel.ClientSize.Height - 6));
            };

            // Linha do áudio gravado: a timeline entra direto no painel (sem
            // quadro branco) quando o áudio é anexado. A transcrição aparece na
            // própria caixa onde a mensagem é digitada.

            ContextMenu trayMenu = new ContextMenu();
            trayMenu.MenuItems.Add("Abrir TailMsg", delegate { ShowFromTray(); });
            trayMenu.MenuItems.Add("Sair", delegate
            {
                allowExit = true;
                Close();
            });
            notifyIcon = new NotifyIcon();
            notifyIcon.Text = "TailMsg - mensagens da rede";
            notifyIcon.Icon = AppResources.ApplicationIcon;
            notifyIcon.ContextMenu = trayMenu;
            notifyIcon.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowFromTray();
            };
            notifyIcon.Visible = true;

            discoveryTimer = new System.Windows.Forms.Timer();
            discoveryTimer.Interval = 5000;
            discoveryTimer.Tick += delegate { RefreshComputers(); };

            restoreTimer = new System.Windows.Forms.Timer();
            restoreTimer.Interval = 180;
            restoreTimer.Tick += delegate
            {
                restoreTimer.Stop();
                ForceRestoreWindow();
            };

            Shown += delegate
            {
                bool serviceReady = disableNetwork;
                string serviceDetail = disableNetwork
                    ? "network-disabled-test"
                    : "";
                bool showServiceError = false;

                // O updater legado pode manter sondas de porta abertas até
                // receber app-confirmed. Liberar o handoff antes do bind
                // permite que ele termine e libere esses sockets. Este
                // marcador não confirma o update atual: depois do bind,
                // CompleteUpdateStartup grava service-ready e um novo
                // app-confirmed, que é a sequência exigida pelo updater atual.
                if (updateStartup != null && updateStartup.VersionMatches)
                {
                    Program.ReleaseUpdateHandoff(updateStartup);
                }

                bool serviceStartPending =
                    !disableNetwork &&
                    !forceServiceFailure;

                if (serviceStartPending)
                {
                    statusLabel.Text = "Iniciando o serviço de rede...";
                    StartNetworkServiceAsync(updateStartup != null);
                }
                else if (forceServiceFailure)
                {
                    serviceReady = false;
                    serviceDetail = "simulated-service-failure";
                    showServiceError = true;
                    statusLabel.ForeColor = Color.FromArgb(185, 28, 28);
                    statusLabel.Text = "Não foi possível iniciar o serviço.";
                }
                else if (!disableNetwork)
                {
                    try
                    {
                        networkService.Start();
                        serviceReady = true;
                        serviceDetail = "network-service-ready";
                        statusLabel.Text = "";
                    }
                    catch (Exception exception)
                    {
                        serviceDetail = exception.Message;
                        showServiceError = true;
                        statusLabel.ForeColor = Color.FromArgb(185, 28, 28);
                        statusLabel.Text = "Não foi possível iniciar o serviço.";
                    }
                }
                else
                {
                    statusLabel.Text = "Modo de validação: rede desativada.";
                }

                if (updateStartup != null && !serviceStartPending)
                {
                    Program.CompleteUpdateStartup(
                        updateStartup,
                        serviceReady,
                        serviceDetail);
                }

                if (updateStartup != null && exitAfterUpdateConfirmation)
                {
                    allowExit = true;
                    if (forceServiceFailure)
                    {
                        // O cenário de falha precisa encerrar a instância
                        // antes que o updater tente remover os arquivos para
                        // o rollback; usar BeginInvoke aqui cria uma corrida.
                        Close();
                    }
                    else
                    {
                        BeginInvoke((MethodInvoker)delegate { Close(); });
                    }
                    return;
                }

                // A falha de rede durante uma atualização já não deve fechar
                // a nova instância: o updater foi confirmado pelo processo e
                // o serviço pode ser recuperado na próxima tentativa normal.
                if (showServiceError && updateStartup == null)
                {
                    MessageBox.Show(
                        this,
                        serviceDetail,
                        "TailMsg",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }

                if (!disableNetwork &&
                    !serviceStartPending &&
                    (updateStartup == null || serviceReady))
                {
                    RefreshComputers();
                    discoveryTimer.Start();
                    CheckForUpdates(false);
                }

                if (startHidden)
                {
                    discoveryTimer.Stop();
                    Hide();
                    // A recriação do handle ocorre uma única vez enquanto a
                    // janela ainda está oculta. Nos cliques futuros a bandeja
                    // não precisa alternar ShowInTaskbar e não herda o estado
                    // minimizado do shell.
                    ShowInTaskbar = true;
                    WindowState = FormWindowState.Normal;
                    Handle.ToInt64();
                }
                else
                {
                    messageBox.Focus();
                }
            };
        }

        private void StartNetworkServiceAsync(bool extendedRetry)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool serviceReady = false;
                string serviceDetail = "";
                try
                {
                    if (extendedRetry)
                        networkService.StartForUpdate();
                    else
                        networkService.Start();
                    serviceReady = true;
                    serviceDetail = "network-service-ready";
                }
                catch (Exception exception)
                {
                    serviceDetail = exception.Message;
                }

                TryBeginInvoke(delegate
                {
                    if (IsDisposed) return;

                    bool updateConfirmed = updateStartup == null ||
                        Program.CompleteUpdateStartup(
                            updateStartup,
                            serviceReady,
                            serviceDetail);
                    if (serviceReady && updateConfirmed)
                    {
                        statusLabel.ForeColor = Color.FromArgb(75, 85, 99);
                        statusLabel.Text = "";
                        RefreshComputers();
                        discoveryTimer.Start();
                        CheckForUpdates(false);
                        return;
                    }

                    statusLabel.ForeColor = Color.FromArgb(185, 28, 28);
                    statusLabel.Text = "Não foi possível iniciar o serviço.";
                    if (updateStartup == null)
                    {
                        MessageBox.Show(
                            this,
                            serviceDetail,
                            "TailMsg",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                });
            });
        }

        private void OpenAbout()
        {
            if (aboutForm != null && !aboutForm.IsDisposed)
            {
                if (aboutForm.WindowState == FormWindowState.Minimized)
                    aboutForm.WindowState = FormWindowState.Normal;
                aboutForm.Show();
                aboutForm.BringToFront();
                aboutForm.Activate();
                return;
            }

            aboutForm = new AboutForm(this);
            aboutForm.FormClosed += delegate { aboutForm = null; };
            aboutForm.Show(this);
            aboutForm.BringToFront();
            aboutForm.Activate();
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == SingleInstanceChannel.ShowMessage)
            {
                ShowFromTray();
                return;
            }

            base.WndProc(ref message);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!allowExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                discoveryTimer.Stop();
                Hide();
                WindowState = FormWindowState.Normal;
                notifyIcon.ShowBalloonTip(
                    2500,
                    "TailMsg continua ativo",
                    "O programa permanece em segundo plano recebendo mensagens.",
                    ToolTipIcon.Info);
                return;
            }

            // Libera as portas antes de o processo desaparecer. Isso evita
            // que o updater inicie a nova instância durante a janela em que
            // o Windows ainda registra os sockets da instância anterior.
            if (allowExit)
            {
                networkService.Stop();
            }

            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            restoreTimer.Stop();
            restoreTimer.Dispose();
            discoveryTimer.Stop();
            discoveryTimer.Dispose();
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            ClearPendingImageThumbnail();
            ClearPendingAudio();
            if (recorder != null) { recorder.Dispose(); recorder = null; }
            networkService.Stop();
            base.OnFormClosed(e);
        }

        private void ShowFromTray()
        {
            if (InvokeRequired)
            {
                TryBeginInvoke(delegate { ShowFromTray(); });
                return;
            }

            ShowInTaskbar = true;
            ForceRestoreWindow();
            restoreTimer.Stop();
            restoreTimer.Start();
            RefreshComputers();
            discoveryTimer.Start();
        }

        private void ForceRestoreWindow()
        {
            if (IsDisposed) return;

            if (!Visible) Show();
            WindowState = FormWindowState.Normal;
            ShowWindow(Handle, SwRestore);
            WindowState = FormWindowState.Normal;

            bool visibleOnScreen = false;
            foreach (Screen screen in Screen.AllScreens)
            {
                if (screen.WorkingArea.IntersectsWith(Bounds))
                {
                    visibleOnScreen = true;
                    break;
                }
            }
            if (!visibleOnScreen)
            {
                Rectangle workingArea = Screen.PrimaryScreen.WorkingArea;
                Left = workingArea.Left + Math.Max(0, (workingArea.Width - Width) / 2);
                Top = workingArea.Top + Math.Max(0, (workingArea.Height - Height) / 2);
            }

            TopMost = true;
            BringToFront();
            Activate();
            SetForegroundWindow(Handle);
            TopMost = false;
        }

        private void ResizeComputerOptions(object sender, EventArgs e)
        {
            ResizeComputerItems();
        }

        // Largura dos itens conforme o espaço visível (sem disparar layout).
        private void ResizeComputerItems()
        {
            if (peerViewport == null || peerClip == null || computerList == null) return;
            int available = peerClip.ClientSize.Width;
            if (available <= 0) return;
            int columns = Math.Max(1, computerList.ColumnCount);
            int width = Math.Max(90, (available - 30) / columns - 12);
            foreach (RadioButton option in DestinationOptions())
            {
                // O rádio cresce com o texto; o mínimo garante a área de clique.
                if (option.MinimumSize.Width != width)
                {
                    option.MinimumSize = new Size(width, 20);
                }
            }
        }

        // Rolagem própria da lista de destinos: a barra fica fora do contorno.
        private bool peerLayingOut;

        // Janelinhas de recebimento por operação, para fechar quando o
        // remetente pedir "Deletar para todos".
        private readonly Dictionary<string, ReceivedMessageForm> notificationsById =
            new Dictionary<string, ReceivedMessageForm>(StringComparer.Ordinal);

        private void RegisterNotification(string operationId, ReceivedMessageForm form)
        {
            if (String.IsNullOrEmpty(operationId) || form == null) return;
            notificationsById[operationId] = form;
        }

        private void LayoutPeerList()
        {
            if (peerViewport == null || peerScroll == null || computerList == null) return;
            if (peerLayingOut) return;
            peerLayingOut = true;
            try
            {
                for (int pass = 0; pass < 4; pass++)
                {
                    bool visibleBefore = peerScroll.Visible;
                    peerClip.Bounds = new Rectangle(
                        1,
                        1,
                        Math.Max(1, peerViewport.ClientSize.Width - 2),
                        Math.Max(1, peerViewport.ClientSize.Height - 2));
                    ResizeComputerItems();
                    int viewportHeight = peerClip.ClientSize.Height;
                    int contentHeight = computerList.Height;
                    int overflow = Math.Max(0, contentHeight - viewportHeight);
                    bool needed = overflow > 0;

                    peerScroll.LargeChange = Math.Max(1, viewportHeight / 4);
                    peerScroll.Maximum = overflow + peerScroll.LargeChange - 1;
                    int maximum = Math.Max(0, peerScroll.Maximum - peerScroll.LargeChange + 1);
                    if (peerScroll.Value > maximum) peerScroll.Value = maximum;
                    computerList.Top = needed ? -peerScroll.Value : 0;

                    if (needed != visibleBefore)
                    {
                        peerScroll.Visible = needed;
                        continue;
                    }
                    break;
                }
            }
            finally
            {
                peerLayingOut = false;
            }
            if (peerListBorder != null)
            {
                peerListBorder.Invalidate();
            }
            if (peerViewport != null)
            {
                peerViewport.Invalidate();
            }
        }

        private void PeerListMouseWheel(object sender, MouseEventArgs e)
        {
            if (peerScroll == null || !peerScroll.Visible) return;
            int delta = e.Delta > 0 ? -peerScroll.SmallChange * 3 : peerScroll.SmallChange * 3;
            peerScroll.Value = Math.Max(0, Math.Min(peerScroll.Value + delta,
                Math.Max(0, peerScroll.Maximum - peerScroll.LargeChange + 1)));
            LayoutPeerList();
        }

        private bool TryBeginInvoke(MethodInvoker action)
        {
            try
            {
                if (IsDisposed || Disposing || !IsHandleCreated)
                    return false;
                BeginInvoke(action);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private void CheckForUpdates(bool manual)
        {
            ToolStripMenuItem checkItem = null;
            foreach (ToolStripItem item in GetMenuItems())
            {
                if (item.Text == "Verificar Atualizações")
                {
                    checkItem = item as ToolStripMenuItem;
                    break;
                }
            }
            if (manual && checkItem != null)
            {
                checkItem.Enabled = false;
                checkItem.Text = "Verificando...";
            }

            TailMsgUpdateClient.CheckAsync(
                delegate(UpdateManifest manifest)
                {
                    if (IsDisposed) return;
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (IsDisposed) return;
                            availableUpdate = manifest;
                            updateButton.Text = "Atualização disponível";
                            updateButton.Enabled = true;
                            updateButton.Visible = true;
                        });
                    }
                    catch { }
                },
                delegate(string error)
                {
                    // Falhas de internet ou do R2/GitHub não interrompem o uso do
                    // mensageiro. Uma nova consulta ocorrerá na próxima abertura.
                },
                delegate(bool found, string checkError)
                {
                    if (!manual || IsDisposed || checkItem == null) return;
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (IsDisposed) return;
                            checkItem.Text = "Verificar Atualizações";
                            checkItem.Enabled = true;
                            if (!found)
                            {
                                string message = String.IsNullOrEmpty(checkError)
                                    ? "Nenhuma atualização disponível. O TailMsg já está na versão mais recente."
                                    : "Não foi possível verificar atualizações.\r\n\r\n" + checkError;
                                MessageBox.Show(
                                    this,
                                    message,
                                    "TailMsg - atualizações",
                                    MessageBoxButtons.OK,
                                    String.IsNullOrEmpty(checkError)
                                        ? MessageBoxIcon.Information
                                        : MessageBoxIcon.Warning);
                            }
                        });
                    }
                    catch { }
                });
        }

        private IEnumerable<ToolStripItem> GetMenuItems()
        {
            foreach (Control control in Controls)
            {
                MenuStrip menu = control as MenuStrip;
                if (menu == null) continue;
                foreach (ToolStripItem item in menu.Items)
                    yield return item;
            }
        }

        private void UpdateButtonClick(object sender, EventArgs e)
        {
            if (updateInProgress || availableUpdate == null) return;

            updateInProgress = true;
            updateButton.Enabled = false;
            updateButton.Text = "Baixando atualização...";

            TailMsgUpdateClient.DownloadAndInstallAsync(
                availableUpdate,
                delegate
                {
                    if (IsDisposed) return;
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            updateButton.Text = "Instalando...";
                        });
                    }
                    catch { }
                },
                delegate
                {
                    // Feche os sockets antes de criar o updater. Assim, o
                    // helper não recebe handles das portas de produção no
                    // handoff, mesmo em ambientes que os herdem.
                    networkService.Stop();
                },
                delegate
                {
                    if (IsDisposed) return;
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            networkService.Stop();
                            allowExit = true;
                            notifyIcon.Visible = false;
                            Close();
                        });
                    }
                    catch { }
                },
                delegate(string error)
                {
                    if (IsDisposed) return;
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            updateInProgress = false;
                            updateButton.Enabled = true;
                            updateButton.Text = "Atualização disponível";
                            StartNetworkServiceAsync(false);
                            MessageBox.Show(
                                this,
                                "Não foi possível atualizar o TailMsg.\r\n\r\n" +
                                error,
                                "TailMsg - atualização",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                        });
                    }
                    catch { }
                });
        }

        private void RefreshComputers()
        {
            if (isRefreshing || isSending)
            {
                return;
            }

            isRefreshing = true;
            UpdateActionStates();
            statusLabel.ForeColor = Color.FromArgb(75, 85, 99);
            statusLabel.Text = "";

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    List<PeerInfo> computers = networkService.Discover(900);
                    TryBeginInvoke(delegate
                    {
                        latestPeers = computers;
                        List<PeerInfo> visibleComputers = GetFilteredPeers();
                        PopulateComputers(visibleComputers);
                        int remoteCount = CountRemotePeers(visibleComputers);
                        statusLabel.Text = remoteCount == 0
                            ? NetworkDiscovery.GetDiscoverySummary()
                            : "";
                    });
                }
                catch (Exception exception)
                {
                    TryBeginInvoke(delegate
                    {
                        computerList.Controls.Clear();
                        countLabel.Text = "";
                        statusLabel.ForeColor = Color.FromArgb(185, 28, 28);
                        statusLabel.Text = "Falha na descoberta da rede.";
                        MessageBox.Show(this, exception.Message, "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    });
                }
                finally
                {
                    TryBeginInvoke(delegate
                    {
                        isRefreshing = false;
                        UpdateActionStates();
                    });
                }
            });
        }

        private void InterfaceFilterChanged(object sender, EventArgs e)
        {
            List<PeerInfo> visibleComputers = GetFilteredPeers();
            PopulateComputers(visibleComputers);

            if (!delegaciaCheckBox.Checked && !tailscaleCheckBox.Checked)
            {
                statusLabel.Text = "Selecione pelo menos uma interface.";
            }
            else
            {
                statusLabel.Text = CountRemotePeers(visibleComputers) == 0
                    ? "Nenhum outro TailMsg visível nos filtros selecionados."
                    : "";
            }
        }

        private List<PeerInfo> GetFilteredPeers()
        {
            List<PeerInfo> result = new List<PeerInfo>();
            foreach (PeerInfo peer in latestPeers)
            {
                IPAddress address;
                if (!IPAddress.TryParse(peer.Address, out address))
                {
                    continue;
                }

                if ((delegaciaCheckBox.Checked && NetworkDiscovery.IsDelegaciaAddress(address)) ||
                    (tailscaleCheckBox.Checked && NetworkDiscovery.IsTailscaleAddress(address)))
                {
                    result.Add(peer);
                }
            }
            return result;
        }

        private static int CountRemotePeers(List<PeerInfo> peers)
        {
            int count = 0;
            foreach (PeerInfo peer in peers)
            {
                if (!peer.IsLocal) count++;
            }
            return count;
        }

        // Uma coluna por interface marcada nas checkboxes, atualizada em tempo
        // real quando elas mudam.
        private List<string> SelectedInterfaces()
        {
            List<string> interfaces = new List<string>();
            if (delegaciaCheckBox.Checked) interfaces.Add("Rede 10.x.x.x");
            if (tailscaleCheckBox.Checked) interfaces.Add("Tailscale 100.x.x.x");
            return interfaces;
        }

        private static bool PeerBelongsToInterface(PeerInfo peer, string iface)
        {
            if (peer == null) return false;
            IPAddress address;
            if (!IPAddress.TryParse(peer.Address, out address)) return false;
            if (iface.StartsWith("Rede", StringComparison.Ordinal))
            {
                return NetworkDiscovery.IsDelegaciaAddress(address);
            }
            return NetworkDiscovery.IsTailscaleAddress(address);
        }

        private void PopulateComputers(List<PeerInfo> computers)
        {
            List<string> selected = GetSelectedAddresses();
            List<string> interfaces = SelectedInterfaces();

            computerList.SuspendLayout();
            computerList.Controls.Clear();
            computerList.ColumnStyles.Clear();
            computerList.RowStyles.Clear();
            computerList.ColumnCount = interfaces.Count;
            computerList.RowCount = 2;
            computerList.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            computerList.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            int interfaceIndex = 0;
            foreach (string iface in interfaces)
            {
                computerList.ColumnStyles.Add(
                    new ColumnStyle(SizeType.Percent, 100F / interfaces.Count));

                Label header = new Label();
                header.Text = iface;
                header.Dock = DockStyle.Fill;
                header.Font = new Font("Segoe UI Semibold", 9F);
                header.ForeColor = Color.FromArgb(107, 114, 128);
                header.TextAlign = ContentAlignment.MiddleLeft;
                computerList.Controls.Add(header, interfaceIndex, 0);

                FlowLayoutPanel column = new FlowLayoutPanel();
                column.FlowDirection = FlowDirection.TopDown;
                column.WrapContents = false;
                column.AutoSize = true;
                column.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                column.Dock = DockStyle.Fill;
                column.BackColor = Color.White;
                column.Margin = new Padding(0);
                computerList.Controls.Add(column, interfaceIndex, 1);

                foreach (PeerInfo computer in computers)
                {
                    if (!PeerBelongsToInterface(computer, iface)) continue;

                    // Cada destinatário vive no seu próprio container: assim o
                    // rádio mantém o comportamento nativo (acessível e clicável)
                    // e ainda é possível marcar vários, sem formar grupo.
                    Panel item = new Panel();
                    item.AutoSize = true;
                    item.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                    item.Margin = new Padding(0);
                    item.BackColor = Color.White;

                    RadioButton option = new RadioButton();
                    option.AutoSize = true;
                    option.MinimumSize = new Size(120, 20);
                    option.Margin = new Padding(0);
                    option.Padding = new Padding(5, 0, 0, 0);
                    option.Text = computer.Name + (computer.IsLocal ? "  [você]" : "");
                    option.Tag = computer;
                    option.Checked = selected.Contains(computer.Address);
                    option.Cursor = Cursors.Hand;
                    option.CheckedChanged += DestinationSelectionChanged;
                    option.MouseDown += DestinationOptionMouseDown;
                    option.Click += DestinationOptionClick;
                    option.MouseUp += DestinationOptionMouseUp;
                    peerTooltip.SetToolTip(option, "IP: " + computer.Address);
                    item.Controls.Add(option);
                    column.Controls.Add(item);
                }
                interfaceIndex++;
            }

            computerList.ResumeLayout();
            countLabel.Text = "(" + computers.Count + ")";
            ResizeComputerItems();
            UpdateActionStates();
        }

        // O RadioButton nativo não se desmarca: guardamos como ele estava no
        // MouseDown para o segundo clique poder desmarcar.
        private RadioButton destinationMouseDownOwner;
        private bool destinationClickWasChecked;

        private void DestinationOptionMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            RadioButton option = sender as RadioButton;
            // Só o clique real de mouse no próprio item pode desmarcar; a
            // seleção por acessibilidade não passa por aqui.
            destinationMouseDownOwner = option;
            destinationClickWasChecked = option != null && option.Checked;
        }

        private void DestinationOptionClick(object sender, EventArgs e)
        {
            RadioButton option = sender as RadioButton;
            if (option == null) return;
            bool desmarcar = option == destinationMouseDownOwner && destinationClickWasChecked;
            destinationMouseDownOwner = null;
            destinationClickWasChecked = false;
            if (desmarcar && option.Checked)
            {
                option.Checked = false;
                UpdateActionStates();
            }
        }

        // Clique com o botão direito copia o IP daquele destinatário.
        private void DestinationOptionMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            RadioButton option = sender as RadioButton;
            PeerInfo peer = option == null ? null : option.Tag as PeerInfo;
            if (peer == null) return;
            try
            {
                Clipboard.SetText(peer.Address);
                statusLabel.ForeColor = Color.FromArgb(75, 85, 99);
                statusLabel.Text = "IP " + peer.Address + " copiado.";
            }
            catch (Exception error)
            {
                MessageBox.Show(this, "Não foi possível copiar o IP: " + error.Message,
                    "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // Endereços marcados, em qualquer coluna.
        private List<string> GetSelectedAddresses()
        {
            List<string> addresses = new List<string>();
            foreach (RadioButton option in DestinationOptions())
            {
                if (!option.Checked) continue;
                PeerInfo peer = option.Tag as PeerInfo;
                if (peer != null) addresses.Add(peer.Address);
            }
            return addresses;
        }

        // Destinatários marcados, em qualquer coluna.
        private List<PeerInfo> GetSelectedComputers()
        {
            List<PeerInfo> peers = new List<PeerInfo>();
            foreach (RadioButton option in DestinationOptions())
            {
                if (!option.Checked) continue;
                PeerInfo peer = option.Tag as PeerInfo;
                if (peer != null) peers.Add(peer);
            }
            return peers;
        }

        // Todos os botões de destinatário, descendo pelas colunas.
        private List<RadioButton> DestinationOptions()
        {
            List<RadioButton> options = new List<RadioButton>();
            CollectDestinationOptions(computerList, options);
            return options;
        }

        private static void CollectDestinationOptions(Control parent, List<RadioButton> options)
        {
            if (parent == null) return;
            foreach (Control child in parent.Controls)
            {
                RadioButton option = child as RadioButton;
                if (option != null)
                {
                    options.Add(option);
                    continue;
                }
                CollectDestinationOptions(child, options);
            }
        }

        private void DestinationSelectionChanged(object sender, EventArgs e)
        {
            UpdateActionStates();
        }

        private void UpdateActionStates()
        {
            sendButton.Enabled = !isSending && GetSelectedComputer() != null;
            if (recordButton != null) recordButton.Enabled = !isSending;
            if (liveMicButton != null) liveMicButton.Enabled = !isSending;
        }

        private string GetSelectedAddress()
        {
            List<string> addresses = GetSelectedAddresses();
            return addresses.Count == 0 ? null : addresses[0];
        }

        private PeerInfo GetSelectedComputer()
        {
            List<PeerInfo> peers = GetSelectedComputers();
            return peers.Count == 0 ? null : peers[0];
        }

        private void MessageBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SendMessage();
            }
        }

        private void SendButtonClick(object sender, EventArgs e)
        {
            SendMessage();
        }

        // Um Ctrl+V de imagem substitui o anexo anterior: cada envio carrega
        // no máximo uma imagem, além do texto eventualmente digitado.
        private void MessageImagePasted(object sender, ImagePastedEventArgs e)
        {
            if (e == null || e.Image == null) return;
            SetPendingImage(e.Image);
        }

        private void MessageImagePasteFailed(object sender, ImagePasteFailedEventArgs e)
        {
            if (e == null || String.IsNullOrEmpty(e.ErrorMessage)) return;
            statusLabel.ForeColor = Color.FromArgb(185, 28, 28);
            statusLabel.Text = e.ErrorMessage;
        }

        private void SetPendingImage(ImagePayload image)
        {
            ClearPendingImageThumbnail();
            pendingImage = image;

            string error;
            pendingImageThumbnail = ImageTransfer.CreateThumbnail(
                image.PngBytes,
                56,
                56,
                out error);
            imagePreviewBox.Image = pendingImageThumbnail;

            string description = "Imagem anexada: " +
                ImageTransfer.DescribeDimensions(image.Width, image.Height) +
                " — " + ImageTransfer.DescribeBytes(image.ByteCount);
            if (ImageTransfer.IsLarge(image.ByteCount))
            {
                description += "  (arquivo grande: o envio pode demorar)";
                imagePreviewLabel.ForeColor = Color.FromArgb(185, 28, 28);
            }
            else
            {
                imagePreviewLabel.ForeColor = Color.FromArgb(55, 65, 81);
            }
            if (!String.IsNullOrEmpty(error))
            {
                description += "  (miniatura indisponível)";
            }
            imagePreviewLabel.Text = description;

            ShowImagePreview(true);
            UpdateActionStates();
        }

        private void ClearPendingImage()
        {
            pendingImage = null;
            ClearPendingImageThumbnail();
            ShowImagePreview(false);
            UpdateActionStates();
        }

        private void ClearPendingImageThumbnail()
        {
            if (pendingImageThumbnail == null) return;
            imagePreviewBox.Image = null;
            pendingImageThumbnail.Dispose();
            pendingImageThumbnail = null;
        }

        private void ShowImagePreview(bool visible)
        {
            if (imagePreviewBorder != null) imagePreviewBorder.Visible = visible;
            if (contentLayout != null && contentLayout.RowStyles.Count > 6)
            {
                contentLayout.RowStyles[6].Height = visible ? 62F : 0F;
            }
        }

        // ---- Áudio ---------------------------------------------------------

        // Cores copiadas dos botões do SIG Windows.
        private static readonly Color SigWhiteCircle = Color.White;
        private static readonly Color SigWhiteOutline = Color.FromArgb(118, 130, 130);
        private static readonly Color SigWhiteBody = Color.FromArgb(83, 101, 101);
        private static readonly Color SigRedCircle = Color.FromArgb(19, 32, 30);
        private static readonly Color SigRedOutline = Color.FromArgb(44, 64, 61);
        private static readonly Color SigRedBody = Color.FromArgb(255, 75, 75);
        private static readonly Color SigRedCapsuleOutline = Color.FromArgb(255, 208, 208);
        private static readonly Color SigRecordingCircle = Color.FromArgb(61, 21, 21);
        private static readonly Color SigRecordingOutline = Color.FromArgb(90, 36, 36);
        private static readonly Color SigCheck = Color.FromArgb(61, 220, 102);

        // Ícones dos três botões conforme o estado (branco, pausa, vermelho).
        // Ícones em imagem (assets embutidos, 256x256 com transparência),
        // carregados uma única vez.
        private static Image whiteMicrophoneImage;
        private static Image redMicrophoneImage;
        private static Image pauseImage;

        internal static Image WhiteMicrophoneIcon
        {
            get { EnsureAudioIcons(); return whiteMicrophoneImage; }
        }

        internal static Image RedMicrophoneIcon
        {
            get { EnsureAudioIcons(); return redMicrophoneImage; }
        }

        internal static Image PauseIcon
        {
            get { EnsureAudioIcons(); return pauseImage; }
        }

        internal static void EnsureAudioIconsPublic()
        {
            EnsureAudioIcons();
        }

        private static void EnsureAudioIcons()
        {
            if (whiteMicrophoneImage == null)
            {
                whiteMicrophoneImage = AppResources.AudioIconWhiteMicrophone();
            }
            if (redMicrophoneImage == null)
            {
                redMicrophoneImage = AppResources.AudioIconRedMicrophone();
            }
            if (pauseImage == null)
            {
                pauseImage = AppResources.AudioIconPause();
            }
        }

        // Ícones dos três botões conforme o estado (branco, pausa, vermelho).
        // Enquanto grava, os dois microfones mostram o check verde desenhado.
        private void UpdateRecordButtons()
        {
            if (recordButton == null) return;
            EnsureAudioIcons();

            bool whiteActive = isRecordingAudio && !recordingLive;
            bool redActive = isRecordingAudio && recordingLive;

            if (whiteActive)
            {
                recordButton.SourceImage = null;
                recordButton.CircleColor = SigRecordingCircle;
                recordButton.CircleOutline = SigRecordingOutline;
                recordButton.Glyph = IconGlyph.Check;
                recordButton.AccessibleName = "Encerrar gravação";
            }
            else
            {
                recordButton.SourceImage = whiteMicrophoneImage;
                recordButton.CircleColor = SigWhiteCircle;
                recordButton.CircleOutline = SigWhiteOutline;
                recordButton.Glyph = IconGlyph.None;
                recordButton.AccessibleName = "Gravar áudio";
            }

            if (redActive)
            {
                liveMicButton.SourceImage = null;
                liveMicButton.CircleColor = SigRedCircle;
                liveMicButton.CircleOutline = SigRedOutline;
                liveMicButton.Glyph = IconGlyph.Check;
                liveMicButton.AccessibleName = "Encerrar transcrição ao vivo";
            }
            else
            {
                liveMicButton.SourceImage = redMicrophoneImage;
                liveMicButton.CircleColor = SigRedCircle;
                liveMicButton.CircleOutline = SigRedOutline;
                liveMicButton.Glyph = IconGlyph.None;
                liveMicButton.AccessibleName = "Gravar com transcrição ao vivo";
            }

            if (isRecordingAudio && !isPausedAudio)
            {
                // Pausar: ícone da pasta de ícones.
                pauseButton.SourceImage = pauseImage;
                pauseButton.Glyph = IconGlyph.None;
                pauseButton.AccessibleName = "Pausar gravação";
            }
            else if (isRecordingAudio && isPausedAudio)
            {
                // Retomar: círculo amarelo com o triângulo do play.
                pauseButton.SourceImage = null;
                pauseButton.CircleColor = Color.FromArgb(242, 207, 55);
                pauseButton.CircleOutline = Color.FromArgb(196, 157, 0);
                pauseButton.Glyph = IconGlyph.Play;
                pauseButton.AccessibleName = "Retomar gravação";
            }
            else
            {
                pauseButton.SourceImage = null;
                pauseButton.Glyph = IconGlyph.None;
                pauseButton.AccessibleName = "";
            }

            recordButton.Invalidate();
            liveMicButton.Invalidate();
            pauseButton.Invalidate();
        }

        private void ToggleWhiteMicrophone()
        {
            if (isRecordingAudio && !recordingLive)
            {
                StopAudioRecording("toggle-branco");
                return;
            }
            if (isRecordingAudio) return;
            StartAudioRecording(false);
        }

        private void ToggleLiveMicrophone()
        {
            if (isRecordingAudio && recordingLive)
            {
                StopAudioRecording("toggle-vermelho");
                return;
            }
            if (isRecordingAudio) return;
            StartAudioRecording(true);
        }

        private void ToggleAudioPause()
        {
            if (!isRecordingAudio || recorder == null) return;

            string error;
            if (isPausedAudio)
            {
                if (!recorder.Resume(out error))
                {
                    statusLabel.ForeColor = Color.FromArgb(185, 28, 28);
                    statusLabel.Text = error;
                    return;
                }
                isPausedAudio = false;
            }
            else
            {
                if (!recorder.Pause(out error))
                {
                    statusLabel.ForeColor = Color.FromArgb(185, 28, 28);
                    statusLabel.Text = error;
                    return;
                }
                isPausedAudio = true;
            }
            TailMsgDiagnostics.WriteMessageEvent(
                TailMsgDiagnostics.CreateOperationId(),
                "audio_recording",
                isPausedAudio ? "paused" : "resumed",
                localComputerName,
                "",
                "",
                0,
                "");
            UpdateRecordButtons();
            UpdateRecordingStatus();
        }

        private void StartAudioRecording(bool live)
        {
            if (isSending || isRecordingAudio) return;

            WaveRecorder candidate = new WaveRecorder();
            string error;
            if (!candidate.Start(out error))
            {
                candidate.Dispose();
                statusLabel.ForeColor = Color.FromArgb(185, 28, 28);
                statusLabel.Text = error;
                MessageBox.Show(
                    this,
                    error,
                    "TailMsg",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            recorder = candidate;
            isRecordingAudio = true;
            recordingLive = live;
            isPausedAudio = false;
            diagnosticTicks = 0;
            TailMsgDiagnostics.WriteMessageEvent(
                TailMsgDiagnostics.CreateOperationId(),
                "audio_recording",
                "started",
                localComputerName,
                "",
                "",
                0,
                "modo=" + (live ? "live" : "normal"));

            if (live)
            {
                liveCommittedText = "";
                liveDraftText = "";
                liveCommitOffset = 0;
                liveLastDraftBytes = 0;
                liveGeneration++;
                liveCommitBusy = false;
                // A caixa da transcrição precisa estar visível durante a
                // gravação: os trechos vão aparecendo nela em tempo real.
                ShowAudioPreview(true);
                SetAudioTranscriptionText("");
                ShowAudioStatus("Transcrevendo ao vivo... aguarde o primeiro trecho.", false);
            }
            else
            {
                ShowAudioPreview(true);
            }

            UpdateRecordButtons();
            if (recordingTimer == null)
            {
                recordingTimer = new System.Windows.Forms.Timer();
                recordingTimer.Interval = 250;
                recordingTimer.Tick += delegate { UpdateRecordingStatus(); };
            }
            recordingTimer.Start();
            UpdateRecordingStatus();
        }

        private void UpdateRecordingStatus()
        {
            if (!isRecordingAudio || recorder == null) return;

            int seconds = (int)Math.Round(recorder.ElapsedSeconds);
            string liveLabel = recordingLive ? " (transcrição ao vivo)" : "";
            statusLabel.ForeColor = isPausedAudio
                ? Color.FromArgb(180, 83, 9)
                : Color.FromArgb(185, 28, 28);
            statusLabel.Text = (isPausedAudio ? "Gravação pausada em " : "Gravando ") +
                FormatDuration(seconds) + liveLabel +
                " — clique no check verde para encerrar e anexar.";

            if (recorder.LimitReached)
            {
                StopAudioRecording("limite");
                statusLabel.ForeColor = Color.FromArgb(185, 28, 28);
                statusLabel.Text = "Limite de gravação de " +
                    (TailMsgProtocol.MaximumAudioSeconds / 60) +
                    " minutos atingido; áudio anexado.";
                return;
            }

            // Diagnóstico: a cada ~2,5 s registra quanto áudio foi capturado.
            // Se o valor estagnar, o driver parou de entregar buffers.
            diagnosticTicks++;
            if (diagnosticTicks % 10 == 0)
            {
                TailMsgDiagnostics.WriteMessageEvent(
                    TailMsgDiagnostics.CreateOperationId(),
                    "audio_capture",
                    "progress",
                    localComputerName,
                    "",
                    "",
                    0,
                    "capturado_ms=" + (recorder.CapturedBytes * 1000L /
                        (TailMsgProtocol.AudioSampleRate *
                         TailMsgProtocol.AudioChannels *
                         (TailMsgProtocol.AudioBitsPerSample / 8))) +
                        ";relogio_ms=" + (int)(recorder.ElapsedSeconds * 1000) +
                        ";reciclados=" + recorder.RequeuedBuffers +
                        ";falhas=" + recorder.RequeueFailures +
                        ";erro=" + recorder.LastRequeueError +
                        ";flags=" + recorder.LastRequeueFlags +
                        ";prepare=" + recorder.LastPrepareError +
                        ";fila=" + recorder.QueueDepth +
                    ";janela_ms=" + ((recorder.CapturedBytes - liveCommitOffset) * 1000L /
                        (TailMsgProtocol.AudioSampleRate *
                         TailMsgProtocol.AudioChannels *
                         (TailMsgProtocol.AudioBitsPerSample / 8))));
            }

            if (recordingLive) UpdateLiveTranscription();
        }

        private void StopAudioRecording()
        {
            StopAudioRecording("desconhecido");
        }

        private void StopAudioRecording(string reason)
        {
            if (!isRecordingAudio || recorder == null) return;
            bool wasLive = recordingLive;

            // A janela em aberto precisa ser copiada ANTES de fechar o
            // gravador: depois do Stop não há mais bytes para transcrever.
            byte[] liveTail = null;
            if (wasLive)
            {
                liveTail = recorder.SnapshotFrom(liveCommitOffset);
            }

            if (recordingTimer != null) recordingTimer.Stop();
            TailMsgDiagnostics.WriteMessageEvent(
                TailMsgDiagnostics.CreateOperationId(),
                "audio_recording",
                "stopped",
                localComputerName,
                "",
                "",
                0,
                "modo=" + (recordingLive ? "live" : "normal") +
                    ";segundos=" + (int)Math.Round(recorder.ElapsedSeconds) +
                    ";origem=" + reason);
            isRecordingAudio = false;
            recordingLive = false;
            isPausedAudio = false;
            AudioPayload payload = recorder.Stop();
            recorder.Dispose();
            recorder = null;

            UpdateRecordButtons();

            if (payload == null)
            {
                statusLabel.ForeColor = Color.FromArgb(185, 28, 28);
                statusLabel.Text = "A gravação ficou curta demais e foi descartada.";
                return;
            }

            SetPendingAudio(payload);
            if (wasLive)
            {
                // Fecha a janela em aberto com uma transcrição final.
                if (liveTail != null && liveTail.Length >= BytesForMillis(400))
                {
                    SendFinalLiveWindow(liveTail);
                }
                liveCommitOffset = 0;
                liveLastDraftBytes = 0;
            }
            else
            {
                StartAudioTranscription(payload);
            }
        }

        private void SetPendingAudio(AudioPayload audio)
        {
            pendingAudio = audio;
            ClearAudioPreviewPanel();

            AudioTrackPanel panel = new AudioTrackPanel(audio, true);
            panel.Dock = DockStyle.Fill;
            // Fundo natural do painel: sem quadro branco em volta.
            panel.BackColor = Color.FromArgb(245, 247, 250);
            panel.RemoveRequested += delegate { ClearPendingAudio(); };
            panel.PlaybackFailed += delegate(object sender, EventArgs e)
            {
                PlaybackFailedEventArgs failure = e as PlaybackFailedEventArgs;
                statusLabel.ForeColor = Color.FromArgb(185, 28, 28);
                statusLabel.Text = failure == null
                    ? "Não foi possível tocar o áudio."
                    : failure.ErrorMessage;
            };
            audioPreviewPanel = panel;
            contentLayout.Controls.Add(panel, 0, 7);

            ShowAudioPreview(true);
            UpdateActionStates();
            statusLabel.ForeColor = Color.FromArgb(21, 128, 61);
            statusLabel.Text = "Áudio anexado (" +
                FormatDuration(audio.DurationMilliseconds / 1000) +
                "). Use Enviar para mandar, ou Remover para descartar.";
        }

        private void ClearPendingAudio()
        {
            pendingAudio = null;
            ShowAudioPreview(false);
            ClearAudioPreviewPanel();
            messageBoxIsTranscription = false;
            UpdateActionStates();
        }

        private void ClearAudioPreviewPanel()
        {
            if (audioPreviewPanel == null) return;
            AudioTrackPanel panel = audioPreviewPanel;
            audioPreviewPanel = null;
            panel.StopPlayback();
            if (contentLayout != null && contentLayout.Controls.Contains(panel))
            {
                contentLayout.Controls.Remove(panel);
            }
            panel.Dispose();
        }

        private void ShowAudioPreview(bool visible)
        {
            // Só a linha do áudio: a timeline fica sobre o fundo do painel.
            if (contentLayout != null && contentLayout.RowStyles.Count > 7)
            {
                contentLayout.RowStyles[7].Height = visible ? 58F : 0F;
            }
        }

        // ---- Transcrição do áudio gravado (remetente) ----------------------

        // A transcrição aparece na caixa onde a mensagem é digitada (além da
        // faixa do áudio). Só o áudio é enviado — o destinatário transcreve no
        // computador dele.
        private void SetAudioTranscriptionText(string transcription)
        {
            if (messageBox == null) return;
            string text = transcription == null ? "" : transcription.Trim();
            messageBoxIsTranscription = text.Length > 0;
            messageBox.SetProgrammaticText(text);
        }

        // Avisos da transcrição saem no status; o texto transcrito em si vai
        // para a caixa onde a mensagem é digitada.
        private void ShowAudioStatus(string text, bool isError)
        {
            if (statusLabel == null) return;
            if (String.IsNullOrEmpty(text)) return;
            statusLabel.ForeColor = isError
                ? Color.FromArgb(185, 28, 28)
                : Color.FromArgb(75, 85, 99);
            statusLabel.Text = text;
        }

        private string LiveTranscriptionText()
        {
            string committed = liveCommittedText.Trim();
            string draft = liveDraftText.Trim();
            if (draft.Length == 0) return committed;
            if (committed.Length == 0) return draft;
            return committed + " " + draft;
        }

        // Microfone branco: transcreve o áudio já encerrado, uma única vez.
        private void StartAudioTranscription(AudioPayload audio)
        {
            if (audio == null || audio.WavBytes == null) return;
            byte[] wavBytes = audio.WavBytes;
            string operationId = TailMsgDiagnostics.CreateOperationId();
            ShowAudioStatus("Transcrevendo o áudio...", false);

            ThreadPool.QueueUserWorkItem(delegate
            {
                string text;
                string error;
                bool ok = TranscriptionClient.TryTranscribe(
                    wavBytes,
                    "audio.wav",
                    operationId,
                    out text,
                    out error);
                TryBeginInvoke(delegate
                {
                    if (ok)
                    {
                        SetAudioTranscriptionText(text);
                        TranscriptionCache.Remember(operationId, text);
                    }
                    else
                    {
                        ShowAudioStatus("Não foi possível transcrever: " + error, true);
                    }
                });
            });
        }

        // Microfone vermelho: a cada 1 s manda a janela em aberto (o rascunho
        // substitui o anterior) e a cada 10 s confirma o que já foi transcrito,
        // recomeçando a janela — o mesmo ritmo do SIG.
        private const int LiveCommitMillis = 10000;
        private const int LiveDraftMillis = 1000;

        private void UpdateLiveTranscription()
        {
            if (recorder == null) return;

            int captured = recorder.CapturedBytes;
            int windowBytes = captured - liveCommitOffset;
            if (windowBytes < BytesForMillis(300)) return;

            // Janela cheia: confirma o texto e reinicia do ponto atual.
            if (windowBytes >= BytesForMillis(LiveCommitMillis) && !liveCommitBusy)
            {
                liveCommitBusy = true;
                liveLastDraftBytes = captured;
                SendLiveWindow(windowBytes, true);
                return;
            }

            // Fora do commit: manda a janela inteira a cada segundo; a resposta
            // substitui o rascunho anterior.
            if (captured - liveLastDraftBytes >= BytesForMillis(LiveDraftMillis))
            {
                liveLastDraftBytes = captured;
                SendLiveWindow(windowBytes, false);
            }
        }

        // Envia a janela atual (do último commit até agora). Os rascunhos não
        // bloqueiam uns aos outros: a geração descarta resposta atrasada.
        private void SendLiveWindow(int windowBytes, bool commit)
        {
            if (recorder == null) return;
            byte[] pcm = recorder.SnapshotFrom(liveCommitOffset);
            if (pcm == null || pcm.Length < BytesForMillis(300)) return;

            byte[] wav = WaveRecorder.WrapPcm(pcm);
            if (wav == null) return;

            int generation = ++liveGeneration;
            int commitEndBytes = liveCommitOffset + windowBytes;
            string operationId = TailMsgDiagnostics.CreateOperationId();

            ThreadPool.QueueUserWorkItem(delegate
            {
                string text;
                string error;
                bool ok = TranscriptionClient.TryTranscribe(
                    wav,
                    "audio.wav",
                    operationId,
                    out text,
                    out error);

                TryBeginInvoke(delegate
                {
                    if (commit)
                    {
                        liveCommitBusy = false;
                        if (!ok)
                        {
                            // Tenta de novo na próxima verificação.
                            liveLastDraftBytes = liveCommitOffset;
                            return;
                        }
                        liveCommittedText = AppendLine(liveCommittedText, text);
                        liveDraftText = "";
                        liveCommitOffset = commitEndBytes;
                        liveLastDraftBytes = commitEndBytes;
                        TranscriptionCache.Remember(operationId, text);
                        SetAudioTranscriptionText(LiveTranscriptionText());
                        return;
                    }

                    // Rascunho obsoleto (já existe outro mais novo) é ignorado.
                    if (generation != liveGeneration) return;
                    if (!ok)
                    {
                        if (liveCommittedText.Length == 0 && liveDraftText.Length == 0)
                        {
                            ShowAudioStatus("Não foi possível transcrever: " + error, true);
                        }
                        return;
                    }
                    liveDraftText = text;
                    SetAudioTranscriptionText(LiveTranscriptionText());
                });
            });
        }

        // Confirmação final com os bytes copiados antes de fechar o gravador.
        private void SendFinalLiveWindow(byte[] pcm)
        {
            byte[] wav = WaveRecorder.WrapPcm(pcm);
            if (wav == null) return;
            int generation = ++liveGeneration;
            string operationId = TailMsgDiagnostics.CreateOperationId();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string text;
                string error;
                bool ok = TranscriptionClient.TryTranscribe(
                    wav,
                    "audio.wav",
                    operationId,
                    out text,
                    out error);
                TryBeginInvoke(delegate
                {
                    if (ok)
                    {
                        liveCommittedText = AppendLine(liveCommittedText, text);
                        liveDraftText = "";
                        TranscriptionCache.Remember(operationId, text);
                    }
                    else if (liveCommittedText.Length == 0 && liveDraftText.Length == 0)
                    {
                        ShowAudioStatus("Não foi possível transcrever: " + error, true);
                    }
                    SetAudioTranscriptionText(LiveTranscriptionText());
                });
            });
        }

        private static string AppendLine(string current, string addition)
        {
            string text = (current ?? "").Trim();
            string extra = (addition ?? "").Trim();
            if (extra.Length == 0) return text;
            if (text.Length == 0) return extra;
            return text + " " + extra;
        }

        private static int BytesForMillis(int millis)
        {
            return (millis * TailMsgProtocol.AudioSampleRate *
                TailMsgProtocol.AudioChannels *
                (TailMsgProtocol.AudioBitsPerSample / 8)) / 1000;
        }

        private static string FormatDuration(int seconds)
        {
            if (seconds < 0) seconds = 0;
            return (seconds / 60).ToString(CultureInfo.InvariantCulture) + ":" +
                (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        private void SendMessage()
        {
            if (isSending) return;

            // Enviar durante a gravação: a captura é encerrada na hora, o
            // áudio entra como anexo e a mensagem segue normalmente.
            if (isRecordingAudio)
            {
                StopAudioRecording("envio");
            }

            List<PeerInfo> targets = GetSelectedComputers();
            // A transcrição exibida na caixa é apenas informativa: o que viaja
            // é o áudio (o destinatário transcreve no computador dele).
            string message = messageBoxIsTranscription ? "" : messageBox.Text.Trim();
            ImagePayload image = pendingImage;
            AudioPayload audio = pendingAudio;

            if (targets.Count == 0)
            {
                MessageBox.Show(this, "Escolha ao menos um computador TailMsg como destino.",
                    "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (String.IsNullOrEmpty(message) && image == null && audio == null)
            {
                MessageBox.Show(this, "Digite a mensagem, cole uma imagem com Ctrl+V ou grave um áudio.",
                    "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Information);
                messageBox.Focus();
                return;
            }

            // Uma recusa vale para todos: evita enviar metade e falhar depois.
            foreach (PeerInfo target in targets)
            {
                if (image != null && !target.SupportsImages)
                {
                    MessageBox.Show(
                        this,
                        "O computador " + target.Name +
                        " usa uma versão do TailMsg sem suporte a imagens." +
                        (message.Length > 0
                            ? " Somente o texto pode ser enviado."
                            : " A imagem não será enviada."),
                        "TailMsg",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                if (audio != null && !target.SupportsAudio)
                {
                    MessageBox.Show(
                        this,
                        "O computador " + target.Name +
                        " usa uma versão do TailMsg sem suporte a áudio." +
                        (message.Length > 0
                            ? " Somente o texto pode ser enviado."
                            : " O áudio não será enviado."),
                        "TailMsg",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }
            }

            isSending = true;
            UpdateActionStates();
            statusLabel.ForeColor = Color.FromArgb(75, 85, 99);
            statusLabel.Text = targets.Count == 1
                ? "Enviando para " + targets[0].Name + " (" + targets[0].Address + ")..."
                : "Enviando para " + targets.Count + " computadores...";

            List<PeerInfo> recipients = targets;
            ThreadPool.QueueUserWorkItem(delegate
            {
                // Um destinatário por thread: quem responde recebe na hora, sem
                // esperar o timeout de quem está fora do ar.
                bool sentText = false;
                bool sentImage = false;
                bool sentAudio = false;
                string failure = "";
                List<Action> registros = new List<Action>();
                object sync = new object();

                int pendentes = recipients.Count;
                using (ManualResetEvent conclusao = new ManualResetEvent(false))
                {
                    foreach (PeerInfo computer in recipients)
                    {
                        PeerInfo alvo = computer;
                        ThreadPool.QueueUserWorkItem(delegate
                        {
                            try
                            {
                                string textId = "";
                                string imageId = "";
                                string audioId = "";

                                if (message.Length > 0)
                                {
                                    MessageSendResult textResult = MessageSender.Send(
                                        alvo, localComputerName, message);
                                    if (textResult.Success)
                                    {
                                        textId = textResult.OperationId;
                                    }
                                    else
                                    {
                                        lock (sync)
                                        {
                                            if (failure.Length == 0) failure = textResult.ErrorMessage;
                                        }
                                    }
                                }

                                if (image != null)
                                {
                                    MessageSendResult imageResult = MessageSender.SendImage(
                                        alvo, localComputerName, image);
                                    if (imageResult.Success)
                                    {
                                        imageId = imageResult.OperationId;
                                    }
                                    else
                                    {
                                        lock (sync)
                                        {
                                            if (failure.Length == 0) failure = imageResult.ErrorMessage;
                                        }
                                    }
                                }

                                if (audio != null)
                                {
                                    MessageSendResult audioResult = MessageSender.SendAudio(
                                        alvo, localComputerName, audio);
                                    if (audioResult.Success)
                                    {
                                        audioId = audioResult.OperationId;
                                    }
                                    else
                                    {
                                        lock (sync)
                                        {
                                            if (failure.Length == 0) failure = audioResult.ErrorMessage;
                                        }
                                    }
                                }

                                bool entregouTexto = textId.Length > 0;
                                bool entregouImagem = imageId.Length > 0;
                                bool entregouAudio = audioId.Length > 0;
                                if (entregouTexto || entregouImagem || entregouAudio)
                                {
                                    string textoId = textId;
                                    string imagemId = imageId;
                                    string audioIdRegistro = audioId;
                                    lock (sync)
                                    {
                                        if (entregouTexto) sentText = true;
                                        if (entregouImagem) sentImage = true;
                                        if (entregouAudio) sentAudio = true;
                                        // O histórico é interface: aplica depois,
                                        // na thread da UI, dentro do TryBeginInvoke.
                                        registros.Add(delegate
                                        {
                                            RegisterSentMessages(
                                                alvo,
                                                entregouTexto, textoId, message,
                                                entregouImagem, imagemId, image,
                                                entregouAudio, audioIdRegistro, audio);
                                        });
                                    }
                                }
                            }
                            finally
                            {
                                bool ultimo;
                                lock (sync)
                                {
                                    pendentes--;
                                    ultimo = pendentes <= 0;
                                }
                                if (ultimo) conclusao.Set();
                            }
                        });
                    }
                    conclusao.WaitOne();
                }

                TryBeginInvoke(delegate
                {
                    // Agora na thread da interface: é aqui que as linhas do
                    // histórico podem ser criadas.
                    foreach (Action registro in registros)
                    {
                        registro();
                    }

                    isSending = false;

                    // O que já foi entregue sai da tela para não ser reenviado
                    // sem querer em uma nova tentativa.
                    if (sentText) messageBox.Clear();
                    if (sentImage) ClearPendingImage();
                    if (sentAudio)
                    {
                        ClearPendingAudio();
                        // A transcrição sai da caixa junto com o áudio.
                        messageBoxIsTranscription = false;
                        messageBox.SetProgrammaticText("");
                    }

                    UpdateActionStates();

                    if (failure.Length == 0)
                    {
                        statusLabel.ForeColor = Color.FromArgb(21, 128, 61);
                        statusLabel.Text = DescribeDelivery(
                            DescribeTargets(recipients),
                            sentText,
                            sentImage,
                            sentAudio);
                        messageBox.Focus();
                    }
                    else
                    {
                        statusLabel.ForeColor = Color.FromArgb(185, 28, 28);
                        statusLabel.Text = "Falha ao enviar para " +
                            DescribeTargets(recipients) + ".";
                        MessageBox.Show(this, failure, "TailMsg",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                });
            });
        }

        // Nome curto do conjunto de destinatários, para a barra de status.
        private static string DescribeTargets(List<PeerInfo> targets)
        {
            if (targets == null || targets.Count == 0) return "nenhum computador";
            if (targets.Count == 1) return targets[0].Name;
            return targets[0].Name + " e mais " + (targets.Count - 1);
        }

        // Cada mensagem entregue entra no histórico como enviada: texto em
        // verde alinhado à direita, imagem e áudio com o mesmo botão de sempre.
        private void RegisterSentMessages(
            PeerInfo computer,
            bool sentText, string textId, string text,
            bool sentImage, string imageId, ImagePayload image,
            bool sentAudio, string audioId, AudioPayload audio)
        {
            string stamp = DateTime.Now.ToString("HH:mm:ss");
            string who = computer == null ? "?" : computer.Name;
            string address = computer == null ? "" : computer.Address;

            if (sentText && text != null && text.Length > 0)
            {
                long seq = HistoryStore.Append(new HistoryEntry
                {
                    Kind = "sent",
                    Time = stamp,
                    Sender = who,
                    Address = address,
                    Text = text,
                    OperationId = textId
                });
                InboxTextRow row = inboxBox.AppendMessage(who, text, stamp, true, seq, textId);
                row.Address = address;
                inboxBox.AttachMenu(row, seq, textId, true, "");
            }

            if (sentImage && image != null)
            {
                long size = image.PngBytes == null ? 0 : image.PngBytes.Length;
                string file = HistoryStore.SaveMedia(image.PngBytes, ".png");
                long seq = HistoryStore.Append(new HistoryEntry
                {
                    Kind = "sent-image",
                    Time = stamp,
                    Sender = who,
                    Address = address,
                    Size = size,
                    FileName = file,
                    OperationId = imageId
                });
                InboxImageRow row = inboxBox.AppendImage(
                    who + ": [imagem " + ImageTransfer.DescribeBytes(size) + "] [" + stamp + "]",
                    image.PngBytes,
                    HistoryStore.MediaPath(file));
                row.Address = address;
                row.Seq = seq;
                row.OperationId = imageId;
                row.Sent = true;
                inboxBox.AttachMenu(row, seq, imageId, true, "");
            }

            if (sentAudio && audio != null)
            {
                string file = HistoryStore.SaveMedia(audio.WavBytes, ".wav");
                long seq = HistoryStore.Append(new HistoryEntry
                {
                    Kind = "sent-audio",
                    Time = stamp,
                    Sender = who,
                    Address = address,
                    DurationMilliseconds = audio.DurationMilliseconds,
                    FileName = file,
                    OperationId = audioId
                });
                InboxAudioRow row = inboxBox.AppendAudio(
                    who + ": [áudio " + FormatDuration(audio.DurationMilliseconds / 1000) + "] [" + stamp + "]",
                    audio,
                    audioId);
                row.Address = address;
                row.Seq = seq;
                row.OperationId = audioId;
                row.Sent = true;
                inboxBox.AttachMenu(row, seq, audioId, true, "");
            }
        }

        private static string DescribeDelivery(
            string computerName,
            bool sentText,
            bool sentImage,
            bool sentAudio)
        {
            int attachments = (sentImage ? 1 : 0) + (sentAudio ? 1 : 0);
            if (!sentText && attachments == 0)
            {
                return "Nada foi enviado para " + computerName + ".";
            }
            if (!sentText && attachments == 1)
            {
                return (sentImage ? "Imagem entregue a " : "Áudio entregue a ") +
                    computerName + ".";
            }

            StringBuilder text = new StringBuilder();
            if (sentText) text.Append("Mensagem");
            if (sentImage) text.Append(text.Length == 0 ? "Imagem" : " e imagem");
            if (sentAudio) text.Append(text.Length == 0 ? "Áudio" : " e áudio");
            text.Append(" entregue");
            if (sentText && attachments > 0) text.Append("s");
            text.Append(" a ").Append(computerName).Append(".");
            return text.ToString();
        }

        private void NetworkServiceMessageReceived(object sender, MessageReceivedEventArgs e)
        {
            if (IsDisposed)
            {
                TailMsgDiagnostics.WriteMessageEvent(
                    e.OperationId,
                    "ui_received",
                    "dropped",
                    e.SenderName,
                    e.RemoteAddress,
                    e.Fingerprint,
                    0,
                    "form-disposed");
                return;
            }

            TailMsgDiagnostics.WriteMessageEvent(
                e.OperationId,
                "ui_queued",
                "success",
                e.SenderName,
                e.RemoteAddress,
                e.Fingerprint,
                0,
                "");

            TryBeginInvoke(delegate
            {
                TailMsgDiagnostics.WriteMessageEvent(
                    e.OperationId,
                    "ui_shown",
                    "success",
                    e.SenderName,
                    e.RemoteAddress,
                    e.Fingerprint,
                    0,
                    "");
                string textStamp = DateTime.Now.ToString("HH:mm:ss");
                long textSeq = HistoryStore.Append(new HistoryEntry
                {
                    Kind = "text",
                    Time = textStamp,
                    Sender = e.SenderName,
                    Address = e.RemoteAddress,
                    Text = e.Message,
                    OperationId = e.OperationId
                });
                InboxTextRow receivedTextRow = inboxBox.AppendMessage(
                    e.SenderName, e.Message, textStamp, false, textSeq, e.OperationId);
                receivedTextRow.Address = e.RemoteAddress;
                inboxBox.AttachMenu(receivedTextRow, textSeq, e.OperationId, false, "");
                ReceivedMessageForm notification = new ReceivedMessageForm(
                    e,
                    localComputerName,
                    delegate { ShowFromTray(); });
                notification.PeerCapabilityLookup = networkService.FindPeerCapabilities;
                RegisterNotification(e.OperationId, notification);
                notification.StatusReporter = delegate(string text, bool isError)
                {
                    statusLabel.ForeColor = isError
                        ? Color.FromArgb(185, 28, 28)
                        : Color.FromArgb(21, 128, 61);
                    statusLabel.Text = text;
                };
                receivedNotifications.Add(notification);
                notification.FormClosed += delegate
                {
                    receivedNotifications.Remove(notification);
                    RepositionNotifications();
                };
                // O popup cresce ao anexar uma imagem na resposta; o
                // empilhamento acompanha.
                notification.SizeChanged += delegate { RepositionNotifications(); };
                RepositionNotifications();
                notification.Show();
                RepositionNotifications();
                statusLabel.ForeColor = Color.FromArgb(21, 128, 61);
                statusLabel.Text = "Nova mensagem recebida de " + e.SenderName + ".";
            });
        }

        private void NetworkServiceImageReceived(object sender, ImageReceivedEventArgs e)
        {
            if (IsDisposed)
            {
                TailMsgDiagnostics.WriteMessageEvent(
                    e.OperationId,
                    "ui_image_received",
                    "dropped",
                    e.SenderName,
                    e.RemoteAddress,
                    e.Fingerprint,
                    0,
                    "form-disposed");
                return;
            }

            TailMsgDiagnostics.WriteMessageEvent(
                e.OperationId,
                "ui_image_queued",
                "success",
                e.SenderName,
                e.RemoteAddress,
                e.Fingerprint,
                0,
                "");

            TryBeginInvoke(delegate
            {
                TailMsgDiagnostics.WriteMessageEvent(
                    e.OperationId,
                    "ui_image_shown",
                    "success",
                    e.SenderName,
                    e.RemoteAddress,
                    e.Fingerprint,
                    0,
                    "");

                // O histórico mostra o resumo e um botão que abre a imagem
                // recebida no aplicativo padrão do Windows.
                string imageStamp = DateTime.Now.ToString("HH:mm:ss");
                long imageSize = e.ImageBytes == null ? 0 : e.ImageBytes.Length;
                string imagePrefix = e.SenderName + ": [imagem " +
                    ImageTransfer.DescribeBytes(imageSize) + "] [" + imageStamp + "]";
                string imageFile = HistoryStore.SaveMedia(e.ImageBytes, ".png");
                long imageSeq = HistoryStore.Append(new HistoryEntry
                {
                    Kind = "image",
                    Time = imageStamp,
                    Sender = e.SenderName,
                    Address = e.RemoteAddress,
                    Size = imageSize,
                    FileName = imageFile,
                    OperationId = e.OperationId
                });
                InboxImageRow receivedImageRow = inboxBox.AppendImage(
                    imagePrefix, e.ImageBytes, HistoryStore.MediaPath(imageFile));
                receivedImageRow.Address = e.RemoteAddress;
                receivedImageRow.Seq = imageSeq;
                receivedImageRow.OperationId = e.OperationId;
                inboxBox.AttachMenu(receivedImageRow, imageSeq, e.OperationId, false, "");

                ReceivedMessageForm notification = new ReceivedMessageForm(
                    e,
                    localComputerName,
                    delegate { ShowFromTray(); });
                notification.PeerCapabilityLookup = networkService.FindPeerCapabilities;
                RegisterNotification(e.OperationId, notification);
                notification.StatusReporter = delegate(string text, bool isError)
                {
                    statusLabel.ForeColor = isError
                        ? Color.FromArgb(185, 28, 28)
                        : Color.FromArgb(21, 128, 61);
                    statusLabel.Text = text;
                };
                receivedNotifications.Add(notification);
                notification.FormClosed += delegate
                {
                    receivedNotifications.Remove(notification);
                    RepositionNotifications();
                };
                notification.SizeChanged += delegate { RepositionNotifications(); };
                RepositionNotifications();
                notification.Show();
                RepositionNotifications();
                statusLabel.ForeColor = Color.FromArgb(21, 128, 61);
                statusLabel.Text = "Imagem recebida de " + e.SenderName + ".";
            });
        }

        private void NetworkServiceMessageDeleted(object sender, MessageDeletedEventArgs e)
        {
            if (IsDisposed) return;
            // O remetente pediu para apagar: sai do histórico e a janelinha
            // daquela mensagem fecha, se ainda estiver aberta.
            string operationId = e == null ? "" : e.OperationId;
            if (String.IsNullOrEmpty(operationId)) return;
            TryBeginInvoke(delegate
            {
                inboxBox.RemoveByOperationId(operationId);
                HistoryStore.DeleteByOperationId(operationId);
                CloseNotificationFor(operationId);
                statusLabel.ForeColor = Color.FromArgb(75, 85, 99);
                statusLabel.Text = "Mensagem apagada por " + (e.SenderName ?? "remetente") + ".";
            });
        }

        // Fecha a janelinha de recebimento daquela operação, se estiver aberta.
        private void CloseNotificationFor(string operationId)
        {
            ReceivedMessageForm notification;
            if (notificationsById.TryGetValue(operationId, out notification))
            {
                notificationsById.Remove(operationId);
                if (notification != null && !notification.IsDisposed)
                {
                    receivedNotifications.Remove(notification);
                    notification.Close();
                    notification.Dispose();
                }
            }
            RepositionNotifications();
        }

        // Menu de contexto das linhas do histórico.
        private void InboxDeleteRequested(object sender, InboxDeleteEventArgs e)
        {
            if (e == null) return;
            if (e.ForEveryone)
            {
                if (MessageBox.Show(this, "Confirma?", "Deletar para todos",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return;
                }
                PeerInfo peer = FindPeerByAddress(e.Address);
                if (peer == null)
                {
                    MessageBox.Show(this,
                        "O destinatário não está na lista agora; a mensagem foi apagada só aqui.",
                        "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    PeerInfo target = peer;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        string error;
                        bool ok = MessageSender.SendDelete(target, e.OperationId, out error);
                        if (!ok)
                        {
                            TryBeginInvoke(delegate
                            {
                                statusLabel.ForeColor = Color.FromArgb(185, 28, 28);
                                statusLabel.Text = "Não foi possível avisar " + target.Name + ".";
                            });
                        }
                    });
                }
            }
            else if (MessageBox.Show(this, "Confirma?", "Deletar",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            inboxBox.RemoveBySeq(e.Seq);
            HistoryStore.DeleteBySeq(e.Seq);
        }

        private PeerInfo FindPeerByAddress(string address)
        {
            if (String.IsNullOrEmpty(address) || latestPeers == null) return null;
            foreach (PeerInfo peer in latestPeers)
            {
                if (peer != null && String.Equals(peer.Address, address,
                    StringComparison.Ordinal))
                {
                    return peer;
                }
            }
            return null;
        }

        private void NetworkServiceAudioReceived(object sender, AudioReceivedEventArgs e)
        {
            if (IsDisposed)
            {
                TailMsgDiagnostics.WriteMessageEvent(
                    e.OperationId,
                    "ui_audio_received",
                    "dropped",
                    e.SenderName,
                    e.RemoteAddress,
                    e.Fingerprint,
                    0,
                    "form-disposed");
                return;
            }

            TailMsgDiagnostics.WriteMessageEvent(
                e.OperationId,
                "ui_audio_queued",
                "success",
                e.SenderName,
                e.RemoteAddress,
                e.Fingerprint,
                0,
                "");

            TryBeginInvoke(delegate
            {
                TailMsgDiagnostics.WriteMessageEvent(
                    e.OperationId,
                    "ui_audio_shown",
                    "success",
                    e.SenderName,
                    e.RemoteAddress,
                    e.Fingerprint,
                    0,
                    "");

                AudioPayload inboxAudio = new AudioPayload();
                inboxAudio.WavBytes = e.AudioBytes;
                inboxAudio.DurationMilliseconds = e.DurationMilliseconds;
                string audioStamp = DateTime.Now.ToString("HH:mm:ss");
                InboxAudioRow audioRow = inboxBox.AppendAudio(
                    e.SenderName + ": [áudio " +
                    FormatDuration(e.DurationMilliseconds / 1000) + "] [" + audioStamp + "]",
                    inboxAudio,
                    e.OperationId);
                audioRow.Address = e.RemoteAddress;
                long audioSeq = HistoryStore.Append(new HistoryEntry
                {
                    Kind = "audio",
                    Time = audioStamp,
                    Sender = e.SenderName,
                    Address = e.RemoteAddress,
                    DurationMilliseconds = e.DurationMilliseconds,
                    FileName = HistoryStore.SaveMedia(e.AudioBytes, ".wav"),
                    OperationId = e.OperationId
                });
                audioRow.Seq = audioSeq;
                inboxBox.AttachMenu(audioRow, audioSeq, e.OperationId, false, "");
                StartInboxTranscription(audioRow);

                ReceivedMessageForm notification = new ReceivedMessageForm(
                    e,
                    localComputerName,
                    delegate { ShowFromTray(); });
                notification.PeerCapabilityLookup = networkService.FindPeerCapabilities;
                RegisterNotification(e.OperationId, notification);
                notification.StatusReporter = delegate(string text, bool isError)
                {
                    statusLabel.ForeColor = isError
                        ? Color.FromArgb(185, 28, 28)
                        : Color.FromArgb(21, 128, 61);
                    statusLabel.Text = text;
                };
                receivedNotifications.Add(notification);
                notification.FormClosed += delegate
                {
                    receivedNotifications.Remove(notification);
                    RepositionNotifications();
                };
                notification.SizeChanged += delegate { RepositionNotifications(); };
                RepositionNotifications();
                notification.Show();
                RepositionNotifications();
                statusLabel.ForeColor = Color.FromArgb(21, 128, 61);
                statusLabel.Text = "Áudio recebido de " + e.SenderName + ".";
            });
        }

        // Transcreve o áudio recebido para o histórico (e alimenta o cache que
        // o popup usa, para não transcrever duas vezes).
        private void StartInboxTranscription(InboxAudioRow row)
        {
            if (row == null) return;
            string operationId = row.OperationId;
            string cached;
            if (TranscriptionCache.TryGet(operationId, out cached))
            {
                row.SetTranscription(cached);
                HistoryStore.UpdateTranscription(row.HistorySeq, cached);
                return;
            }

            byte[] audioBytes = row.Audio == null ? null : row.Audio.WavBytes;
            if (audioBytes == null || audioBytes.Length == 0) return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                string text;
                string error;
                bool ok = TranscriptionClient.TryTranscribe(
                    audioBytes,
                    "audio.wav",
                    operationId,
                    out text,
                    out error);
                if (!ok) return;
                TranscriptionCache.Remember(operationId, text);
                TryBeginInvoke(delegate
                {
                    row.SetTranscription(text);
                    HistoryStore.UpdateTranscription(row.HistorySeq, text);
                });
            });
        }

        // Reconstroi o histórico salvo em disco no mesmo formato usado ao vivo:
        // as imagens e os áudios voltam com o botão de abrir/tocar apontando
        // para o arquivo guardado em %LOCALAPPDATA%\TailMsg\historico-midia.
        private void LoadHistoryIntoInbox()
        {
            List<HistoryEntry> entries = HistoryStore.Load();
            foreach (HistoryEntry entry in entries)
            {
                bool sent = entry.Kind != null && entry.Kind.StartsWith("sent", StringComparison.Ordinal);
                bool isImage = entry.Kind != null && entry.Kind.EndsWith("image", StringComparison.Ordinal);
                bool isAudio = entry.Kind != null && entry.Kind.EndsWith("audio", StringComparison.Ordinal);

                if (!isImage && !isAudio)
                {
                    InboxTextRow row = inboxBox.AppendMessage(
                        entry.Sender, entry.Text, entry.Time, sent, entry.Seq, entry.OperationId);
                    row.Address = entry.Address;
                    inboxBox.AttachMenu(row, entry.Seq, entry.OperationId, sent, "");
                    continue;
                }

                string path = HistoryStore.MediaPath(entry.FileName);
                bool exists = path.Length > 0 && File.Exists(path);
                if (isImage)
                {
                    InboxImageRow row = inboxBox.AppendImage(
                        entry.Sender + ": [imagem " + ImageTransfer.DescribeBytes(entry.Size) + "] [" + entry.Time + "]",
                        exists ? File.ReadAllBytes(path) : null,
                        exists ? path : null);
                    row.Address = entry.Address;
                    row.Seq = entry.Seq;
                    row.OperationId = entry.OperationId;
                    row.Sent = sent;
                    inboxBox.AttachMenu(row, entry.Seq, entry.OperationId, sent, "");
                    continue;
                }

                if (!exists) continue;
                AudioPayload payload = new AudioPayload();
                payload.WavBytes = File.ReadAllBytes(path);
                payload.DurationMilliseconds = entry.DurationMilliseconds;
                InboxAudioRow audioRow = inboxBox.AppendAudio(
                    entry.Sender + ": [áudio " + FormatDuration(entry.DurationMilliseconds / 1000) + "] [" + entry.Time + "]",
                    payload,
                    entry.OperationId);
                audioRow.Address = entry.Address;
                audioRow.Seq = entry.Seq;
                audioRow.OperationId = entry.OperationId;
                audioRow.Sent = sent;
                if (!String.IsNullOrEmpty(entry.Text)) audioRow.SetTranscription(entry.Text);
                inboxBox.AttachMenu(audioRow, entry.Seq, entry.OperationId, sent, entry.Text);
            }
        }

        private void RepositionNotifications()
        {
            for (int index = receivedNotifications.Count - 1; index >= 0; index--)
            {
                ReceivedMessageForm notification = receivedNotifications[index];
                if (notification == null || notification.IsDisposed)
                {
                    receivedNotifications.RemoveAt(index);
                }
            }

            if (receivedNotifications.Count == 0)
            {
                return;
            }

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            const int edgeMargin = 12;
            const int notificationGap = 6;
            int left = area.Right - edgeMargin - receivedNotifications[0].Width;

            for (int index = 0; index < receivedNotifications.Count; index++)
            {
                ReceivedMessageForm notification = receivedNotifications[index];
                int top = area.Bottom - edgeMargin - notification.Height -
                    (index * (notification.Height + notificationGap));
                notification.SetNotificationLocation(new Point(left, top));
            }
        }

    }

    internal static class WineEnvironment
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

        private static readonly bool isWine = DetectOnce();

        public static bool IsWine
        {
            get { return isWine; }
        }

        // Detecta execução sob Wine/Mono procurando o export
        // wine_get_version na ntdll. Em Windows nativo ele não existe.
        private static bool DetectOnce()
        {
            try
            {
                IntPtr module = GetModuleHandle("ntdll.dll");
                if (module == IntPtr.Zero) return false;
                return GetProcAddress(module, "wine_get_version") != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }
    }

    internal sealed class ReceivedMessageForm : Form
    {
        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;
        private static readonly bool runningUnderWine = WineEnvironment.IsWine;
        private readonly MessageReceivedEventArgs message;
        private readonly ImageReceivedEventArgs imageMessage;
        private readonly AudioReceivedEventArgs audioMessage;
        private readonly string localComputerName;
        private readonly Action openMainWindow;
        private readonly TextBox contentBox;
        private readonly MessageTextBox replyBox;
        private readonly Button copyButton;
        private readonly Button replyButton;
        private readonly Button transparencyButton;
        private readonly ContextMenuStrip transparencyMenu;
        private readonly Font transparencyRegularFont;
        private readonly Font transparencySelectedFont;
        private readonly Bitmap imageThumbnail;
        private readonly string imageThumbnailError;
        private Panel replyAttachmentBorder;
        private PictureBox replyAttachmentBox;
        private Label replyAttachmentLabel;
        private Label replyLabel;
        private Bitmap replyAttachmentThumbnail;
        private ImagePayload pendingReplyImage;
        private int baseReplyButtonTop;
        private int baseTransparencyButtonTop;
        private int basePopupHeight;
        private bool replySupportsImages;
        private bool replySupportsAudio;
        private int replyCapabilities;
        private AudioTrackPanel audioPanel;
        private bool transcriptionInProgress;
        private Button retryTranscriptionButton;
        // Resposta com áudio: os mesmos três botões do painel principal,
        // quadrados com a altura do botão Enviar do popup.
        private IconButton replyMicButton;
        private IconButton replyPauseButton;
        private IconButton replyLiveMicButton;
        private WaveRecorder replyRecorder;
        private System.Windows.Forms.Timer replyRecordingTimer;
        private bool replyRecording;
        private bool replyRecordingLive;
        private bool replyPaused;
        private AudioPayload replyAudio;
        private AudioTrackPanel replyAudioPanel;
        private Panel replyAudioHolder;
        private Panel bodyPanel;
        private string replyCommittedText = "";
        private string replyDraftText = "";
        private int replyCommitOffset;
        private int replyLastDraftBytes;
        private int replyGeneration;
        private bool replyCommitBusy;
        private int replyDiagnosticTicks;
        private int replyToolsTop;
        // A transcrição do áudio que gravamos para responder fica na caixa de
        // digitação, apenas informativa (só o áudio é enviado).
        private bool replyBoxIsTranscription;
        // Preenchidos conforme o tipo de popup: texto ou imagem.
        private readonly string senderName;
        private readonly string remoteAddress;
        // O popup de imagem espelha o popup de texto: mesma altura, mesmos
        // botões nas mesmas posições; a miniatura ocupa a área de conteúdo.
        private const int PopupHeight = 248;
        // O popup de áudio reserva espaço para a timeline e a transcrição.
        private const int AudioPopupHeight = 305;
        private const int AttachmentBandHeight = 62;

        // Consulta de capacidade do remetente, fornecida pela janela
        // principal. Sem ela, o anexo na resposta seria recusado sempre.
        internal Func<string, int> PeerCapabilityLookup;

        // Retorno do resultado na barra de status da janela principal, já que
        // o popup fecha quando a resposta é entregue.
        internal Action<string, bool> StatusReporter;

        public ReceivedMessageForm(
            MessageReceivedEventArgs message,
            string localComputerName,
            Action openMainWindow)
            : this(message, null, null, localComputerName, openMainWindow)
        {
        }

        public ReceivedMessageForm(
            ImageReceivedEventArgs image,
            string localComputerName,
            Action openMainWindow)
            : this(null, image, null, localComputerName, openMainWindow)
        {
        }

        public ReceivedMessageForm(
            AudioReceivedEventArgs audio,
            string localComputerName,
            Action openMainWindow)
            : this(null, null, audio, localComputerName, openMainWindow)
        {
        }

        private ReceivedMessageForm(
            MessageReceivedEventArgs message,
            ImageReceivedEventArgs image,
            AudioReceivedEventArgs audio,
            string localComputerName,
            Action openMainWindow)
        {
            this.message = message;
            this.imageMessage = image;
            this.audioMessage = audio;
            this.localComputerName = localComputerName;
            this.openMainWindow = openMainWindow;

            bool isImage = image != null;
            bool hasAudio = audio != null;
            if (hasAudio)
            {
                senderName = audio.SenderName;
                remoteAddress = audio.RemoteAddress;
            }
            else if (isImage)
            {
                senderName = image.SenderName;
                remoteAddress = image.RemoteAddress;
            }
            else
            {
                senderName = message.SenderName;
                remoteAddress = message.RemoteAddress;
            }

            // Quem nos enviou uma imagem comprovadamente aceita imagens; nos
            // outros casos a resposta só anexa se a descoberta anunciar a
            // capacidade (consultada pela janela principal).
            replySupportsImages = isImage;   // refinado abaixo, se houver consulta

            if (isImage)
            {
                imageThumbnail = ImageTransfer.CreateThumbnail(
                    image.ImageBytes,
                    408,
                    52,
                    out imageThumbnailError);
            }
            else
            {
                imageThumbnail = null;
                imageThumbnailError = "";
            }

            Text = "Mensagem recebida - TailMsg";
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(420, hasAudio ? AudioPopupHeight : PopupHeight);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ShowIcon = false;
            TopMost = true;
            Opacity = NotificationSettings.WindowOpacity;
            Font = new Font("Segoe UI", 9F);

            Panel body = new Panel();
            bodyPanel = body;
            body.Dock = DockStyle.Fill;
            body.Padding = new Padding(10);
            body.BackColor = Color.FromArgb(31, 41, 55);
            Controls.Add(body);

            Label openButton = new Label();
            openButton.Text = "↖";
            openButton.Font = new Font("Segoe UI Symbol", 17F, FontStyle.Bold);
            openButton.Size = new Size(32, 28);
            openButton.Location = new Point(3, 3);
            openButton.TextAlign = ContentAlignment.MiddleCenter;
            openButton.BackColor = body.BackColor;
            openButton.ForeColor = Color.White;
            openButton.Cursor = Cursors.Hand;
            openButton.TabStop = false;
            openButton.Click += OpenMainButtonClick;
            body.Controls.Add(openButton);

            Label closeButton = new Label();
            closeButton.Text = "×";
            closeButton.Font = new Font("Segoe UI", 15F, FontStyle.Bold);
            closeButton.Size = new Size(32, 28);
            closeButton.Location = new Point(body.ClientSize.Width - 35, 3);
            closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            closeButton.TextAlign = ContentAlignment.MiddleCenter;
            closeButton.BackColor = body.BackColor;
            closeButton.ForeColor = Color.White;
            closeButton.Cursor = Cursors.Hand;
            closeButton.TabStop = false;
            closeButton.Click += delegate { Close(); };
            body.Controls.Add(closeButton);

            Label title = new Label();
            title.AutoSize = false;
            title.Text = "Mensagem recebida";
            title.TextAlign = ContentAlignment.MiddleCenter;
            title.ForeColor = Color.White;
            title.Font = new Font("Segoe UI Semibold", 11F);
            title.Location = new Point(52, 8);
            title.Size = new Size(body.ClientSize.Width - 104, 30);
            title.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            body.Controls.Add(title);

            Label sender = new Label();
            sender.AutoSize = false;
            sender.Text = "De: " + senderName + "  (" + remoteAddress + ")";
            sender.TextAlign = ContentAlignment.MiddleLeft;
            sender.ForeColor = Color.FromArgb(209, 213, 219);
            sender.Location = new Point(10, 43);
            sender.Size = new Size(body.ClientSize.Width - 20, 22);
            sender.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            body.Controls.Add(sender);

            // A área de conteúdo é a mesma do popup de texto: a miniatura ocupa
            // o lugar da caixa de mensagem e o restante do layout (botões,
            // resposta) fica idêntico ao popup original.
            if (hasAudio)
            {
                // A timeline fica sobre o fundo natural da janela (sem quadro
                // branco) e a transcrição usa a MESMA caixa de texto da
                // mensagem de texto, logo abaixo.
                audioPanel = new AudioTrackPanel(CreateAudioPayload(audio), false);
                audioPanel.Location = new Point(5, 63);
                audioPanel.Size = new Size(body.ClientSize.Width - 10, 40);
                audioPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                audioPanel.UsePopupColors(30);
                audioPanel.PlaybackFailed += delegate(object source, EventArgs args)
                {
                    PlaybackFailedEventArgs failure = args as PlaybackFailedEventArgs;
                    MessageBox.Show(
                        this,
                        failure == null
                            ? "Não foi possível tocar o áudio."
                            : failure.ErrorMessage,
                        "TailMsg",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                };
                body.Controls.Add(audioPanel);

                retryTranscriptionButton = new Button();
                retryTranscriptionButton.Text = "Tentar de novo";
                retryTranscriptionButton.Size = new Size(104, 20);
                retryTranscriptionButton.Location = new Point(
                    body.ClientSize.Width - 109,
                    70);
                retryTranscriptionButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                retryTranscriptionButton.BackColor = Color.FromArgb(55, 65, 81);
                retryTranscriptionButton.ForeColor = Color.White;
                retryTranscriptionButton.FlatStyle = FlatStyle.Flat;
                retryTranscriptionButton.FlatAppearance.BorderSize = 0;
                retryTranscriptionButton.Cursor = Cursors.Hand;
                retryTranscriptionButton.Visible = false;
                retryTranscriptionButton.Click += delegate { StartTranscription(); };
                body.Controls.Add(retryTranscriptionButton);

                contentBox = new TextBox();
                contentBox.Multiline = true;
                contentBox.ReadOnly = true;
                contentBox.ScrollBars = ScrollBars.Vertical;
                contentBox.Font = new Font("Segoe UI", 10F);
                contentBox.BackColor = Color.White;
                contentBox.Location = new Point(5, 107);
                contentBox.Size = new Size(body.ClientSize.Width - 10, 84);
                contentBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                contentBox.MouseDown += ActivateForInteraction;
                body.Controls.Add(contentBox);
            }
            else if (isImage)
            {
                Panel imageBorder = new Panel();
                imageBorder.Location = new Point(5, 65);
                imageBorder.Size = new Size(body.ClientSize.Width - 10, 54);
                imageBorder.Padding = new Padding(1);
                imageBorder.BackColor = Color.FromArgb(107, 114, 128);
                imageBorder.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                body.Controls.Add(imageBorder);

                PictureBox pictureBox = new PictureBox();
                pictureBox.Dock = DockStyle.Fill;
                pictureBox.SizeMode = PictureBoxSizeMode.Zoom;
                pictureBox.BackColor = Color.White;
                pictureBox.Image = imageThumbnail;
                pictureBox.MouseDown += ActivateForInteraction;
                imageBorder.Controls.Add(pictureBox);

                if (!String.IsNullOrEmpty(imageThumbnailError))
                {
                    Label imageFailure = new Label();
                    imageFailure.AutoSize = false;
                    imageFailure.Dock = DockStyle.Bottom;
                    imageFailure.Height = 16;
                    imageFailure.Text = "Não foi possível exibir a imagem recebida.";
                    imageFailure.TextAlign = ContentAlignment.MiddleLeft;
                    imageFailure.ForeColor = Color.FromArgb(185, 28, 28);
                    imageFailure.BackColor = Color.White;
                    imageBorder.Controls.Add(imageFailure);
                    imageFailure.BringToFront();
                }
            }
            else
            {
                contentBox = new TextBox();
                contentBox.Multiline = true;
                contentBox.ReadOnly = true;
                contentBox.ScrollBars = ScrollBars.Vertical;
                contentBox.Font = new Font("Segoe UI", 10F);
                contentBox.BackColor = Color.White;
                contentBox.Text = message.Message;
                contentBox.Location = new Point(5, 65);
                contentBox.Size = new Size(body.ClientSize.Width - 10, 54);
                contentBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                contentBox.MouseDown += ActivateForInteraction;
                body.Controls.Add(contentBox);
            }

            replyLabel = new Label();
            replyLabel.AutoSize = true;
            replyLabel.Text = "Responder:";
            replyLabel.ForeColor = Color.FromArgb(209, 213, 219);
            replyLabel.Location = new Point(5, hasAudio ? 197 : 145);
            body.Controls.Add(replyLabel);

            replyBox = new MessageTextBox();
            replyBox.Multiline = true;
            replyBox.ScrollBars = ScrollBars.Vertical;
            replyBox.Font = new Font("Segoe UI", 10F);
            replyBox.BackColor = Color.White;
            replyBox.Location = new Point(5, hasAudio ? 217 : 165);
            replyBox.Size = new Size(body.ClientSize.Width - 10, 54);
            replyBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            replyBox.MouseDown += ActivateForInteraction;
            replyBox.UserEdited += delegate { replyBoxIsTranscription = false; };
            replyBox.ImagePasted += ReplyImagePasted;
            replyBox.ImagePasteFailed += ReplyImagePasteFailed;
            body.Controls.Add(replyBox);

            // Faixa do anexo da resposta: fica escondida e o popup cresce
            // quando o usuário cola uma imagem para responder.
            replyAttachmentBorder = new Panel();
            replyAttachmentBorder.Size = new Size(
                body.ClientSize.Width - 10,
                AttachmentBandHeight - 6);
            replyAttachmentBorder.Padding = new Padding(1);
            replyAttachmentBorder.BackColor = Color.FromArgb(107, 114, 128);
            replyAttachmentBorder.Anchor =
                AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            replyAttachmentBorder.Visible = false;
            body.Controls.Add(replyAttachmentBorder);

            Panel replyAttachmentInner = new Panel();
            replyAttachmentInner.Dock = DockStyle.Fill;
            replyAttachmentInner.BackColor = Color.White;
            replyAttachmentBorder.Controls.Add(replyAttachmentInner);

            replyAttachmentBox = new PictureBox();
            replyAttachmentBox.Size = new Size(48, 48);
            replyAttachmentBox.Location = new Point(3, 2);
            replyAttachmentBox.SizeMode = PictureBoxSizeMode.Zoom;
            replyAttachmentBox.BackColor = Color.FromArgb(243, 244, 246);
            replyAttachmentInner.Controls.Add(replyAttachmentBox);

            replyAttachmentLabel = new Label();
            replyAttachmentLabel.AutoSize = false;
            replyAttachmentLabel.Location = new Point(58, 2);
            replyAttachmentLabel.Size = new Size(replyAttachmentInner.Width - 150, 48);
            replyAttachmentLabel.Anchor =
                AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            replyAttachmentLabel.TextAlign = ContentAlignment.MiddleLeft;
            replyAttachmentLabel.ForeColor = Color.FromArgb(55, 65, 81);
            replyAttachmentInner.Controls.Add(replyAttachmentLabel);

            Button removeAttachmentButton = new Button();
            removeAttachmentButton.Text = "Remover";
            removeAttachmentButton.Size = new Size(78, 24);
            removeAttachmentButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            removeAttachmentButton.BackColor = Color.FromArgb(55, 65, 81);
            removeAttachmentButton.ForeColor = Color.White;
            removeAttachmentButton.FlatStyle = FlatStyle.Flat;
            removeAttachmentButton.FlatAppearance.BorderSize = 0;
            removeAttachmentButton.Cursor = Cursors.Hand;
            removeAttachmentButton.Click += delegate { ClearReplyAttachment(); };
            replyAttachmentInner.Controls.Add(removeAttachmentButton);
            replyAttachmentInner.Resize += delegate
            {
                removeAttachmentButton.Location = new Point(
                    Math.Max(60, replyAttachmentInner.ClientSize.Width -
                        removeAttachmentButton.Width - 6),
                    Math.Max(1, (replyAttachmentInner.ClientSize.Height -
                        removeAttachmentButton.Height) / 2));
                replyAttachmentLabel.Size = new Size(
                    Math.Max(40, removeAttachmentButton.Left -
                        replyAttachmentLabel.Left - 6),
                    Math.Max(20, replyAttachmentInner.ClientSize.Height - 4));
            };

            replyButton = new Button();
            replyButton.Text = "Enviar";
            replyButton.Size = new Size(64, 22);
            replyButton.Location = new Point(body.ClientSize.Width - 69, hasAudio ? 279 : 221);
            replyButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            replyButton.BackColor = Color.FromArgb(37, 99, 235);
            replyButton.ForeColor = Color.White;
            replyButton.FlatStyle = FlatStyle.Flat;
            replyButton.FlatAppearance.BorderSize = 0;
            replyButton.Cursor = Cursors.Hand;
            replyButton.Click += ReplyButtonClick;
            body.Controls.Add(replyButton);

            transparencyMenu = new ContextMenuStrip();
            transparencyMenu.AutoSize = false;
            transparencyMenu.ShowCheckMargin = false;
            transparencyMenu.ShowImageMargin = false;
            transparencyRegularFont = new Font("Segoe UI", 9F, FontStyle.Regular);
            transparencySelectedFont = new Font("Segoe UI", 9F, FontStyle.Bold);
            for (int percent = NotificationSettings.MinimumTransparencyPercent;
                 percent <= NotificationSettings.MaximumTransparencyPercent;
                 percent += 10)
            {
                int selectedPercent = percent;
                ToolStripMenuItem option = new ToolStripMenuItem(percent + "%");
                option.AutoSize = false;
                option.Height = 22;
                option.Width = 40;
                option.Click += delegate
                {
                    NotificationSettings.SetTransparencyPercent(selectedPercent);
                    UpdateTransparencyMenu();
                    ApplyTransparency();
                };
                transparencyMenu.Items.Add(option);
            }

            transparencyButton = new Button();
            transparencyButton.Text = NotificationSettings.TransparencyPercent + "%";
            transparencyButton.Size = new Size(40, 22);
            transparencyButton.Location = new Point(5, hasAudio ? 279 : 221);
            transparencyButton.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            transparencyButton.BackColor = Color.FromArgb(55, 65, 81);
            transparencyButton.ForeColor = Color.White;
            transparencyButton.FlatStyle = FlatStyle.Flat;
            transparencyButton.FlatAppearance.BorderSize = 0;
            transparencyButton.Cursor = Cursors.Hand;
            transparencyButton.Click += TransparencyButtonClick;
            transparencyMenu.Width = transparencyButton.Width;
            transparencyMenu.Height =
                (transparencyMenu.Items.Count * transparencyButton.Height) + 4;
            body.Controls.Add(transparencyButton);

            copyButton = new Button();
            copyButton.Text = "Copiar";
            copyButton.Size = new Size(64, 22);
            copyButton.Location = new Point(body.ClientSize.Width - 69, 122);
            copyButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            copyButton.BackColor = Color.FromArgb(55, 65, 81);
            copyButton.ForeColor = Color.White;
            copyButton.FlatStyle = FlatStyle.Flat;
            copyButton.FlatAppearance.BorderSize = 0;
            copyButton.Cursor = Cursors.Hand;
            copyButton.Click += CopyButtonClick;
            copyButton.Visible = !hasAudio;
            body.Controls.Add(copyButton);

            // Microfone branco, pausa e microfone vermelho na resposta, na
            // mesma linha do Enviar e com a mesma altura dele (22 px).
            int replyRowTop = hasAudio ? 279 : 221;
            replyToolsTop = replyRowTop;
            replyMicButton = new IconButton();
            replyMicButton.Size = new Size(22, 22);
            replyMicButton.Location = new Point(420 - 147, replyRowTop);
            replyMicButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            replyMicButton.AccessibleName = "Gravar áudio na resposta";
            replyMicButton.Click += delegate { ToggleReplyWhiteMicrophone(); };
            body.Controls.Add(replyMicButton);

            replyPauseButton = new IconButton();
            replyPauseButton.Size = new Size(22, 22);
            replyPauseButton.Location = new Point(420 - 121, replyRowTop);
            replyPauseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            replyPauseButton.AccessibleName = "";
            replyPauseButton.Glyph = IconGlyph.None;
            replyPauseButton.Click += delegate { ToggleReplyPause(); };
            body.Controls.Add(replyPauseButton);

            replyLiveMicButton = new IconButton();
            replyLiveMicButton.Size = new Size(22, 22);
            replyLiveMicButton.Location = new Point(420 - 95, replyRowTop);
            replyLiveMicButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            replyLiveMicButton.AccessibleName = "Gravar na resposta com transcrição ao vivo";
            replyLiveMicButton.Click += delegate { ToggleReplyLiveMicrophone(); };
            body.Controls.Add(replyLiveMicButton);

            UpdateReplyRecordButtons();            UpdateReplyRecordButtons();

            if (hasAudio) StartTranscription();

            baseReplyButtonTop = replyButton.Top;
            baseTransparencyButtonTop = transparencyButton.Top;
            basePopupHeight = ClientSize.Height;
            RefreshReplyCapability();
            ApplyReplyLayout();
        }

        // Atualiza se o remetente aceita imagens: quem nos enviou uma imagem
        // certamente aceita; nos outros casos vale a descoberta do peer.
        private void RefreshReplyCapability()
        {
            // Capacidades anunciadas pelo remetente, somadas ao que já sabemos
            // por ele ter nos enviado algo: quem mandou áudio aceita áudio.
            int capabilities = 0;
            if (PeerCapabilityLookup != null)
            {
                capabilities = PeerCapabilityLookup(remoteAddress);
            }
            if (imageMessage != null)
            {
                capabilities |= TailMsgProtocol.CapabilityImage;
            }
            if (audioMessage != null)
            {
                capabilities |= TailMsgProtocol.CapabilityAudio;
            }

            replyCapabilities = capabilities;
            replySupportsImages = TailMsgProtocol.SupportsImages(capabilities);
            replySupportsAudio = TailMsgProtocol.SupportsAudio(capabilities);
            UpdateReplyLabel();
        }

        private void UpdateReplyLabel()
        {
            if (replyLabel == null) return;
            replyLabel.Text = replySupportsImages
                ? "Responder:  (Ctrl+V anexa uma imagem)"
                : "Responder:";
        }

        private void ReplyImagePasted(object sender, ImagePastedEventArgs e)
        {
            if (e == null || e.Image == null) return;
            SetReplyAttachment(e.Image);
        }

        private void ReplyImagePasteFailed(object sender, ImagePasteFailedEventArgs e)
        {
            if (e == null || String.IsNullOrEmpty(e.ErrorMessage)) return;
            MessageBox.Show(
                this,
                e.ErrorMessage,
                "TailMsg",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private void SetReplyAttachment(ImagePayload image)
        {
            RefreshReplyCapability();
            ClearReplyAttachmentThumbnail();
            pendingReplyImage = image;

            string error;
            replyAttachmentThumbnail = ImageTransfer.CreateThumbnail(
                image.PngBytes,
                48,
                48,
                out error);
            replyAttachmentBox.Image = replyAttachmentThumbnail;

            string description = "Anexo: " +
                ImageTransfer.DescribeDimensions(image.Width, image.Height) +
                " — " + ImageTransfer.DescribeBytes(image.ByteCount);
            if (ImageTransfer.IsLarge(image.ByteCount))
            {
                description += "  (arquivo grande: o envio pode demorar)";
            }
            if (!String.IsNullOrEmpty(error))
            {
                description += "  (miniatura indisponível)";
            }
            replyAttachmentLabel.Text = description;

            ApplyReplyLayout();
        }

        private void ClearReplyAttachment()
        {
            pendingReplyImage = null;
            ClearReplyAttachmentThumbnail();
            ApplyReplyLayout();
        }

        private void ClearReplyAttachmentThumbnail()
        {
            if (replyAttachmentThumbnail == null) return;
            replyAttachmentBox.Image = null;
            replyAttachmentThumbnail.Dispose();
            replyAttachmentThumbnail = null;
        }

        private static AudioPayload CreateAudioPayload(AudioReceivedEventArgs audio)
        {
            AudioPayload payload = new AudioPayload();
            payload.WavBytes = audio.AudioBytes;
            payload.DurationMilliseconds = audio.DurationMilliseconds;
            return payload;
        }

        // A transcrição é buscada em segundo plano: o popup abre na hora e o
        // texto aparece quando o serviço responde.
        private void StartTranscription()
        {
            if (audioMessage == null || contentBox == null) return;
            if (transcriptionInProgress) return;

            string cached;
            if (TranscriptionCache.TryGet(audioMessage.OperationId, out cached))
            {
                ApplyTranscription(true, cached, "");
                return;
            }

            transcriptionInProgress = true;
            SetTranscriptionText("Transcrevendo...");
            retryTranscriptionButton.Visible = false;

            byte[] audioBytes = audioMessage.AudioBytes;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string text;
                string error;
                bool ok = TranscriptionClient.TryTranscribe(
                    audioBytes,
                    "audio.wav",
                    audioMessage.OperationId,
                    out text,
                    out error);

                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        transcriptionInProgress = false;
                        ApplyTranscription(ok, text, error);
                    });
                }
                catch (InvalidOperationException) { }
            });
        }

        // A transcrição aparece na mesma caixa de texto usada pelas mensagens
        // de texto recebidas.
        private void SetTranscriptionText(string text)
        {
            if (contentBox == null || contentBox.IsDisposed) return;
            contentBox.Text = text == null ? "" : text;
            contentBox.SelectionStart = contentBox.TextLength;
            contentBox.ScrollToCaret();
        }

        private void ApplyTranscription(bool ok, string text, string error)
        {
            if (contentBox == null || contentBox.IsDisposed) return;
            if (ok)
            {
                SetTranscriptionText(text);
                retryTranscriptionButton.Visible = false;
                TranscriptionCache.Remember(audioMessage.OperationId, text);
                return;
            }

            SetTranscriptionText(String.IsNullOrEmpty(error)
                ? "Não foi possível transcrever este áudio."
                : "Não foi possível transcrever: " + error);
            retryTranscriptionButton.Visible = true;
        }

        // ---- Áudio na resposta -------------------------------------------------

        // Três botões na mesma linha do Enviar: branco, pausa e vermelho, com
        // os mesmos ícones do painel principal.
        private void UpdateReplyRecordButtons()
        {
            if (replyMicButton == null) return;
            EnsureAudioIconsStatic();

            bool whiteActive = replyRecording && !replyRecordingLive;
            bool redActive = replyRecording && replyRecordingLive;

            if (whiteActive)
            {
                replyMicButton.SourceImage = null;
                replyMicButton.CircleColor = Color.FromArgb(61, 21, 21);
                replyMicButton.CircleOutline = Color.FromArgb(90, 36, 36);
                replyMicButton.Glyph = IconGlyph.Check;
                replyMicButton.AccessibleName = "Encerrar gravação na resposta";
            }
            else
            {
                replyMicButton.SourceImage = MainForm.WhiteMicrophoneIcon;
                replyMicButton.Glyph = IconGlyph.None;
                replyMicButton.AccessibleName = "Gravar áudio na resposta";
            }

            if (redActive)
            {
                replyLiveMicButton.SourceImage = null;
                replyLiveMicButton.CircleColor = Color.FromArgb(19, 32, 30);
                replyLiveMicButton.CircleOutline = Color.FromArgb(44, 64, 61);
                replyLiveMicButton.Glyph = IconGlyph.Check;
                replyLiveMicButton.AccessibleName = "Encerrar transcrição na resposta";
            }
            else
            {
                replyLiveMicButton.SourceImage = MainForm.RedMicrophoneIcon;
                replyLiveMicButton.Glyph = IconGlyph.None;
                replyLiveMicButton.AccessibleName =
                    "Gravar na resposta com transcrição ao vivo";
            }

            if (replyRecording && !replyPaused)
            {
                replyPauseButton.SourceImage = MainForm.PauseIcon;
                replyPauseButton.Glyph = IconGlyph.None;
                replyPauseButton.AccessibleName = "Pausar gravação da resposta";
            }
            else if (replyRecording && replyPaused)
            {
                replyPauseButton.SourceImage = null;
                replyPauseButton.CircleColor = Color.FromArgb(242, 207, 55);
                replyPauseButton.CircleOutline = Color.FromArgb(196, 157, 0);
                replyPauseButton.Glyph = IconGlyph.Play;
                replyPauseButton.AccessibleName = "Retomar gravação da resposta";
            }
            else
            {
                replyPauseButton.SourceImage = null;
                replyPauseButton.Glyph = IconGlyph.None;
                replyPauseButton.AccessibleName = "";
            }

            replyMicButton.Invalidate();
            replyPauseButton.Invalidate();
            replyLiveMicButton.Invalidate();
        }

        private static void EnsureAudioIconsStatic()
        {
            MainForm.EnsureAudioIconsPublic();
        }

        private void ToggleReplyWhiteMicrophone()
        {
            if (replyRecording && !replyRecordingLive)
            {
                StopReplyRecording();
                return;
            }
            if (replyRecording) return;
            StartReplyRecording(false);
        }

        private void ToggleReplyLiveMicrophone()
        {
            if (replyRecording && replyRecordingLive)
            {
                StopReplyRecording();
                return;
            }
            if (replyRecording) return;
            StartReplyRecording(true);
        }

        private void ToggleReplyPause()
        {
            if (!replyRecording || replyRecorder == null) return;
            string error;
            if (replyPaused)
            {
                if (!replyRecorder.Resume(out error)) return;
                replyPaused = false;
            }
            else
            {
                if (!replyRecorder.Pause(out error)) return;
                replyPaused = true;
            }
            UpdateReplyRecordButtons();
        }

        private void StartReplyRecording(bool live)
        {
            if (replyRecording) return;
            WaveRecorder candidate = new WaveRecorder();
            string error;
            if (!candidate.Start(out error))
            {
                candidate.Dispose();
                MessageBox.Show(this, error, "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            replyRecorder = candidate;
            replyRecording = true;
            replyRecordingLive = live;
            replyPaused = false;
            replyDiagnosticTicks = 0;
            replyCommittedText = "";
            replyDraftText = "";
            replyCommitOffset = 0;
            replyLastDraftBytes = 0;
            replyGeneration++;
            replyCommitBusy = false;
            UpdateReplyRecordButtons();


            if (replyRecordingTimer == null)
            {
                replyRecordingTimer = new System.Windows.Forms.Timer();
                replyRecordingTimer.Interval = 250;
                replyRecordingTimer.Tick += delegate { UpdateReplyRecording(); };
            }
            replyRecordingTimer.Start();
        }

        private void UpdateReplyRecording()
        {
            if (!replyRecording || replyRecorder == null) return;

            int seconds = (int)Math.Round(replyRecorder.ElapsedSeconds);
            string partial = ReplyTranscriptionText();
            if (partial.Length > 0) SetReplyTranscriptionText(partial);

            if (replyRecorder.LimitReached)
            {
                StopReplyRecording();
                return;
            }

            if (replyRecordingLive) UpdateReplyLiveTranscription();
        }

        private void StopReplyRecording()
        {
            if (!replyRecording || replyRecorder == null) return;
            bool wasLive = replyRecordingLive;

            byte[] liveTail = null;
            if (wasLive) liveTail = replyRecorder.SnapshotFrom(replyCommitOffset);

            if (replyRecordingTimer != null) replyRecordingTimer.Stop();
            replyRecording = false;
            replyRecordingLive = false;
            replyPaused = false;
            AudioPayload payload = replyRecorder.Stop();
            replyRecorder.Dispose();
            replyRecorder = null;
            UpdateReplyRecordButtons();

            if (payload == null)
            {
                HideReplyAudioPlayer();
                return;
            }

            SetReplyAudio(payload);
            if (wasLive)
            {
                if (liveTail != null && liveTail.Length >= 12800)
                {
                    TranscribeReplyWindow(liveTail, true);
                }
                replyCommitOffset = 0;
                replyLastDraftBytes = 0;
            }
            else
            {
                StartReplyTranscription(payload);
            }
        }

        private string ReplyTranscriptionText()
        {
            string committed = replyCommittedText.Trim();
            string draft = replyDraftText.Trim();
            if (draft.Length == 0) return committed;
            if (committed.Length == 0) return draft;
            return committed + " " + draft;
        }

        private void StartReplyTranscription(AudioPayload payload)
        {
            if (payload == null || payload.WavBytes == null) return;
            byte[] wavBytes = payload.WavBytes;
            string operationId = TailMsgDiagnostics.CreateOperationId();
            SetReplyTranscriptionText("Transcrevendo...");

            ThreadPool.QueueUserWorkItem(delegate
            {
                string text;
                string error;
                bool ok = TranscriptionClient.TryTranscribe(
                    wavBytes,
                    "audio.wav",
                    operationId,
                    out text,
                    out error);
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (ok)
                        {
                            TranscriptionCache.Remember(operationId, text);
                            SetReplyTranscriptionText(text);
                        }
                    });
                }
                catch (InvalidOperationException) { }
            });
        }

        // Transcrição incremental do microfone vermelho na resposta, no mesmo
        // ritmo do painel principal: rascunho a cada 1 s, confirmação a cada 10 s.
        private void UpdateReplyLiveTranscription()
        {
            if (replyRecorder == null || replyCommitBusy) return;
            int captured = replyRecorder.CapturedBytes;
            int windowBytes = captured - replyCommitOffset;
            if (windowBytes < 4800) return;

            if (windowBytes >= 16000 * 2 * 10 && !replyCommitBusy)
            {
                replyCommitBusy = true;
                replyLastDraftBytes = captured;
                TranscribeReplyWindow(windowBytes, true);
                return;
            }

            if (captured - replyLastDraftBytes >= 16000 * 2)
            {
                replyLastDraftBytes = captured;
                TranscribeReplyWindow(windowBytes, false);
            }
        }

        private void TranscribeReplyWindow(int windowBytes, bool commit)
        {
            byte[] pcm;
            if (replyRecorder != null)
            {
                pcm = replyRecorder.SnapshotFrom(replyCommitOffset);
            }
            else
            {
                return;
            }
            TranscribeReplyBytes(pcm, commit, replyCommitOffset + windowBytes);
        }

        private void TranscribeReplyWindow(byte[] pcm, bool commit)
        {
            TranscribeReplyBytes(pcm, commit, 0);
        }

        private void TranscribeReplyBytes(byte[] pcm, bool commit, int commitEnd)
        {
            if (pcm == null || pcm.Length < 4800) return;
            byte[] wav = WaveRecorder.WrapPcm(pcm);
            if (wav == null) return;

            int generation = ++replyGeneration;
            string operationId = TailMsgDiagnostics.CreateOperationId();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string text;
                string error;
                bool ok = TranscriptionClient.TryTranscribe(
                    wav,
                    "audio.wav",
                    operationId,
                    out text,
                    out error);
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (commit)
                        {
                            replyCommitBusy = false;
                            if (!ok) return;
                            replyCommittedText = AppendReplyText(replyCommittedText, text);
                            replyDraftText = "";
                            replyCommitOffset = commitEnd;
                            replyLastDraftBytes = commitEnd;
                            TranscriptionCache.Remember(operationId, text);
                        }
                        else
                        {
                            if (generation != replyGeneration) return;
                            if (!ok) return;
                            replyDraftText = text;
                        }
                        SetReplyTranscriptionText(ReplyTranscriptionText());
                    });
                }
                catch (InvalidOperationException) { }
            });
        }

        private static string AppendReplyText(string current, string addition)
        {
            string text = (current ?? "").Trim();
            string extra = (addition ?? "").Trim();
            if (extra.Length == 0) return text;
            if (text.Length == 0) return extra;
            return text + " " + extra;
        }

        private void SetReplyAudio(AudioPayload payload)
        {
            replyAudio = payload;
            ShowReplyAudioPlayer();
        }

        private void ClearReplyAudio()
        {
            if (replyRecording) StopReplyRecording();
            replyAudio = null;
            replyCommittedText = "";
            replyDraftText = "";
            replyBoxIsTranscription = false;
            replyBox.SetProgrammaticText("");
            HideReplyAudioPlayer();
        }

        private void SetReplyTranscriptionText(string transcription)
        {
            if (replyBox == null) return;
            string text = transcription == null ? "" : transcription.Trim();
            replyBoxIsTranscription = text.Length > 0;
            replyBox.SetProgrammaticText(text);
        }

        // O áudio gravado para a resposta aparece como o MESMO player do áudio
        // recebido (timeline com play/pause, sobre o fundo da janela).
        private void ShowReplyAudioPlayer()
        {
            if (replyAudio == null) return;
            HideReplyAudioPlayer();

            AudioTrackPanel panel = new AudioTrackPanel(replyAudio, false);
            panel.UsePopupColors(30);
            panel.Location = new Point(5, 0);
            panel.Size = new Size(Math.Max(120, replyBox.Width), 40);
            panel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            panel.PlaybackFailed += delegate(object sender, EventArgs e)
            {
                PlaybackFailedEventArgs failure = e as PlaybackFailedEventArgs;
                MessageBox.Show(
                    this,
                    failure == null
                        ? "Não foi possível tocar o áudio."
                        : failure.ErrorMessage,
                    "TailMsg",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            };
            replyAudioPanel = panel;

            Panel holder = new Panel();
            holder.Name = "replyAudioHolder";
            holder.BackColor = Color.FromArgb(31, 41, 55);
            holder.Padding = new Padding(0);
            holder.Controls.Add(panel);
            replyAudioHolder = holder;
            bodyPanel.Controls.Add(holder);
            ApplyReplyLayout();
        }

        private void HideReplyAudioPlayer()
        {
            if (replyAudioPanel != null)
            {
                replyAudioPanel.StopPlayback();
                replyAudioPanel.Dispose();
                replyAudioPanel = null;
            }
            if (replyAudioHolder != null)
            {
                replyAudioHolder.Dispose();
                replyAudioHolder = null;
            }
            ApplyReplyLayout();
        }

        // A faixa de anexos cresce conforme o que estiver anexado (imagem e/ou
        // áudio) e a linha de botões (Enviar, transparência e os três de áudio)
        // desce para DEPOIS das faixas — antes ela caía na mesma área e o painel
        // do anexo (adicionado depois, no topo do z-order) engolia os cliques.
        private void ApplyReplyLayout()
        {
            if (replyBox == null) return;

            int top = replyBox.Bottom + 6;
            if (pendingReplyImage != null)
            {
                replyAttachmentBorder.Location = new Point(5, top);
                replyAttachmentBorder.Visible = true;
                top += AttachmentBandHeight;
            }
            else
            {
                replyAttachmentBorder.Visible = false;
            }

            bool audioVisible = replyAudio != null && replyAudioHolder != null;
            if (audioVisible)
            {
                replyAudioHolder.Location = new Point(5, top);
                replyAudioHolder.Size = new Size(Math.Max(120, replyBox.Width), 40);
                replyAudioHolder.Visible = true;
                if (replyAudioPanel != null)
                {
                    replyAudioPanel.Size = replyAudioHolder.Size;
                }
                top += 46;
            }

            int toolsTop = top + 3;
            replyButton.Top = toolsTop;
            transparencyButton.Top = toolsTop;
            replyMicButton.Top = toolsTop;
            replyPauseButton.Top = toolsTop;
            replyLiveMicButton.Top = toolsTop;

            // Os três botões de áudio e o Enviar precisam ficar acima das
            // faixas no z-order.
            replyMicButton.BringToFront();
            replyPauseButton.BringToFront();
            replyLiveMicButton.BringToFront();
            replyButton.BringToFront();
            transparencyButton.BringToFront();

            int required = toolsTop + replyButton.Height + 10;
            int height = Math.Max(basePopupHeight, required);
            if (height != ClientSize.Height)
            {
                ClientSize = new Size(ClientSize.Width, height);
            }
        }

        internal void SetNotificationLocation(Point location)
        {
            StartPosition = FormStartPosition.Manual;
            Location = location;
        }

        public void ApplyTransparency()
        {
            if (!IsDisposed)
            {
                Opacity = NotificationSettings.WindowOpacity;
                if (transparencyButton != null)
                {
                    transparencyButton.Text = NotificationSettings.TransparencyPercent + "%";
                }
            }
        }

        private void TransparencyButtonClick(object sender, EventArgs e)
        {
            UpdateTransparencyMenu();
            Size menuSize = transparencyMenu.GetPreferredSize(
                new Size(transparencyButton.Width, 0));
            transparencyMenu.Show(
                transparencyButton,
                new Point(
                    0,
                    -menuSize.Height));
        }

        private void UpdateTransparencyMenu()
        {
            int selected = NotificationSettings.TransparencyPercent;
            transparencyButton.Text = selected + "%";
            foreach (ToolStripItem item in transparencyMenu.Items)
            {
                ToolStripMenuItem option = item as ToolStripMenuItem;
                if (option == null) continue;
                int percent;
                bool selectedOption = Int32.TryParse(
                    option.Text.TrimEnd('%'),
                    out percent) && percent == selected;
                option.Font = selectedOption ?
                    transparencySelectedFont :
                    transparencyRegularFont;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (transparencyMenu != null) transparencyMenu.Dispose();
                if (transparencyRegularFont != null) transparencyRegularFont.Dispose();
                if (transparencySelectedFont != null) transparencySelectedFont.Dispose();
                if (imageThumbnail != null) imageThumbnail.Dispose();
                if (replyAttachmentThumbnail != null) replyAttachmentThumbnail.Dispose();
                if (audioPanel != null) audioPanel.Dispose();
                if (replyRecordingTimer != null)
                {
                    replyRecordingTimer.Stop();
                    replyRecordingTimer.Dispose();
                }
                if (replyRecorder != null)
                {
                    replyRecorder.Dispose();
                    replyRecorder = null;
                }
            }
            base.Dispose(disposing);
        }

        protected override bool ShowWithoutActivation
        {
            get { return !runningUnderWine; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                // No Wine, WS_EX_NOACTIVATE impede o clique nos campos de
                // resposta do popup; sem ele o popup rouba o foco, mas
                // permanece interativo.
                if (!runningUnderWine)
                {
                    parameters.ExStyle |= WsExNoActivate;
                }
                parameters.ExStyle |= WsExToolWindow;
                return parameters;
            }
        }

        private void OpenMainButtonClick(object sender, EventArgs e)
        {
            Close();
            if (openMainWindow != null) openMainWindow();
        }

        private void ActivateForInteraction(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Activate();
            }
        }

        private void ReplyButtonClick(object sender, EventArgs e)
        {
            // Responder durante a gravação: encerra a captura e anexa o áudio.
            if (replyRecording)
            {
                StopReplyRecording();
            }

            // A transcrição exibida é informativa: só o áudio é enviado.
            string reply = replyBoxIsTranscription ? "" : replyBox.Text.Trim();
            ImagePayload attachment = pendingReplyImage;
            AudioPayload replyAudioPayload = replyAudio;

            if (reply.Length == 0 && attachment == null && replyAudioPayload == null)
            {
                replyBox.Focus();
                return;
            }

            RefreshReplyCapability();
            if (replyAudioPayload != null && !replySupportsAudio)
            {
                MessageBox.Show(
                    this,
                    "O computador " + senderName +
                    " usa uma versão do TailMsg sem suporte a áudio.",
                    "TailMsg",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            if (attachment != null && !replySupportsImages)
            {
                MessageBox.Show(
                    this,
                    "O computador " + senderName +
                    " usa uma versão do TailMsg sem suporte a imagens." +
                    (reply.Length > 0
                        ? " Somente o texto pode ser enviado."
                        : " A imagem não será enviada."),
                    "TailMsg",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            replyButton.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                PeerInfo peer = new PeerInfo();
                peer.Name = senderName;
                peer.Address = remoteAddress;
                peer.Port = NetworkService.TcpPort;
                peer.Capabilities = replyCapabilities;

                // Texto e imagem viajam separados, como no envio principal.
                bool sentText = false;
                bool sentImage = false;
                string failure = "";

                if (reply.Length > 0)
                {
                    MessageSendResult textResult = MessageSender.Send(
                        peer,
                        localComputerName,
                        reply);
                    sentText = textResult.Success;
                    if (!textResult.Success) failure = textResult.ErrorMessage;
                }

                if (failure.Length == 0 && attachment != null)
                {
                    MessageSendResult imageResult = MessageSender.SendImage(
                        peer,
                        localComputerName,
                        attachment);
                    sentImage = imageResult.Success;
                    if (!imageResult.Success) failure = imageResult.ErrorMessage;
                }

                bool sentAudio = false;
                if (failure.Length == 0 && replyAudioPayload != null)
                {
                    MessageSendResult audioResult = MessageSender.SendAudio(
                        peer,
                        localComputerName,
                        replyAudioPayload);
                    sentAudio = audioResult.Success;
                    if (!audioResult.Success) failure = audioResult.ErrorMessage;
                }

                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        replyButton.Enabled = true;
                        if (sentText) replyBox.Clear();
                        if (sentImage) ClearReplyAttachment();
                        if (sentAudio)
                        {
                            ClearReplyAudio();
                            replyBoxIsTranscription = false;
                            replyBox.SetProgrammaticText("");
                        }

                        if (failure.Length == 0)
                        {
                            // Resposta entregue: o popup fecha e o resultado
                            // aparece na janela principal.
                            ReportStatus(
                                sentImage
                                    ? (sentText
                                        ? "Resposta e imagem enviadas a " + senderName + "."
                                        : "Imagem enviada a " + senderName + ".")
                                    : "Resposta enviada a " + senderName + ".",
                                false);
                            Close();
                            return;
                        }

                        ReportStatus(
                            "Falha ao responder " + senderName + ".",
                            true);
                        MessageBox.Show(this, failure, "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    });
                }
                catch (InvalidOperationException) { }
            });
        }

        private void ReportStatus(string text, bool isError)
        {
            Action<string, bool> reporter = StatusReporter;
            if (reporter == null) return;
            try
            {
                reporter(text, isError);
            }
            catch
            {
                // O retorno de status nunca pode impedir o envio.
            }
        }

        private void CopyButtonClick(object sender, EventArgs e)
        {
            if (imageMessage != null)
            {
                // Copia a imagem original (não a miniatura exibida).
                string copyError;
                if (ImageTransfer.TryCopyToClipboard(imageMessage.ImageBytes, out copyError))
                {
                    copyButton.Text = "Copiado!";
                    return;
                }

                MessageBox.Show(
                    this,
                    "Não foi possível copiar a imagem: " + copyError,
                    "TailMsg",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            try
            {
                Clipboard.SetText(contentBox.Text);
                copyButton.Text = "Copiado!";
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    this,
                    "Não foi possível copiar o texto: " + exception.Message,
                    "TailMsg",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
    }

    // Guarda a transcrição por operação: o popup e o histórico de mensagens
    // recebidas mostram o mesmo texto sem transcrever duas vezes.
    internal static class TranscriptionCache
    {
        private const int MaximumEntries = 64;
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, string> Entries =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public static void Remember(string operationId, string text)
        {
            if (String.IsNullOrEmpty(operationId) ||
                String.IsNullOrEmpty(text) ||
                !TailMsgDiagnostics.IsSafeOperationId(operationId))
            {
                return;
            }
            lock (Sync)
            {
                Entries[operationId] = text;
                if (Entries.Count <= MaximumEntries) return;
                List<string> keys = new List<string>(Entries.Keys);
                for (int index = 0; index < keys.Count - MaximumEntries; index++)
                {
                    Entries.Remove(keys[index]);
                }
            }
        }

        public static bool TryGet(string operationId, out string text)
        {
            text = "";
            if (String.IsNullOrEmpty(operationId)) return false;
            lock (Sync)
            {
                return Entries.TryGetValue(operationId, out text);
            }
        }
    }

    internal sealed class PeerInfo
    {
        public string Name;
        public string Address;
        public int Port;
        public bool IsLocal;
        public int Capabilities;

        public bool SupportsImages
        {
            get { return TailMsgProtocol.SupportsImages(Capabilities); }
        }

        public bool SupportsAudio
        {
            get { return TailMsgProtocol.SupportsAudio(Capabilities); }
        }

        public override string ToString()
        {
            return Name + " (" + Address + ")";
        }
    }

    internal sealed class MessageReceivedEventArgs : EventArgs
    {
        public string SenderName;
        public string Message;
        public string RemoteAddress;
        public string OperationId;
        public string Fingerprint;
    }

    internal sealed class ImageReceivedEventArgs : EventArgs
    {
        public string SenderName;
        public string RemoteAddress;
        public string OperationId;
        public string Fingerprint;
        public byte[] ImageBytes;
        public int Width;
        public int Height;
        public string Sha256;
    }

    internal sealed class AudioReceivedEventArgs : EventArgs
    {
        public string SenderName;
        public string RemoteAddress;
        public string OperationId;
        public string Fingerprint;
        public byte[] AudioBytes;
        public int DurationMilliseconds;
        public string Sha256;
    }

    internal sealed class MessageDeletedEventArgs : EventArgs
    {
        public string SenderName;
        public string RemoteAddress;
        public string OperationId;
    }

    internal sealed class MessageSendResult
    {
        public bool Success;
        public string ErrorMessage;
        public string OperationId;
        public string Fingerprint;

        public static MessageSendResult Succeeded(
            string operationId,
            string fingerprint)
        {
            return new MessageSendResult
            {
                Success = true,
                ErrorMessage = "",
                OperationId = operationId,
                Fingerprint = fingerprint
            };
        }

        public static MessageSendResult Failed(
            string error,
            string operationId,
            string fingerprint)
        {
            return new MessageSendResult
            {
                Success = false,
                ErrorMessage = error,
                OperationId = operationId,
                Fingerprint = fingerprint
            };
        }
    }

    internal static class TailMsgProtocol
    {
        public const int MaxGuiMessageCharacters = 100000;
        public const int MaxCommandLineMessageCharacters = 7000;
        public const string DiscoveryRequest = "TAILMSG_DISCOVER";
        public const string DiscoveryResponse = "TAILMSG_HERE";
        public const string Message = "TAILMSG_MESSAGE";
        public const string Acknowledgement = "TAILMSG_ACK";
        public const string Image = "TAILMSG_IMAGE";
        public const string ImageChunk = "TAILMSG_CHUNK";
        public const string ImageEnd = "TAILMSG_IMAGE_END";
        public const string ImageFormatPng = "png";
        public const string Audio = "TAILMSG_AUDIO";
        public const string AudioEnd = "TAILMSG_AUDIO_END";
        // Pedido para o destinatário apagar a mensagem (e fechar a janelinha).
        public const string Delete = "TAILMSG_DELETE";
        public const string AudioFormatWav = "wav";
        public const int CapabilityImage = 1;
        public const int CapabilityAudio = 2;
        // Formato único de captura: 16 kHz, mono, 16 bits (PCM).
        public const int AudioSampleRate = 16000;
        public const int AudioChannels = 1;
        public const int AudioBitsPerSample = 16;
        public const int AudioChunkBytes = ImageChunkBytes;
        public const int MaximumAudioLineBytes = MaximumImageLineBytes;
        // Teto de segurança da gravação contínua (30 minutos ≈ 57 MB).
        public const int MaximumAudioSeconds = 30 * 60;
        public const int ImageChunkBytes = 48 * 1024;
        public const int MaximumImageLineBytes = ImageChunkBytes + (ImageChunkBytes / 2) + 1024;
        public const long LargeImageWarningBytes = 50L * 1024L * 1024L;
        public const string AcknowledgementOk = Acknowledgement + "|1|OK";
        public const string AcknowledgementRejected = Acknowledgement + "|1|REJECT";

        public static string BuildDelete(string operationId)
        {
            return Delete + "|1|" + (operationId ?? "");
        }

        public static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        }

        public static string Decode(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? ""));
        }

        public static string BuildDiscoveryRequest(string name)
        {
            return DiscoveryRequest + "|1|" + Encode(name);
        }

        // Sem o campo de capacidades: usado como fixture de peer antigo.
        public static string BuildDiscoveryResponse(string name, string address, int port)
        {
            return DiscoveryResponse + "|1|" + Encode(name) + "|" + address + "|" + port;
        }

        // O campo extra fica no fim de propósito: peers antigos ignoram.
        public static string BuildDiscoveryResponse(
            string name,
            string address,
            int port,
            int capabilities)
        {
            return BuildDiscoveryResponse(name, address, port) + "|" +
                capabilities.ToString(CultureInfo.InvariantCulture);
        }

        public static bool TryParseDiscoveryResponse(
            string line,
            out string name,
            out string address,
            out int port,
            out int capabilities)
        {
            name = "";
            address = "";
            port = 0;
            capabilities = 0;

            if (String.IsNullOrEmpty(line)) return false;
            string[] pieces = line.Split('|');
            if (pieces.Length < 5) return false;
            if (pieces[0] != DiscoveryResponse || pieces[1] != "1") return false;

            int parsedPort;
            if (!Int32.TryParse(pieces[4], out parsedPort)) return false;
            if (parsedPort <= 0 || parsedPort > 65535) return false;

            try
            {
                name = Decode(pieces[2]);
            }
            catch (FormatException)
            {
                return false;
            }

            address = pieces[3];
            port = parsedPort;

            // Campo opcional: ausente ou inválido significa peer sem capacidade.
            if (pieces.Length >= 6)
            {
                int parsedCapabilities;
                if (Int32.TryParse(pieces[5], out parsedCapabilities) &&
                    parsedCapabilities >= 0)
                {
                    capabilities = parsedCapabilities;
                }
            }

            return true;
        }

        public static bool SupportsImages(int capabilities)
        {
            return (capabilities & CapabilityImage) == CapabilityImage;
        }

        public static bool SupportsAudio(int capabilities)
        {
            return (capabilities & CapabilityAudio) == CapabilityAudio;
        }

        public static string BuildMessage(string senderName, string message)
        {
            return BuildMessage(senderName, message, null);
        }

        public static string BuildMessage(
            string senderName,
            string message,
            string operationId)
        {
            string line = Message + "|1|" + Encode(senderName) + "|" + Encode(message);
            if (TailMsgDiagnostics.IsSafeOperationId(operationId))
            {
                line += "|" + operationId;
            }
            return line;
        }

        public static string ComputeSha256Hex(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(data ?? new byte[0]);
                StringBuilder result = new StringBuilder(digest.Length * 2);
                foreach (byte item in digest)
                {
                    result.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                }
                return result.ToString();
            }
        }

        public static bool IsSafeSha256(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length != 64) return false;
            foreach (char item in value)
            {
                bool hex = (item >= '0' && item <= '9') || (item >= 'a' && item <= 'f');
                if (!hex) return false;
            }
            return true;
        }

        public static int CountImageChunks(long byteCount)
        {
            if (byteCount <= 0) return 0;
            long chunks = (byteCount + ImageChunkBytes - 1) / ImageChunkBytes;
            return chunks > Int32.MaxValue ? Int32.MaxValue : (int)chunks;
        }

        public static string BuildImageHeader(
            string senderName,
            string format,
            int width,
            int height,
            long byteCount,
            string sha256,
            string operationId)
        {
            string line = Image + "|1|" + Encode(senderName) + "|" + format + "|" +
                width.ToString(CultureInfo.InvariantCulture) + "|" +
                height.ToString(CultureInfo.InvariantCulture) + "|" +
                byteCount.ToString(CultureInfo.InvariantCulture) + "|" + sha256;
            if (TailMsgDiagnostics.IsSafeOperationId(operationId))
            {
                line += "|" + operationId;
            }
            return line;
        }

        public static bool TryParseImageHeader(string line, out ImageHeader header)
        {
            header = null;
            if (String.IsNullOrEmpty(line)) return false;
            string[] pieces = line.Split('|');
            if (pieces.Length < 8) return false;
            if (pieces[0] != Image || pieces[1] != "1") return false;
            if (pieces[3] != ImageFormatPng) return false;

            int width;
            int height;
            long byteCount;
            if (!Int32.TryParse(pieces[4], NumberStyles.None, CultureInfo.InvariantCulture, out width)) return false;
            if (!Int32.TryParse(pieces[5], NumberStyles.None, CultureInfo.InvariantCulture, out height)) return false;
            if (!Int64.TryParse(pieces[6], NumberStyles.None, CultureInfo.InvariantCulture, out byteCount)) return false;
            if (width <= 0 || height <= 0 || width > 100000 || height > 100000) return false;
            if (byteCount <= 0 || byteCount > Int32.MaxValue) return false;
            if (!IsSafeSha256(pieces[7])) return false;

            string senderName;
            try
            {
                senderName = Decode(pieces[2]);
            }
            catch (FormatException)
            {
                return false;
            }

            ImageHeader parsed = new ImageHeader();
            parsed.SenderName = senderName;
            parsed.Format = pieces[3];
            parsed.Width = width;
            parsed.Height = height;
            parsed.ByteCount = byteCount;
            parsed.Sha256 = pieces[7];
            parsed.OperationId = null;
            if (pieces.Length >= 9 && TailMsgDiagnostics.IsSafeOperationId(pieces[8]))
            {
                parsed.OperationId = pieces[8];
            }

            header = parsed;
            return true;
        }

        public static string BuildImageChunk(int index, string base64)
        {
            return ImageChunk + "|1|" +
                index.ToString(CultureInfo.InvariantCulture) + "|" + base64;
        }

        public static bool TryParseImageChunk(
            string line,
            out int index,
            out string base64)
        {
            index = 0;
            base64 = "";
            if (String.IsNullOrEmpty(line)) return false;
            string[] pieces = line.Split('|');
            if (pieces.Length != 4) return false;
            if (pieces[0] != ImageChunk || pieces[1] != "1") return false;
            if (!Int32.TryParse(pieces[2], NumberStyles.None, CultureInfo.InvariantCulture, out index)) return false;
            if (index < 0) return false;
            if (pieces[3].Length == 0) return false;
            base64 = pieces[3];
            return true;
        }

        public static string BuildImageEnd(string sha256)
        {
            return ImageEnd + "|1|" + sha256;
        }

        public static bool TryParseImageEnd(string line, out string sha256)
        {
            sha256 = "";
            if (String.IsNullOrEmpty(line)) return false;
            string[] pieces = line.Split('|');
            if (pieces.Length != 3) return false;
            if (pieces[0] != ImageEnd || pieces[1] != "1") return false;
            if (!IsSafeSha256(pieces[2])) return false;
            sha256 = pieces[2];
            return true;
        }

        public static bool TryParseAcknowledgement(string line, out bool accepted)
        {
            accepted = false;
            if (line == AcknowledgementOk)
            {
                accepted = true;
                return true;
            }
            if (line == AcknowledgementRejected)
            {
                accepted = false;
                return true;
            }
            return false;
        }

        public static string BuildAudioHeader(
            string senderName,
            int durationMilliseconds,
            int sampleRate,
            int channels,
            int bitsPerSample,
            long byteCount,
            string sha256,
            string operationId)
        {
            string line = Audio + "|1|" + Encode(senderName) + "|" +
                AudioFormatWav + "|" +
                durationMilliseconds.ToString(CultureInfo.InvariantCulture) + "|" +
                sampleRate.ToString(CultureInfo.InvariantCulture) + "|" +
                channels.ToString(CultureInfo.InvariantCulture) + "|" +
                bitsPerSample.ToString(CultureInfo.InvariantCulture) + "|" +
                byteCount.ToString(CultureInfo.InvariantCulture) + "|" + sha256;
            if (TailMsgDiagnostics.IsSafeOperationId(operationId))
            {
                line += "|" + operationId;
            }
            return line;
        }

        public static bool TryParseAudioHeader(string line, out AudioHeader header)
        {
            header = null;
            if (String.IsNullOrEmpty(line)) return false;
            string[] pieces = line.Split('|');
            if (pieces.Length < 10) return false;
            if (pieces[0] != Audio || pieces[1] != "1") return false;
            if (pieces[3] != AudioFormatWav) return false;

            int durationMilliseconds;
            int sampleRate;
            int channels;
            int bitsPerSample;
            long byteCount;
            if (!Int32.TryParse(pieces[4], NumberStyles.None, CultureInfo.InvariantCulture, out durationMilliseconds)) return false;
            if (!Int32.TryParse(pieces[5], NumberStyles.None, CultureInfo.InvariantCulture, out sampleRate)) return false;
            if (!Int32.TryParse(pieces[6], NumberStyles.None, CultureInfo.InvariantCulture, out channels)) return false;
            if (!Int32.TryParse(pieces[7], NumberStyles.None, CultureInfo.InvariantCulture, out bitsPerSample)) return false;
            if (!Int64.TryParse(pieces[8], NumberStyles.None, CultureInfo.InvariantCulture, out byteCount)) return false;

            if (durationMilliseconds <= 0 ||
                durationMilliseconds > MaximumAudioSeconds * 1000 + 5000) return false;
            // Formato único: qualquer combinação diferente é recusada.
            if (sampleRate != AudioSampleRate) return false;
            if (channels != AudioChannels) return false;
            if (bitsPerSample != AudioBitsPerSample) return false;
            if (byteCount <= 0 || byteCount > Int32.MaxValue) return false;
            if (!IsSafeSha256(pieces[9])) return false;

            string senderName;
            try
            {
                senderName = Decode(pieces[2]);
            }
            catch (FormatException)
            {
                return false;
            }

            AudioHeader parsed = new AudioHeader();
            parsed.SenderName = senderName;
            parsed.Format = pieces[3];
            parsed.DurationMilliseconds = durationMilliseconds;
            parsed.SampleRate = sampleRate;
            parsed.Channels = channels;
            parsed.BitsPerSample = bitsPerSample;
            parsed.ByteCount = byteCount;
            parsed.Sha256 = pieces[9];
            parsed.OperationId = null;
            if (pieces.Length >= 11 && TailMsgDiagnostics.IsSafeOperationId(pieces[10]))
            {
                parsed.OperationId = pieces[10];
            }

            header = parsed;
            return true;
        }

        public static string BuildAudioEnd(string sha256)
        {
            return AudioEnd + "|1|" + sha256;
        }

        public static bool TryParseAudioEnd(string line, out string sha256)
        {
            sha256 = "";
            if (String.IsNullOrEmpty(line)) return false;
            string[] pieces = line.Split('|');
            if (pieces.Length != 3) return false;
            if (pieces[0] != AudioEnd || pieces[1] != "1") return false;
            if (!IsSafeSha256(pieces[2])) return false;
            sha256 = pieces[2];
            return true;
        }

        // Empacota amostras PCM num contêiner WAV (usado pelos testes e por
        // qualquer reempacotamento futuro).
        public static byte[] BuildWavContainer(
            byte[] samples,
            int sampleRate,
            short channels,
            short bitsPerSample)
        {
            int dataLength = samples == null ? 0 : samples.Length;
            int blockAlign = channels * (bitsPerSample / 8);
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(new char[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + dataLength);
                writer.Write(new char[] { 'W', 'A', 'V', 'E' });
                writer.Write(new char[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write(channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * blockAlign);
                writer.Write((short)blockAlign);
                writer.Write(bitsPerSample);
                writer.Write(new char[] { 'd', 'a', 't', 'a' });
                writer.Write(dataLength);
                if (dataLength > 0) writer.Write(samples, 0, dataLength);
                writer.Flush();
                return stream.ToArray();
            }
        }

        // Duração de um WAV PCM 16 kHz mono 16 bits, lida do próprio arquivo.
        public static bool TryReadWavDurationMilliseconds(
            byte[] wavBytes,
            out int durationMilliseconds)
        {
            durationMilliseconds = 0;
            int dataBytes;
            int sampleRate;
            short channels;
            short bits;
            if (!TryReadWavFormat(wavBytes, out dataBytes, out sampleRate, out channels, out bits))
            {
                return false;
            }
            if (sampleRate <= 0 || channels <= 0 || bits <= 0) return false;
            long bytesPerSecond = (long)sampleRate * channels * (bits / 8);
            if (bytesPerSecond <= 0) return false;
            durationMilliseconds = (int)((dataBytes * 1000L) / bytesPerSecond);
            return true;
        }

        // Lê o bloco fmt e o tamanho do bloco data de um WAV PCM.
        public static bool TryReadWavFormat(
            byte[] wavBytes,
            out int dataBytes,
            out int sampleRate,
            out short channels,
            out short bitsPerSample)
        {
            dataBytes = 0;
            sampleRate = 0;
            channels = 0;
            bitsPerSample = 0;
            if (wavBytes == null || wavBytes.Length < 44) return false;
            if (!(wavBytes[0] == 'R' && wavBytes[1] == 'I' && wavBytes[2] == 'F' && wavBytes[3] == 'F'))
                return false;
            if (!(wavBytes[8] == 'W' && wavBytes[9] == 'A' && wavBytes[10] == 'V' && wavBytes[11] == 'E'))
                return false;

            bool foundFormat = false;
            int offset = 12;
            while (offset + 8 <= wavBytes.Length)
            {
                int size = (int)(wavBytes[offset + 4] |
                    (wavBytes[offset + 5] << 8) |
                    (wavBytes[offset + 6] << 16) |
                    (wavBytes[offset + 7] << 24));
                string chunkId = "" + (char)wavBytes[offset] + (char)wavBytes[offset + 1] +
                    (char)wavBytes[offset + 2] + (char)wavBytes[offset + 3];

                if (chunkId == "fmt ")
                {
                    if (size < 16 || offset + 8 + 16 > wavBytes.Length) return false;
                    int format = wavBytes[offset + 8] | (wavBytes[offset + 9] << 8);
                    if (format != 1) return false;   // somente PCM
                    channels = (short)(wavBytes[offset + 10] | (wavBytes[offset + 11] << 8));
                    sampleRate = wavBytes[offset + 12] |
                        (wavBytes[offset + 13] << 8) |
                        (wavBytes[offset + 14] << 16) |
                        (wavBytes[offset + 15] << 24);
                    bitsPerSample = (short)(wavBytes[offset + 22] | (wavBytes[offset + 23] << 8));
                    foundFormat = true;
                }
                else if (chunkId == "data")
                {
                    dataBytes = size;
                    if (offset + 8 + size > wavBytes.Length)
                    {
                        dataBytes = wavBytes.Length - (offset + 8);
                    }
                }

                if (size < 0) return false;
                // Blocos de tamanho ímpar são preenchidos com um byte.
                offset += 8 + size + (size % 2);
            }

            return foundFormat && dataBytes > 0;
        }
    }

    internal sealed class AudioHeader
    {
        public string SenderName;
        public string Format;
        public int DurationMilliseconds;
        public int SampleRate;
        public int Channels;
        public int BitsPerSample;
        public long ByteCount;
        public string Sha256;
        public string OperationId;
    }

    // Áudio pronto para transporte: WAV PCM 16 kHz mono 16 bits.
    internal sealed class AudioPayload
    {
        public byte[] WavBytes;
        public int DurationMilliseconds;

        public long ByteCount
        {
            get { return WavBytes == null ? 0 : WavBytes.Length; }
        }
    }

    internal sealed class ImageHeader
    {
        public string SenderName;
        public string Format;
        public int Width;
        public int Height;
        public long ByteCount;
        public string Sha256;
        public string OperationId;
    }

    // Imagem pronta para transporte: PNG (sem perda) e dimensões conhecidas.
    internal sealed class ImagePayload
    {
        public byte[] PngBytes;
        public int Width;
        public int Height;

        public long ByteCount
        {
            get { return PngBytes == null ? 0 : PngBytes.Length; }
        }
    }

    internal sealed class NetworkService
    {
        public const int TcpPort = 38257;
        public const int DiscoveryPort = 38258;
        public const string DiscoveryMulticast = "239.255.42.99";
        private const int MaximumMessageLineBytes =
            TailMsgProtocol.MaxGuiMessageCharacters * 4 + 8192;
        private readonly string localName;
        private readonly int tcpPort;
        private readonly int discoveryPort;
        private readonly bool allowLoopback;
        private readonly object peersLock = new object();
        internal int Capabilities =
            TailMsgProtocol.CapabilityImage | TailMsgProtocol.CapabilityAudio;
        private readonly Dictionary<string, PeerInfo> peers = new Dictionary<string, PeerInfo>(StringComparer.OrdinalIgnoreCase);
        private TcpListener tcpListener;
        private UdpClient udpClient;
        private volatile bool running;
        private const int NormalStartAttempts = 100;
        private const int UpdateStartAttempts = 240;

        public event EventHandler<MessageReceivedEventArgs> MessageReceived;
        public event EventHandler<ImageReceivedEventArgs> ImageReceived;
        public event EventHandler<AudioReceivedEventArgs> AudioReceived;
        public event EventHandler<MessageDeletedEventArgs> MessageDeleted;

        public NetworkService(string name)
            : this(name, TcpPort, DiscoveryPort, false)
        {
        }

        internal NetworkService(
            string name,
            int tcpPort,
            int discoveryPort,
            bool allowLoopback)
        {
            localName = name;
            this.tcpPort = tcpPort;
            this.discoveryPort = discoveryPort;
            this.allowLoopback = allowLoopback;
        }

        internal int ListeningTcpPort
        {
            get
            {
                if (tcpListener == null || tcpListener.LocalEndpoint == null)
                    return tcpPort;
                return ((IPEndPoint)tcpListener.LocalEndpoint).Port;
            }
        }

        internal int ListeningDiscoveryPort
        {
            get
            {
                if (udpClient == null || udpClient.Client.LocalEndPoint == null)
                    return discoveryPort;
                return ((IPEndPoint)udpClient.Client.LocalEndPoint).Port;
            }
        }

        public void Start()
        {
            StartCore(NormalStartAttempts);
        }

        internal void StartForUpdate()
        {
            StartCore(UpdateStartAttempts);
        }

        private void StartCore(int maximumAttempts)
        {
            if (running)
            {
                return;
            }

            Exception lastError = null;
            // A troca de processo pode deixar um socket antigo em liberação
            // por alguns segundos. A abertura normal conserva a espera
            // histórica; o caminho de atualização usa uma janela maior, mas
            // é executado fora da thread da interface.
            for (int attempt = 0; attempt < maximumAttempts && !running; attempt++)
            {
                try
                {
                    tcpListener = new TcpListener(IPAddress.Any, tcpPort);
                    tcpListener.Start();

                    // Use o construtor original do TailMsg para preservar a
                    // política de bind que já funcionava no Windows e no
                    // Wine durante a troca de processo.
                    udpClient = new UdpClient(discoveryPort);
                    udpClient.EnableBroadcast = true;

                    // Participa do grupo de multicast de descoberta para
                    // responder a requisições que chegam por ele.
                    try
                    {
                        if (!allowLoopback)
                        {
                            IPAddress multicastAddress = IPAddress.Parse(DiscoveryMulticast);
                            foreach (NetworkEndpoint endpoint in NetworkDiscovery.GetEndpoints())
                            {
                                try
                                {
                                    udpClient.Client.SetSocketOption(
                                        SocketOptionLevel.IP,
                                        SocketOptionName.AddMembership,
                                        new MulticastOption(
                                            multicastAddress,
                                            IPAddress.Parse(endpoint.LocalAddress)));
                                }
                                catch { }
                            }
                            try
                            {
                                udpClient.Client.SetSocketOption(
                                    SocketOptionLevel.IP,
                                    SocketOptionName.AddMembership,
                                    new MulticastOption(multicastAddress));
                            }
                            catch { }
                        }
                    }
                    catch { }

                    lastError = null;
                    break;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                    try { if (tcpListener != null) tcpListener.Stop(); } catch { }
                    try { if (udpClient != null) udpClient.Close(); } catch { }
                    tcpListener = null;
                    udpClient = null;
                    Thread.Sleep(250);
                }
            }

            if (tcpListener == null || udpClient == null)
            {
                throw lastError ?? new SocketException(10048);
            }

            running = true;

            Thread tcpThread = new Thread(AcceptLoop);
            tcpThread.IsBackground = true;
            tcpThread.Start();

            Thread udpThread = new Thread(DiscoveryLoop);
            udpThread.IsBackground = true;
            udpThread.Start();

            AddLocalPeers();
        }

        public void Stop()
        {
            running = false;
            try
            {
                if (tcpListener != null) tcpListener.Stop();
            }
            catch { }
            try
            {
                if (udpClient != null) udpClient.Close();
            }
            catch { }
        }

        public List<PeerInfo> Discover(int waitMilliseconds)
        {
            if (!running)
            {
                Start();
            }

            AddLocalPeers();
            SendDiscoveryPackets();
            Thread.Sleep(waitMilliseconds);

            lock (peersLock)
            {
                List<PeerInfo> result = new List<PeerInfo>(peers.Values);
                result.Sort(delegate(PeerInfo left, PeerInfo right)
                {
                    if (left.IsLocal != right.IsLocal) return left.IsLocal ? -1 : 1;
                    return StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
                });
                return result;
            }
        }

        private void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    TcpClient client = tcpListener.AcceptTcpClient();
                    Thread worker = new Thread(HandleClient);
                    worker.IsBackground = true;
                    worker.Start(client);
                }
                catch
                {
                    if (!running) return;
                }
            }
        }

        private void HandleClient(object state)
        {
            TcpClient client = (TcpClient)state;
            string remoteAddress = "desconhecido";
            string operationId = TailMsgDiagnostics.CreateOperationId();
            string fingerprint = "";
            string senderName = "";

            try
            {
                remoteAddress = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                client.ReceiveTimeout = 8000;
                client.SendTimeout = 8000;

                using (NetworkStream stream = client.GetStream())
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    string line = ReadLineLimited(
                        stream,
                        MaximumMessageLineBytes);
                    if (String.IsNullOrEmpty(line)) return;

                    string[] pieces = line.Split('|');
                    if (pieces.Length >= 2 && pieces[1] == "1" &&
                        (pieces[0] == TailMsgProtocol.Image ||
                         pieces[0] == TailMsgProtocol.Audio))
                    {
                        if (pieces[0] == TailMsgProtocol.Image)
                        {
                            HandleImageTransfer(stream, writer, line, remoteAddress);
                        }
                        else
                        {
                            HandleAudioTransfer(stream, writer, line, remoteAddress);
                        }
                        return;
                    }

                    if (pieces.Length >= 3 && pieces[0] == TailMsgProtocol.Delete &&
                        pieces[1] == "1" &&
                        TailMsgDiagnostics.IsSafeOperationId(pieces[2]))
                    {
                        MessageDeletedEventArgs deleted = new MessageDeletedEventArgs();
                        deleted.OperationId = pieces[2];
                        deleted.RemoteAddress = remoteAddress;
                        deleted.SenderName = remoteAddress;
                        try
                        {
                            writer.WriteLine(TailMsgProtocol.AcknowledgementOk);
                            writer.Flush();
                        }
                        catch (IOException)
                        {
                        }
                        if (MessageDeleted != null)
                        {
                            MessageDeleted(this, deleted);
                        }
                        return;
                    }

                    if (pieces.Length >= 4 && pieces[0] == TailMsgProtocol.Message && pieces[1] == "1")
                    {
                        if (pieces.Length >= 5 &&
                            TailMsgDiagnostics.IsSafeOperationId(pieces[4]))
                        {
                            operationId = pieces[4];
                        }
                        senderName = TailMsgProtocol.Decode(pieces[2]);
                        string message = TailMsgProtocol.Decode(pieces[3]);
                        fingerprint = TailMsgDiagnostics.ComputeFingerprint(senderName, message);

                        if (message.Length > TailMsgProtocol.MaxGuiMessageCharacters)
                        {
                            message = message.Substring(
                                0,
                                TailMsgProtocol.MaxGuiMessageCharacters);
                        }
                        MessageReceivedEventArgs eventArgs = new MessageReceivedEventArgs();
                        eventArgs.SenderName = senderName;
                        eventArgs.Message = message;
                        eventArgs.RemoteAddress = remoteAddress;
                        eventArgs.OperationId = operationId;
                        eventArgs.Fingerprint = fingerprint;
                        TailMsgDiagnostics.WriteMessageEvent(
                            operationId,
                            "received",
                            "success",
                            senderName,
                            remoteAddress,
                            fingerprint,
                            0,
                            "");
                        EventHandler<MessageReceivedEventArgs> handler = MessageReceived;
                        if (handler != null) handler(this, eventArgs);

                        writer.WriteLine(TailMsgProtocol.Acknowledgement + "|1|OK");
                        writer.Flush();
                        TailMsgDiagnostics.WriteMessageEvent(
                            operationId,
                            "ack_sent",
                            "success",
                            senderName,
                            remoteAddress,
                            fingerprint,
                            0,
                            "");
                    }
                }
            }
            catch (Exception exception)
            {
                TailMsgDiagnostics.WriteMessageEvent(
                    operationId,
                    "received",
                    "failed",
                    senderName,
                    remoteAddress,
                    fingerprint,
                    0,
                    exception.Message);
                // A conexão interrompida não deve derrubar o serviço de mensagens.
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        // Núcleo compartilhado de recepção binária (imagem e áudio): lê os
        // blocos em ordem, confere o encerramento e valida tamanho e hash do
        // conjunto. A validação específica do conteúdo fica com o chamador.
        private static void TryReceiveBinaryPayload(
            NetworkStream stream,
            long declaredBytes,
            string declaredSha256,
            int maximumLineBytes,
            int chunkBytes,
            out byte[] payload,
            out string failure)
        {
            payload = null;
            failure = "";
            int expectedChunks = (int)((declaredBytes + chunkBytes - 1) / chunkBytes);

            using (MemoryStream buffer = new MemoryStream())
            {
                for (int index = 0; index < expectedChunks && failure.Length == 0; index++)
                {
                    string chunkLine = ReadLineLimited(stream, maximumLineBytes);
                    if (String.IsNullOrEmpty(chunkLine))
                    {
                        failure = "bloco ausente";
                        break;
                    }

                    int chunkIndex;
                    string base64;
                    if (!TailMsgProtocol.TryParseImageChunk(chunkLine, out chunkIndex, out base64))
                    {
                        failure = "bloco invalido";
                        break;
                    }
                    if (chunkIndex != index)
                    {
                        failure = "bloco fora de ordem";
                        break;
                    }

                    try
                    {
                        byte[] block = Convert.FromBase64String(base64);
                        if (block.Length == 0 || block.Length > chunkBytes)
                        {
                            failure = "bloco fora do limite";
                        }
                        else if (buffer.Length + block.Length > declaredBytes)
                        {
                            failure = "bloco excede o tamanho declarado";
                        }
                        else
                        {
                            buffer.Write(block, 0, block.Length);
                        }
                    }
                    catch (FormatException)
                    {
                        failure = "bloco corrompido";
                    }
                }

                if (failure.Length == 0)
                {
                    string endLine = ReadLineLimited(stream, maximumLineBytes);
                    string endSha256;
                    // O encerramento de imagem e o de áudio diferem apenas no
                    // tipo; o hash do conjunto é a validação que importa.
                    if (!TailMsgProtocol.TryParseImageEnd(endLine, out endSha256) &&
                        !TailMsgProtocol.TryParseAudioEnd(endLine, out endSha256))
                    {
                        failure = "encerramento ausente";
                    }
                    else if (endSha256 != declaredSha256)
                    {
                        failure = "hash de encerramento divergente";
                    }
                    else if (buffer.Length != declaredBytes)
                    {
                        failure = "tamanho recebido divergente";
                    }
                    else
                    {
                        byte[] received = buffer.ToArray();
                        if (TailMsgProtocol.ComputeSha256Hex(received) != declaredSha256)
                        {
                            failure = "hash divergente";
                        }
                        else
                        {
                            payload = received;
                        }
                    }
                }
            }
        }

        // Recebe o áudio anunciado pelo cabeçalho já lido.
        private void HandleAudioTransfer(
            NetworkStream stream,
            StreamWriter writer,
            string headerLine,
            string remoteAddress)
        {
            string operationId = TailMsgDiagnostics.CreateOperationId();
            string fingerprint = "";
            string senderName = "";

            AudioHeader header;
            if (!TailMsgProtocol.TryParseAudioHeader(headerLine, out header))
            {
                RejectImage(writer);
                TailMsgDiagnostics.WriteMessageEvent(
                    operationId,
                    "audio_received",
                    "failed",
                    "",
                    remoteAddress,
                    "",
                    0,
                    "cabecalho-invalido");
                return;
            }

            senderName = header.SenderName;
            if (TailMsgDiagnostics.IsSafeOperationId(header.OperationId))
            {
                operationId = header.OperationId;
            }
            fingerprint = TailMsgDiagnostics.ComputeFingerprint(
                senderName,
                "audio:" + header.Sha256);

            TailMsgDiagnostics.WriteMessageEvent(
                operationId,
                "audio_received",
                "pending",
                senderName,
                remoteAddress,
                fingerprint,
                0,
                "bytes=" + header.ByteCount + ";duracao_ms=" +
                    header.DurationMilliseconds);

            writer.WriteLine(TailMsgProtocol.AcknowledgementOk);
            writer.Flush();

            byte[] audioBytes;
            string failure;
            int expectedChunks = TailMsgProtocol.CountImageChunks(header.ByteCount);
            TryReceiveBinaryPayload(
                stream,
                header.ByteCount,
                header.Sha256,
                TailMsgProtocol.MaximumAudioLineBytes,
                TailMsgProtocol.AudioChunkBytes,
                out audioBytes,
                out failure);

            if (failure.Length == 0)
            {
                int dataBytes;
                int sampleRate;
                short channels;
                short bitsPerSample;
                int durationMilliseconds;
                if (!TailMsgProtocol.TryReadWavFormat(
                        audioBytes,
                        out dataBytes,
                        out sampleRate,
                        out channels,
                        out bitsPerSample) ||
                    sampleRate != TailMsgProtocol.AudioSampleRate ||
                    channels != TailMsgProtocol.AudioChannels ||
                    bitsPerSample != TailMsgProtocol.AudioBitsPerSample)
                {
                    failure = "wav fora do formato esperado";
                    audioBytes = null;
                }
                else if (!TailMsgProtocol.TryReadWavDurationMilliseconds(
                    audioBytes,
                    out durationMilliseconds))
                {
                    failure = "wav sem duracao legivel";
                    audioBytes = null;
                }
            }

            if (failure.Length > 0)
            {
                RejectImage(writer);
                TailMsgDiagnostics.WriteMessageEvent(
                    operationId,
                    "audio_received",
                    "failed",
                    senderName,
                    remoteAddress,
                    fingerprint,
                    0,
                    failure);
                return;
            }

            writer.WriteLine(TailMsgProtocol.AcknowledgementOk);
            writer.Flush();
            TailMsgDiagnostics.WriteMessageEvent(
                operationId,
                "audio_received",
                "success",
                senderName,
                remoteAddress,
                fingerprint,
                0,
                "bytes=" + audioBytes.Length + ";blocos=" + expectedChunks);

            AudioReceivedEventArgs eventArgs = new AudioReceivedEventArgs();
            eventArgs.SenderName = senderName;
            eventArgs.RemoteAddress = remoteAddress;
            eventArgs.OperationId = operationId;
            eventArgs.Fingerprint = fingerprint;
            eventArgs.AudioBytes = audioBytes;
            eventArgs.DurationMilliseconds = header.DurationMilliseconds;
            eventArgs.Sha256 = header.Sha256;
            EventHandler<AudioReceivedEventArgs> handler = AudioReceived;
            if (handler != null) handler(this, eventArgs);

            TailMsgDiagnostics.WriteMessageEvent(
                operationId,
                "audio_ack_sent",
                "success",
                senderName,
                remoteAddress,
                fingerprint,
                0,
                "");
        }

        // Recebe a transferência de imagem anunciada pelo cabeçalho já lido.
        // Confirma o cabeçalho antes dos blocos para o remetente poder falhar
        // cedo; só confirma o fim depois de validar tamanho, hash e assinatura.
        private void HandleImageTransfer(
            NetworkStream stream,
            StreamWriter writer,
            string headerLine,
            string remoteAddress)
        {
            string operationId = TailMsgDiagnostics.CreateOperationId();
            string fingerprint = "";
            string senderName = "";

            ImageHeader header;
            if (!TailMsgProtocol.TryParseImageHeader(headerLine, out header))
            {
                RejectImage(writer);
                TailMsgDiagnostics.WriteMessageEvent(
                    operationId,
                    "image_received",
                    "failed",
                    "",
                    remoteAddress,
                    "",
                    0,
                    "cabecalho-invalido");
                return;
            }

            senderName = header.SenderName;
            if (TailMsgDiagnostics.IsSafeOperationId(header.OperationId))
            {
                operationId = header.OperationId;
            }
            fingerprint = TailMsgDiagnostics.ComputeFingerprint(
                senderName,
                "image:" + header.Sha256);

            TailMsgDiagnostics.WriteMessageEvent(
                operationId,
                "image_received",
                "pending",
                senderName,
                remoteAddress,
                fingerprint,
                0,
                "bytes=" + header.ByteCount + ";dimensoes=" +
                    header.Width + "x" + header.Height);

            writer.WriteLine(TailMsgProtocol.AcknowledgementOk);
            writer.Flush();

            // A partir daqui o protocolo é o mesmo para imagem e áudio:
            // blocos, encerramento com hash e confirmações.
            byte[] imageBytes;
            string failure;
            int expectedChunks = TailMsgProtocol.CountImageChunks(header.ByteCount);
            TryReceiveBinaryPayload(
                stream,
                header.ByteCount,
                header.Sha256,
                TailMsgProtocol.MaximumImageLineBytes,
                TailMsgProtocol.ImageChunkBytes,
                out imageBytes,
                out failure);

            if (failure.Length == 0 && !IsPngSignature(imageBytes))
            {
                failure = "assinatura png ausente";
                imageBytes = null;
            }

            if (failure.Length > 0)
            {
                RejectImage(writer);
                TailMsgDiagnostics.WriteMessageEvent(
                    operationId,
                    "image_received",
                    "failed",
                    senderName,
                    remoteAddress,
                    fingerprint,
                    0,
                    failure);
                return;
            }

            writer.WriteLine(TailMsgProtocol.AcknowledgementOk);
            writer.Flush();
            TailMsgDiagnostics.WriteMessageEvent(
                operationId,
                "image_received",
                "success",
                senderName,
                remoteAddress,
                fingerprint,
                0,
                "bytes=" + imageBytes.Length + ";blocos=" + expectedChunks);

            ImageReceivedEventArgs eventArgs = new ImageReceivedEventArgs();
            eventArgs.SenderName = senderName;
            eventArgs.RemoteAddress = remoteAddress;
            eventArgs.OperationId = operationId;
            eventArgs.Fingerprint = fingerprint;
            eventArgs.ImageBytes = imageBytes;
            eventArgs.Width = header.Width;
            eventArgs.Height = header.Height;
            eventArgs.Sha256 = header.Sha256;
            EventHandler<ImageReceivedEventArgs> handler = ImageReceived;
            if (handler != null) handler(this, eventArgs);

            TailMsgDiagnostics.WriteMessageEvent(
                operationId,
                "image_ack_sent",
                "success",
                senderName,
                remoteAddress,
                fingerprint,
                0,
                "");
        }

        private static void RejectImage(StreamWriter writer)
        {
            try
            {
                writer.WriteLine(TailMsgProtocol.AcknowledgementRejected);
                writer.Flush();
            }
            catch
            {
                // A recusa é melhor esforço: o remetente também tem timeout.
            }
        }

        internal static bool IsPngSignature(byte[] data)
        {
            if (data == null || data.Length < 8) return false;
            return data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E &&
                data[3] == 0x47 && data[4] == 0x0D && data[5] == 0x0A &&
                data[6] == 0x1A && data[7] == 0x0A;
        }

        internal static string ReadLineLimited(Stream stream, int maximumBytes)
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                while (true)
                {
                    int value = stream.ReadByte();
                    if (value < 0)
                    {
                        if (buffer.Length == 0) return null;
                        break;
                    }
                    if (value == '\n') break;
                    if (buffer.Length >= maximumBytes)
                    {
                        throw new InvalidDataException(
                            "A mensagem recebida excede o limite permitido.");
                    }
                    buffer.WriteByte((byte)value);
                }

                byte[] bytes = buffer.ToArray();
                int length = bytes.Length;
                if (length > 0 && bytes[length - 1] == '\r')
                    length--;
                return new UTF8Encoding(false, true).GetString(bytes, 0, length);
            }
        }

        private void DiscoveryLoop()
        {
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                try
                {
                    byte[] data = udpClient.Receive(ref remote);
                    string line = Encoding.UTF8.GetString(data);
                    string[] pieces = line.Split('|');

                    if (pieces.Length >= 3 && pieces[0] == TailMsgProtocol.DiscoveryRequest && pieces[1] == "1")
                    {
                        SendDiscoveryResponse(remote);
                    }
                    else if (pieces.Length >= 2 &&
                        pieces[0] == TailMsgProtocol.DiscoveryResponse)
                    {
                        string name;
                        string address;
                        int port;
                        int capabilities;
                        if (TailMsgProtocol.TryParseDiscoveryResponse(
                            line,
                            out name,
                            out address,
                            out port,
                            out capabilities))
                        {
                            AddPeer(name, remote.Address.ToString(), port, false, capabilities);
                        }
                    }
                }
                catch
                {
                    if (!running) return;
                }
            }
        }

        private void SendDiscoveryResponse(IPEndPoint remote)
        {
            string address = allowLoopback && IPAddress.IsLoopback(remote.Address)
                ? "127.0.0.1"
                : NetworkDiscovery.FindBestLocalAddress(remote.Address);
            byte[] response = Encoding.UTF8.GetBytes(
                TailMsgProtocol.BuildDiscoveryResponse(
                    localName,
                    address,
                    ListeningTcpPort,
                    Capabilities));
            try { udpClient.Send(response, response.Length, remote); } catch { }
        }

        private void SendDiscoveryPackets()
        {
            string request = TailMsgProtocol.BuildDiscoveryRequest(localName);
            byte[] data = Encoding.UTF8.GetBytes(request);
            HashSet<string> sent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (NetworkEndpoint endpoint in NetworkDiscovery.GetEndpoints())
            {
                IPAddress address;
                if (!String.IsNullOrEmpty(endpoint.BroadcastAddress) &&
                    IPAddress.TryParse(endpoint.BroadcastAddress, out address))
                {
                    SendPacket(data, address, sent);
                }
            }
            foreach (IPAddress address in TailscaleDiscovery.FindPeerAddresses())
            {
                SendPacket(data, address, sent);
            }

            // Broadcast limitado: alcança a rede local mesmo quando a
            // enumeração de interfaces não funciona (Wine/Mono).
            SendPacket(data, IPAddress.Parse(DiscoveryMulticast), sent);
            SendPacket(data, IPAddress.Parse("255.255.255.255"), sent);
        }

        internal void SendDiscoveryRequestForTest(
            IPAddress address,
            int port,
            string operationId)
        {
            if (!allowLoopback || udpClient == null)
            {
                throw new InvalidOperationException(
                    "A descoberta direta só está disponível no modo de teste.");
            }

            byte[] data = Encoding.UTF8.GetBytes(
                TailMsgProtocol.BuildDiscoveryRequest(localName));
            udpClient.Send(data, data.Length, new IPEndPoint(address, port));
            TailMsgDiagnostics.WriteMessageEvent(
                operationId,
                "discovery_request_sent",
                "success",
                localName,
                address.ToString(),
                "",
                0,
                "port=" + port);
        }

        internal List<PeerInfo> GetPeersSnapshot()
        {
            lock (peersLock)
            {
                return new List<PeerInfo>(peers.Values);
            }
        }

        // Capacidades anunciadas para um endereço. Zero significa
        // "desconhecido ou peer sem suporte a imagens".
        internal int FindPeerCapabilities(string address)
        {
            if (String.IsNullOrEmpty(address)) return 0;
            lock (peersLock)
            {
                int capabilities = 0;
                foreach (PeerInfo peer in peers.Values)
                {
                    if (!String.Equals(
                        peer.Address,
                        address,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (peer.Capabilities > capabilities)
                    {
                        capabilities = peer.Capabilities;
                    }
                }
                return capabilities;
            }
        }

        private void SendPacket(byte[] data, IPAddress address, HashSet<string> sent)
        {
            string key = address.ToString();
            if (!sent.Add(key)) return;
            try
            {
                IPEndPoint target = new IPEndPoint(address, discoveryPort);
                udpClient.Send(data, data.Length, target);
            }
            catch { }
        }

        private void AddLocalPeers()
        {
            foreach (NetworkEndpoint endpoint in NetworkDiscovery.GetEndpoints())
            {
                AddPeer(localName, endpoint.LocalAddress, ListeningTcpPort, true, Capabilities);
            }
        }

        private void AddPeer(
            string name,
            string address,
            int port,
            bool isLocal,
            int capabilities)
        {
            if (String.IsNullOrEmpty(address) || address == "0.0.0.0") return;
            IPAddress parsedAddress;
            if (!IPAddress.TryParse(address, out parsedAddress)) return;
            if (!allowLoopback && !NetworkDiscovery.IsTailMsgAddress(parsedAddress)) return;
            if (allowLoopback && !NetworkDiscovery.IsTailMsgAddress(parsedAddress) &&
                !IPAddress.IsLoopback(parsedAddress)) return;

            // Broadcast e multicast podem voltar para a própria máquina.
            isLocal = isLocal || NetworkDiscovery.IsLocalAddress(address);

            lock (peersLock)
            {
                string key = address + ":" + port;
                PeerInfo peer;
                if (!peers.TryGetValue(key, out peer))
                {
                    peer = new PeerInfo();
                    peers[key] = peer;
                }
                peer.Name = String.IsNullOrEmpty(name) ? address : name;
                peer.Address = address;
                peer.Port = port;
                peer.IsLocal = isLocal;
                // A capacidade pode chegar por mais de uma rota de descoberta;
                // mantém o maior valor observado para não perder suporte.
                if (capabilities > peer.Capabilities)
                {
                    peer.Capabilities = capabilities;
                }
            }
        }
    }

    internal sealed class NetworkEndpoint
    {
        public string LocalAddress;
        public string BroadcastAddress;
        public string Mask;
    }

    internal static class NetworkDiscovery
    {
        public static List<IPAddress> GetDiscoveryTargets()
        {
            List<IPAddress> result = new List<IPAddress>();
            HashSet<string> addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (NetworkEndpoint endpoint in GetEndpoints())
            {
                IPAddress address;
                if (!String.IsNullOrEmpty(endpoint.BroadcastAddress) &&
                    IPAddress.TryParse(endpoint.BroadcastAddress, out address) &&
                    addresses.Add(address.ToString()))
                {
                    result.Add(address);
                }
            }

            foreach (IPAddress address in TailscaleDiscovery.FindPeerAddresses())
            {
                if (addresses.Add(address.ToString()))
                {
                    result.Add(address);
                }
            }

            IPAddress multicast = IPAddress.Parse(NetworkService.DiscoveryMulticast);
            if (addresses.Add(multicast.ToString()))
            {
                result.Add(multicast);
            }

            IPAddress limitedBroadcast = IPAddress.Parse("255.255.255.255");
            if (addresses.Add(limitedBroadcast.ToString()))
            {
                result.Add(limitedBroadcast);
            }

            return result;
        }

        public static bool IsLocalAddress(string address)
        {
            foreach (NetworkEndpoint endpoint in GetEndpoints())
            {
                if (String.Equals(endpoint.LocalAddress, address, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool IsTailMsgAddress(IPAddress address)
        {
            return IsDelegaciaAddress(address) || IsTailscaleAddress(address);
        }

        public static bool IsDelegaciaAddress(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes.Length == 4 && bytes[0] == 10;
        }

        public static bool IsTailscaleAddress(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes.Length == 4 &&
                bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
        }

        public static List<NetworkEndpoint> GetEndpoints()
        {
            List<NetworkEndpoint> result = new List<NetworkEndpoint>();
            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    // De propósito não filtra por OperationalStatus: Wine/Mono
                    // pode reportar estados inesperados; basta ter um endereço
                    // 10.x/100.x associado à interface.
                    if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    IPInterfaceProperties properties = adapter.GetIPProperties();
                    foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                            !IsTailMsgAddress(unicast.Address))
                        {
                            continue;
                        }

                        IPAddress mask = GetMask(unicast);
                        NetworkEndpoint endpoint = new NetworkEndpoint();
                        endpoint.LocalAddress = unicast.Address.ToString();
                        endpoint.BroadcastAddress = GetBroadcast(unicast.Address, mask);
                        endpoint.Mask = mask == null ? null : mask.ToString();
                        result.Add(endpoint);
                    }
                }
            }
            catch
            {
                // O ambiente pode bloquear a enumeração de interfaces; o
                // serviço ainda pode receber mensagens no endereço Any.
            }

            return result;
        }

        public static string GetDiscoverySummary()
        {
            List<NetworkEndpoint> endpoints = GetEndpoints();
            if (endpoints.Count == 0)
            {
                return "Nenhuma interface 10.x/100.x elegível foi encontrada.";
            }

            if (WineEnvironment.IsWine)
            {
                string source = TailscaleDiscovery.LastSuccessSource;
                if (!String.IsNullOrEmpty(source))
                {
                    return "Interfaces: " + endpoints.Count +
                        ". Tailscale/Wine respondeu pela rota configurada.";
                }

                string attempt = TailscaleDiscovery.LastAttemptSummary;
                if (String.IsNullOrEmpty(attempt))
                {
                    attempt = "rota do helper não informada";
                }
                attempt = attempt.Replace("\r", " ").Replace("\n", " ");
                if (attempt.Length > 180)
                {
                    attempt = attempt.Substring(0, 180) + "...";
                }
                return "Interfaces: " + endpoints.Count +
                    ". Tailscale/Wine: " + attempt;
            }

            return "Interfaces elegíveis: " + endpoints.Count +
                ". Nenhum outro TailMsg respondeu.";
        }

        private static IPAddress GetMask(UnicastIPAddressInformation unicast)
        {
            try
            {
                IPAddress mask = unicast.IPv4Mask;
                if (mask != null && !mask.Equals(IPAddress.Any))
                {
                    return mask;
                }
            }
            catch
            {
                // Mono/Wine pode não expor a máscara por esta propriedade.
            }

            // IPv4PrefixLength existe no Mono/.NET 4.5+ mas não no .NET
            // Framework 4.0 usado na compilação; acessamos via reflexão para
            // funcionar em qualquer runtime.
            try
            {
                System.Reflection.PropertyInfo property = unicast.GetType()
                    .GetProperty("IPv4PrefixLength");
                if (property != null)
                {
                    int prefix = Convert.ToInt32(property.GetValue(unicast, null));
                    if (prefix > 0 && prefix <= 32)
                    {
                        uint value = prefix == 32 ?
                            0xFFFFFFFFu :
                            (0xFFFFFFFFu << (32 - prefix));
                        byte[] bytes = new byte[]
                        {
                            (byte)((value >> 24) & 0xFF),
                            (byte)((value >> 16) & 0xFF),
                            (byte)((value >> 8) & 0xFF),
                            (byte)(value & 0xFF)
                        };
                        return new IPAddress(bytes);
                    }
                }
            }
            catch
            {
                // Prefixo indisponível neste ambiente.
            }

            // Último recurso: máscara clássica da faixa, para não perder o
            // broadcast de descoberta quando o ambiente (Wine/Mono antigo)
            // não expõe máscara nem prefixo.
            byte[] addressBytes = unicast.Address.GetAddressBytes();
            if (addressBytes.Length == 4 && addressBytes[0] == 10)
            {
                return IPAddress.Parse("255.0.0.0");
            }
            if (addressBytes.Length == 4 && addressBytes[0] == 100 &&
                addressBytes[1] >= 64 && addressBytes[1] <= 127)
            {
                return IPAddress.Parse("255.192.0.0");
            }
            return null;
        }

        public static string FindBestLocalAddress(IPAddress remote)
        {
            foreach (NetworkEndpoint endpoint in GetEndpoints())
            {
                if (SameNetwork(remote.ToString(), endpoint.LocalAddress, endpoint.Mask))
                {
                    return endpoint.LocalAddress;
                }
            }

            List<NetworkEndpoint> endpoints = GetEndpoints();
            return endpoints.Count == 0 ? "0.0.0.0" : endpoints[0].LocalAddress;
        }

        private static bool SameNetwork(string remote, string local, string mask)
        {
            if (String.IsNullOrEmpty(mask)) return false;
            byte[] remoteBytes = IPAddress.Parse(remote).GetAddressBytes();
            byte[] localBytes = IPAddress.Parse(local).GetAddressBytes();
            byte[] maskBytes = IPAddress.Parse(mask).GetAddressBytes();
            for (int index = 0; index < 4; index++)
            {
                if ((remoteBytes[index] & maskBytes[index]) != (localBytes[index] & maskBytes[index]))
                {
                    return false;
                }
            }
            return true;
        }

        private static string GetBroadcast(IPAddress address, IPAddress mask)
        {
            if (mask == null) return null;
            byte[] ip = address.GetAddressBytes();
            byte[] subnet = mask.GetAddressBytes();
            if (ip.Length != 4 || subnet.Length != 4) return null;

            // Verifica se mascara/equal ao IP -> prefixo /32 (sem bits de host).
            // Broadcast normal fica nulo; usa-se broadcast subnet dedicado.
            bool isHostOnly = true;
            for (int index = 0; index < 4; index++)
            {
                if (subnet[index] != 0xFF)
                {
                    isHostOnly = false;
                    break;
                }
            }
            if (!isHostOnly)
            {
                byte[] broadcast = new byte[4];
                bool hasHostBits = false;
                for (int index = 0; index < 4; index++)
                {
                    broadcast[index] = (byte)(ip[index] | (byte)~subnet[index]);
                    if (broadcast[index] != ip[index]) hasHostBits = true;
                }
                return hasHostBits ? new IPAddress(broadcast).ToString() : null;
            }

            // Mascara /32: interface point-to-point (ex.: Tailscale).
            // Usa broadcast subnet da faixa 100.x/10 (.5) ou 10.x/8.
            // Alcaca todos os peers naquela sub-rede mesmo sem rotas L2 completas.
            if (ip[0] == 100 && ip[1] >= 64 && ip[1] <= 127)
            {
                // Rede 100.64.0.0/10 -> broadcast 100.127.255.255
                return "100.127.255.255";
            }
            if (ip[0] == 10)
            {
                // Rede 10.0.0.0/8 -> broadcast 10.255.255.255
                return "10.255.255.255";
            }
            return null;
        }
    }

    internal static class TailscaleDiscovery
    {
        private static readonly Regex AddressPattern = new Regex(
            @"\b100\.(?:6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.\d{1,3}\.\d{1,3}\b",
            RegexOptions.Compiled);

        private static readonly object wineCacheLock = new object();
        private static List<IPAddress> wineCachedAddresses = new List<IPAddress>();
        private static DateTime wineCacheExpiresUtc = DateTime.MinValue;
        private static volatile string lastAttemptSummary = "";
        private static int wineCommandSequence;

        private static volatile string lastSuccessSource = "";

        public static string LastSuccessSource
        {
            get { return lastSuccessSource; }
        }

        public static string LastAttemptSummary
        {
            get { return lastAttemptSummary; }
        }

        private static int RemainingMilliseconds(
            DateTime deadlineUtc,
            int maximumMilliseconds)
        {
            double remaining = (deadlineUtc - DateTime.UtcNow).TotalMilliseconds;
            if (remaining < 100) return 0;
            return Math.Min(maximumMilliseconds, (int)remaining);
        }

        public static List<IPAddress> FindPeerAddresses()
        {
            List<IPAddress> addresses = new List<IPAddress>();

            if (WineEnvironment.IsWine)
            {
                List<IPAddress> wineAddresses = FindWinePeerAddresses();
                if (wineAddresses.Count > 0)
                    return wineAddresses;
            }

            // --- Estrategia Windows nativo: buscar tailscale.exe nos locais padrao ---
            List<string> windowsCommands = new List<string>();
            windowsCommands.Add("tailscale.exe");
            windowsCommands.Add("tailscale");

            string programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            string programFiles64 = Environment.GetEnvironmentVariable("ProgramW6432");
            if (!String.IsNullOrEmpty(programFiles))
            {
                windowsCommands.Add(Path.Combine(programFiles, "Tailscale\\tailscale.exe"));
            }
            if (!String.IsNullOrEmpty(programFiles64) && programFiles64 != programFiles)
            {
                windowsCommands.Add(Path.Combine(programFiles64, "Tailscale\\tailscale.exe"));
            }

            DateTime nativeDeadline = DateTime.UtcNow.AddSeconds(5);
            foreach (string cmd in windowsCommands)
            {
                int timeoutMilliseconds = RemainingMilliseconds(nativeDeadline, 3000);
                if (timeoutMilliseconds <= 0) break;
                try
                {
                    ProcessStartInfo info = new ProcessStartInfo();
                    info.FileName = cmd;
                    info.Arguments = "status --json";
                    info.UseShellExecute = false;
                    info.CreateNoWindow = true;
                    info.RedirectStandardOutput = true;
                    info.RedirectStandardError = true;

                    using (Process process = Process.Start(info))
                    {
                        StringBuilder output = new StringBuilder();
                        StringBuilder errors = new StringBuilder();
                        process.OutputDataReceived += delegate(
                            object sender,
                            DataReceivedEventArgs eventArgs)
                        {
                            if (eventArgs.Data != null)
                                output.AppendLine(eventArgs.Data);
                        };
                        process.ErrorDataReceived += delegate(
                            object sender,
                            DataReceivedEventArgs eventArgs)
                        {
                            if (eventArgs.Data != null)
                                errors.AppendLine(eventArgs.Data);
                        };
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                        if (!process.WaitForExit(timeoutMilliseconds))
                        {
                            try { process.Kill(); } catch { }
                        }
                        process.WaitForExit(1000);

                        string text = output.ToString() + errors.ToString();
                        foreach (Match match in AddressPattern.Matches(text))
                        {
                            IPAddress address;
                            if (IPAddress.TryParse(match.Value, out address) && !Contains(addresses, address))
                            {
                                addresses.Add(address);
                            }
                        }
                    }

                    if (addresses.Count > 0)
                    {
                        lastSuccessSource = cmd;
                        return addresses;
                    }
                }
                catch { }
            }

            return addresses;
        }

        private static List<IPAddress> FindWinePeerAddresses()
        {
            lock (wineCacheLock)
            {
                if (DateTime.UtcNow < wineCacheExpiresUtc)
                    return new List<IPAddress>(wineCachedAddresses);

                List<IPAddress> addresses = new List<IPAddress>();
                StringBuilder attempts = new StringBuilder();
                lastSuccessSource = "";

                string configuredBinary = Environment.GetEnvironmentVariable("TAILMSG_TAILSCALE_BIN");
                List<string> binaries = new List<string>();
                if (!String.IsNullOrEmpty(configuredBinary))
                    binaries.Add(configuredBinary);
                binaries.Add("/usr/bin/tailscale");
                binaries.Add("/usr/local/bin/tailscale");
                binaries.Add("/snap/bin/tailscale");

                string configuredBash = Environment.GetEnvironmentVariable("TAILMSG_WINE_BASH");
                List<string> shells = new List<string>();
                if (!String.IsNullOrEmpty(configuredBash))
                    shells.Add(configuredBash);
                shells.Add("/usr/bin/bash");
                shells.Add("/bin/bash");

                foreach (string shell in UniqueStrings(shells))
                {
                    foreach (string binary in UniqueStrings(binaries))
                    {
                        string output;
                        string command = "exec " + ShellQuote(binary) + " status --json";
                        if (TryRunWineUnixShell(
                            shell,
                            command,
                            out output))
                        {
                            int found = AddAddressesFromOutput(output, addresses);
                            if (found > 0)
                            {
                                lastSuccessSource = "Wine start /unix: " + binary;
                                lastAttemptSummary = lastSuccessSource;
                                wineCachedAddresses = new List<IPAddress>(addresses);
                                wineCacheExpiresUtc = DateTime.UtcNow.AddSeconds(4);
                                return new List<IPAddress>(addresses);
                            }
                            AppendAttempt(attempts, shell + " + " + binary +
                                " respondeu sem IP Tailscale");
                        }
                        else
                        {
                            AppendAttempt(attempts, shell + " + " + binary + " falhou");
                        }
                    }

                    // Permite que o PATH do host seja usado quando o Tailscale
                    // estiver instalado fora dos caminhos usuais.
                    string pathOutput;
                    if (TryRunWineUnixShell(
                        shell,
                        "command -v tailscale >/dev/null 2>&1 && exec tailscale status --json",
                        out pathOutput))
                    {
                        int found = AddAddressesFromOutput(pathOutput, addresses);
                        if (found > 0)
                        {
                            lastSuccessSource = "Wine start /unix: PATH do host";
                            lastAttemptSummary = lastSuccessSource;
                            wineCachedAddresses = new List<IPAddress>(addresses);
                            wineCacheExpiresUtc = DateTime.UtcNow.AddSeconds(4);
                            return new List<IPAddress>(addresses);
                        }
                        AppendAttempt(attempts, shell + " + PATH respondeu sem IP Tailscale");
                    }
                    else
                    {
                        AppendAttempt(attempts, shell + " + PATH falhou");
                    }
                }

                // Fallback opcional: algumas instalações expõem a LocalAPI TCP
                // apenas para clientes locais. A URL pode ser substituída por
                // TAILMSG_TAILSCALE_API; nenhum comportamento Windows passa por
                // este bloco.
                string apiSource;
                if (TryReadWineLocalApi(addresses, out apiSource))
                {
                    lastSuccessSource = apiSource;
                    lastAttemptSummary = apiSource;
                    wineCachedAddresses = new List<IPAddress>(addresses);
                    wineCacheExpiresUtc = DateTime.UtcNow.AddSeconds(4);
                    return new List<IPAddress>(addresses);
                }

                lastAttemptSummary = attempts.Length == 0
                    ? "Wine: nenhuma estratégia executada"
                    : "Wine: " + attempts.ToString();
                wineCachedAddresses = new List<IPAddress>();
                wineCacheExpiresUtc = DateTime.UtcNow.AddSeconds(4);
                return new List<IPAddress>();
            }
        }

        private static bool TryRunWineUnixShell(
            string unixShell,
            string shellCommand,
            out string output)
        {
            output = "";
            string fileName = "tailmsg-tailscale-" +
                Process.GetCurrentProcess().Id + "-" +
                Interlocked.Increment(ref wineCommandSequence) + ".out";
            string unixOutputPath = "/tmp/" + fileName;
            string wineOutputPath = @"Z:\tmp\" + fileName;
            string marker = "TAILMSG_DONE_" + fileName;
            string redirectedCommand = shellCommand +
                " > " + ShellQuote(unixOutputPath) +
                " 2>&1; printf '\\n" + marker + "\\n' >> " +
                ShellQuote(unixOutputPath);

            try
            {
                DeleteIfExists(wineOutputPath);
                DeleteIfExists(unixOutputPath);

                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = GetWineStartPath();
                info.Arguments = "/wait /unix " + QuoteCommandLine(unixShell) +
                    " -c " + QuoteCommandLine(redirectedCommand);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;

                using (Process process = Process.Start(info))
                {
                    DateTime deadline = DateTime.UtcNow.AddMilliseconds(5000);
                    string content = "";
                    bool completed = false;
                    while (DateTime.UtcNow < deadline)
                    {
                        content = ReadWineOutput(wineOutputPath, unixOutputPath);
                        int markerIndex = content.IndexOf(marker, StringComparison.Ordinal);
                        if (markerIndex >= 0)
                        {
                            output = content.Substring(0, markerIndex);
                            completed = true;
                            break;
                        }

                        if (process.HasExited)
                        {
                            // start.exe pode retornar antes da gravação final
                            // do arquivo; dê uma pequena janela ao shell host.
                            Thread.Sleep(100);
                            content = ReadWineOutput(wineOutputPath, unixOutputPath);
                            markerIndex = content.IndexOf(marker, StringComparison.Ordinal);
                            if (markerIndex >= 0)
                            {
                                output = content.Substring(0, markerIndex);
                                completed = true;
                            }
                            break;
                        }

                        Thread.Sleep(50);
                    }

                    if (!process.HasExited)
                    {
                        try { process.Kill(); } catch { }
                    }

                    if (!completed)
                    {
                        output = ReadWineOutput(wineOutputPath, unixOutputPath);
                    }
                }

                return output.Length > 0;
            }
            catch (Exception exception)
            {
                lastAttemptSummary = "Wine: " + exception.Message;
                return false;
            }
            finally
            {
                DeleteIfExists(wineOutputPath);
                DeleteIfExists(unixOutputPath);
            }
        }

        private static bool TryReadWineLocalApi(
            List<IPAddress> addresses,
            out string source)
        {
            source = "";
            string configuredUrl = Environment.GetEnvironmentVariable("TAILMSG_TAILSCALE_API");
            List<string> urls = new List<string>();
            if (!String.IsNullOrEmpty(configuredUrl))
            {
                if (configuredUrl.EndsWith("/status", StringComparison.OrdinalIgnoreCase))
                {
                    urls.Add(configuredUrl);
                }
                else
                {
                    urls.Add(configuredUrl.TrimEnd('/') + "/localapi/v0/status");
                    urls.Add(configuredUrl.TrimEnd('/') + "/v2/status");
                }
            }
            else
            {
                urls.Add("http://127.0.0.1:45909/localapi/v0/status");
                urls.Add("http://127.0.0.1:45909/v2/status");
            }

            string token = Environment.GetEnvironmentVariable("TAILMSG_TAILSCALE_API_TOKEN");
            string authorization = Environment.GetEnvironmentVariable("TAILMSG_TAILSCALE_API_AUTH");
            if (String.IsNullOrEmpty(authorization) && !String.IsNullOrEmpty(token))
            {
                authorization = "Basic " + Convert.ToBase64String(
                    Encoding.ASCII.GetBytes(":" + token));
            }

            foreach (string url in UniqueStrings(urls))
            {
                try
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                    request.Method = "GET";
                    request.Proxy = null;
                    request.AllowAutoRedirect = false;
                    request.Timeout = 900;
                    request.ReadWriteTimeout = 900;
                    if (!String.IsNullOrEmpty(authorization))
                        request.Headers["Authorization"] = authorization;

                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        int found = AddAddressesFromOutput(reader.ReadToEnd(), addresses);
                        if (found > 0)
                        {
                            source = "Wine LocalAPI";
                            return true;
                        }
                    }
                }
                catch
                {
                    // A LocalAPI protegida ou inexistente não deve impedir
                    // as tentativas pelo CLI do host.
                }
            }

            return false;
        }

        private static int AddAddressesFromOutput(string output, List<IPAddress> addresses)
        {
            int found = 0;
            foreach (Match match in AddressPattern.Matches(output ?? ""))
            {
                IPAddress address;
                if (IPAddress.TryParse(match.Value, out address) && !Contains(addresses, address))
                {
                    addresses.Add(address);
                    found++;
                }
            }
            return found;
        }

        internal static List<IPAddress> ParsePeerAddressesForTest(string output)
        {
            List<IPAddress> addresses = new List<IPAddress>();
            AddAddressesFromOutput(output, addresses);
            return addresses;
        }

        private static string GetWineStartPath()
        {
            string systemDirectory = Environment.SystemDirectory;
            if (!String.IsNullOrEmpty(systemDirectory))
            {
                string startPath = Path.Combine(systemDirectory, "start.exe");
                if (File.Exists(startPath))
                    return startPath;
            }
            return "start.exe";
        }

        private static string ShellQuote(string value)
        {
            return "'" + (value ?? "").Replace("'", "'\\''") + "'";
        }

        private static string QuoteCommandLine(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private static string ReadIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                    return File.ReadAllText(path, Encoding.UTF8);
            }
            catch { }
            return "";
        }

        private static string ReadWineOutput(string winePath, string unixPath)
        {
            string content = ReadIfExists(winePath);
            return content.Length > 0 ? content : ReadIfExists(unixPath);
        }

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        private static List<string> UniqueStrings(List<string> values)
        {
            List<string> result = new List<string>();
            foreach (string value in values)
            {
                bool alreadyPresent = false;
                foreach (string existing in result)
                {
                    if (String.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyPresent = true;
                        break;
                    }
                }
                if (!String.IsNullOrEmpty(value) && !alreadyPresent)
                {
                    result.Add(value);
                }
            }
            return result;
        }

        private static void AppendAttempt(StringBuilder attempts, string value)
        {
            if (attempts.Length > 0)
                attempts.Append("; ");
            attempts.Append(value);
        }

        private static bool Contains(List<IPAddress> addresses, IPAddress candidate)
        {
            foreach (IPAddress address in addresses)
            {
                if (address.Equals(candidate)) return true;
            }
            return false;
        }
    }

    // Descreve um envio binário (imagem ou áudio) para o transporte comum.
    internal sealed class BinaryTransfer
    {
        public string Kind;
        public int Capability;
        public string ArtifactLabel;
        public string SourceTag;
        public string HeaderStage;
        public string ChunksStage;
        public string AckStage;
        public string Fingerprint;
        public string Detail;
        public string HeaderLine;
        public string EndLine;
        public byte[] Payload;
    }

    internal static class MessageSender
    {
        // Avisa o destinatário para apagar a mensagem daquela operação.
        public static bool SendDelete(PeerInfo peer, string operationId, out string error)
        {
            error = "";
            if (peer == null || String.IsNullOrEmpty(peer.Address) || peer.Port <= 0)
            {
                error = "O destinatário é inválido.";
                return false;
            }
            if (String.IsNullOrEmpty(operationId))
            {
                error = "A mensagem não tem identificação para apagar no destinatário.";
                return false;
            }

            TcpClient client = new TcpClient();
            try
            {
                IAsyncResult connection = client.BeginConnect(peer.Address, peer.Port, null, null);
                if (!connection.AsyncWaitHandle.WaitOne(2000))
                {
                    error = "O computador não respondeu na porta do TailMsg (38257).";
                    return false;
                }
                client.EndConnect(connection);
                client.SendTimeout = 6000;
                client.ReceiveTimeout = 6000;

                using (NetworkStream stream = client.GetStream())
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.WriteLine(TailMsgProtocol.BuildDelete(operationId));
                    writer.Flush();
                    string response = NetworkService.ReadLineLimited(stream, 1024);
                    if (response != TailMsgProtocol.Acknowledgement + "|1|OK")
                    {
                        error = "O destinatário não confirmou a deleção.";
                        return false;
                    }
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
            finally
            {
                client.Close();
            }
        }

        public static MessageSendResult Send(PeerInfo peer, string senderName, string message)
        {
            return Send(peer, senderName, message, null);
        }

        public static MessageSendResult Send(
            PeerInfo peer,
            string senderName,
            string message,
            string operationId)
        {
            if (String.IsNullOrEmpty(operationId))
            {
                operationId = TailMsgDiagnostics.CreateOperationId();
            }
            string fingerprint = TailMsgDiagnostics.ComputeFingerprint(senderName, message);
            DateTime started = DateTime.UtcNow;
            TailMsgDiagnostics.WriteMessageEvent(
                operationId,
                "attempt_started",
                "pending",
                peer == null ? "" : peer.Name,
                peer == null ? "" : peer.Address,
                fingerprint,
                0,
                "source=network");

            if (peer == null || String.IsNullOrEmpty(peer.Address) || peer.Port <= 0)
            {
                string invalid = "O destinatário da mensagem é inválido.";
                TailMsgDiagnostics.WriteMessageEvent(
                    operationId,
                    "completed",
                    "failed",
                    "",
                    "",
                    fingerprint,
                    ElapsedMilliseconds(started),
                    invalid);
                return MessageSendResult.Failed(invalid, operationId, fingerprint);
            }

            TcpClient client = new TcpClient();
            try
            {
                TailMsgDiagnostics.WriteMessageEvent(
                    operationId,
                    "peer_selected",
                    "success",
                    peer.Name,
                    peer.Address,
                    fingerprint,
                    ElapsedMilliseconds(started),
                    "port=" + peer.Port);
                IAsyncResult connection = client.BeginConnect(peer.Address, peer.Port, null, null);
                if (!connection.AsyncWaitHandle.WaitOne(2000))
                {
                    string timeout = "O computador não respondeu na porta do TailMsg (38257). Verifique o firewall do Windows.";
                    TailMsgDiagnostics.WriteMessageEvent(
                        operationId,
                        "tcp_connect",
                        "timeout",
                        peer.Name,
                        peer.Address,
                        fingerprint,
                        ElapsedMilliseconds(started),
                        timeout);
                    return Failed(
                        timeout,
                        operationId,
                        fingerprint,
                        peer,
                        started);
                }

                client.EndConnect(connection);
                TailMsgDiagnostics.WriteMessageEvent(
                    operationId,
                    "tcp_connect",
                    "success",
                    peer.Name,
                    peer.Address,
                    fingerprint,
                    ElapsedMilliseconds(started),
                    "");
                client.SendTimeout = 6000;
                client.ReceiveTimeout = 6000;

                using (NetworkStream stream = client.GetStream())
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.WriteLine(
                        TailMsgProtocol.BuildMessage(
                            senderName,
                            message,
                            operationId));
                    writer.Flush();
                    TailMsgDiagnostics.WriteMessageEvent(
                        operationId,
                        "payload_sent",
                        "success",
                        peer.Name,
                        peer.Address,
                        fingerprint,
                        ElapsedMilliseconds(started),
                        "bytes=redacted");
                    string response = NetworkService.ReadLineLimited(stream, 1024);
                    if (response == TailMsgProtocol.Acknowledgement + "|1|OK")
                    {
                        TailMsgDiagnostics.WriteMessageEvent(
                            operationId,
                            "ack_received",
                            "success",
                            peer.Name,
                            peer.Address,
                            fingerprint,
                            ElapsedMilliseconds(started),
                            "");
                        return Succeeded(
                            operationId,
                            fingerprint,
                            peer,
                            started);
                    }
                }

                return Failed(
                    "O computador recebeu uma resposta inválida.",
                    operationId,
                    fingerprint,
                    peer,
                    started);
            }
            catch (Exception exception)
            {
                return Failed(
                    "Não foi possível entregar a mensagem: " + exception.Message,
                    operationId,
                    fingerprint,
                    peer,
                    started);
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        // Envia a imagem em blocos. O destinatário confirma o cabeçalho antes
        // dos blocos (recusa antecipada) e confirma o fim somente depois de
        // validar tamanho, hash e assinatura PNG.
        public static MessageSendResult SendImage(
            PeerInfo peer,
            string senderName,
            ImagePayload image)
        {
            return SendImage(peer, senderName, image, null);
        }

        public static MessageSendResult SendImage(
            PeerInfo peer,
            string senderName,
            ImagePayload image,
            string operationId)
        {
            if (image == null || image.PngBytes == null || image.PngBytes.Length == 0)
            {
                return SendEmptyArtifact(
                    peer,
                    operationId,
                    "A imagem a enviar está vazia.");
            }

            string sha256 = TailMsgProtocol.ComputeSha256Hex(image.PngBytes);
            BinaryTransfer transfer = new BinaryTransfer();
            transfer.Kind = "image";
            transfer.Capability = TailMsgProtocol.CapabilityImage;
            transfer.ArtifactLabel = "imagem";
            transfer.SourceTag = "network-image";
            transfer.HeaderStage = "image_header_sent";
            transfer.ChunksStage = "image_chunks_sent";
            transfer.AckStage = "image_ack_received";
            transfer.Fingerprint = TailMsgDiagnostics.ComputeFingerprint(
                senderName,
                "image:" + sha256);
            transfer.Detail = "bytes=" + image.PngBytes.Length +
                ";dimensoes=" + image.Width + "x" + image.Height;
            transfer.HeaderLine = TailMsgProtocol.BuildImageHeader(
                senderName,
                TailMsgProtocol.ImageFormatPng,
                image.Width,
                image.Height,
                image.PngBytes.Length,
                sha256,
                operationId);
            transfer.EndLine = TailMsgProtocol.BuildImageEnd(sha256);
            transfer.Payload = image.PngBytes;
            return SendBinary(peer, senderName, transfer, operationId);
        }

        // Áudio WAV (16 kHz mono 16 bits): mesmo transporte da imagem.
        public static MessageSendResult SendAudio(
            PeerInfo peer,
            string senderName,
            AudioPayload audio)
        {
            return SendAudio(peer, senderName, audio, null);
        }

        public static MessageSendResult SendAudio(
            PeerInfo peer,
            string senderName,
            AudioPayload audio,
            string operationId)
        {
            if (audio == null || audio.WavBytes == null || audio.WavBytes.Length == 0)
            {
                return SendEmptyArtifact(
                    peer,
                    operationId,
                    "O áudio a enviar está vazio.");
            }

            int durationMilliseconds = audio.DurationMilliseconds;
            if (durationMilliseconds <= 0)
            {
                TailMsgProtocol.TryReadWavDurationMilliseconds(
                    audio.WavBytes,
                    out durationMilliseconds);
            }
            if (durationMilliseconds <= 0)
            {
                return SendEmptyArtifact(
                    peer,
                    operationId,
                    "O áudio a enviar não tem duração legível.");
            }

            string sha256 = TailMsgProtocol.ComputeSha256Hex(audio.WavBytes);
            BinaryTransfer transfer = new BinaryTransfer();
            transfer.Kind = "audio";
            transfer.Capability = TailMsgProtocol.CapabilityAudio;
            transfer.ArtifactLabel = "áudio";
            transfer.SourceTag = "network-audio";
            transfer.HeaderStage = "audio_header_sent";
            transfer.ChunksStage = "audio_chunks_sent";
            transfer.AckStage = "audio_ack_received";
            transfer.Fingerprint = TailMsgDiagnostics.ComputeFingerprint(
                senderName,
                "audio:" + sha256);
            transfer.Detail = "bytes=" + audio.WavBytes.Length +
                ";duracao_ms=" + durationMilliseconds;
            transfer.HeaderLine = TailMsgProtocol.BuildAudioHeader(
                senderName,
                durationMilliseconds,
                TailMsgProtocol.AudioSampleRate,
                TailMsgProtocol.AudioChannels,
                TailMsgProtocol.AudioBitsPerSample,
                audio.WavBytes.Length,
                sha256,
                operationId);
            transfer.EndLine = TailMsgProtocol.BuildAudioEnd(sha256);
            transfer.Payload = audio.WavBytes;
            return SendBinary(peer, senderName, transfer, operationId);
        }

        private static MessageSendResult SendEmptyArtifact(
            PeerInfo peer,
            string operationId,
            string message)
        {
            if (String.IsNullOrEmpty(operationId))
            {
                operationId = TailMsgDiagnostics.CreateOperationId();
            }
            TailMsgDiagnostics.WriteMessageEvent(
                operationId,
                "completed",
                "failed",
                peer == null ? "" : peer.Name,
                peer == null ? "" : peer.Address,
                "",
                0,
                message);
            return MessageSendResult.Failed(message, operationId, "");
        }

        // Transporte binário compartilhado (imagem e áudio): anuncia o
        // cabeçalho, espera a confirmação do destinatário, envia os blocos e só
        // considera entregue depois do encerramento confirmado.
        private static MessageSendResult SendBinary(
            PeerInfo peer,
            string senderName,
            BinaryTransfer transfer,
            string operationId)
        {
            if (String.IsNullOrEmpty(operationId))
            {
                operationId = TailMsgDiagnostics.CreateOperationId();
            }

            string fingerprint = transfer.Fingerprint;
            DateTime started = DateTime.UtcNow;
            TailMsgDiagnostics.WriteMessageEvent(
                operationId,
                "attempt_started",
                "pending",
                peer == null ? "" : peer.Name,
                peer == null ? "" : peer.Address,
                fingerprint,
                0,
                "source=" + transfer.SourceTag + ";" + transfer.Detail);

            if (peer == null || String.IsNullOrEmpty(peer.Address) || peer.Port <= 0)
            {
                string invalid = "O destinatário do " + transfer.ArtifactLabel +
                    " é inválido.";
                TailMsgDiagnostics.WriteMessageEvent(
                    operationId,
                    "completed",
                    "failed",
                    "",
                    "",
                    fingerprint,
                    ElapsedMilliseconds(started),
                    invalid);
                return MessageSendResult.Failed(invalid, operationId, fingerprint);
            }

            if ((peer.Capabilities & transfer.Capability) != transfer.Capability)
            {
                string unsupported =
                    "O computador " + peer.Name +
                    " usa uma versão do TailMsg sem suporte a " +
                    transfer.ArtifactLabel + ".";
                TailMsgDiagnostics.WriteMessageEvent(
                    operationId,
                    "completed",
                    "failed",
                    peer.Name,
                    peer.Address,
                    fingerprint,
                    ElapsedMilliseconds(started),
                    "peer-sem-suporte");
                return MessageSendResult.Failed(unsupported, operationId, fingerprint);
            }

            TcpClient client = new TcpClient();
            try
            {
                IAsyncResult connection = client.BeginConnect(peer.Address, peer.Port, null, null);
                if (!connection.AsyncWaitHandle.WaitOne(2000))
                {
                    string timeout = "O computador não respondeu na porta do TailMsg (38257). Verifique o firewall do Windows.";
                    TailMsgDiagnostics.WriteMessageEvent(
                        operationId,
                        "tcp_connect",
                        "timeout",
                        peer.Name,
                        peer.Address,
                        fingerprint,
                        ElapsedMilliseconds(started),
                        timeout);
                    return Failed(timeout, operationId, fingerprint, peer, started);
                }

                client.EndConnect(connection);
                TailMsgDiagnostics.WriteMessageEvent(
                    operationId,
                    "tcp_connect",
                    "success",
                    peer.Name,
                    peer.Address,
                    fingerprint,
                    ElapsedMilliseconds(started),
                    "");
                client.SendTimeout = 15000;
                client.ReceiveTimeout = 15000;

                using (NetworkStream stream = client.GetStream())
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.WriteLine(transfer.HeaderLine);
                    writer.Flush();
                    TailMsgDiagnostics.WriteMessageEvent(
                        operationId,
                        transfer.HeaderStage,
                        "success",
                        peer.Name,
                        peer.Address,
                        fingerprint,
                        ElapsedMilliseconds(started),
                        "bytes=" + transfer.Payload.Length);

                    string response = NetworkService.ReadLineLimited(stream, 64);
                    bool accepted;
                    if (!TailMsgProtocol.TryParseAcknowledgement(response, out accepted) ||
                        !accepted)
                    {
                        string rejected =
                            "O computador " + peer.Name +
                            " não aceitou o " + transfer.ArtifactLabel + ".";
                        TailMsgDiagnostics.WriteMessageEvent(
                            operationId,
                            transfer.HeaderStage,
                            "rejected",
                            peer.Name,
                            peer.Address,
                            fingerprint,
                            ElapsedMilliseconds(started),
                            "recusa-do-destinatario");
                        return Failed(rejected, operationId, fingerprint, peer, started);
                    }

                    int chunks = TailMsgProtocol.CountImageChunks(transfer.Payload.Length);
                    for (int index = 0; index < chunks; index++)
                    {
                        int offset = index * TailMsgProtocol.ImageChunkBytes;
                        int count = Math.Min(
                            TailMsgProtocol.ImageChunkBytes,
                            transfer.Payload.Length - offset);
                        writer.WriteLine(
                            TailMsgProtocol.BuildImageChunk(
                                index,
                                Convert.ToBase64String(transfer.Payload, offset, count)));
                    }
                    writer.Flush();
                    TailMsgDiagnostics.WriteMessageEvent(
                        operationId,
                        transfer.ChunksStage,
                        "success",
                        peer.Name,
                        peer.Address,
                        fingerprint,
                        ElapsedMilliseconds(started),
                        "blocos=" + chunks);

                    writer.WriteLine(transfer.EndLine);
                    writer.Flush();

                    response = NetworkService.ReadLineLimited(stream, 64);
                    if (!TailMsgProtocol.TryParseAcknowledgement(response, out accepted) ||
                        !accepted)
                    {
                        string unconfirmed =
                            "O " + transfer.ArtifactLabel +
                            " não foi confirmado pelo computador " +
                            peer.Name + ".";
                        TailMsgDiagnostics.WriteMessageEvent(
                            operationId,
                            transfer.AckStage,
                            "failed",
                            peer.Name,
                            peer.Address,
                            fingerprint,
                            ElapsedMilliseconds(started),
                            "confirmacao-invalida");
                        return Failed(unconfirmed, operationId, fingerprint, peer, started);
                    }

                    TailMsgDiagnostics.WriteMessageEvent(
                        operationId,
                        transfer.AckStage,
                        "success",
                        peer.Name,
                        peer.Address,
                        fingerprint,
                        ElapsedMilliseconds(started),
                        "");
                    return Succeeded(operationId, fingerprint, peer, started);
                }
            }
            catch (Exception exception)
            {
                return Failed(
                    "Não foi possível enviar o " + transfer.ArtifactLabel + ": " +
                    exception.Message,
                    operationId,
                    fingerprint,
                    peer,
                    started);
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        private static MessageSendResult Succeeded(
            string operationId,
            string fingerprint,
            PeerInfo peer,
            DateTime started)
        {
            TailMsgDiagnostics.WriteMessageEvent(
                operationId,
                "completed",
                "success",
                peer.Name,
                peer.Address,
                fingerprint,
                ElapsedMilliseconds(started),
                "");
            return MessageSendResult.Succeeded(operationId, fingerprint);
        }

        private static MessageSendResult Failed(
            string error,
            string operationId,
            string fingerprint,
            PeerInfo peer,
            DateTime started)
        {
            TailMsgDiagnostics.WriteMessageEvent(
                operationId,
                "completed",
                "failed",
                peer == null ? "" : peer.Name,
                peer == null ? "" : peer.Address,
                fingerprint,
                ElapsedMilliseconds(started),
                error);
            return MessageSendResult.Failed(error, operationId, fingerprint);
        }

        private static long ElapsedMilliseconds(DateTime started)
        {
            return (long)(DateTime.UtcNow - started).TotalMilliseconds;
        }
    }
}
