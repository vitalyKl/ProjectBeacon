namespace ProjectBeacon.Cli.Client;

/// <summary>
/// Append-only daemon log file in the client store directory. Lines are timestamped and the file rotates when it grows too large.
/// </summary>
internal sealed class ClientFileLog
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private static readonly string LogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectBeacon");
    private readonly object _gate = new();
    private readonly string _path = Path.Combine(LogDir, "client.log");
    private bool _failureReported;

    public void Write(string message)
    {
        lock (_gate)
        {
            try
            {
                EnsureFile();
                if (new FileInfo(_path).Length > MaxBytes)
                    File.Move(_path, _path + ".1", overwrite: true);
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                ReportOnce(ex);
            }
        }
    }

    // Observed on the reference device: AppendAllText failed with FNF for a missing file in the store directory. Create the file explicitly first.
    private void EnsureFile()
    {
        if (!File.Exists(_path))
            using (File.Create(_path)) { }
    }

    private void ReportOnce(Exception ex)
    {
        // Logging must never take the daemon down, but surface a persistent failure once.
        if (_failureReported)
            return;
        _failureReported = true;
        var diag = $"client.log: cannot write to {_path}: {ex.GetType().Name}: {ex.Message}";
        diag += " | dirExists=" + Directory.Exists(LogDir);
        try { using (File.Create(_path)) { } diag += " | create=ok"; }
        catch (Exception e2) { diag += " | create=" + e2.GetType().Name; }
        try { File.AppendAllText(_path, "diag-retry"); diag += " | retry=ok"; }
        catch (Exception e3) { diag += " | retry=" + e3.GetType().Name; }
        Console.Error.WriteLine(diag);
    }
}
