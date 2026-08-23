using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace TailMsg
{
    internal sealed class UpdateManifest
    {
        public string Version;
        public string FileId;
        public string Sha256;
        public long Size;
        public string Signature;
        public string DownloadUrl;
        public string Source;
    }

    internal static class TailMsgUpdateClient
    {
        private const string PublicKeyResource = "TailMsg.UpdatePublicKey";

        public static void CheckAsync(
            Action<UpdateManifest> updateAvailable,
            Action<string> failed,
            Action<bool> completed = null)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool found = false;
                UpdateManifest selected = null;
                List<string> failures = new List<string>();
                try
                {
                    EnableTls12();
                    try
                    {
                        UpdateManifest r2Manifest = LoadR2Manifest();
                        if (IsNewerThanCurrent(r2Manifest))
                        {
                            selected = r2Manifest;
                        }
                    }
                    catch (Exception exception)
                    {
                        failures.Add("R2: " + exception.Message);
                    }

                    try
                    {
                        UpdateManifest githubManifest = LoadGitHubManifest();
                        if (IsNewerThanCurrent(githubManifest) &&
                            (selected == null ||
                             String.CompareOrdinal(
                                 githubManifest.Version,
                                 selected.Version) > 0))
                        {
                            selected = githubManifest;
                        }
                    }
                    catch (Exception exception)
                    {
                        failures.Add("GitHub: " + exception.Message);
                    }

                    if (selected != null)
                    {
                        found = true;
                        updateAvailable(selected);
                    }
                    else if (failures.Count == 2 && failed != null)
                    {
                        failed(String.Join(" | ", failures.ToArray()));
                    }
                }
                catch (Exception exception)
                {
                    if (failed != null) failed(exception.Message);
                }
                finally
                {
                    if (completed != null) completed(found);
                }
            });
        }

        public static void DownloadAndInstallAsync(
            UpdateManifest manifest,
            Action downloaded,
            Action readyToClose,
            Action<string> failed)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    EnableTls12();
                    ValidateManifest(manifest);

                    string updateDirectory = Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.LocalApplicationData),
                        "TailMsg",
                        "updates");
                    Directory.CreateDirectory(updateDirectory);
                    string zipPath = Path.Combine(
                        updateDirectory,
                        manifest.Version + ".zip");

                    using (WebClient client = CreateWebClient())
                    {
                        client.DownloadFile(
                            GetDownloadUrl(manifest),
                            zipPath);
                    }

                    if (downloaded != null) downloaded();
                    ValidatePackage(zipPath, manifest);

                    string applicationDirectory = Path.GetDirectoryName(
                        ApplicationPath());
                    string updaterPath = Path.Combine(
                        applicationDirectory,
                        "TailMsgUpdater.exe");
                    if (!File.Exists(updaterPath))
                    {
                        throw new FileNotFoundException(
                            "O instalador auxiliar TailMsgUpdater.exe não foi encontrado. " +
                            "Execute install.ps1 uma vez para habilitar atualizações automáticas.",
                            updaterPath);
                    }

                    string temporaryUpdater = Path.Combine(
                        Path.GetTempPath(),
                        "TailMsgUpdater-" + Guid.NewGuid().ToString("N") + ".exe");
                    File.Copy(updaterPath, temporaryUpdater, true);

                    ProcessStartInfo info = new ProcessStartInfo();
                    info.FileName = temporaryUpdater;
                    info.Arguments =
                        "--zip " + Quote(zipPath) +
                        " --target " + Quote(applicationDirectory) +
                        " --pid " + Process.GetCurrentProcess().Id;
                    info.UseShellExecute = false;
                    info.CreateNoWindow = true;
                    Process.Start(info);

                    readyToClose();
                }
                catch (Exception exception)
                {
                    failed(exception.Message);
                }
            });
        }

        private static void ValidateManifest(UpdateManifest manifest)
        {
            if (manifest == null ||
                !Regex.IsMatch(manifest.Version ?? "", @"^\d{8}_\d{3}$") ||
                !Regex.IsMatch(manifest.FileId ?? "", @"^[A-Za-z0-9_.-]{1,200}$") ||
                !Regex.IsMatch(manifest.Sha256 ?? "", @"^[A-Fa-f0-9]{64}$") ||
                manifest.Size <= 0 ||
                String.IsNullOrEmpty(manifest.Signature))
            {
                throw new InvalidDataException(
                    "O manifesto de atualização é inválido.");
            }

            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(manifest.Signature);
            }
            catch
            {
                throw new InvalidDataException(
                    "A assinatura da atualização é inválida.");
            }

            string payload = BuildSignedPayload(manifest);
            using (RSACryptoServiceProvider rsa =
                new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(ReadPublicKey());
                bool valid = rsa.VerifyData(
                    Encoding.UTF8.GetBytes(payload),
                    CryptoConfig.MapNameToOID("SHA256"),
                    signature);
                if (!valid)
                {
                    throw new CryptographicException(
                        "A atualização não foi assinada pelo responsável do TailMsg.");
                }
            }
        }

        private static void ValidatePackage(
            string zipPath,
            UpdateManifest manifest)
        {
            FileInfo file = new FileInfo(zipPath);
            if (!file.Exists || file.Length != manifest.Size)
            {
                throw new InvalidDataException(
                    "O tamanho do pacote baixado não confere.");
            }

            string hash;
            using (FileStream stream = File.OpenRead(zipPath))
            using (SHA256 sha = SHA256.Create())
            {
                hash = ToHex(sha.ComputeHash(stream));
            }
            if (!String.Equals(
                hash,
                manifest.Sha256,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException(
                    "O pacote baixado foi alterado ou está corrompido.");
            }
        }

        private static UpdateManifest ParseManifest(string json)
        {
            UpdateManifest manifest = new UpdateManifest();
            manifest.Version = ReadJsonString(json, "version");
            manifest.FileId = ReadJsonString(json, "fileId");
            manifest.Sha256 = ReadJsonString(json, "sha256");
            manifest.Signature = ReadJsonString(json, "signature");

            Match size = Regex.Match(
                json ?? "",
                "\"size\"\\s*:\\s*(\\d+)",
                RegexOptions.IgnoreCase);
            long parsedSize;
            if (!size.Success ||
                !Int64.TryParse(size.Groups[1].Value, out parsedSize))
            {
                parsedSize = 0;
            }
            manifest.Size = parsedSize;
            return manifest;
        }

        private static UpdateManifest LoadR2Manifest()
        {
            string json;
            using (WebClient client = CreateWebClient())
            {
                json = client.DownloadString(BuildR2Url(
                    UpdateConfig.ManifestFileName));
            }

            UpdateManifest manifest = ParseManifest(json);
            manifest.DownloadUrl = BuildR2Url(manifest.FileId);
            manifest.Source = "R2";
            ValidateManifest(manifest);
            return manifest;
        }

        private static UpdateManifest LoadGitHubManifest()
        {
            string releaseJson;
            using (WebClient client = CreateWebClient())
            {
                releaseJson = client.DownloadString(BuildGitHubLatestReleaseUrl());
            }

            string releaseVersion = ReadJsonString(releaseJson, "tag_name");
            if (!Regex.IsMatch(releaseVersion ?? "", @"^\d{8}_\d{3}$"))
            {
                throw new InvalidDataException(
                    "A release mais recente do GitHub não possui uma versão válida.");
            }

            string manifestUrl = FindGitHubAssetUrl(
                releaseJson,
                "tailmsg-update.json");
            string packageUrl = FindGitHubAssetUrl(
                releaseJson,
                releaseVersion + ".zip");
            if (String.IsNullOrEmpty(manifestUrl) ||
                String.IsNullOrEmpty(packageUrl))
            {
                throw new InvalidDataException(
                    "A release do GitHub não contém o manifesto assinado e o ZIP completo.");
            }

            string manifestJson;
            using (WebClient client = CreateWebClient())
            {
                manifestJson = client.DownloadString(manifestUrl);
            }

            UpdateManifest manifest = ParseManifest(manifestJson);
            if (!String.Equals(
                manifest.Version,
                releaseVersion,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A versão do manifesto do GitHub não corresponde à release.");
            }
            if (!String.Equals(
                manifest.FileId,
                releaseVersion + ".zip",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "O manifesto do GitHub aponta para um arquivo inesperado.");
            }

            manifest.DownloadUrl = packageUrl;
            manifest.Source = "GitHub";
            ValidateManifest(manifest);
            return manifest;
        }

        private static bool IsNewerThanCurrent(UpdateManifest manifest)
        {
            return manifest != null && String.CompareOrdinal(
                manifest.Version,
                UpdateConfig.CurrentVersion) > 0;
        }

        private static string ReadJsonString(string json, string name)
        {
            Match match = Regex.Match(
                json ?? "",
                "\"" + Regex.Escape(name) +
                "\"\\s*:\\s*\"([^\"]*)\"",
                RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : "";
        }

        private static string FindGitHubAssetUrl(
            string json,
            string assetName)
        {
            MatchCollection urls = Regex.Matches(
                json ?? "",
                "\\\"browser_download_url\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"",
                RegexOptions.IgnoreCase);
            foreach (Match urlMatch in urls)
            {
                string prefix = (json ?? "").Substring(0, urlMatch.Index);
                MatchCollection names = Regex.Matches(
                    prefix,
                    "\\\"name\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"",
                    RegexOptions.IgnoreCase);
                if (names.Count == 0) continue;
                string name = names[names.Count - 1].Groups[1].Value;
                if (String.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
                {
                    return urlMatch.Groups[1].Value;
                }
            }
            return "";
        }

        internal static string BuildSignedPayload(UpdateManifest manifest)
        {
            return manifest.Version + "\n" +
                manifest.FileId + "\n" +
                manifest.Sha256.ToUpperInvariant() + "\n" +
                manifest.Size;
        }

        private static string ReadPublicKey()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream =
                assembly.GetManifestResourceStream(PublicKeyResource))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException(
                        "A chave pública de atualização não está incorporada.");
                }
                using (StreamReader reader =
                    new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static WebClient CreateWebClient()
        {
            WebClient client = new WebClient();
            client.Encoding = Encoding.UTF8;
            client.Headers[HttpRequestHeader.UserAgent] =
                "TailMsg/" + UpdateConfig.CurrentVersion;
            return client;
        }

        private static string BuildGitHubLatestReleaseUrl()
        {
            return "https://api.github.com/repos/" +
                UpdateConfig.GitHubRepository +
                "/releases/latest";
        }

        private static string BuildR2Url(string fileName)
        {
            return UpdateConfig.R2PublicBase + "/" +
                Uri.EscapeDataString(fileName);
        }

        private static string GetDownloadUrl(UpdateManifest manifest)
        {
            if (!String.IsNullOrEmpty(manifest.DownloadUrl))
            {
                Uri uri;
                if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out uri) ||
                    uri.Scheme != Uri.UriSchemeHttps ||
                    (uri.Host != "github.com" &&
                     uri.Host != "objects.githubusercontent.com" &&
                     !uri.Host.EndsWith(".r2.dev")))
                {
                    throw new InvalidDataException(
                        "A origem da atualização não é confiável.");
                }
                return manifest.DownloadUrl;
            }
            return BuildR2Url(manifest.FileId);
        }

        private static void EnableTls12()
        {
            ServicePointManager.SecurityProtocol =
                (SecurityProtocolType)3072;
        }

        private static string ApplicationPath()
        {
            return Assembly.GetExecutingAssembly().Location;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
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
    }
}
