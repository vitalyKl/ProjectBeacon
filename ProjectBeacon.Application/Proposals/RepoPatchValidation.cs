namespace ProjectBeacon.Application.Proposals;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Enums;
using ProjectBeacon.Application.Tasks;

public sealed record RepoPatchFile(
    string GitPath,
    string ProjectPath,
    string? BaseHash,
    string? ResultHash);

public sealed record ValidatedRepoPatch(
    string Patch,
    IReadOnlyList<RepoPatchFile> Files,
    string BaseHash,
    string PayloadJson);

/// <summary>
/// Validates the server-facing RepoPatch manifest. Client paths are untrusted: projectPath is
/// checked against the persisted subtask grant, while gitPath is checked again against the
/// actual project-root mapping by the workstation before any file is changed.
/// </summary>
public static class RepoPatchValidation
{
    public const int MaxPatchLength = 30_000;
    public const int MaxFiles = 50;

    public static bool TryValidate(
        string? payloadJson,
        IReadOnlyList<string> allowedPaths,
        IReadOnlyList<string> profileDefaultPaths,
        out ValidatedRepoPatch? patch,
        out string? error)
    {
        patch = null;
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(payloadJson) || payloadJson.Length > Proposal.MaxPayloadLength)
                throw new ArgumentException("RepoPatch payload is missing or exceeds the proposal limit.");

            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            Exact(root, "patch", "files");
            var patchText = RequiredString(root, "patch", MaxPatchLength);
            if (patchText.Contains('\0') || patchText.Contains("GIT binary patch", StringComparison.OrdinalIgnoreCase)
                || patchText.Contains("Binary files ", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Binary or invalid patch content is not supported.");
            if (patchText.Contains("old mode ", StringComparison.Ordinal)
                || patchText.Contains("new mode ", StringComparison.Ordinal)
                || patchText.Contains("new file mode 120000", StringComparison.Ordinal)
                || patchText.Contains("deleted file mode 120000", StringComparison.Ordinal)
                || patchText.Contains("rename from ", StringComparison.Ordinal)
                || patchText.Contains("rename to ", StringComparison.Ordinal)
                || patchText.Contains("copy from ", StringComparison.Ordinal)
                || patchText.Contains("copy to ", StringComparison.Ordinal))
                throw new ArgumentException("RepoPatch cannot change file modes, create symlinks, rename, or copy files.");

            var filesElement = root.GetProperty("files");
            if (filesElement.ValueKind != JsonValueKind.Array
                || filesElement.GetArrayLength() is < 1 or > MaxFiles)
                throw new ArgumentException($"RepoPatch must contain between 1 and {MaxFiles} files.");

            var files = new List<RepoPatchFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in filesElement.EnumerateArray())
            {
                Exact(item, "gitPath", "projectPath", "baseHash", "resultHash");
                var gitPath = RequiredString(item, "gitPath", 512);
                var projectPath = RequiredString(item, "projectPath", 512);
                ValidateRelativePath(gitPath);
                ValidateRelativePath(projectPath);
                if (!seen.Add(projectPath))
                    throw new ArgumentException($"RepoPatch contains duplicate path '{projectPath}'.");
                if (!BuiltInAgentProfiles.IsWithinDefaults(projectPath, profileDefaultPaths)
                    || !BuiltInAgentProfiles.IsWithinDefaults(projectPath, allowedPaths))
                    throw new ArgumentException($"Path '{projectPath}' is outside the docs-writer profile or subtask path grant.");
                if (!GitPathEndsWithProjectPath(gitPath, projectPath))
                    throw new ArgumentException($"Git path '{gitPath}' does not map to project path '{projectPath}'.");

                var baseHash = NullableHash(item.GetProperty("baseHash"), "baseHash");
                var resultHash = NullableHash(item.GetProperty("resultHash"), "resultHash");
                if (baseHash is null && resultHash is null)
                    throw new ArgumentException($"RepoPatch file '{projectPath}' cannot have both hashes null.");
                files.Add(new RepoPatchFile(gitPath, projectPath, baseHash, resultHash));
            }

            files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.ProjectPath, right.ProjectPath));
            ValidatePatchFileHeaders(patchText, files);

            var baseManifest = string.Join("\n", files.Select(file =>
                $"{file.ProjectPath}\0{file.BaseHash ?? "<new>"}\0{file.ResultHash ?? "<deleted>"}"));
            var baseHashManifest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseManifest))).ToLowerInvariant();
            var normalized = JsonSerializer.Serialize(new
            {
                patch = patchText,
                files = files.Select(file => new
                {
                    gitPath = file.GitPath,
                    projectPath = file.ProjectPath,
                    baseHash = file.BaseHash,
                    resultHash = file.ResultHash
                })
            });
            patch = new ValidatedRepoPatch(patchText, files.AsReadOnly(), baseHashManifest, normalized);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException)
        {
            error = exception.Message;
            return false;
        }
    }

    public static bool TryRead(string? payloadJson, out string? patchText, out IReadOnlyList<RepoPatchFile> files)
    {
        patchText = null;
        files = [];
        try
        {
            using var document = JsonDocument.Parse(payloadJson ?? string.Empty);
            var root = document.RootElement;
            Exact(root, "patch", "files");
            patchText = RequiredString(root, "patch", MaxPatchLength);
            var parsed = new List<RepoPatchFile>();
            foreach (var item in root.GetProperty("files").EnumerateArray())
            {
                Exact(item, "gitPath", "projectPath", "baseHash", "resultHash");
                parsed.Add(new RepoPatchFile(
                    RequiredString(item, "gitPath", 512),
                    RequiredString(item, "projectPath", 512),
                    NullableHash(item.GetProperty("baseHash"), "baseHash"),
                    NullableHash(item.GetProperty("resultHash"), "resultHash")));
            }
            files = parsed.AsReadOnly();
            return parsed.Count is > 0 and <= MaxFiles;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void ValidatePatchFileHeaders(string patch, IReadOnlyList<RepoPatchFile> files)
    {
        var headers = new List<(string Git, string Project)>();
        foreach (var line in patch.Split('\n'))
        {
            if (!line.StartsWith("diff --git a/", StringComparison.Ordinal))
                continue;
            var separator = line.IndexOf(" b/", "diff --git a/".Length, StringComparison.Ordinal);
            if (separator < 0)
                throw new ArgumentException("RepoPatch contains a malformed diff header.");
            var gitPath = line["diff --git a/".Length..separator];
            var rightPath = line[(separator + 3)..];
            ValidateRelativePath(gitPath);
            ValidateRelativePath(rightPath);
            if (!string.Equals(gitPath, rightPath, StringComparison.Ordinal))
                throw new ArgumentException("RepoPatch cannot rename files.");
            headers.Add((gitPath, gitPath));
        }

        if (headers.Count != files.Count)
            throw new ArgumentException("RepoPatch file manifest does not match the patch file sections.");

        foreach (var file in files)
        {
            if (headers.Count(header => string.Equals(header.Git, file.GitPath, StringComparison.Ordinal)) != 1)
                throw new ArgumentException($"RepoPatch patch section for '{file.GitPath}' is missing or duplicated.");
        }

        var hasPathTraversal = patch.Split('\n').Any(line =>
            (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
            && line.Contains("/../", StringComparison.Ordinal));
        if (hasPathTraversal)
            throw new ArgumentException("RepoPatch contains a parent path segment.");
    }

    private static bool GitPathEndsWithProjectPath(string gitPath, string projectPath) =>
        string.Equals(gitPath, projectPath, StringComparison.OrdinalIgnoreCase)
        || gitPath.EndsWith("/" + projectPath, StringComparison.OrdinalIgnoreCase);

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains('\\') || path.Contains(':') || path.Contains('\0')
            || Path.IsPathRooted(path))
            throw new ArgumentException("RepoPatch paths must be safe relative paths.");
        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or "..")
            || segments.Any(segment => string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("RepoPatch paths cannot contain empty, dot, parent, or .git segments.");
        if (segments.Any(segment => segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal)))
            throw new ArgumentException("RepoPatch path segments cannot end with a dot or space.");
    }

    private static string? NullableHash(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"'{name}' must be a SHA-256 hexadecimal string or null.");
        var hash = value.GetString();
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new ArgumentException($"'{name}' must be a SHA-256 hexadecimal string or null.");
        return hash.ToLowerInvariant();
    }

    private static string RequiredString(JsonElement root, string name, int maximum)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"'{name}' must be a string.");
        var result = value.GetString()!;
        if (string.IsNullOrWhiteSpace(result) || result.Length > maximum)
            throw new ArgumentException($"'{name}' must contain 1-{maximum} characters.");
        return result;
    }

    private static void Exact(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("RepoPatch payload entries must be JSON objects.");
        var actual = root.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (actual.Count != names.Length || names.Any(name => !actual.Contains(name)))
            throw new ArgumentException("RepoPatch payload contains missing or unsupported fields.");
    }
}
