namespace ProjectBeacon.Cli.Client;

public static class WindowsAutostart
{
    public const string ShortcutName = "Beacon Client.cmd";

    public static string DefaultDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    public static bool IsEnabled(string? directory = null) =>
        File.Exists(Path.Combine(directory ?? DefaultDirectory, ShortcutName));

    public static void Enable(string? directory = null, string? fileName = null, string? arguments = null)
    {
        var dir = directory ?? DefaultDirectory;
        Directory.CreateDirectory(dir);
        var (file, args) = fileName is null ? LaunchCommand() : (fileName, arguments ?? "client");
        File.WriteAllText(Path.Combine(dir, ShortcutName), RenderScript(file, args));
    }

    public static void Disable(string? directory = null)
    {
        var path = Path.Combine(directory ?? DefaultDirectory, ShortcutName);
        if (File.Exists(path))
            File.Delete(path);
    }

    public static string RenderScript(string fileName, string arguments) =>
        $"@echo off{Environment.NewLine}\"{fileName}\" {arguments}{Environment.NewLine}";

    public static (string FileName, string Arguments) LaunchCommand()
    {
        var process = Environment.ProcessPath ?? "beacon";
        var entry = Environment.GetCommandLineArgs()[0];
        if (Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return (process, $"\"{entry}\" client");
        return (process, "client");
    }
}
