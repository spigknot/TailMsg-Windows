using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TailMsgUpdater
{
    internal static class Program
    {
        private const string CurrentVersion = TailMsg.UpdateConfig.CurrentVersion;
        private const string GitHubRepository = TailMsg.UpdateConfig.GitHubRepository;
        private const string LatestReleaseUrl =
            "https://api.github.com/repos/" + GitHubRepository + "/releases/latest";
        private const string GitHubUserAgent = "TailMsgUpdater/" + CurrentVersion;
        private const string PublicKeyResource = "TailMsg.UpdatePublicKey";
        private const int MaximumReleaseResponseBytes = 2 * 1024 * 1024;
        private const int MaximumZipEntries = 1000;
        private const long MaximumZipBytes = 512L * 1024L * 1024L;
        private const int NetworkPortGracePeriodMilliseconds = 1000;

        private sealed class FullRelease
        {
            public string Version;
            public string DownloadUrl;
            public string Sha256;
            public long Size;
        }

        private sealed class InstallTransaction
        {
            public string TargetDirectory;
            public string BackupDirectory;
            public List<string> InstalledRelativePaths;
        }

        private sealed class TimeoutWebClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                HttpWebRequest request =
                    (HttpWebRequest)base.GetWebRequest(address);
                if (request != null)
                {
                    request.Timeout = 10000;
                    request.ReadWriteTimeout = 10000;
                }
                return request;
            }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                if (args == null || args.Length == 0)
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new StandaloneUpdaterForm());
                    return;
                }

                Dictionary<string, string> options = ParseArguments(args);
                if (options.ContainsKey("--standalone-install"))
                {
                    RunStandaloneInstall(options);
                    return;
                }

                string zipPath = Require(options, "--zip");
                string targetDirectory = Path.GetFullPath(
                    Require(options, "--target"));
                int processId = Int32.Parse(Require(options, "--pid"));
                string testValue;
                bool testOnly = options.TryGetValue(
                    "--test-only",
                    out testValue) &&
                    String.Equals(
                        testValue,
                        "true",
                        StringComparison.OrdinalIgnoreCase);
                bool isolated = options.TryGetValue(
                    "--isolated",
                    out testValue) &&
                    String.Equals(
                        testValue,
                        "true",
                        StringComparison.OrdinalIgnoreCase);
                bool quiet = options.TryGetValue(
                    "--quiet",
                    out testValue) &&
                    String.Equals(
                        testValue,
                        "true",
                        StringComparison.OrdinalIgnoreCase);
                string operationId = options.ContainsKey("--operation-id")
                    ? options["--operation-id"]
                    : UpdateJournal.CreateOperation(targetDirectory, CurrentVersion);
                string expectedVersion = options.ContainsKey("--expected-version")
                    ? options["--expected-version"]
                    : CurrentVersion;
                if (options.ContainsKey("--operation-id"))
                {
                    UpdateJournal.WriteState(
                        operationId,
                        "created",
                        expectedVersion,
                        targetDirectory,
                        "worker-attached");
                }
                InstallTransaction transaction = null;
                bool applicationWasRunning =
                    IsProcessRunning(processId) ||
                    HasTailMsgRunning(targetDirectory);

                try
                {
                    WaitForApplication(processId);
                    UpdateJournal.WriteState(
                        operationId,
                        "app-closed",
                        expectedVersion,
                        targetDirectory,
                        "pid=" + processId);
                    if (!isolated && !testOnly) WaitForNetworkPorts();
                    transaction = InstallPackage(zipPath, targetDirectory, operationId);
                    UpdateJournal.WriteState(
                        operationId,
                        "package-applied",
                        expectedVersion,
                        targetDirectory,
                        "");
                    if (IsTrue(options, "--simulate-failure-after-apply"))
                    {
                        throw new InvalidOperationException(
                            "Falha simulada depois da aplicação do pacote.");
                    }
                    if (testOnly)
                    {
                        UpdateJournal.WriteState(
                            operationId,
                            "test-only-completed",
                            expectedVersion,
                            targetDirectory,
                            "");
                        CommitTransaction(transaction);
                        return;
                    }

                    if (!isolated)
                    {
                        ConfigureUser(targetDirectory);
                        ConfigureFirewallIfElevated(targetDirectory);
                    }
                    StartApplication(
                        targetDirectory,
                        operationId,
                        expectedVersion,
                        isolated,
                        IsTrue(options, "--simulate-service-failure"));
                    WaitForApplicationConfirmation(operationId);
                    if (!UpdateJournal.WriteState(
                        operationId,
                        "completed",
                        expectedVersion,
                        targetDirectory,
                        "post-install-confirmed"))
                    {
                        throw new IOException(
                            "Não foi possível persistir a conclusão da atualização.");
                    }
                    CommitTransaction(transaction);
                }
                catch (Exception exception)
                {
                    bool restartApplication = applicationWasRunning;
                    if (transaction != null)
                    {
                        try
                        {
                            CloseTailMsgInTarget(targetDirectory);
                            RollbackTransaction(transaction);
                            UpdateJournal.WriteState(
                                operationId,
                                "rollback",
                                expectedVersion,
                                targetDirectory,
                                exception.Message);
                        }
                        catch (Exception rollbackException)
                        {
                            restartApplication = false;
                            UpdateJournal.WriteState(
                                operationId,
                                "rollback-failed",
                                expectedVersion,
                                targetDirectory,
                                rollbackException.Message);
                        }
                    }
                    if (restartApplication)
                    {
                        TryRestartApplication(targetDirectory);
                    }
                    UpdateJournal.WriteState(
                        operationId,
                        "failed",
                        expectedVersion,
                        targetDirectory,
                        exception.Message);
                    throw;
                }
            }
            catch (Exception exception)
            {
                bool quiet = false;
                if (args != null)
                {
                    Dictionary<string, string> quietOptions = ParseArguments(args);
                    string quietValue;
                    quiet = quietOptions.TryGetValue("--quiet", out quietValue) &&
                        String.Equals(quietValue, "true", StringComparison.OrdinalIgnoreCase);
                }
                if (quiet)
                    Console.Error.WriteLine("ERRO: " + exception.Message);
                else
                    MessageBox.Show(
                        "A atualização automática não pôde ser concluída.\r\n\r\n" +
                        exception.Message,
                        "TailMsg - erro na atualização",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                Environment.ExitCode = 1;
            }
        }

        private static void RunStandaloneInstall(
            Dictionary<string, string> options)
        {
            string zipPath = Require(options, "--zip");
            string targetDirectory = Path.GetFullPath(
                Require(options, "--target"));
            bool testOnly = IsTrue(options, "--test-only");
            bool isolated = IsTrue(options, "--isolated");
            string expectedVersion = options.ContainsKey("--expected-version")
                ? options["--expected-version"]
                : ReadPackageVersion(zipPath);
            string operationId = options.ContainsKey("--operation-id")
                ? options["--operation-id"]
                : UpdateJournal.CreateOperation(targetDirectory, expectedVersion);
            InstallTransaction transaction = null;
            if (options.ContainsKey("--operation-id"))
            {
                UpdateJournal.WriteState(
                    operationId,
                    "created",
                    expectedVersion,
                    targetDirectory,
                    "standalone-attached");
            }

            ValidateStandaloneTarget(targetDirectory);
            bool applicationWasRunning = HasTailMsgRunning(targetDirectory);
            try
            {
                CloseTailMsgInTarget(targetDirectory);
                UpdateJournal.WriteState(
                    operationId,
                    "app-closed",
                    expectedVersion,
                    targetDirectory,
                    "standalone");
                if (!isolated && !testOnly) WaitForNetworkPorts();
                transaction = InstallPackage(zipPath, targetDirectory, operationId);
                UpdateJournal.WriteState(
                    operationId,
                    "package-applied",
                    expectedVersion,
                    targetDirectory,
                    "standalone");
                if (IsTrue(options, "--simulate-failure-after-apply"))
                {
                    throw new InvalidOperationException(
                        "Falha simulada depois da aplicação do pacote.");
                }
                if (testOnly)
                {
                    UpdateJournal.WriteState(
                        operationId,
                        "test-only-completed",
                        expectedVersion,
                        targetDirectory,
                        "standalone");
                    CommitTransaction(transaction);
                    return;
                }

                if (!isolated)
                {
                    ConfigureUser(targetDirectory);
                    ConfigureFirewallIfElevated(targetDirectory);
                }
                StartApplication(
                    targetDirectory,
                    operationId,
                    expectedVersion,
                    isolated,
                    IsTrue(options, "--simulate-service-failure"));
                WaitForApplicationConfirmation(operationId);
                if (!UpdateJournal.WriteState(
                    operationId,
                    "completed",
                    expectedVersion,
                    targetDirectory,
                    "standalone-post-install-confirmed"))
                {
                    throw new IOException(
                        "Não foi possível persistir a conclusão da atualização.");
                }
                CommitTransaction(transaction);
            }
            catch (Exception exception)
            {
                bool restartApplication = applicationWasRunning;
                if (transaction != null)
                {
                    try
                    {
                        CloseTailMsgInTarget(targetDirectory);
                        RollbackTransaction(transaction);
                        UpdateJournal.WriteState(
                            operationId,
                            "rollback",
                            expectedVersion,
                            targetDirectory,
                            exception.Message);
                    }
                    catch (Exception rollbackException)
                    {
                        restartApplication = false;
                        UpdateJournal.WriteState(
                            operationId,
                            "rollback-failed",
                            expectedVersion,
                            targetDirectory,
                            rollbackException.Message);
                    }
                }
                if (restartApplication)
                {
                    TryRestartApplication(targetDirectory);
                }
                UpdateJournal.WriteState(
                    operationId,
                    "failed",
                    expectedVersion,
                    targetDirectory,
                    exception.Message);
                throw;
            }
        }

        private static FullRelease LoadLatestFullRelease()
        {
            string json = DownloadText(LatestReleaseUrl);
            string version = ReadJsonString(json, "tag_name");
            if (!Regex.IsMatch(version ?? "", @"^\d{8}_\d{3}$"))
            {
                throw new InvalidDataException(
                    "A release mais recente do GitHub possui uma versão inválida.");
            }

            string expectedName = version + ".zip";
            List<string> assets = ExtractJsonObjects(json, "assets");
            string manifestUrl = "";
            FullRelease release = null;
            foreach (string asset in assets)
            {
                string name = ReadJsonString(asset, "name");
                if (String.Equals(
                    name,
                    "tailmsg-update.json",
                    StringComparison.OrdinalIgnoreCase))
                {
                    manifestUrl = ReadJsonString(asset, "browser_download_url");
                    continue;
                }
                if (!String.Equals(
                    name,
                    expectedName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string digest = ReadJsonString(asset, "digest") ?? "";
                digest = Regex.Replace(
                    digest,
                    @"^sha256:",
                    "",
                    RegexOptions.IgnoreCase);
                long size = ReadJsonLong(asset, "size");
                string url = ReadJsonString(asset, "browser_download_url");
                Uri parsed;
                if (!Regex.IsMatch(digest, @"^[0-9a-fA-F]{64}$") || size <= 0)
                {
                    throw new InvalidDataException(
                        "O GitHub não forneceu tamanho e SHA-256 válidos para o pacote full.");
                }
                if (!IsTrustedGitHubUrl(url, out parsed))
                {
                    throw new InvalidDataException(
                        "A release completa contém uma URL de download não confiável.");
                }

                release = new FullRelease
                {
                    Version = version,
                    DownloadUrl = url,
                    Sha256 = digest.ToLowerInvariant(),
                    Size = size
                };
            }

            if (release == null)
            {
                throw new FileNotFoundException(
                    "A release mais recente não possui o pacote full " + expectedName + ".");
            }
            Uri manifestUri;
            if (!IsTrustedGitHubUrl(manifestUrl, out manifestUri))
            {
                throw new InvalidDataException(
                    "A release completa não contém uma URL de manifesto confiável.");
            }

            ValidateReleaseManifest(
                DownloadText(manifestUrl),
                version,
                expectedName,
                release);
            return release;
        }

        private static bool IsTrustedGitHubUrl(string value, out Uri uri)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }
            return uri.Host == "github.com" ||
                uri.Host == "objects.githubusercontent.com" ||
                uri.Host == "release-assets.githubusercontent.com";
        }

        private static void ValidateReleaseManifest(
            string json,
            string expectedVersion,
            string expectedFileId,
            FullRelease release)
        {
            string version = ReadJsonString(json, "version");
            string fileId = ReadJsonString(json, "fileId");
            string sha256 = ReadJsonString(json, "sha256");
            string signatureText = ReadJsonString(json, "signature");
            long size = ReadJsonLong(json, "size");
            if (!String.Equals(version, expectedVersion, StringComparison.Ordinal) ||
                !String.Equals(fileId, expectedFileId, StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(sha256 ?? "", @"^[A-Fa-f0-9]{64}$") ||
                size <= 0 || String.IsNullOrEmpty(signatureText))
            {
                throw new InvalidDataException(
                    "O manifesto assinado da release é inválido.");
            }
            if (!String.Equals(sha256, release.Sha256, StringComparison.OrdinalIgnoreCase) ||
                size != release.Size)
            {
                throw new InvalidDataException(
                    "O manifesto não corresponde ao pacote full da release.");
            }

            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(signatureText);
            }
            catch
            {
                throw new InvalidDataException(
                    "A assinatura do manifesto é inválida.");
            }

            string payload = version + "\n" + fileId + "\n" +
                sha256.ToUpperInvariant() + "\n" + size;
            using (RSACryptoServiceProvider rsa =
                new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(ReadPublicKey());
                if (!rsa.VerifyData(
                    Encoding.UTF8.GetBytes(payload),
                    CryptoConfig.MapNameToOID("SHA256"),
                    signature))
                {
                    throw new CryptographicException(
                        "O manifesto da release não foi assinado pelo responsável do TailMsg.");
                }
            }
        }

        private static string ReadPublicKey()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream(PublicKeyResource))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException(
                        "A chave pública de atualização não está incorporada no atualizador.");
                }
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static string DownloadText(string url)
        {
            EnableTls12();
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = GitHubUserAgent;
            request.Accept = "application/vnd.github+json";
            request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            using (WebResponse response = request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int read;
                int total = 0;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > MaximumReleaseResponseBytes)
                    {
                        throw new InvalidDataException(
                            "A resposta da release do GitHub excedeu o tamanho permitido.");
                    }
                    output.Write(buffer, 0, read);
                }
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }

        private static void EnableTls12()
        {
            ServicePointManager.SecurityProtocol =
                ServicePointManager.SecurityProtocol | (SecurityProtocolType)3072;
        }

        private static string ReadJsonString(string json, string name)
        {
            Match match = Regex.Match(
                json ?? "",
                "\\\"" + Regex.Escape(name) +
                "\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"\\\\])*)\\\"",
                RegexOptions.Singleline);
            return match.Success ? Regex.Unescape(match.Groups[1].Value) : null;
        }

        private static long ReadJsonLong(string json, string name)
        {
            Match match = Regex.Match(
                json ?? "",
                "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*(\\d+)",
                RegexOptions.Singleline);
            long result;
            return match.Success && Int64.TryParse(match.Groups[1].Value, out result)
                ? result
                : 0;
        }

        private static List<string> ExtractJsonObjects(
            string json,
            string arrayName)
        {
            List<string> result = new List<string>();
            int property = (json ?? "").IndexOf(
                "\"" + arrayName + "\"",
                StringComparison.Ordinal);
            if (property < 0) return result;
            int arrayStart = json.IndexOf('[', property);
            if (arrayStart < 0) return result;

            bool inString = false;
            bool escaped = false;
            int depth = 0;
            int objectStart = -1;
            for (int index = arrayStart + 1; index < json.Length; index++)
            {
                char current = json[index];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (current == '\\')
                    {
                        escaped = true;
                    }
                    else if (current == '\"')
                    {
                        inString = false;
                    }
                    continue;
                }
                if (current == '\"')
                {
                    inString = true;
                }
                else if (current == '{')
                {
                    if (depth == 0) objectStart = index;
                    depth++;
                }
                else if (current == '}')
                {
                    if (depth > 0) depth--;
                    if (depth == 0 && objectStart >= 0)
                    {
                        result.Add(json.Substring(
                            objectStart,
                            index - objectStart + 1));
                        objectStart = -1;
                    }
                }
                else if (current == ']' && depth == 0)
                {
                    break;
                }
            }
            return result;
        }

        private static void DownloadFullPackage(
            FullRelease release,
            string zipPath,
            Action<int, long, long> progress)
        {
            EnableTls12();
            string parent = Path.GetDirectoryName(zipPath);
            if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
            using (WebClient client = new TimeoutWebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = GitHubUserAgent;
                client.Headers[HttpRequestHeader.Accept] =
                    "application/octet-stream";
                client.DownloadProgressChanged += delegate(
                    object sender,
                    DownloadProgressChangedEventArgs eventArgs)
                {
                    if (progress != null)
                    {
                        progress(
                            eventArgs.ProgressPercentage,
                            eventArgs.BytesReceived,
                            eventArgs.TotalBytesToReceive);
                    }
                };
                client.DownloadFile(release.DownloadUrl, zipPath);
            }

            FileInfo file = new FileInfo(zipPath);
            if (!file.Exists || file.Length != release.Size)
            {
                throw new InvalidDataException(
                    "O tamanho do pacote baixado não confere com o GitHub.");
            }
            string actualHash;
            using (FileStream input = File.OpenRead(zipPath))
            using (SHA256 sha = SHA256.Create())
            {
                actualHash = ToHex(sha.ComputeHash(input));
            }
            if (!String.Equals(
                actualHash,
                release.Sha256,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException(
                    "O pacote baixado foi alterado ou está corrompido.");
            }
            ValidatePackageLayout(zipPath, release.Version);
        }

        private static string ToHex(byte[] bytes)
        {
            StringBuilder builder = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes)
            {
                builder.Append(value.ToString("x2"));
            }
            return builder.ToString();
        }

        private static void ValidatePackageLayout(
            string zipPath,
            string expectedVersion)
        {
            bool hasApplication = false;
            bool hasUpdater = false;
            bool hasVersion = false;
            HashSet<string> names = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            int entryCount = 0;

            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (String.IsNullOrEmpty(entry.FullName)) continue;
                    entryCount++;
                    if (entryCount > MaximumZipEntries)
                    {
                        throw new InvalidDataException(
                            "O pacote possui entradas demais.");
                    }

                    string normalized = entry.FullName.Replace('\\', '/');
                    if (normalized.StartsWith("/", StringComparison.Ordinal) ||
                        normalized.IndexOf(':') >= 0)
                    {
                        throw new InvalidDataException(
                            "O pacote contém um caminho absoluto inseguro.");
                    }
                    string[] parts = normalized.Split('/');
                    foreach (string part in parts)
                    {
                        if (part == ".." || part == ".")
                        {
                            throw new InvalidDataException(
                                "O pacote contém um caminho inseguro: " + entry.FullName);
                        }
                    }
                    if (!names.Add(normalized))
                    {
                        throw new InvalidDataException(
                            "O pacote contém entradas duplicadas: " + normalized);
                    }

                    bool directory = normalized.EndsWith(
                        "/",
                        StringComparison.Ordinal);
                    if (directory) continue;
                    totalBytes += entry.Length;
                    if (totalBytes > MaximumZipBytes)
                    {
                        throw new InvalidDataException(
                            "O conteúdo descompactado do pacote é grande demais.");
                    }
                    if (String.Equals(normalized, "TailMsg.exe", StringComparison.OrdinalIgnoreCase))
                        hasApplication = true;
                    if (String.Equals(normalized, "TailMsgUpdater.exe", StringComparison.OrdinalIgnoreCase))
                        hasUpdater = true;
                    if (String.Equals(normalized, "version.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        hasVersion = true;
                        using (Stream input = entry.Open())
                        using (StreamReader reader = new StreamReader(input, Encoding.UTF8, true))
                        {
                            string version = reader.ReadToEnd().Trim();
                            if (expectedVersion != null &&
                                !String.Equals(version, expectedVersion, StringComparison.Ordinal))
                            {
                                throw new InvalidDataException(
                                    "A versão dentro do pacote não corresponde à release do GitHub.");
                            }
                        }
                    }
                }
            }

            if (!hasApplication || !hasUpdater || !hasVersion)
            {
                throw new InvalidDataException(
                    "O pacote full não contém TailMsg.exe, TailMsgUpdater.exe e version.txt.");
            }
        }

        private static void ValidateStandaloneTarget(string targetDirectory)
        {
            string target = Path.GetFullPath(targetDirectory);
            string root = Path.GetPathRoot(target);
            if (String.IsNullOrEmpty(root) ||
                String.Equals(
                    target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Escolha uma pasta específica para o TailMsg; a raiz do disco não é permitida.");
            }

            Directory.CreateDirectory(target);
            bool recognizable =
                File.Exists(Path.Combine(target, "TailMsg.exe")) ||
                File.Exists(Path.Combine(target, "TailMsgUpdater.exe")) ||
                File.Exists(Path.Combine(target, "version.txt"));
            if (!recognizable && Directory.GetFileSystemEntries(target).Length > 0)
            {
                throw new InvalidOperationException(
                    "A pasta escolhida não está vazia e não parece ser uma instalação do TailMsg.");
            }
        }

        private static List<int> FindTailMsgProcesses(string targetDirectory)
        {
            List<int> result = new List<int>();
            string applicationPath = Path.GetFullPath(
                Path.Combine(targetDirectory, "TailMsg.exe"));
            foreach (Process process in Process.GetProcessesByName("TailMsg"))
            {
                try
                {
                    if (!process.HasExited &&
                        String.Equals(
                            process.MainModule.FileName,
                            applicationPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(process.Id);
                    }
                }
                catch
                {
                    // Um processo protegido não pode ser identificado com segurança;
                    // nesse caso a própria substituição de arquivo dará o erro adequado.
                }
                finally
                {
                    process.Dispose();
                }
            }
            return result;
        }

        private static bool HasTailMsgRunning(string targetDirectory)
        {
            return FindTailMsgProcesses(targetDirectory).Count > 0;
        }

        private static void CloseTailMsgInTarget(string targetDirectory)
        {
            List<int> processIds = FindTailMsgProcesses(targetDirectory);
            foreach (int processId in processIds)
            {
                Process process = null;
                try
                {
                    process = Process.GetProcessById(processId);
                    if (process.HasExited) continue;
                    try
                    {
                        if (process.MainWindowHandle != IntPtr.Zero)
                        {
                            process.CloseMainWindow();
                        }
                    }
                    catch { }

                    if (process.WaitForExit(5000)) continue;

                    // O TailMsg pode estar somente na bandeja e, nesse caso,
                    // não possuir uma janela principal fechável. A confirmação
                    // dada na tela do updater autoriza concluir o encerramento.
                    process.Kill();
                    if (!process.WaitForExit(5000))
                    {
                        throw new TimeoutException(
                            "O TailMsg não encerrou a tempo para receber a atualização.");
                    }
                }
                catch (ArgumentException)
                {
                    // O processo encerrou entre a enumeração e a abertura.
                }
                finally
                {
                    if (process != null) process.Dispose();
                }
            }
        }

        private static void LaunchStandaloneWorker(
            string zipPath,
            string targetDirectory)
        {
            string helperDirectory = Path.Combine(
                Path.GetTempPath(),
                "TailMsgUpdater-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(helperDirectory);
            string helperPath = Path.Combine(
                helperDirectory,
                "TailMsgUpdater.exe");
            File.Copy(Application.ExecutablePath, helperPath, true);

            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = helperPath;
            info.Arguments =
                "--standalone-install true" +
                " --zip " + Quote(zipPath) +
                " --target " + Quote(targetDirectory);
            info.WorkingDirectory = targetDirectory;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            Process.Start(info);
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private static void WaitForApplication(int processId)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    if (!process.WaitForExit(30000))
                    {
                        throw new TimeoutException(
                            "O TailMsg não encerrou dentro de 30 segundos.");
                    }
                }
            }
            catch (ArgumentException)
            {
                // O processo já encerrou.
            }
        }

        private static bool IsProcessRunning(int processId)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    return !process.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static void WaitForApplicationConfirmation(string operationId)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (UpdateJournal.HasStateAfter(
                    operationId,
                    "service-ready",
                    "app-confirmed"))
                {
                    return;
                }

                string state = UpdateJournal.ReadLastState(operationId);
                if (String.Equals(
                        state,
                        "app-service-failed",
                        StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(
                        state,
                        "app-journal-failed",
                        StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(
                        state,
                        "app-version-mismatch",
                        StringComparison.OrdinalIgnoreCase))
                {
                    string detail = UpdateJournal.ReadLastDetail(operationId);
                    throw new InvalidOperationException(
                        "A nova instância não confirmou o serviço após o reinício. " +
                        "Estado: " + state +
                        (String.IsNullOrEmpty(detail)
                            ? ""
                            : "; detalhe: " + detail));
                }

                Thread.Sleep(100);
            }

            throw new InvalidOperationException(
                "A nova instância não confirmou o serviço após o reinício. Estado: " +
                UpdateJournal.ReadLastState(operationId));
        }

        private static void TryRestartApplication(string targetDirectory)
        {
            try
            {
                if (File.Exists(Path.Combine(targetDirectory, "TailMsg.exe")))
                {
                    StartApplication(targetDirectory, "", "", false, false);
                }
            }
            catch { }
        }

        private static void WaitForNetworkPorts()
        {
            // Não abra as portas de produção para fazer uma sonda. Fechar um
            // probe TCP/UDP e iniciar imediatamente a nova instância pode
            // deixar o registro do socket em transição e causar o erro 10048.
            // O processo anterior já foi aguardado; apenas damos uma pequena
            // janela para o Windows concluir a liberação. O bind real fica
            // exclusivamente no NetworkService da nova instância, que possui
            // retry próprio e preserva a compatibilidade com Wine.
            Thread.Sleep(NetworkPortGracePeriodMilliseconds);
        }

        private static InstallTransaction InstallPackage(
            string zipPath,
            string targetDirectory,
            string operationId)
        {
            if (!File.Exists(zipPath))
            {
                throw new FileNotFoundException(
                    "O pacote de atualização não foi encontrado.",
                    zipPath);
            }

            string extractionDirectory = Path.Combine(
                Path.GetTempPath(),
                "TailMsgUpdate-" + Guid.NewGuid().ToString("N"));
            string backupDirectory = Path.Combine(
                targetDirectory,
                ".tailmsg-update-backup-" + operationId);
            Directory.CreateDirectory(extractionDirectory);
            Directory.CreateDirectory(backupDirectory);
            List<string> copiedDestinations = new List<string>();
            List<string> installedRelativePaths = new List<string>();
            Dictionary<string, string> backups =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                ValidatePackageLayout(zipPath, null);
                ExtractSafely(zipPath, extractionDirectory);
                string newApplication = Path.Combine(
                    extractionDirectory,
                    "TailMsg.exe");
                string newUpdater = Path.Combine(
                    extractionDirectory,
                    "TailMsgUpdater.exe");
                if (!File.Exists(newApplication) ||
                    !File.Exists(newUpdater))
                {
                    throw new InvalidDataException(
                        "O pacote não contém TailMsg.exe e TailMsgUpdater.exe.");
                }

                Directory.CreateDirectory(targetDirectory);

                foreach (string sourceFile in Directory.GetFiles(
                    extractionDirectory,
                    "*",
                    SearchOption.AllDirectories))
                {
                    string relative = sourceFile.Substring(
                        extractionDirectory.Length)
                        .TrimStart(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar);
                    string destination = Path.GetFullPath(
                        Path.Combine(targetDirectory, relative));
                    string targetRoot = targetDirectory.TrimEnd(
                        Path.DirectorySeparatorChar) +
                        Path.DirectorySeparatorChar;
                    if (!destination.StartsWith(
                        targetRoot,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "O pacote contém um caminho de destino inválido.");
                    }

                    string parent = Path.GetDirectoryName(destination);
                    if (!Directory.Exists(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }

                    if (!backups.ContainsKey(destination) &&
                        File.Exists(destination))
                    {
                        string backupFile = Path.Combine(
                            backupDirectory,
                            relative);
                        string backupParent = Path.GetDirectoryName(backupFile);
                        if (!Directory.Exists(backupParent))
                        {
                            Directory.CreateDirectory(backupParent);
                        }
                        CopyWithRetry(destination, backupFile, true);
                        backups.Add(destination, backupFile);
                    }

                    copiedDestinations.Add(destination);
                    installedRelativePaths.Add(relative);
                    CopyWithRetry(sourceFile, destination, true);
                }

                File.WriteAllLines(
                    Path.Combine(backupDirectory, "installed-files.txt"),
                    installedRelativePaths.ToArray(),
                    new UTF8Encoding(false));

                return new InstallTransaction
                {
                    TargetDirectory = targetDirectory,
                    BackupDirectory = backupDirectory,
                    InstalledRelativePaths = installedRelativePaths
                };
            }
            catch
            {
                for (int index = copiedDestinations.Count - 1; index >= 0; index--)
                {
                    try
                    {
                        if (File.Exists(copiedDestinations[index]))
                            File.Delete(copiedDestinations[index]);
                    }
                    catch { }
                }
                foreach (KeyValuePair<string, string> backup in backups)
                {
                    try
                    {
                        CopyWithRetry(backup.Value, backup.Key, true);
                    }
                    catch { }
                }
                try
                {
                    if (Directory.Exists(backupDirectory))
                        Directory.Delete(backupDirectory, true);
                }
                catch { }
                throw;
            }
            finally
            {
                try
                {
                    Directory.Delete(extractionDirectory, true);
                }
                catch { }
            }
        }

        private static void CommitTransaction(InstallTransaction transaction)
        {
            if (transaction == null || String.IsNullOrEmpty(transaction.BackupDirectory)) return;
            try
            {
                if (Directory.Exists(transaction.BackupDirectory))
                    Directory.Delete(transaction.BackupDirectory, true);
            }
            catch (Exception exception)
            {
                throw new IOException(
                    "A atualização foi confirmada, mas o backup não pôde ser removido.",
                    exception);
            }
        }

        private static void RollbackTransaction(InstallTransaction transaction)
        {
            if (transaction == null) return;
            string targetRoot = transaction.TargetDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string installedList = Path.Combine(
                transaction.BackupDirectory,
                "installed-files.txt");
            List<string> installed = new List<string>();
            if (File.Exists(installedList))
            {
                installed.AddRange(File.ReadAllLines(installedList, Encoding.UTF8));
            }

            foreach (string relative in installed)
            {
                string destination = Path.GetFullPath(
                    Path.Combine(transaction.TargetDirectory, relative));
                if (!destination.StartsWith(
                    targetRoot,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "O journal contém um caminho de rollback inválido.");
                }
                try
                {
                    DeleteWithRetry(destination);
                }
                catch (Exception exception)
                {
                    throw new IOException(
                        "Não foi possível remover arquivo durante o rollback: " + relative,
                        exception);
                }
            }

            if (Directory.Exists(transaction.BackupDirectory))
            {
                foreach (string backupFile in Directory.GetFiles(
                    transaction.BackupDirectory,
                    "*",
                    SearchOption.AllDirectories))
                {
                    if (String.Equals(
                        Path.GetFileName(backupFile),
                        "installed-files.txt",
                        StringComparison.OrdinalIgnoreCase))
                        continue;
                    string relative = backupFile.Substring(
                        transaction.BackupDirectory.Length)
                        .TrimStart(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar);
                    string destination = Path.GetFullPath(
                        Path.Combine(transaction.TargetDirectory, relative));
                    if (!destination.StartsWith(
                        targetRoot,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "O backup contém um caminho de rollback inválido.");
                    }
                    string parent = Path.GetDirectoryName(destination);
                    if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
                    CopyWithRetry(backupFile, destination, true);
                }
            }

            try
            {
                if (Directory.Exists(transaction.BackupDirectory))
                    Directory.Delete(transaction.BackupDirectory, true);
            }
            catch { }
        }

        private static void DeleteWithRetry(string path)
        {
            Exception lastError = null;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) return;
                    File.Delete(path);
                    if (!File.Exists(path)) return;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                }
                Thread.Sleep(250);
            }

            throw new IOException(
                "Não foi possível remover arquivo durante o rollback.",
                lastError);
        }

        private static void ExtractSafely(
            string zipPath,
            string destinationDirectory)
        {
            string destinationRoot = Path.GetFullPath(destinationDirectory)
                .TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;

            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (String.IsNullOrEmpty(entry.FullName)) continue;
                    string destination = Path.GetFullPath(Path.Combine(
                        destinationDirectory,
                        entry.FullName));
                    if (!destination.StartsWith(
                        destinationRoot,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "O pacote contém um caminho inseguro: " +
                            entry.FullName);
                    }

                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                        entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    string parent = Path.GetDirectoryName(destination);
                    if (!Directory.Exists(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }
                    using (Stream source = entry.Open())
                    using (FileStream target = new FileStream(
                        destination,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None))
                    {
                        source.CopyTo(target);
                    }
                }
            }
        }

        private static void CopyWithRetry(
            string source,
            string destination,
            bool overwrite)
        {
            Exception lastError = null;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    File.Copy(source, destination, overwrite);
                    return;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                    Thread.Sleep(250);
                }
            }
            throw new IOException(
                "Não foi possível substituir " + destination + ".",
                lastError);
        }

        private static void ConfigureUser(string targetDirectory)
        {
            string applicationPath = Path.Combine(
                targetDirectory,
                "TailMsg.exe");
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                key.SetValue(
                    "TailMsg",
                    "\"" + applicationPath + "\" --background",
                    RegistryValueKind.String);
            }

            string userPath = Environment.GetEnvironmentVariable(
                "Path",
                EnvironmentVariableTarget.User) ?? "";
            string[] entries = userPath.Split(
                new char[] { ';' },
                StringSplitOptions.RemoveEmptyEntries);
            bool found = false;
            foreach (string entry in entries)
            {
                if (String.Equals(
                    entry.Trim(),
                    targetDirectory,
                    StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                string updated = userPath.TrimEnd(';');
                if (updated.Length > 0) updated += ";";
                updated += targetDirectory;
                Environment.SetEnvironmentVariable(
                    "Path",
                    updated,
                    EnvironmentVariableTarget.User);
            }
        }

        private static void ConfigureFirewallIfElevated(
            string targetDirectory)
        {
            WindowsPrincipal principal = new WindowsPrincipal(
                WindowsIdentity.GetCurrent());
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                return;
            }

            string applicationPath = Path.Combine(
                targetDirectory,
                "TailMsg.exe");
            RunNetsh(
                "advfirewall firewall delete rule name=\"TailMsg - mensagens TCP\"");
            RunNetsh(
                "advfirewall firewall delete rule name=\"TailMsg - descoberta UDP\"");
            RunNetsh(
                "advfirewall firewall add rule name=\"TailMsg - mensagens TCP\" " +
                "dir=in action=allow protocol=TCP localport=38257 program=\"" +
                applicationPath +
                "\" profile=any remoteip=10.0.0.0/8,100.64.0.0/10");
            RunNetsh(
                "advfirewall firewall add rule name=\"TailMsg - descoberta UDP\" " +
                "dir=in action=allow protocol=UDP localport=38258 program=\"" +
                applicationPath +
                "\" profile=any remoteip=10.0.0.0/8,100.64.0.0/10");
        }

        private static void RunNetsh(string arguments)
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = "netsh.exe";
            info.Arguments = arguments;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            using (Process process = Process.Start(info))
            {
                process.WaitForExit(10000);
            }
        }

        private static void StartApplication(
            string targetDirectory,
            string operationId,
            string expectedVersion,
            bool isolated,
            bool simulateServiceFailure)
        {
            string applicationPath = Path.Combine(
                targetDirectory,
                "TailMsg.exe");
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = applicationPath;
            // Uma atualização real deve devolver a janela ao usuário. O
            // modo oculto continua reservado ao início automático, ao
            // rollback e aos cenários isolados do smoke test.
            bool startInBackground =
                String.IsNullOrEmpty(operationId) || isolated;
            info.Arguments = startInBackground ? "--background" : "";
            if (!String.IsNullOrEmpty(operationId))
            {
                info.Arguments +=
                    " --update-operation-id " + Quote(operationId) +
                    " --update-expected-version " + Quote(expectedVersion);
                if (isolated)
                {
                    info.Arguments +=
                        " --test-instance " + Quote(operationId) +
                        " --test-no-network" +
                        " --test-exit-after-confirm";
                }
                if (simulateServiceFailure)
                {
                    info.Arguments += " --test-force-service-failure";
                }
            }
            info.WorkingDirectory = targetDirectory;
            info.UseShellExecute = true;
            Process.Start(info);
        }

        private static Dictionary<string, string> ParseArguments(
            string[] args)
        {
            Dictionary<string, string> result =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException(
                        "Parâmetros incompletos para o atualizador.");
                }
                result[args[index]] = args[index + 1];
            }
            return result;
        }

        private static string Require(
            Dictionary<string, string> options,
            string name)
        {
            string value;
            if (!options.TryGetValue(name, out value) ||
                String.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "Parâmetro obrigatório ausente: " + name);
            }
            return value;
        }

        private static bool IsTrue(
            Dictionary<string, string> options,
            string name)
        {
            string value;
            return options.TryGetValue(name, out value) &&
                String.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadPackageVersion(string zipPath)
        {
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (!String.Equals(
                        entry.FullName,
                        "version.txt",
                        StringComparison.OrdinalIgnoreCase))
                        continue;
                    using (Stream stream = entry.Open())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string version = (reader.ReadToEnd() ?? "").Trim();
                        if (Regex.IsMatch(version, @"^\d{8}_\d{3}$"))
                            return version;
                    }
                }
            }
            throw new InvalidDataException(
                "O pacote não contém uma versão válida em version.txt.");
        }

        private sealed class StandaloneUpdaterForm : Form
        {
            private readonly TextBox targetBox;
            private readonly Button checkButton;
            private readonly Button installButton;
            private readonly Button browseButton;
            private readonly ProgressBar progressBar;
            private readonly Label installedLabel;
            private readonly Label availableLabel;
            private readonly Label statusLabel;
            private FullRelease availableRelease;
            private bool busy;

            public StandaloneUpdaterForm()
            {
                Text = "TailMsg - Atualizador";
                ClientSize = new Size(620, 300);
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                StartPosition = FormStartPosition.CenterScreen;
                AutoScaleMode = AutoScaleMode.Font;
                Font = new Font("Segoe UI", 9F);

                Label title = new Label();
                title.Text = "Atualizador do TailMsg";
                title.Font = new Font("Segoe UI Semibold", 17F);
                title.Location = new Point(18, 14);
                title.Size = new Size(580, 32);
                Controls.Add(title);

                Label description = new Label();
                description.Text =
                    "Baixe e instale o pacote full mais recente diretamente do GitHub. " +
                    "Este modo também repara uma instalação cujo executável principal foi perdido.";
                description.Location = new Point(20, 49);
                description.Size = new Size(575, 38);
                description.AutoEllipsis = false;
                Controls.Add(description);

                Label targetLabel = new Label();
                targetLabel.Text = "Pasta da instalação:";
                targetLabel.Location = new Point(20, 99);
                targetLabel.Size = new Size(125, 22);
                Controls.Add(targetLabel);

                targetBox = new TextBox();
                targetBox.Location = new Point(146, 96);
                targetBox.Size = new Size(365, 24);
                targetBox.Text = Path.GetDirectoryName(Application.ExecutablePath);
                Controls.Add(targetBox);

                browseButton = new Button();
                browseButton.Text = "Procurar...";
                browseButton.Location = new Point(518, 95);
                browseButton.Size = new Size(84, 26);
                browseButton.Click += BrowseClick;
                Controls.Add(browseButton);

                installedLabel = new Label();
                installedLabel.Location = new Point(20, 132);
                installedLabel.Size = new Size(580, 22);
                Controls.Add(installedLabel);

                availableLabel = new Label();
                availableLabel.Location = new Point(20, 155);
                availableLabel.Size = new Size(580, 22);
                Controls.Add(availableLabel);

                progressBar = new ProgressBar();
                progressBar.Location = new Point(20, 190);
                progressBar.Size = new Size(580, 18);
                progressBar.Minimum = 0;
                progressBar.Maximum = 100;
                Controls.Add(progressBar);

                statusLabel = new Label();
                statusLabel.Location = new Point(20, 214);
                statusLabel.Size = new Size(580, 22);
                Controls.Add(statusLabel);

                checkButton = new Button();
                checkButton.Text = "Verificar atualizações";
                checkButton.Location = new Point(20, 252);
                checkButton.Size = new Size(150, 30);
                checkButton.Click += CheckClick;
                Controls.Add(checkButton);

                installButton = new Button();
                installButton.Text = "Baixar e instalar pacote full";
                installButton.Location = new Point(178, 252);
                installButton.Size = new Size(205, 30);
                installButton.Enabled = false;
                installButton.Click += InstallClick;
                Controls.Add(installButton);

                Button closeButton = new Button();
                closeButton.Text = "Fechar";
                closeButton.Location = new Point(518, 252);
                closeButton.Size = new Size(84, 30);
                closeButton.DialogResult = DialogResult.Cancel;
                Controls.Add(closeButton);
                CancelButton = closeButton;

                RefreshInstalledVersion();
                availableLabel.Text = "Versão disponível: consultando o GitHub...";
                statusLabel.Text = "Aguardando consulta.";
                Shown += delegate { BeginCheck(); };
                FormClosing += FormClosingHandler;
            }

            private string TargetPath()
            {
                string value = (targetBox.Text ?? "").Trim();
                if (value.Length == 0)
                {
                    throw new ArgumentException(
                        "Informe a pasta onde o TailMsg deve ser instalado.");
                }
                return Path.GetFullPath(value);
            }

            private void RefreshInstalledVersion()
            {
                try
                {
                    string target = TargetPath();
                    string versionPath = Path.Combine(target, "version.txt");
                    if (File.Exists(versionPath))
                    {
                        string version = File.ReadAllText(versionPath).Trim();
                        installedLabel.Text = "Versão instalada: " +
                            (version.Length == 0 ? "não identificada" : version);
                    }
                    else if (File.Exists(Path.Combine(target, "TailMsg.exe")) ||
                             File.Exists(Path.Combine(target, "TailMsgUpdater.exe")))
                    {
                        installedLabel.Text = "Versão instalada: não identificada";
                    }
                    else
                    {
                        installedLabel.Text = "Nenhuma instalação identificada nesta pasta.";
                    }
                }
                catch
                {
                    installedLabel.Text = "Pasta da instalação ainda não foi validada.";
                }
            }

            private void BrowseClick(object sender, EventArgs args)
            {
                if (busy) return;
                using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "Escolha a pasta da instalação do TailMsg";
                    dialog.ShowNewFolderButton = true;
                    try { dialog.SelectedPath = TargetPath(); } catch { }
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    targetBox.Text = dialog.SelectedPath;
                    RefreshInstalledVersion();
                }
            }

            private void CheckClick(object sender, EventArgs args)
            {
                BeginCheck();
            }

            private void BeginCheck()
            {
                if (busy) return;
                availableRelease = null;
                SetBusy(true, "Consultando a release full mais recente...");
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        FullRelease release = LoadLatestFullRelease();
                        PostToUi(delegate
                        {
                            availableRelease = release;
                            availableLabel.Text =
                                "Versão disponível: " + release.Version + " (" +
                                FormatSize(release.Size) + ")";
                            SetBusy(false, "Pacote full pronto para baixar.");
                        });
                    }
                    catch (Exception exception)
                    {
                        PostToUi(delegate
                        {
                            availableLabel.Text = "Versão disponível: não encontrada.";
                            SetBusy(false, "Não foi possível consultar o GitHub.");
                            MessageBox.Show(
                                this,
                                "Não foi possível consultar a atualização.\r\n\r\n" +
                                exception.Message,
                                "TailMsg - atualizador",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                        });
                    }
                });
            }

            private void InstallClick(object sender, EventArgs args)
            {
                if (busy || availableRelease == null) return;
                string target;
                try
                {
                    target = TargetPath();
                    ValidateStandaloneTarget(target);
                }
                catch (Exception exception)
                {
                    MessageBox.Show(
                        this,
                        exception.Message,
                        "TailMsg - atualizador",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                bool tailMsgRunning = HasTailMsgRunning(target);
                string runningWarning = tailMsgRunning
                    ? "O TailMsg está aberto. Se você confirmar, o próprio updater " +
                      "irá fechá-lo antes de substituir os arquivos.\r\n\r\n"
                    : "";
                DialogResult answer = MessageBox.Show(
                    this,
                    runningWarning +
                    "Será baixado e instalado o pacote full " +
                    availableRelease.Version + ".\r\n\r\n" +
                    "A instalação será feita na pasta:\r\n" + target +
                    "\r\n\r\nContinuar?",
                    "TailMsg - atualizador",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;

                FullRelease release = availableRelease;
                SetBusy(true, "Preparando o download...");
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string zipPath = Path.Combine(
                        Path.GetTempPath(),
                        "TailMsgUpdaterDownloads",
                        release.Version + ".zip");
                    try
                    {
                        PostToUi(delegate
                        {
                            progressBar.Style = ProgressBarStyle.Continuous;
                            progressBar.Value = 0;
                            statusLabel.Text = "Baixando o pacote full...";
                        });
                        DownloadFullPackage(
                            release,
                            zipPath,
                            delegate(int percent, long received, long total)
                            {
                                PostToUi(delegate
                                {
                                    progressBar.Style = ProgressBarStyle.Continuous;
                                    progressBar.Value = Math.Max(0, Math.Min(100, percent));
                                    statusLabel.Text = "Baixando: " + percent + "% (" +
                                        FormatSize(received) + " de " +
                                        FormatSize(total > 0 ? total : release.Size) + ")";
                                });
                            });
                        PostToUi(delegate { statusLabel.Text = "Download validado. Preparando a instalação..."; });
                        LaunchStandaloneWorker(zipPath, target);
                        PostToUi(delegate
                        {
                            SetBusy(false, "Instalação iniciada. O TailMsg será aberto em instantes.");
                            Close();
                        });
                    }
                    catch (Exception exception)
                    {
                        PostToUi(delegate
                        {
                            SetBusy(false, "A instalação não foi iniciada.");
                            MessageBox.Show(
                                this,
                                "Não foi possível baixar ou validar o pacote.\r\n\r\n" +
                                exception.Message,
                                "TailMsg - atualizador",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                        });
                    }
                });
            }

            private void SetBusy(bool value, string status)
            {
                busy = value;
                checkButton.Enabled = !value;
                browseButton.Enabled = !value;
                installButton.Enabled = !value && availableRelease != null;
                targetBox.Enabled = !value;
                statusLabel.Text = status;
                progressBar.Style = value
                    ? ProgressBarStyle.Marquee
                    : ProgressBarStyle.Continuous;
                if (!value && progressBar.Value == 0) progressBar.Value = 0;
            }

            private void PostToUi(Action action)
            {
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(new MethodInvoker(delegate { action(); }));
                }
                catch { }
            }

            private void FormClosingHandler(object sender, FormClosingEventArgs args)
            {
                if (busy)
                {
                    MessageBox.Show(
                        this,
                        "Aguarde a operação em andamento terminar.",
                        "TailMsg - atualizador",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    args.Cancel = true;
                }
            }

            private static string FormatSize(long value)
            {
                double size = value;
                string[] units = new string[] { "B", "KB", "MB", "GB" };
                int index = 0;
                while (size >= 1024 && index < units.Length - 1)
                {
                    size /= 1024;
                    index++;
                }
                return index == 0
                    ? ((long)size).ToString() + " " + units[index]
                    : size.ToString("0.0") + " " + units[index];
            }
        }
    }
}
