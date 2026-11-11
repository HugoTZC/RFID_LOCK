internal static class AgentLog
{
    public static void Write(string message)
    {
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "RfidLock");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "diagnostico.log");
            if (File.Exists(path) && new FileInfo(path).Length >= 1_000_000)
                File.Move(path, path + ".old", overwrite: true);
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never interrupt input recovery.
        }
    }
}
