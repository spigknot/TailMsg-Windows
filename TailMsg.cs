using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Reflection;
using Microsoft.Win32;

namespace TailMsg
{
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

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length >= 2 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                string[] messageParts = new string[args.Length - 1];
                Array.Copy(args, 1, messageParts, 0, messageParts.Length);
                CommandLineMode.Send(args[0], String.Join(" ", messageParts));
                return;
            }

            bool startHidden = args.Length == 1 && args[0] == "--background";
            bool createdNew;
            using (Mutex instanceMutex = new Mutex(true, @"Local\TailMsg-8E47A034", out createdNew))
            {
                if (!createdNew)
                {
                    if (!startHidden)
                    {
                        SingleInstanceChannel.SendShowCommand();
                    }
                    return;
                }

                StartupRegistration.EnsureRegistered();
                Application.Run(new MainForm(startHidden));
            }
        }

        private static void RunSelfTest()
        {
            bool valid10 = NetworkDiscovery.IsTailMsgAddress(IPAddress.Parse("10.12.3.4"));
            bool validTailscale = NetworkDiscovery.IsTailMsgAddress(IPAddress.Parse("100.100.10.20"));
            bool invalidTailscale = NetworkDiscovery.IsTailMsgAddress(IPAddress.Parse("100.63.10.20"));
            string encoded = TailMsgProtocol.Encode("Olá | TailMsg");
            string decoded = TailMsgProtocol.Decode(encoded);
            string largeMessage = new string(
                'X',
                TailMsgProtocol.MaxGuiMessageCharacters);
            bool largeMessageValid =
                TailMsgProtocol.Decode(TailMsgProtocol.Encode(largeMessage)) ==
                largeMessage;

            if (!valid10 || !validTailscale || invalidTailscale ||
                decoded != "Olá | TailMsg" || !largeMessageValid)
            {
                Console.Error.WriteLine("Falha no teste do protocolo de rede.");
                Environment.ExitCode = 1;
                return;
            }

            Console.WriteLine("OK - protocolo e filtros de endereço funcionando.");
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
        public static void Send(string computerName, string message)
        {
            computerName = (computerName ?? "").Trim();
            message = (message ?? "").Trim();

            if (computerName.Length == 0 || message.Length == 0)
            {
                MessageBox.Show(
                    "Uso:\r\n\r\ntailmsg NOME_DO_PC \"Mensagem\"",
                    "TailMsg - linha de comando",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            if (message.Length > TailMsgProtocol.MaxCommandLineMessageCharacters)
            {
                MessageBox.Show(
                    "A mensagem excede o limite de " +
                    TailMsgProtocol.MaxCommandLineMessageCharacters +
                    " caracteres da linha de comando.",
                    "TailMsg - erro",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            PeerInfo peer;
            string discoveryError;
            if (!CommandLineDiscovery.TryFindComputer(computerName, 4000, out peer, out discoveryError))
            {
                MessageBox.Show(
                    discoveryError,
                    "TailMsg - computador não encontrado",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            MessageSendResult result = MessageSender.Send(peer, Environment.MachineName, message);
            if (result.Success)
            {
                MessageBox.Show(
                    "Mensagem entregue a " + peer.Name + ".\r\n\r\nEndereço usado: " + peer.Address,
                    "TailMsg - mensagem enviada",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(
                    "Falha ao enviar para " + peer.Name + " (" + peer.Address + ").\r\n\r\n" +
                    result.ErrorMessage,
                    "TailMsg - erro no envio",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }

    internal static class CommandLineDiscovery
    {
        public static bool TryFindComputer(
            string computerName,
            int waitMilliseconds,
            out PeerInfo selectedPeer,
            out string error)
        {
            selectedPeer = null;
            error = "";
            List<PeerInfo> matches = new List<PeerInfo>();

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
                return false;
            }

            if (matches.Count == 0)
            {
                error = "Nenhum TailMsg ativo com o nome \"" + computerName + "\" foi encontrado.\r\n\r\n" +
                    "Verifique se o computador está ligado, se o TailMsg está em segundo plano e se o firewall permite UDP 38258.";
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
            return true;
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

    internal sealed class MainForm : Form
    {
        private const int SwRestore = 9;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr windowHandle);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr windowHandle, int command);

        private readonly FlowLayoutPanel computerList;
        private readonly TextBox messageBox;
        private readonly TextBox inboxBox;
        private readonly Button sendButton;
        private readonly Button refreshButton;
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
        private AboutForm aboutForm;
        private ReceivedMessageForm activeNotification;
        private List<PeerInfo> latestPeers = new List<PeerInfo>();
        private bool isRefreshing;
        private bool isSending;
        private bool updateInProgress;
        private bool allowExit;
        private UpdateManifest availableUpdate;

        public MainForm(bool startInBackground)
        {
            startHidden = startInBackground;
            localComputerName = Environment.MachineName;
            networkService = new NetworkService(localComputerName);
            networkService.MessageReceived += NetworkServiceMessageReceived;

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
            footer.Controls.Add(sendButton);

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
            layout.RowCount = 8;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 38F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 37F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            content.Controls.Add(layout);

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

            refreshButton = new Button();
            refreshButton.Text = "Atualizar";
            refreshButton.Size = new Size(142, 30);
            refreshButton.Location = new Point(0, 101);
            refreshButton.FlatStyle = FlatStyle.Flat;
            refreshButton.FlatAppearance.BorderColor = Color.FromArgb(209, 213, 219);
            refreshButton.BackColor = Color.White;
            refreshButton.Cursor = Cursors.Hand;
            refreshButton.Click += delegate { RefreshComputers(); };
            interfacePanel.Controls.Add(refreshButton);

            Panel listBorder = new Panel();
            listBorder.Dock = DockStyle.Fill;
            listBorder.Padding = new Padding(1);
            listBorder.BackColor = Color.FromArgb(209, 213, 219);
            computerArea.Controls.Add(listBorder, 0, 1);

            computerList = new FlowLayoutPanel();
            computerList.Dock = DockStyle.Fill;
            computerList.FlowDirection = FlowDirection.TopDown;
            computerList.WrapContents = false;
            computerList.AutoScroll = true;
            computerList.Padding = new Padding(10, 8, 10, 8);
            computerList.BackColor = Color.White;
            computerList.Resize += ResizeComputerOptions;
            listBorder.Controls.Add(computerList);

            Label inboxLabel = new Label();
            inboxLabel.Dock = DockStyle.Fill;
            inboxLabel.Text = "Mensagens recebidas";
            inboxLabel.Font = new Font("Segoe UI Semibold", 10F);
            inboxLabel.TextAlign = ContentAlignment.BottomLeft;
            layout.Controls.Add(inboxLabel, 0, 3);

            inboxBox = new TextBox();
            inboxBox.Dock = DockStyle.Fill;
            inboxBox.Multiline = true;
            inboxBox.ReadOnly = true;
            inboxBox.ScrollBars = ScrollBars.Vertical;
            inboxBox.BackColor = Color.White;
            inboxBox.BorderStyle = BorderStyle.FixedSingle;
            inboxBox.Font = new Font("Segoe UI", 9.5F);
            layout.Controls.Add(inboxBox, 0, 4);

            Label messageLabel = new Label();
            messageLabel.Dock = DockStyle.Fill;
            messageLabel.Text = "Mensagem";
            messageLabel.Font = new Font("Segoe UI Semibold", 10F);
            messageLabel.TextAlign = ContentAlignment.BottomLeft;
            layout.Controls.Add(messageLabel, 0, 5);

            messageBox = new TextBox();
            messageBox.Dock = DockStyle.Fill;
            messageBox.Multiline = true;
            messageBox.ScrollBars = ScrollBars.Vertical;
            messageBox.Font = new Font("Segoe UI", 11F);
            messageBox.BorderStyle = BorderStyle.FixedSingle;
            messageBox.MaxLength = TailMsgProtocol.MaxGuiMessageCharacters;
            messageBox.KeyDown += MessageBoxKeyDown;
            layout.Controls.Add(messageBox, 0, 6);

            Label senderLabel = new Label();
            senderLabel.Dock = DockStyle.Fill;
            senderLabel.ForeColor = Color.FromArgb(107, 114, 128);
            senderLabel.Text = "Será enviada como: " + localComputerName + ": sua mensagem";
            senderLabel.TextAlign = ContentAlignment.BottomLeft;
            layout.Controls.Add(senderLabel, 0, 7);

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
                try
                {
                    networkService.Start();
                    statusLabel.Text = "Serviço ativo. Procurando TailMsg na rede...";
                }
                catch (Exception exception)
                {
                    statusLabel.ForeColor = Color.FromArgb(185, 28, 28);
                    statusLabel.Text = "Não foi possível iniciar o serviço.";
                    MessageBox.Show(this, exception.Message, "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                RefreshComputers();
                discoveryTimer.Start();
                CheckForUpdates(false);

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
            networkService.Stop();
            base.OnFormClosed(e);
        }

        private void ShowFromTray()
        {
            if (InvokeRequired)
            {
                BeginInvoke((MethodInvoker)delegate { ShowFromTray(); });
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
            int width = Math.Max(100, computerList.ClientSize.Width - 30);
            foreach (Control control in computerList.Controls)
            {
                control.Width = width;
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
                    // Falhas de internet ou do Drive não interrompem o uso do
                    // mensageiro. Uma nova consulta ocorrerá na próxima abertura.
                },
                delegate(bool found)
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
                                MessageBox.Show(
                                    this,
                                    "Nenhuma atualização disponível. O TailMsg já está na versão mais recente.",
                                    "TailMsg - atualizações",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Information);
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
                    if (IsDisposed) return;
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
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
            statusLabel.Text = "Procurando TailMsg nas redes 10.x.x.x e 100.x.x.x...";

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    List<PeerInfo> computers = networkService.Discover(2200);
                    BeginInvoke((MethodInvoker)delegate
                    {
                        latestPeers = computers;
                        List<PeerInfo> visibleComputers = GetFilteredPeers();
                        PopulateComputers(visibleComputers);
                        int remoteCount = CountRemotePeers(visibleComputers);
                        statusLabel.Text = remoteCount == 0
                            ? "Nenhum outro TailMsg respondeu."
                            : "Pronto para enviar.";
                    });
                }
                catch (Exception exception)
                {
                    BeginInvoke((MethodInvoker)delegate
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
                    BeginInvoke((MethodInvoker)delegate
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
                    : "Pronto para enviar.";
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

        private void PopulateComputers(List<PeerInfo> computers)
        {
            string selectedAddress = GetSelectedAddress();
            computerList.SuspendLayout();
            computerList.Controls.Clear();

            foreach (PeerInfo computer in computers)
            {
                RadioButton option = new RadioButton();
                option.AutoSize = false;
                option.Height = 20;
                option.Width = Math.Max(100, computerList.ClientSize.Width - 30);
                option.Margin = new Padding(0);
                option.Padding = new Padding(5, 0, 0, 0);
                option.Text = computer.Name + "  (" + computer.Address + ")" +
                    (computer.IsLocal ? "  [você]" : "");
                option.Tag = computer;
                option.Checked = computer.Address == selectedAddress;
                option.CheckedChanged += DestinationSelectionChanged;
                option.Cursor = Cursors.Hand;
                computerList.Controls.Add(option);
            }

            computerList.ResumeLayout();
            countLabel.Text = "(" + computers.Count + ")";
            UpdateActionStates();
        }

        private void DestinationSelectionChanged(object sender, EventArgs e)
        {
            UpdateActionStates();
        }

        private void UpdateActionStates()
        {
            sendButton.Enabled = !isSending && GetSelectedComputer() != null;
            refreshButton.Enabled = !isSending && !isRefreshing;
        }

        private string GetSelectedAddress()
        {
            foreach (Control control in computerList.Controls)
            {
                RadioButton option = control as RadioButton;
                if (option != null && option.Checked)
                {
                    PeerInfo peer = option.Tag as PeerInfo;
                    return peer == null ? null : peer.Address;
                }
            }

            return null;
        }

        private PeerInfo GetSelectedComputer()
        {
            foreach (Control control in computerList.Controls)
            {
                RadioButton option = control as RadioButton;
                if (option != null && option.Checked)
                {
                    return option.Tag as PeerInfo;
                }
            }

            return null;
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

        private void SendMessage()
        {
            if (isSending) return;

            PeerInfo computer = GetSelectedComputer();
            string message = messageBox.Text.Trim();

            if (computer == null)
            {
                MessageBox.Show(this, "Escolha um computador TailMsg como destino.", "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (String.IsNullOrEmpty(message))
            {
                MessageBox.Show(this, "Digite a mensagem que deseja enviar.", "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Information);
                messageBox.Focus();
                return;
            }

            isSending = true;
            UpdateActionStates();
            statusLabel.ForeColor = Color.FromArgb(75, 85, 99);
            statusLabel.Text = "Enviando para " + computer.Name + " (" + computer.Address + ")...";

            ThreadPool.QueueUserWorkItem(delegate
            {
                MessageSendResult result = MessageSender.Send(computer, localComputerName, message);
                BeginInvoke((MethodInvoker)delegate
                {
                    isSending = false;
                    UpdateActionStates();

                    if (result.Success)
                    {
                        statusLabel.ForeColor = Color.FromArgb(21, 128, 61);
                        statusLabel.Text = "Mensagem entregue a " + computer.Name + ".";
                        messageBox.Clear();
                        messageBox.Focus();
                    }
                    else
                    {
                        statusLabel.ForeColor = Color.FromArgb(185, 28, 28);
                        statusLabel.Text = "Falha ao enviar para " + computer.Name + ".";
                        MessageBox.Show(this, result.ErrorMessage, "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                });
            });
        }

        private void NetworkServiceMessageReceived(object sender, MessageReceivedEventArgs e)
        {
            if (IsDisposed)
            {
                return;
            }

            BeginInvoke((MethodInvoker)delegate
            {
                string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " +
                    e.SenderName + " (" + e.RemoteAddress + "): " + e.Message;
                inboxBox.AppendText(line + Environment.NewLine);
                if (activeNotification != null && !activeNotification.IsDisposed)
                {
                    activeNotification.Close();
                }
                ReceivedMessageForm notification = new ReceivedMessageForm(
                    e,
                    localComputerName,
                    delegate { ShowFromTray(); });
                activeNotification = notification;
                notification.FormClosed += delegate
                {
                    if (Object.ReferenceEquals(activeNotification, notification))
                    {
                        activeNotification = null;
                    }
                };
                notification.Show();
                statusLabel.ForeColor = Color.FromArgb(21, 128, 61);
                statusLabel.Text = "Nova mensagem recebida de " + e.SenderName + ".";
            });
        }

    }

    internal sealed class ReceivedMessageForm : Form
    {
        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;
        private readonly MessageReceivedEventArgs message;
        private readonly string localComputerName;
        private readonly Action openMainWindow;
        private readonly TextBox contentBox;
        private readonly TextBox replyBox;
        private readonly Button copyButton;
        private readonly Button replyButton;
        private readonly Button transparencyButton;
        private readonly ContextMenuStrip transparencyMenu;
        private readonly Font transparencyRegularFont;
        private readonly Font transparencySelectedFont;

        public ReceivedMessageForm(
            MessageReceivedEventArgs message,
            string localComputerName,
            Action openMainWindow)
        {
            this.message = message;
            this.localComputerName = localComputerName;
            this.openMainWindow = openMainWindow;

            Text = "Mensagem recebida - TailMsg";
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(420, 248);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ShowIcon = false;
            TopMost = true;
            Opacity = NotificationSettings.WindowOpacity;
            Font = new Font("Segoe UI", 9F);

            Panel body = new Panel();
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
            sender.Text = "De: " + message.SenderName + "  (" + message.RemoteAddress + ")";
            sender.TextAlign = ContentAlignment.MiddleLeft;
            sender.ForeColor = Color.FromArgb(209, 213, 219);
            sender.Location = new Point(10, 43);
            sender.Size = new Size(body.ClientSize.Width - 20, 22);
            sender.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            body.Controls.Add(sender);

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

            Label replyLabel = new Label();
            replyLabel.AutoSize = true;
            replyLabel.Text = "Responder:";
            replyLabel.ForeColor = Color.FromArgb(209, 213, 219);
            replyLabel.Location = new Point(5, 145);
            body.Controls.Add(replyLabel);

            replyBox = new TextBox();
            replyBox.Multiline = true;
            replyBox.ScrollBars = ScrollBars.Vertical;
            replyBox.Font = new Font("Segoe UI", 10F);
            replyBox.BackColor = Color.White;
            replyBox.Location = new Point(5, 165);
            replyBox.Size = new Size(body.ClientSize.Width - 10, 54);
            replyBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            replyBox.MouseDown += ActivateForInteraction;
            body.Controls.Add(replyBox);

            replyButton = new Button();
            replyButton.Text = "Enviar";
            replyButton.Size = new Size(64, 22);
            replyButton.Location = new Point(body.ClientSize.Width - 69, 221);
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
            transparencyButton.Location = new Point(5, 221);
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
            body.Controls.Add(copyButton);

            Shown += delegate { PositionNearTray(); };
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
            }
            base.Dispose(disposing);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= WsExNoActivate | WsExToolWindow;
                return parameters;
            }
        }

        private void PositionNearTray()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Left = area.Right - Width - 12;
            Top = area.Bottom - Height - 12;
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
            string reply = replyBox.Text.Trim();
            if (reply.Length == 0)
            {
                replyBox.Focus();
                return;
            }

            replyButton.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                PeerInfo peer = new PeerInfo();
                peer.Name = message.SenderName;
                peer.Address = message.RemoteAddress;
                peer.Port = NetworkService.TcpPort;
                MessageSendResult result = MessageSender.Send(peer, localComputerName, reply);
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        replyButton.Enabled = true;
                        if (result.Success)
                        {
                            replyBox.Clear();
                            replyButton.Text = "Enviado";
                        }
                        else
                        {
                            MessageBox.Show(this, result.ErrorMessage, "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    });
                }
                catch (InvalidOperationException) { }
            });
        }

        private void CopyButtonClick(object sender, EventArgs e)
        {
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

    internal sealed class PeerInfo
    {
        public string Name;
        public string Address;
        public int Port;
        public bool IsLocal;

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
    }

    internal sealed class MessageSendResult
    {
        public bool Success;
        public string ErrorMessage;

        public static MessageSendResult Succeeded()
        {
            return new MessageSendResult { Success = true, ErrorMessage = "" };
        }

        public static MessageSendResult Failed(string error)
        {
            return new MessageSendResult { Success = false, ErrorMessage = error };
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

        public static string BuildDiscoveryResponse(string name, string address, int port)
        {
            return DiscoveryResponse + "|1|" + Encode(name) + "|" + address + "|" + port;
        }

        public static string BuildMessage(string senderName, string message)
        {
            return Message + "|1|" + Encode(senderName) + "|" + Encode(message);
        }
    }

    internal sealed class NetworkService
    {
        public const int TcpPort = 38257;
        public const int DiscoveryPort = 38258;
        public const string DiscoveryMulticast = "239.255.42.99";
        private readonly string localName;
        private readonly object peersLock = new object();
        private readonly Dictionary<string, PeerInfo> peers = new Dictionary<string, PeerInfo>(StringComparer.OrdinalIgnoreCase);
        private TcpListener tcpListener;
        private UdpClient udpClient;
        private volatile bool running;

        public event EventHandler<MessageReceivedEventArgs> MessageReceived;

        public NetworkService(string name)
        {
            localName = name;
        }

        public void Start()
        {
            if (running)
            {
                return;
            }

            Exception lastError = null;
            for (int attempt = 0; attempt < 40 && !running; attempt++)
            {
                try
                {
                    tcpListener = new TcpListener(IPAddress.Any, TcpPort);
                    tcpListener.Start();

                    udpClient = new UdpClient(DiscoveryPort);
                    udpClient.EnableBroadcast = true;
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

            try
            {
                remoteAddress = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                client.ReceiveTimeout = 8000;
                client.SendTimeout = 8000;

                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    string line = reader.ReadLine();
                    if (String.IsNullOrEmpty(line)) return;

                    string[] pieces = line.Split('|');
                    if (pieces.Length >= 4 && pieces[0] == TailMsgProtocol.Message && pieces[1] == "1")
                    {
                        string senderName = TailMsgProtocol.Decode(pieces[2]);
                        string message = TailMsgProtocol.Decode(pieces[3]);

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
                        EventHandler<MessageReceivedEventArgs> handler = MessageReceived;
                        if (handler != null) handler(this, eventArgs);

                        writer.WriteLine(TailMsgProtocol.Acknowledgement + "|1|OK");
                        writer.Flush();
                    }
                }
            }
            catch
            {
                // A conexão interrompida não deve derrubar o serviço de mensagens.
            }
            finally
            {
                try { client.Close(); } catch { }
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
                    else if (pieces.Length >= 5 && pieces[0] == TailMsgProtocol.DiscoveryResponse && pieces[1] == "1")
                    {
                        string name = TailMsgProtocol.Decode(pieces[2]);
                        int port;
                        if (Int32.TryParse(pieces[4], out port) && port > 0 && port < 65536)
                        {
                            AddPeer(name, remote.Address.ToString(), port, false);
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
            string address = NetworkDiscovery.FindBestLocalAddress(remote.Address);
            byte[] response = Encoding.UTF8.GetBytes(TailMsgProtocol.BuildDiscoveryResponse(localName, address, TcpPort));
            try { udpClient.Send(response, response.Length, remote); } catch { }
        }

        private void SendDiscoveryPackets()
        {
            string request = TailMsgProtocol.BuildDiscoveryRequest(localName);
            byte[] data = Encoding.UTF8.GetBytes(request);
            HashSet<string> sent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (NetworkEndpoint endpoint in NetworkDiscovery.GetEndpoints())
            {
                if (!String.IsNullOrEmpty(endpoint.BroadcastAddress))
                {
                    SendPacket(data, IPAddress.Parse(endpoint.BroadcastAddress), sent);
                }
            }

            foreach (IPAddress address in TailscaleDiscovery.FindPeerAddresses())
            {
                SendPacket(data, address, sent);
            }

            // Alguns ambientes Tailscale permitem multicast na interface; é
            // barato tentar e ajuda quando o executável tailscale.exe não está no PATH.
            SendPacket(data, IPAddress.Parse(DiscoveryMulticast), sent);
        }

        private void SendPacket(byte[] data, IPAddress address, HashSet<string> sent)
        {
            string key = address.ToString();
            if (!sent.Add(key)) return;
            try
            {
                IPEndPoint target = new IPEndPoint(address, DiscoveryPort);
                udpClient.Send(data, data.Length, target);
            }
            catch { }
        }

        private void AddLocalPeers()
        {
            foreach (NetworkEndpoint endpoint in NetworkDiscovery.GetEndpoints())
            {
                AddPeer(localName, endpoint.LocalAddress, TcpPort, true);
            }
        }

        private void AddPeer(string name, string address, int port, bool isLocal)
        {
            if (String.IsNullOrEmpty(address) || address == "0.0.0.0") return;
            if (!NetworkDiscovery.IsTailMsgAddress(IPAddress.Parse(address))) return;

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
                    if (adapter.OperationalStatus != OperationalStatus.Up ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
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

                        NetworkEndpoint endpoint = new NetworkEndpoint();
                        endpoint.LocalAddress = unicast.Address.ToString();
                        endpoint.BroadcastAddress = GetBroadcast(unicast.Address, unicast.IPv4Mask);
                        endpoint.Mask = unicast.IPv4Mask == null ? null : unicast.IPv4Mask.ToString();
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
            byte[] broadcast = new byte[4];
            bool hasHostBits = false;
            for (int index = 0; index < 4; index++)
            {
                broadcast[index] = (byte)(ip[index] | (byte)~subnet[index]);
                if (broadcast[index] != ip[index]) hasHostBits = true;
            }
            return hasHostBits ? new IPAddress(broadcast).ToString() : null;
        }
    }

    internal static class TailscaleDiscovery
    {
        private static readonly Regex AddressPattern = new Regex(
            @"\b100\.(?:6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.\d{1,3}\.\d{1,3}\b",
            RegexOptions.Compiled);

        public static List<IPAddress> FindPeerAddresses()
        {
            List<IPAddress> addresses = new List<IPAddress>();
            List<string> commands = new List<string>();
            commands.Add("tailscale.exe");
            commands.Add("tailscale");

            string programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            string programFiles64 = Environment.GetEnvironmentVariable("ProgramW6432");
            if (!String.IsNullOrEmpty(programFiles))
            {
                commands.Add(Path.Combine(programFiles, "Tailscale\\tailscale.exe"));
            }
            if (!String.IsNullOrEmpty(programFiles64) && programFiles64 != programFiles)
            {
                commands.Add(Path.Combine(programFiles64, "Tailscale\\tailscale.exe"));
            }

            foreach (string command in commands)
            {
                try
                {
                    ProcessStartInfo info = new ProcessStartInfo();
                    info.FileName = command;
                    info.Arguments = "status --json";
                    info.UseShellExecute = false;
                    info.CreateNoWindow = true;
                    info.RedirectStandardOutput = true;
                    info.RedirectStandardError = true;

                    using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(info))
                    {
                        string output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit(2500);
                        foreach (Match match in AddressPattern.Matches(output))
                        {
                            IPAddress address;
                            if (IPAddress.TryParse(match.Value, out address) && !Contains(addresses, address))
                            {
                                addresses.Add(address);
                            }
                        }
                    }

                    if (addresses.Count > 0) return addresses;
                }
                catch
                {
                    // Tailscale pode não estar instalado ou o comando pode não estar no PATH.
                }
            }

            return addresses;
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

    internal static class MessageSender
    {
        public static MessageSendResult Send(PeerInfo peer, string senderName, string message)
        {
            TcpClient client = new TcpClient();
            try
            {
                IAsyncResult connection = client.BeginConnect(peer.Address, peer.Port, null, null);
                if (!connection.AsyncWaitHandle.WaitOne(4000))
                {
                    return MessageSendResult.Failed("O computador não respondeu na porta do TailMsg (38257). Verifique o firewall do Windows.");
                }

                client.EndConnect(connection);
                client.SendTimeout = 6000;
                client.ReceiveTimeout = 6000;

                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.WriteLine(TailMsgProtocol.BuildMessage(senderName, message));
                    writer.Flush();
                    string response = reader.ReadLine();
                    if (response == TailMsgProtocol.Acknowledgement + "|1|OK")
                    {
                        return MessageSendResult.Succeeded();
                    }
                }

                return MessageSendResult.Failed("O computador recebeu uma resposta inválida.");
            }
            catch (Exception exception)
            {
                return MessageSendResult.Failed("Não foi possível entregar a mensagem: " + exception.Message);
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }
    }
}
