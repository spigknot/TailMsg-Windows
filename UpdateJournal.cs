using System;
using System.IO;
using System.Text;
using System.Threading;

internal static class UpdateJournal
{
    private static readonly object Sync = new object();

    public static string OperationsDirectory
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "TailMsg",
                "updates",
                "operations");
        }
    }

    public static string CreateOperation(
        string targetDirectory,
        string expectedVersion)
    {
        string operationId = Guid.NewGuid().ToString("N");
        if (!WriteState(
            operationId,
            "created",
            expectedVersion,
            targetDirectory,
            "operation-created"))
        {
            throw new IOException(
                "Não foi possível criar o journal da operação de atualização.");
        }
        return operationId;
    }

    public static bool WriteState(
        string operationId,
        string state,
        string expectedVersion,
        string targetDirectory,
        string detail)
    {
        if (!IsSafeOperationId(operationId)) return false;
        string path = GetPath(operationId);
        string directory = Path.GetDirectoryName(path);
        try
        {
            lock (Sync)
            {
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                using (FileStream stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read))
                using (StreamWriter writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(false)))
                {
                    writer.WriteLine(
                        "utc=" + DateTime.UtcNow.ToString("o") +
                        ";state=" + Safe(state) +
                        ";version=" + Safe(expectedVersion) +
                        ";target=" + Safe(targetDirectory) +
                        ";detail=" + Safe(detail));
                }
                return true;
            }
        }
        catch
        {
            // A journal failure must be handled by the caller as an update
            // failure, but it must not crash the running messenger.
            return false;
        }
    }

    public static string ReadLastState(string operationId)
    {
        if (!IsSafeOperationId(operationId)) return "";
        try
        {
            string path = GetPath(operationId);
            if (!File.Exists(path)) return "";
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int index = lines.Length - 1; index >= 0; index--)
            {
                string state = ReadField(lines[index], "state");
                if (!String.IsNullOrEmpty(state)) return state;
            }
        }
        catch { }
        return "";
    }

    public static string ReadLastDetail(string operationId)
    {
        if (!IsSafeOperationId(operationId)) return "";
        try
        {
            string path = GetPath(operationId);
            if (!File.Exists(path)) return "";
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int index = lines.Length - 1; index >= 0; index--)
            {
                if (!String.IsNullOrEmpty(ReadField(lines[index], "state")))
                    return ReadField(lines[index], "detail");
            }
        }
        catch { }
        return "";
    }

    public static bool WaitForState(
        string operationId,
        string expectedState,
        int timeoutMilliseconds)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (String.Equals(
                ReadLastState(operationId),
                expectedState,
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            Thread.Sleep(100);
        }
        return String.Equals(
            ReadLastState(operationId),
            expectedState,
            StringComparison.OrdinalIgnoreCase);
    }

    public static string GetPath(string operationId)
    {
        if (!IsSafeOperationId(operationId))
            throw new ArgumentException("operationId inválido.");
        return Path.Combine(OperationsDirectory, operationId + ".log");
    }

    private static string ReadField(string line, string field)
    {
        string prefix = field + "=";
        string[] parts = (line ?? "").Split(';');
        foreach (string part in parts)
        {
            if (part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return part.Substring(prefix.Length);
        }
        return "";
    }

    private static bool IsSafeOperationId(string value)
    {
        if (String.IsNullOrEmpty(value) || value.Length > 64) return false;
        foreach (char item in value)
        {
            if (!((item >= 'a' && item <= 'z') ||
                  (item >= 'A' && item <= 'Z') ||
                  (item >= '0' && item <= '9') || item == '-'))
                return false;
        }
        return true;
    }

    private static string Safe(string value)
    {
        return (value ?? "")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace(";", ",")
            .Replace("=", ":");
    }
}
