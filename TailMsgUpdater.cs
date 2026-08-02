using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TailMsgUpdater
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                Dictionary<string, string> options = ParseArguments(args);
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

                WaitForApplication(processId);
                WaitForNetworkPorts();
                InstallPackage(zipPath, targetDirectory);
                if (!testOnly)
                {
                    ConfigureUser(targetDirectory);
                    ConfigureFirewallIfElevated(targetDirectory);
                    StartApplication(targetDirectory);
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "A atualização automática não pôde ser concluída.\r\n\r\n" +
                    exception.Message,
                    "TailMsg - erro na atualização",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                Environment.ExitCode = 1;
            }
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

        private static void WaitForNetworkPorts()
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                TcpListener tcpProbe = null;
                UdpClient udpProbe = null;
                try
                {
                    tcpProbe = new TcpListener(IPAddress.Any, 38257);
                    tcpProbe.Start();
                    udpProbe = new UdpClient(38258);
                    return;
                }
                catch (SocketException)
                {
                    Thread.Sleep(150);
                }
                finally
                {
                    try { if (tcpProbe != null) tcpProbe.Stop(); } catch { }
                    try { if (udpProbe != null) udpProbe.Close(); } catch { }
                }
            }
        }

        private static void InstallPackage(
            string zipPath,
            string targetDirectory)
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
            Directory.CreateDirectory(extractionDirectory);

            string backupPath = Path.Combine(
                targetDirectory,
                "TailMsg.exe.bak");
            string currentApplication = Path.Combine(
                targetDirectory,
                "TailMsg.exe");

            try
            {
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
                if (File.Exists(currentApplication))
                {
                    CopyWithRetry(currentApplication, backupPath, true);
                }

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
                    CopyWithRetry(sourceFile, destination, true);
                }
            }
            catch
            {
                if (File.Exists(backupPath))
                {
                    try
                    {
                        CopyWithRetry(backupPath, currentApplication, true);
                    }
                    catch { }
                }
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

        private static void StartApplication(string targetDirectory)
        {
            string applicationPath = Path.Combine(
                targetDirectory,
                "TailMsg.exe");
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = applicationPath;
            info.Arguments = "--background";
            info.WorkingDirectory = targetDirectory;
            info.UseShellExecute = true;
            Process.Start(info);

            string backupPath = Path.Combine(
                targetDirectory,
                "TailMsg.exe.bak");
            Thread.Sleep(1500);
            try
            {
                if (File.Exists(backupPath)) File.Delete(backupPath);
            }
            catch { }
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
    }
}
