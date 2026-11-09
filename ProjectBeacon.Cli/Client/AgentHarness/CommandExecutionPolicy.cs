namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Workstation-local command policy. Project approvals and manifest fingerprints are stored
/// outside the repository; repository files can suggest commands but cannot approve them.
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

    private static readonly HashSet<string> WorkflowExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet", "npm", "pnpm", "yarn", "bun", "cargo", "go", "python", "python3",
        "pytest", "make", "gradle", "mvn", "ruby", "bundle", "rake", "node", "deno"
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string NormalizeMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        Off => Off,
        Auto => Auto,
        Ask => Ask,
        _ => Ask
    };

    public static IReadOnlyList<string> GetManuallyApprovedPrefixes(string projectDirectory)
        => GetManuallyApprovedPrefixes(projectDirectory, GetManifestFingerprint(projectDirectory));

    /// <summary>Reads local approvals only when their stored fingerprint matches this execution snapshot.</summary>
    public static IReadOnlyList<string> GetManuallyApprovedPrefixes(string projectDirectory, string expectedManifestFingerprint)
        => ReadProjectApprovals(projectDirectory, expectedManifestFingerprint).ToArray();

    public static bool ManifestSourcesMatch(string projectRoot, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(workingDirectory))
            return false;
        return string.Equals(
            GetManifestFingerprint(projectRoot),
            GetManifestFingerprint(workingDirectory),
            StringComparison.Ordinal);
    }

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
                hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
                hash.AppendData(File.ReadAllBytes(path));
                hash.AppendData([0]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                hash.AppendData(Encoding.UTF8.GetBytes("unreadable:" + Path.GetFileName(path)));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    /// Returns true only after a human reviewed and selected the complete command suggestion
    /// list for this exact manifest/workflow fingerprint. An unseen repository is never trusted
    /// merely because it contains familiar manifest filenames.
    /// </summary>
    public static bool IsManifestFingerprintTrusted(string projectDirectory)
    {
        var current = GetManifestFingerprint(projectDirectory);
        if (string.IsNullOrWhiteSpace(current))
            return false;

        var path = ManifestApprovalPath(projectDirectory);
        if (!File.Exists(path))
            return false;

        try
        {
            var trusted = JsonSerializer.Deserialize<string>(File.ReadAllText(path), JsonOptions);
            return string.Equals(trusted, current, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Records the current manifest/workflow fingerprint after the user reviews suggestions.</summary>
    public static bool TrustCurrentManifestFingerprint(string projectDirectory)
    {
        var current = GetManifestFingerprint(projectDirectory);
        return !string.IsNullOrWhiteSpace(current) && WriteManifestFingerprint(projectDirectory, current);
    }

    /// <summary>
    /// Collects safe manifest commands, simple commands observed in GitHub Actions, and optional
    /// agent proposals. The returned strings are data only; they are never executed.
    /// </summary>
    public static IReadOnlyList<(string Prefix, string Source)> GetCommandSuggestions(
        string projectDirectory,
        IEnumerable<string>? agentProposals = null)
    {
        var suggestions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string prefix, string source)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                return;
            if (!suggestions.TryGetValue(prefix, out var sources))
            {
                sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                suggestions.Add(prefix, sources);
            }
            sources.Add(source);
        }

        foreach (var prefix in DetectManifestPrefixes(projectDirectory))
            Add(prefix, "Detected from project manifests");

        if (!string.IsNullOrWhiteSpace(projectDirectory) && Directory.Exists(projectDirectory))
        {
            foreach (var workflow in EnumerateWorkflowFiles(Path.GetFullPath(projectDirectory)))
            {
                string[] lines;
                try { lines = File.ReadAllLines(workflow); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                for (var i = 0; i < lines.Length; i++)
                {
                    var match = Regex.Match(
                        lines[i], @"^(?<indent>\s*)(?:-\s*)?run\s*:\s*(?<value>.*)$",
                        RegexOptions.IgnoreCase);
                    if (!match.Success)
                        continue;

                    var parentIndent = match.Groups["indent"].Value.Length;
                    var value = match.Groups["value"].Value.Trim();
                    var commands = new List<string>();
                    if (value is "|" or "|-" or "|+" or ">" or ">-" or ">+")
                    {
                        while (i + 1 < lines.Length)
                        {
                            var next = lines[i + 1];
                            if (!string.IsNullOrWhiteSpace(next)
                                && next.TakeWhile(char.IsWhiteSpace).Count() <= parentIndent)
                                break;
                            i++;
                            commands.Add(lines[i].Trim());
                        }
                    }
                    else
                    {
                        commands.Add(value);
                    }

                    foreach (var command in commands)
                    {
                        if (TryParseWorkflowCommand(command, projectDirectory, out var prefix))
                            Add(prefix, "Observed in a GitHub Actions workflow");
                    }
                }
            }
        }

        foreach (var proposal in agentProposals ?? [])
        {
            if (TryNormalizeSuggestedPrefix(proposal, projectDirectory, out var prefix))
                Add(prefix, "Proposed by the agent");
        }

        return suggestions
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => (pair.Key, string.Join("; ", pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))))
            .ToArray();
    }

    public static bool TryNormalizeSuggestedPrefix(string? value, string projectDirectory, out string prefix)
    {
        prefix = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 240)
            return false;

        var trimmed = value.Trim();
        if (trimmed.Contains('"')
            || trimmed.Contains('\'')
            || trimmed.Contains((char)96)
            || trimmed.Contains('$')
            || trimmed.Contains(';')
            || trimmed.Contains('|')
            || trimmed.Contains('&')
            || trimmed.Contains('<')
            || trimmed.Contains('>')
            || trimmed.Contains('\r')
            || trimmed.Contains('\n'))
            return false;

        var argv = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (argv.Length == 0 || argv.Length > 8)
            return false;
        if (!TryValidate(argv, projectDirectory, out _))
            return false;

        prefix = CreateApprovalPrefix(argv);
        return !string.IsNullOrWhiteSpace(prefix);
    }

    private static bool TryParseWorkflowCommand(string raw, string projectDirectory, out string prefix)
    {
        prefix = string.Empty;
        var command = raw.Trim();
        if (command.Length == 0 || command.StartsWith('#') || command.StartsWith(':'))
            return false;

        // YAML may wrap a simple scalar in quotes. Remove only a matching outer pair.
        if (command.Length >= 2
            && ((command[0] == '"' && command[^1] == '"')
                || (command[0] == '\'' && command[^1] == '\'')))
            command = command[1..^1].Trim();

        // Chaining, substitutions, redirections, and compound shell syntax are not argv prefixes.
        if (command.Contains('|')
            || command.Contains('&')
            || command.Contains(';')
            || command.Contains('<')
            || command.Contains('>')
            || command.Contains((char)96)
            || command.Contains('$')
            || command.Contains('"')
            || command.Contains('\'')
            || command.Contains('(')
            || command.Contains(')')
            || command.Contains('{')
            || command.Contains('}')
            || command.Contains('[')
            || command.Contains(']'))
            return false;

        var comment = command.IndexOf(" #", StringComparison.Ordinal);
        if (comment >= 0)
            command = command[..comment].TrimEnd();

        var argv = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (argv.Length == 0 || !WorkflowExecutables.Contains(Path.GetFileName(argv[0])))
            return false;
        if (!TryValidate(argv, projectDirectory, out _))
            return false;

        // make -C/-f is path-sensitive and too easy to widen accidentally.
        if (Path.GetFileName(argv[0]).Equals("make", StringComparison.OrdinalIgnoreCase)
            && argv.Length > 1 && argv[1].StartsWith("-", StringComparison.Ordinal))
            return false;

        prefix = CreateApprovalPrefix(argv);
        return !string.IsNullOrWhiteSpace(prefix);
    }

    private static bool WriteManifestFingerprint(string projectDirectory, string fingerprint)
    {
        var path = ManifestApprovalPath(projectDirectory);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(fingerprint, JsonOptions));
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
            return false;
        }
    }

    private static string ManifestApprovalPath(string projectDirectory)
    {
        var approvalPath = ApprovalPath(projectDirectory);
        return Path.Combine(Path.GetDirectoryName(approvalPath)!, "manifest-" + Path.GetFileName(approvalPath));
    }

    private static string? FindRepositoryRoot(string directory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(directory));
        while (current is not null)
        {
            var gitMetadata = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(gitMetadata) || File.Exists(gitMetadata))
                return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    private static IReadOnlyList<string> EnumerateWorkflowFiles(string root)
    {
        var repositoryRoot = FindRepositoryRoot(root) ?? root;
        var directory = Path.Combine(repositoryRoot, ".github", "workflows");
        if (!Directory.Exists(directory))
            return [];

        try
        {
            return Directory.EnumerateFiles(directory)
                .Where(file =>
                {
                    var extension = Path.GetExtension(file);
                    return extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase);
                })
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
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
        if (executable == "make" && argv.Count > 2 && (argv[1] is "-C" or "-f"))
            count = Math.Min(4, argv.Count);
        else if (argv.Count > 2 && (
            ((executable is "npm" or "pnpm" or "yarn" or "bun") && argv[1] == "run")
            || ((executable is "python" or "python3") && argv[1] == "-m")
            || (executable == "dotnet" && (argv[1] == "tool" || argv[1] == "ef"))))
            count = 3;
        if (argv.Count > 3 && (executable is "python" or "python3") && argv[1] == "-m" && argv[2] == "pip")
            count = 4;

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
            || executable.Contains('/')
            || executable.Contains('\\')
            || executable.Contains(':')
            || executable.Contains('\0'))
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

            if (ContainsPathOutsideWorktree(argument, workingDirectory))
            {
                error = "Absolute paths and parent-directory traversal outside the isolated worktree are not allowed. Synchronize the real project from the ProjectBeacon interface; an agent session cannot access the real project directory.";
                return false;
            }
        }

        var args = argv.Skip(1).ToArray();
        var command = basename.ToLowerInvariant();
        if (command == "git" && !TryValidateGitArguments(args, out error))
            return false;

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

    private static bool ContainsPathOutsideWorktree(string argument, string workingDirectory)
    {
        var value = argument.Trim().Trim('"');
        if (value.Length == 0)
            return false;

        var isMsBuildSwitch = Regex.IsMatch(value,
            @"^/(?:p:|property:|m(?:$|:)|maxcpucount(?:$|:)|nologo$|verbosity:|v:|target:|t:|restore$|nr:|clp:|warnaserror|fl(?:p:|$))",
            RegexOptions.IgnoreCase);
        var candidates = new List<string>();
        if (!isMsBuildSwitch)
            candidates.Add(value);

        // Path-bearing option values and response files must be checked separately:
        // resolving the complete "--project=/outside/file" token would treat it as a
        // harmless relative filename inside the worktree.
        var equals = value.LastIndexOf('=');
        if (equals >= 0 && equals < value.Length - 1)
            candidates.Add(value[(equals + 1)..].Trim().Trim('"'));
        var colon = value.StartsWith('-') ? value.IndexOf(':') : -1;
        if (colon >= 0 && colon < value.Length - 1)
            candidates.Add(value[(colon + 1)..].Trim().Trim('"'));
        if (value.StartsWith('@') && value.Length > 1)
            candidates.Add(value[1..].Trim().Trim('"'));

        foreach (var rawCandidate in candidates.Distinct(StringComparer.Ordinal))
        {
            var candidate = rawCandidate;
            if (candidate.Length == 0)
                continue;

            // Do not let Uri classify POSIX absolute paths as file:// URIs and bypass
            // the worktree boundary check. Only skip genuine non-file URIs.
            var hasWindowsRoot = Regex.IsMatch(candidate, @"^[A-Za-z]:[\\/]|^\\\\");
            if (!hasWindowsRoot
                && Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && uri.Scheme.Length > 1)
            {
                if (!uri.IsFile)
                    continue;
                candidate = uri.LocalPath;
            }

            var hasPathSeparator = candidate.Contains('/') || candidate.Contains('\\');
            if (!Path.IsPathRooted(candidate) && !hasWindowsRoot && !hasPathSeparator)
                continue;

            if (hasWindowsRoot && !OperatingSystem.IsWindows())
                return true;

            try
            {
                var root = FindRepositoryRoot(workingDirectory) ?? workingDirectory;
                var fullRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var fullPath = Path.GetFullPath(candidate, workingDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!string.Equals(fullPath, fullRoot, comparison)
                    && !fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)
                    && !fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, comparison))
                    return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return true;
            }
        }

        return false;
    }

    private static readonly HashSet<string> BlockedGitGlobalOptions = new(StringComparer.Ordinal)
    {
        "-c", "-C", "--config-env", "--exec-path", "--git-dir", "--work-tree", "--super-prefix"
    };

    // Allow only built-in inspection commands. A deny-list is unsafe here because
    // unrecognized names can resolve to user-defined aliases, including shell aliases.
    private static readonly HashSet<string> AllowedGitSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "diff", "log", "show", "rev-parse", "ls-files", "grep", "blame",
        "shortlog", "describe", "rev-list", "merge-base", "diff-tree", "name-rev",
        "count-objects", "check-ignore", "check-attr"
    };

    private static readonly string[] BlockedGitOptions =
    [
        "--upload-pack", "--receive-pack", "--exec", "--ext-diff", "--textconv", "--output", "-o",
        "--pager", "--paginate", "--open-files-in-pager", "-p"
    ];

    private static bool TryValidateGitArguments(IReadOnlyList<string> args, out string error)
    {
        error = string.Empty;
        var subcommand = GetGitSubcommand(args);
        if (subcommand is null || !AllowedGitSubcommands.Contains(subcommand))
        {
            error = subcommand is not null && subcommand.Equals("push", StringComparison.OrdinalIgnoreCase)
                ? "git push is blocked on the workstation command interface."
                : "Only built-in read-only Git inspection commands are allowed in agent sessions. Use repo_status for the real project branch and synchronize from the ProjectBeacon interface.";
            return false;
        }
        foreach (var argument in args)
        {
            if (BlockedGitGlobalOptions.Contains(argument)
                || argument == "--paginate"
                || argument.StartsWith("--pager=", StringComparison.Ordinal)
                || argument.StartsWith("--open-files-in-pager=", StringComparison.Ordinal)
                || (argument.StartsWith("-c", StringComparison.Ordinal) && argument.Length > 2)
                || (argument.StartsWith("-C", StringComparison.Ordinal) && argument.Length > 2)
                || BlockedGitGlobalOptions.Any(option => option.StartsWith("--", StringComparison.Ordinal)
                    && argument.StartsWith(option + "=", StringComparison.Ordinal))
                || BlockedGitOptions.Any(option => argument == option
                    || (option.StartsWith("--", StringComparison.Ordinal)
                        && argument.StartsWith(option + "=", StringComparison.Ordinal))
                    || (option == "-o" && argument.StartsWith("-o", StringComparison.Ordinal) && argument.Length > 2)))
            {
                error = "Git configuration, executable override, and external program options are blocked in agent sessions. Use the structured repo_status tool for repository status.";
                return false;
            }
        }

        return true;
    }

    private static string? GetGitSubcommand(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-C" or "-c" or "--git-dir" or "--work-tree")
            {
                i++;
                continue;
            }
            if (arg.StartsWith("-", StringComparison.Ordinal))
                continue;
            return arg;
        }
        return null;
    }

    public static bool ApprovePrefix(
        string projectDirectory,
        string prefix,
        string approvedManifestFingerprint,
        out string? error)
    {
        error = null;
        var parts = prefix.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "An empty command prefix cannot be approved.";
            return false;
        }
        if (!TryValidate(parts, projectDirectory, out var validationError))
        {
            error = validationError;
            return false;
        }
        if (string.IsNullOrWhiteSpace(approvedManifestFingerprint))
        {
            error = "A current project manifest fingerprint is required to save command approval.";
            return false;
        }

        // Existing approvals survive only while the exact manifests and CI workflows they
        // were reviewed against remain unchanged.
        var existing = ReadProjectApprovals(projectDirectory, approvedManifestFingerprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        existing.Add(string.Join(' ', parts));
        var path = ApprovalPath(projectDirectory);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            var approvalDocument = new
            {
                manifestFingerprint = approvedManifestFingerprint,
                prefixes = existing.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
            };
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(approvalDocument, JsonOptions));
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

    private static IEnumerable<string> ReadProjectApprovals(string projectDirectory, string expectedManifestFingerprint)
    {
        if (string.IsNullOrWhiteSpace(expectedManifestFingerprint))
            return [];

        var path = ApprovalPath(projectDirectory);
        if (!File.Exists(path))
            return [];

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("manifestFingerprint", out var fingerprint)
                || fingerprint.ValueKind != JsonValueKind.String
                || !string.Equals(fingerprint.GetString(), expectedManifestFingerprint, StringComparison.Ordinal)
                || !root.TryGetProperty("prefixes", out var prefixes)
                || prefixes.ValueKind != JsonValueKind.Array
                || prefixes.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
                return [];

            return prefixes.EnumerateArray()
                .Select(value => value.GetString() ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
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
                || arg.Equals("--global", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--location=global", StringComparison.OrdinalIgnoreCase));
        if (executable == "yarn")
            return args.Count >= 2 && args[0].Equals("global", StringComparison.OrdinalIgnoreCase);
        if (executable == "dotnet")
            return args.Count >= 3 && args[0].Equals("tool", StringComparison.OrdinalIgnoreCase)
                && (args[1] is "install" or "update")
                && !args.Any(arg => arg.Equals("--local", StringComparison.OrdinalIgnoreCase));
        if (executable == "cargo")
            return args[0].Equals("install", StringComparison.OrdinalIgnoreCase);
        if (executable is "pip" or "pip3")
            return args[0].Equals("install", StringComparison.OrdinalIgnoreCase)
                && !args.Any(arg => arg.Equals("--target", StringComparison.OrdinalIgnoreCase));
        if (executable is "python" or "python3")
        {
            var pip = args.Count >= 3 && args[0] == "-m" && args[1] == "pip" && args[2] == "install";
            return pip && !args.Any(arg => arg.Equals("--target", StringComparison.OrdinalIgnoreCase));
        }
        if (executable == "pipx")
            return args[0].Equals("install", StringComparison.OrdinalIgnoreCase);
        if (executable == "go")
            return args[0].Equals("install", StringComparison.OrdinalIgnoreCase);
        if (executable == "gem")
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

}
