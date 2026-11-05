namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Workstation-local command policy. Project approvals are stored outside the repository and
/// keyed by its canonical path; repository files can suggest commands but cannot approve them.
/// </summary>
internal static class CommandExecutionPolicy
{
    public const string Off = "off";
    public const string Ask = "ask";
    public const string Auto = "auto";

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bower_components", "bin", "obj",
        ".vs", ".idea", ".opencode", ".beacon", ".venv", "venv", "__pycache__",
        "dist", "build", "target", "vendor", "packages"
    };

    private static readonly HashSet<string> BlockedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "sudo", "su", "ssh", "scp", "sftp", "curl", "wget", "nc", "ncat", "netcat",
        "telnet", "ftp", "socat", "sh", "bash", "zsh", "fish", "cmd", "cmd.exe",
        "powershell", "powershell.exe", "pwsh", "pwsh.exe"
    };

    public static string NormalizeMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        Off => Off,
        Auto => Auto,
        Ask => Ask,
        _ => Ask
    };

    public static IReadOnlyList<string> GetApprovedPrefixes(string projectDirectory)
    {
        var prefixes = DetectManifestPrefixes(projectDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var approved in ReadProjectApprovals(projectDirectory))
            prefixes.Add(approved);
        return prefixes.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<string> GetManuallyApprovedPrefixes(string projectDirectory)
        => ReadProjectApprovals(projectDirectory).ToArray();

    /// <summary>
    /// Hashes the manifests and CI workflow files used for command discovery. A change during a
    /// turn invalidates automatic manifest trust for that turn and forces a fresh human decision.
    /// </summary>
    public static string GetManifestFingerprint(string projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
            return string.Empty;

        var root = Path.GetFullPath(projectDirectory);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in EnumerateManifestFiles(root)
            .Concat(EnumerateWorkflowFiles(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                hash.AppendData(Encoding.UTF8.GetBytes(relative + "\\0"));
                hash.AppendData(File.ReadAllBytes(path));
                hash.AppendData([0]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable manifests are omitted; a changed fingerprint remains conservative
                // because command detection will not infer permission from them.
                hash.AppendData(Encoding.UTF8.GetBytes("unreadable:" + Path.GetFileName(path)));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IEnumerable<string> EnumerateWorkflowFiles(string root)
    {
        var directory = Path.Combine(root, ".github", "workflows");
        if (!Directory.Exists(directory))
            yield break;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(directory).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file);
            if (extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase))
                yield return file;
        }
    }

    public static IReadOnlyList<string> DetectManifestPrefixes(string projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
            return [];

        var root = Path.GetFullPath(projectDirectory);
        var files = EnumerateManifestFiles(root).ToArray();
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (files.Any(path => Path.GetExtension(path).Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".vbproj", StringComparison.OrdinalIgnoreCase)))
        {
            prefixes.Add("dotnet build");
            prefixes.Add("dotnet test");
        }

        foreach (var packageJson in files.Where(path => Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(packageJson));
                if (!document.RootElement.TryGetProperty("scripts", out var scripts)
                    || scripts.ValueKind != JsonValueKind.Object)
                    continue;

                var packageManager = document.RootElement.TryGetProperty("packageManager", out var manager)
                    && manager.ValueKind == JsonValueKind.String
                        ? manager.GetString()?.Split('@')[0]
                        : null;
                var runner = packageManager switch
                {
                    "pnpm" => "pnpm",
                    "yarn" => "yarn",
                    "bun" => "bun",
                    _ => "npm"
                };

                if (scripts.TryGetProperty("test", out _))
                    prefixes.Add(runner == "npm" ? "npm test" : runner + " test");
                if (scripts.TryGetProperty("build", out _))
                    prefixes.Add(runner == "npm" ? "npm run build" : runner + " run build");
                if (scripts.TryGetProperty("lint", out _))
                    prefixes.Add(runner == "npm" ? "npm run lint" : runner + " run lint");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A malformed or unreadable manifest never grants command permission.
            }
        }

        if (files.Any(path => Path.GetFileName(path).Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase)))
        {
            prefixes.Add("cargo build");
            prefixes.Add("cargo test");
        }

        if (files.Any(path => Path.GetFileName(path).Equals("go.mod", StringComparison.OrdinalIgnoreCase)))
        {
            prefixes.Add("go test");
            prefixes.Add("go build");
        }

        if (files.Any(path => Path.GetFileName(path).Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Equals("pytest.ini", StringComparison.OrdinalIgnoreCase)))
            prefixes.Add("python -m pytest");

        foreach (var makefile in files.Where(path =>
            Path.GetFileName(path).Equals("Makefile", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Equals("GNUmakefile", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                foreach (var line in File.ReadLines(makefile))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0 || char.IsWhiteSpace(line[0]))
                        continue;
                    var target = line[..colon].Trim();
                    if (target is "build" or "test" or "check" or "lint" or "format" or "fmt")
                        prefixes.Add("make " + target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ignore inaccessible optional manifests.
            }
        }

        return prefixes.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string CreateApprovalPrefix(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0)
            return string.Empty;

        var executable = Path.GetFileName(argv[0]).ToLowerInvariant();
        var count = argv.Count > 1 ? 2 : 1;
        if (argv.Count > 2 && (
            (executable is "npm" or "pnpm" or "yarn" or "bun" && argv[1] == "run")
            || (executable is "python" or "python3" && argv[1] == "-m")
            || (executable == "dotnet" && argv[1] == "tool")))
            count = 3;

        return string.Join(' ', argv.Take(count));
    }

    public static bool MatchesApprovedPrefix(IReadOnlyList<string> argv, IReadOnlyCollection<string> prefixes)
    {
        if (argv.Count == 0)
            return false;

        foreach (var prefix in prefixes)
        {
            var parts = prefix.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Length > argv.Count)
                continue;

            var matches = true;
            for (var i = 0; i < parts.Length; i++)
            {
                if (!string.Equals(argv[i], parts[i], OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return true;
        }

        return false;
    }

    public static bool TryValidate(IReadOnlyList<string> argv, string workingDirectory, out string error)
    {
        error = string.Empty;
        if (argv.Count == 0 || argv.Count > 128 || argv.Any(string.IsNullOrWhiteSpace))
        {
            error = "argv must contain between 1 and 128 non-empty strings.";
            return false;
        }

        var executable = argv[0].Trim();
        if (Path.IsPathRooted(executable)
            || executable.Contains('/') || executable.Contains('\\')
            || executable.Contains(':') || executable.Contains('\0'))
        {
            error = "The executable must be a name from PATH, not a path.";
            return false;
        }

        var basename = Path.GetFileName(executable);
        if (BlockedExecutables.Contains(basename))
        {
            error = $"The executable '{basename}' is blocked by the workstation command policy.";
            return false;
        }

        foreach (var argument in argv.Skip(1))
        {
            if (argument.Contains('\0'))
            {
                error = "Command arguments cannot contain null characters.";
                return false;
            }

            if (Path.IsPathRooted(argument)
                || System.Text.RegularExpressions.Regex.IsMatch(argument, @"(^|[=:])([A-Za-z]:[\\/]|/|\\\\)")
                || System.Text.RegularExpressions.Regex.IsMatch(argument.Replace('\\', '/'), @"(^|[=:/])\.\.(?:/|$)"))
            {
                error = "Absolute paths and parent-directory traversal are not allowed in command arguments.";
                return false;
            }
        }

        var args = argv.Skip(1).ToArray();
        var command = basename.ToLowerInvariant();
        if (command == "git" && args.Length > 0 && args[0].Equals("push", StringComparison.OrdinalIgnoreCase))
        {
            error = "git push is blocked on the workstation command interface.";
            return false;
        }

        if (IsGlobalInstall(command, args))
        {
            error = "Global package and tool installation is blocked. Use a project-local environment.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            error = "The project working directory does not exist.";
            return false;
        }

        return true;
    }

    public static bool ApprovePrefix(string projectDirectory, string prefix, out string? error)
    {
        error = null;
        var parts = prefix.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "An empty command prefix cannot be approved.";
            return false;
        }

        var existing = ReadProjectApprovals(projectDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        existing.Add(string.Join(' ', parts));
        var path = ApprovalPath(projectDirectory);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(existing.OrderBy(x => x).ToArray(), JsonOptions));
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Could not save the project command approval ({ex.GetType().Name}).";
            return false;
        }
    }

    private static IEnumerable<string> ReadProjectApprovals(string projectDirectory)
    {
        var path = ApprovalPath(projectDirectory);
        if (!File.Exists(path))
            return [];

        try
        {
            return (JsonSerializer.Deserialize<string[]>(File.ReadAllText(path), JsonOptions) ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Corrupt local approvals fail closed; they are not repaired from repository content.
            return [];
        }
    }

    private static IEnumerable<string> EnumerateManifestFiles(string root)
    {
        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((root, 0));
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Dequeue();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (depth < 3 && !SkippedDirectories.Contains(name))
                        pending.Enqueue((entry, depth + 1));
                    continue;
                }

                if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("go.mod", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("pytest.ini", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Makefile", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("GNUmakefile", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(name).Equals(".sln", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(name).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(name).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(name).Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(name).Equals(".vbproj", StringComparison.OrdinalIgnoreCase))
                    yield return entry;
            }
        }
    }

    private static bool IsGlobalInstall(string executable, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return false;

        if (executable is "npm" or "pnpm" or "bun")
            return args.Any(arg => arg.Equals("-g", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--global", StringComparison.OrdinalIgnoreCase));
        if (executable == "yarn")
            return args.Count >= 2 && args[0].Equals("global", StringComparison.OrdinalIgnoreCase);
        if (executable == "dotnet")
            return args.Count >= 3 && args[0].Equals("tool", StringComparison.OrdinalIgnoreCase)
                && args[1].Equals("install", StringComparison.OrdinalIgnoreCase)
                && args.Any(arg => arg.Equals("-g", StringComparison.OrdinalIgnoreCase)
                    || arg.Equals("--global", StringComparison.OrdinalIgnoreCase));
        if (executable == "cargo")
            return args[0].Equals("install", StringComparison.OrdinalIgnoreCase);
        if (executable is "pip" or "pip3")
            return args[0].Equals("install", StringComparison.OrdinalIgnoreCase)
                && args.Any(arg => arg.Equals("--user", StringComparison.OrdinalIgnoreCase));
        if (executable is "go")
            return args[0].Equals("install", StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private static string ApprovalPath(string projectDirectory)
    {
        var canonical = Path.GetFullPath(projectDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparisonValue = OperatingSystem.IsWindows() ? canonical.ToUpperInvariant() : canonical;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(comparisonValue))).ToLowerInvariant();
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProjectBeacon",
            "project-command-approvals",
            key + ".json");
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
