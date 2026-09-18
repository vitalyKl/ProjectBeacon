namespace ProjectBeacon.Cli.Client;

public static class ClientSingleton
{
    public const string MutexName = @"Local\ProjectBeacon.Client";

    public static bool TryEnter(out Mutex? mutex)
    {
        var created = false;
        Mutex? acquired = null;
        try
        {
            acquired = new Mutex(true, MutexName, out created);
            if (created)
            {
                mutex = acquired;
                return true;
            }
        }
        catch
        {
        }
        acquired?.Dispose();
        mutex = null;
        return false;
    }
}
