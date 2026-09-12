using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TailMsg
{
    // Uma linha do histórico persistente: mensagem de texto, imagem ou áudio.
    internal sealed class HistoryEntry
    {
        public long Seq;
        public string Kind;   // text | image | audio
        public string Time;   // HH:mm:ss
        public string Sender;
        public string Address;
        public long Size;     // tamanho da imagem, em bytes
        public int DurationMilliseconds;
        public string FileName;
        public string Text;   // mensagem recebida ou transcrição do áudio
        public string OperationId;
    }

    // Guarda o histórico em %LOCALAPPDATA%\TailMsg: um índice de texto
    // (historico.tsv) e a mídia em arquivos (historico-midia), para o histórico
    // sobreviver ao fechamento do aplicativo.
    internal static class HistoryStore
    {
        private const int MaxEntries = 300;
        private const long MaxMediaBytes = 200L * 1024L * 1024L;
        private static readonly object Gate = new object();

        public static string RootDirectory
        {
            get
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TailMsg");
                Directory.CreateDirectory(root);
                return root;
            }
        }

        public static string IndexPath
        {
            get { return Path.Combine(RootDirectory, "historico.tsv"); }
        }

        public static string MediaDirectory
        {
            get
            {
                string media = Path.Combine(RootDirectory, "historico-midia");
                Directory.CreateDirectory(media);
                return media;
            }
        }

        public static List<HistoryEntry> Load()
        {
            List<HistoryEntry> entries = new List<HistoryEntry>();
            lock (Gate)
            {
                if (!File.Exists(IndexPath)) return entries;
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(IndexPath, Encoding.UTF8);
                }
                catch (IOException)
                {
                    return entries;
                }
                foreach (string line in lines)
                {
                    HistoryEntry entry = ParseLine(line);
                    if (entry != null) entries.Add(entry);
                }
            }
            entries.Sort(delegate(HistoryEntry left, HistoryEntry right)
            {
                return left.Seq.CompareTo(right.Seq);
            });
            return entries;
        }

        // Grava a linha e devolve o identificador usado depois para atualizar a
        // transcrição do áudio.
        public static long Append(HistoryEntry entry)
        {
            if (entry == null) return 0;
            if (entry.Seq == 0) entry.Seq = DateTime.Now.Ticks;
            lock (Gate)
            {
                try
                {
                    File.AppendAllText(IndexPath, BuildLine(entry) + "\n", Encoding.UTF8);
                }
                catch (IOException)
                {
                    return entry.Seq;
                }
                PruneLocked();
            }
            return entry.Seq;
        }

        // Salva a mídia recebida e devolve o nome do arquivo gravado.
        public static string SaveMedia(byte[] bytes, string extension)
        {
            if (bytes == null || bytes.Length == 0) return "";
            string name = "m-" + DateTime.Now.Ticks.ToString("x", CultureInfo.InvariantCulture) +
                "-" + bytes.Length.ToString("x", CultureInfo.InvariantCulture) + extension;
            try
            {
                File.WriteAllBytes(Path.Combine(MediaDirectory, name), bytes);
            }
            catch (IOException)
            {
                return "";
            }
            return name;
        }

        public static string MediaPath(string fileName)
        {
            if (String.IsNullOrEmpty(fileName)) return "";
            return Path.Combine(MediaDirectory, fileName);
        }

        public static void UpdateTranscription(long seq, string text)
        {
            if (seq == 0 || String.IsNullOrEmpty(text)) return;
            lock (Gate)
            {
                List<HistoryEntry> entries = Load();
                bool changed = false;
                foreach (HistoryEntry entry in entries)
                {
                    if (entry.Seq == seq && entry.Text != text)
                    {
                        entry.Text = text;
                        changed = true;
                    }
                }
                if (!changed) return;
                RewriteLocked(entries);
            }
        }

        // Apaga uma entrada (e a mídia dela) — usada pelo "Deletar".
        public static bool DeleteBySeq(long seq)
        {
            if (seq == 0) return false;
            lock (Gate)
            {
                List<HistoryEntry> entries = Load();
                for (int index = 0; index < entries.Count; index++)
                {
                    if (entries[index].Seq != seq) continue;
                    DeleteMedia(entries[index]);
                    entries.RemoveAt(index);
                    RewriteLocked(entries);
                    return true;
                }
            }
            return false;
        }

        // Apaga a entrada de uma operação ��� usada pelo "Deletar para todos".
        public static bool DeleteByOperationId(string operationId)
        {
            if (String.IsNullOrEmpty(operationId)) return false;
            lock (Gate)
            {
                List<HistoryEntry> entries = Load();
                bool changed = false;
                for (int index = entries.Count - 1; index >= 0; index--)
                {
                    if (!String.Equals(entries[index].OperationId, operationId,
                        StringComparison.Ordinal))
                    {
                        continue;
                    }
                    DeleteMedia(entries[index]);
                    entries.RemoveAt(index);
                    changed = true;
                }
                if (changed) RewriteLocked(entries);
                return changed;
            }
        }

        // Mantém as últimas entradas e limita o tamanho total da mídia,
        // apagando os arquivos das linhas descartadas.
        private static void PruneLocked()
        {
            List<HistoryEntry> entries = Load();
            bool changed = false;

            while (entries.Count > MaxEntries)
            {
                DeleteMedia(entries[0]);
                entries.RemoveAt(0);
                changed = true;
            }

            long total = 0;
            List<long> sizes = new List<long>();
            foreach (HistoryEntry entry in entries)
            {
                long size = 0;
                string path = MediaPath(entry.FileName);
                if (path.Length > 0 && File.Exists(path))
                {
                    size = new FileInfo(path).Length;
                }
                sizes.Add(size);
                total += size;
            }
            int drop = 0;
            while (total > MaxMediaBytes && drop < entries.Count - 1)
            {
                total -= sizes[drop];
                DeleteMedia(entries[drop]);
                drop++;
                changed = true;
            }
            if (drop > 0) entries.RemoveRange(0, drop);

            if (changed) RewriteLocked(entries);
        }

        private static void DeleteMedia(HistoryEntry entry)
        {
            string path = MediaPath(entry == null ? "" : entry.FileName);
            if (path.Length == 0) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
            }
        }

        private static void RewriteLocked(List<HistoryEntry> entries)
        {
            StringBuilder text = new StringBuilder();
            foreach (HistoryEntry entry in entries)
            {
                text.Append(BuildLine(entry)).Append('\n');
            }
            try
            {
                File.WriteAllText(IndexPath, text.ToString(), Encoding.UTF8);
            }
            catch (IOException)
            {
            }
        }

        private static string BuildLine(HistoryEntry entry)
        {
            return String.Join("\t", new string[]
            {
                entry.Seq.ToString(CultureInfo.InvariantCulture),
                entry.Kind ?? "",
                entry.Time ?? "",
                Escape(entry.Sender),
                Escape(entry.Address),
                entry.Size.ToString(CultureInfo.InvariantCulture),
                entry.DurationMilliseconds.ToString(CultureInfo.InvariantCulture),
                Escape(entry.FileName),
                Escape(entry.Text),
                Escape(entry.OperationId)
            });
        }

        private static HistoryEntry ParseLine(string line)
        {
            if (String.IsNullOrEmpty(line)) return null;
            string[] parts = line.Split('\t');
            if (parts.Length < 9) return null;
            HistoryEntry entry = new HistoryEntry();
            long seq;
            if (!Int64.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out seq))
            {
                return null;
            }
            entry.Seq = seq;
            entry.Kind = parts[1];
            entry.Time = parts[2];
            entry.Sender = Unescape(parts[3]);
            entry.Address = Unescape(parts[4]);
            long size;
            entry.Size = Int64.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out size) ? size : 0;
            int duration;
            entry.DurationMilliseconds = Int32.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out duration) ? duration : 0;
            entry.FileName = Unescape(parts[7]);
            entry.Text = Unescape(parts[8]);
            entry.OperationId = parts.Length > 9 ? Unescape(parts[9]) : "";
            return entry;
        }

        private static string Escape(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            return value.Replace("\\", "\\\\").Replace("\t", "\\t")
                .Replace("\r", "").Replace("\n", "\\n");
        }

        private static string Unescape(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            StringBuilder text = new StringBuilder(value.Length);
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                if (current != '\\' || index + 1 >= value.Length)
                {
                    text.Append(current);
                    continue;
                }
                index++;
                char next = value[index];
                if (next == 'n') text.Append('\n');
                else if (next == 't') text.Append('\t');
                else text.Append(next);
            }
            return text.ToString();
        }
    }
}
