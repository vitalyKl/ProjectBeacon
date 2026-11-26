namespace ProjectBeacon.Domain.Entities.Projects;

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Tomlyn;
using Tomlyn.Model;

/// <summary>Known-safe default. No version source is guessed for a new customer project.</summary>
public static class ProjectVersionPolicyDefaults
{
    public const string Json = """{"scheme":"SemVer","componentCount":3,"calendarFormat":null,"tagPattern":"v{version}","allowPrerelease":false,"releaseManagementEnabled":false,"deploymentVersionEndpoint":null,"sources":[]}""";
}

/// <summary>File projection source configured by a project Owner/Admin.</summary>
public sealed record ProjectVersionSourcePolicy(string Path, string Format, string Selector);

/// <summary>Policy schema used to validate the authoritative project version and repository projections.</summary>
public sealed record ProjectVersionPolicy(
    string Scheme,
    int ComponentCount,
    string? CalendarFormat,
    string TagPattern,
    bool AllowPrerelease,
    IReadOnlyList<ProjectVersionSourcePolicy> Sources,
    bool ReleaseManagementEnabled = false,
    string? DeploymentVersionEndpoint = null);

/// <summary>Deterministic parsing/validation for version policies and source files.</summary>
public static class ProjectVersionPolicyCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex NumericPattern = new(@"^[0-9]+(?:\.[0-9]+)*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex XmlLocationPathPattern = new(
        @"^(?:/[A-Za-z_][A-Za-z0-9_.-]*(?:/[A-Za-z_][A-Za-z0-9_.-]*)*|//[A-Za-z_][A-Za-z0-9_.-]*(?:/[A-Za-z_][A-Za-z0-9_.-]*)*)$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex SemVerPattern = new(
        @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool TryRead(string? json, out ProjectVersionPolicy policy, out string? error)
    {
        policy = new ProjectVersionPolicy("SemVer", 3, null, "v{version}", false, []);
        error = null;
        if (string.IsNullOrWhiteSpace(json))
            json = ProjectVersionPolicyDefaults.Json;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Version policy must be a JSON object.";
                return false;
            }
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "scheme", "componentCount", "calendarFormat", "tagPattern", "allowPrerelease", "releaseManagementEnabled", "deploymentVersionEndpoint", "sources"
            };
            var names = document.RootElement.EnumerateObject().Select(item => item.Name).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length
                || names.Any(name => !allowed.Contains(name)))
            {
                error = "Version policy contains duplicate or unsupported properties.";
                return false;
            }
            policy = JsonSerializer.Deserialize<ProjectVersionPolicy>(json, JsonOptions)
                ?? throw new JsonException("Version policy is empty.");
            return Validate(policy, out error);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or RegexMatchTimeoutException)
        {
            error = "Version policy JSON is invalid: " + ex.Message;
            return false;
        }
    }

    public static string Serialize(ProjectVersionPolicy policy) =>
        JsonSerializer.Serialize(policy, JsonOptions);

    public static bool TryValidateDeploymentVersionEndpoint(string? value, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;
        var normalized = value.Trim();
        if (normalized.Length > 512
            || !Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.AbsolutePath.EndsWith("/v1/version", StringComparison.Ordinal))
        {
            error = "Deployment version endpoint must be an HTTPS URL ending in /v1/version, without credentials, query, or fragment.";
            return false;
        }
        return true;
    }

    public static bool IsSafeGitTagName(string? value)
    {
        if (value is not { Length: > 0 and <= 200 }
            || value.StartsWith("-", StringComparison.Ordinal)
            || value.StartsWith("/", StringComparison.Ordinal)
            || value.EndsWith("/", StringComparison.Ordinal)
            || value.Contains("//", StringComparison.Ordinal)
            || value.Contains("..", StringComparison.Ordinal)
            || value.Contains("@{", StringComparison.Ordinal)
            || !value.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '_' or '-' or '/'))
            return false;

        return value.Split('/').All(segment =>
            segment.Length > 0
            && !segment.StartsWith(".", StringComparison.Ordinal)
            && !segment.EndsWith(".", StringComparison.Ordinal)
            && !segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSafeBranchName(string? value)
    {
        if (value is not { Length: > 0 and <= 200 }
            || value.StartsWith("/", StringComparison.Ordinal)
            || value.EndsWith("/", StringComparison.Ordinal)
            || value.Contains("//", StringComparison.Ordinal)
            || value.Contains("..", StringComparison.Ordinal)
            || value.Contains("@{", StringComparison.Ordinal)
            || !value.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '_' or '-' or '/'))
            return false;

        return value.Split('/').All(segment =>
            segment.Length > 0
            && !segment.StartsWith(".", StringComparison.Ordinal)
            && !segment.EndsWith(".", StringComparison.Ordinal)
            && !segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));
    }

    public static bool Validate(ProjectVersionPolicy? policy, out string? error)
    {
        error = null;
        if (policy is null)
        {
            error = "Version policy is required.";
            return false;
        }
        if (policy.Scheme is not ("NumericComponents" or "SemVer" or "Calendar"))
        {
            error = "Version scheme must be NumericComponents, SemVer, or Calendar.";
            return false;
        }
        if (policy.Scheme == "NumericComponents" && policy.ComponentCount is < 1 or > 8)
        {
            error = "Numeric version component count must be between 1 and 8.";
            return false;
        }
        if (policy.Scheme == "SemVer" && policy.ComponentCount != 3)
        {
            error = "SemVer always has three numeric core components.";
            return false;
        }
        if (policy.Scheme == "Calendar")
        {
            if (string.IsNullOrWhiteSpace(policy.CalendarFormat) || policy.CalendarFormat.Length > 40)
            {
                error = "Calendar scheme requires a .NET DateTime format string (max 40 characters).";
                return false;
            }
            if (!IsSupportedCalendarFormat(policy.CalendarFormat))
            {
                error = "Calendar format must contain yyyy, MM and dd once, with optional HH, mm and ss tokens and literal separators supported by the release workflow.";
                return false;
            }
            try
            {
                var probe = new DateTime(2026, 11, 24, 13, 14, 15, DateTimeKind.Utc);
                var formatted = probe.ToString(policy.CalendarFormat, CultureInfo.InvariantCulture);
                _ = DateTime.ParseExact(formatted, policy.CalendarFormat, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                error = "Calendar format is not a supported invariant-culture date format.";
                return false;
            }
        }
        if (!IsSafeTagPattern(policy.TagPattern))
        {
            error = "Tag pattern must contain exactly one {version} placeholder and produce a safe Git tag name.";
            return false;
        }
        if (!TryValidateDeploymentVersionEndpoint(policy.DeploymentVersionEndpoint, out error))
            return false;
        if (policy.Sources is null || policy.Sources.Count > 16)
        {
            error = "A version policy may define at most 16 file sources.";
            return false;
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in policy.Sources)
        {
            if (source is null || !IsSafeRelativePath(source.Path) || !paths.Add(source.Path))
            {
                error = "Version source paths must be unique, relative, and stay inside the project workspace.";
                return false;
            }
            if (source.Selector is null || source.Selector.Length is < 1 or > 500 || source.Selector.Any(char.IsControl))
            {
                error = $"Version source '{source.Path}' has an invalid selector.";
                return false;
            }
            switch (source.Format)
            {
                case "Xml":
                    if (!XmlLocationPathPattern.IsMatch(source.Selector))
                    {
                        error = $"XML selector for '{source.Path}' must be an absolute or descendant element path (for example '/Project/PropertyGroup/Version' or '//Version'); predicates, attributes, axes, functions, wildcards, and namespace prefixes are not supported by the release workflow.";
                        return false;
                    }
                    try { _ = XPathExpression.Compile(source.Selector); }
                    catch (XPathException)
                    {
                        error = $"XML selector for '{source.Path}' is invalid XPath.";
                        return false;
                    }
                    break;
                case "Json":
                    if (!IsValidJsonPointer(source.Selector))
                    {
                        error = $"JSON selector for '{source.Path}' must be a valid RFC 6901 JSON Pointer with only '~0' and '~1' escape sequences.";
                        return false;
                    }
                    break;
                case "Toml":
                    if (!source.Selector.Split('.').All(part =>
                        part.Length > 0 && part.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
                    {
                        error = $"TOML selector for '{source.Path}' must be a dotted key path.";
                        return false;
                    }
                    break;
                case "Regex":
                    if (!TryValidatePortableRegex(source.Selector, out error))
                    {
                        error = $"Regex selector for '{source.Path}' is not portable to the release workflow. {error}";
                        return false;
                    }
                    break;
                default:
                    error = $"Version source '{source.Path}' format must be Xml, Json, Toml, or Regex.";
                    return false;
            }
        }
        return true;
    }

    public static bool TryNormalizeVersion(ProjectVersionPolicy policy, string? value, out string normalized, out string? error)
    {
        normalized = value?.Trim() ?? string.Empty;
        error = null;
        if (normalized.Length is 0 or > 100 || normalized.Any(char.IsWhiteSpace))
        {
            error = "Version value must contain 1-100 non-whitespace characters.";
            return false;
        }

        switch (policy.Scheme)
        {
            case "NumericComponents":
                if (!NumericPattern.IsMatch(normalized)
                    || normalized.Split('.').Length != policy.ComponentCount)
                {
                    error = $"Version must contain exactly {policy.ComponentCount} numeric components.";
                    return false;
                }
                break;
            case "SemVer":
                var match = SemVerPattern.Match(normalized);
                if (!match.Success || (!policy.AllowPrerelease && match.Groups[4].Success))
                {
                    error = policy.AllowPrerelease
                        ? "Version must be valid SemVer 2.0.0."
                        : "Version must be SemVer 2.0.0 without a prerelease suffix.";
                    return false;
                }
                break;
            case "Calendar":
                if (!DateTime.TryParseExact(normalized, policy.CalendarFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    error = $"Version does not match calendar format '{policy.CalendarFormat}'.";
                    return false;
                }
                break;
            default:
                error = "Unsupported version scheme.";
                return false;
        }
        return true;
    }

    public static bool TryExtractVersion(
        ProjectVersionPolicy policy,
        ProjectVersionSourcePolicy source,
        string content,
        out string version,
        out string? error)
    {
        version = string.Empty;
        error = null;
        try
        {
            string? extracted = source.Format switch
            {
                "Xml" => ExtractXml(source.Selector, content),
                "Json" => ExtractJsonPointer(source.Selector, content),
                "Toml" => ExtractToml(source.Selector, content),
                "Regex" => ExtractRegex(source.Selector, content),
                _ => null
            };
            if (!TryNormalizeVersion(policy, extracted, out version, out error))
            {
                error ??= $"Version source '{source.Path}' did not yield a valid version.";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is XmlException or JsonException or XPathException or RegexMatchTimeoutException or FormatException or TomlException)
        {
            error = $"Could not read version from '{source.Path}': {ex.Message}";
            return false;
        }
    }

    public static string FormatTag(ProjectVersionPolicy policy, string version) =>
        policy.TagPattern.Replace("{version}", version, StringComparison.Ordinal);

    private static bool TryValidatePortableRegex(string pattern, out string? error)
    {
        error = null;
        var captureGroups = 0;
        var hasNamedVersionGroup = false;
        var inCharacterClass = false;

        for (var index = 0; index < pattern.Length;)
        {
            var character = pattern[index];
            if ((int)character == 92)
            {
                index++;
                if (index >= pattern.Length)
                {
                    error = "A trailing escape is not supported.";
                    return false;
                }

                var escaped = pattern[index];
                if (escaped == 'k')
                {
                    const string marker = "k<version>";
                    if (!hasNamedVersionGroup)
                    {
                        error = "A version backreference must follow its named capture group.";
                        return false;
                    }
                    if (inCharacterClass)
                    {
                        error = "Named backreferences inside a character class are not supported.";
                        return false;
                    }
                    if (index + marker.Length > pattern.Length)
                    {
                        error = "Only the version named backreference is supported.";
                        return false;
                    }
                    if (!string.Equals(pattern.Substring(index, marker.Length), marker, StringComparison.Ordinal))
                    {
                        error = "Only the version named backreference is supported.";
                        return false;
                    }
                    index += marker.Length;
                    continue;
                }

                var portableEscape = escaped is 'd' or 'D' or 's' or 'S' or 't' or 'r' or 'n' or 'f' or 'v' or ']' or '-' or '^';
                if ((int)escaped == 92)
                    portableEscape = true;
                if (!inCharacterClass)
                {
                    if (escaped is 'A' or 'z' or '.' or '$' or '?' or '*' or '+' or '(' or ')' or '[' or '{' or '}')
                        portableEscape = true;
                    if ((int)escaped == 124)
                        portableEscape = true;
                }
                if (!portableEscape)
                {
                    error = "The Regex selector contains an escape unsupported by the release workflow.";
                    return false;
                }
                index++;
                continue;
            }

            if (inCharacterClass)
            {
                if (character == '[')
                {
                    error = "Nested and subtractive character classes are not supported.";
                    return false;
                }
                if (character == ']')
                    inCharacterClass = false;
                index++;
                continue;
            }

            if (character == '[')
            {
                inCharacterClass = true;
                index++;
                continue;
            }
            if (character != '(')
            {
                index++;
                continue;
            }

            const string namedGroup = "(?<version>";
            if (pattern.AsSpan(index).StartsWith(namedGroup, StringComparison.Ordinal))
            {
                captureGroups++;
                hasNamedVersionGroup = true;
                index += namedGroup.Length;
                continue;
            }
            if (pattern.AsSpan(index).StartsWith("(?:", StringComparison.Ordinal))
            {
                index += 3;
                continue;
            }
            if (pattern.AsSpan(index).StartsWith("(?=", StringComparison.Ordinal))
            {
                index += 3;
                continue;
            }
            if (pattern.AsSpan(index).StartsWith("(?!", StringComparison.Ordinal))
            {
                index += 3;
                continue;
            }
            if (pattern.AsSpan(index).StartsWith("(?", StringComparison.Ordinal))
            {
                error = "Only version captures, non-capturing groups and look-ahead groups are supported.";
                return false;
            }

            captureGroups++;
            index++;
        }

        if (inCharacterClass)
        {
            error = "Character class is not closed.";
            return false;
        }
        if (captureGroups != 1)
        {
            error = "A portable version selector must contain exactly one capture group.";
            return false;
        }
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException)
        {
            error = "Regex selector is invalid.";
            return false;
        }
    }

    private static bool IsSupportedCalendarFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < format.Length;)
        {
            var character = format[index];
            var token = character switch
            {
                'y' => "yyyy",
                'M' => "MM",
                'd' => "dd",
                'H' => "HH",
                'm' => "mm",
                's' => "ss",
                _ => null
            };
            if (token is null)
            {
                if (character is not ('.' or '-' or '/' or '_' or ' ' or ':' or 'T' or 'Z'))
                    return false;
                index++;
                continue;
            }

            if (index + token.Length > format.Length
                || !string.Equals(format.Substring(index, token.Length), token, StringComparison.Ordinal)
                || (index + token.Length < format.Length && format[index + token.Length] == character)
                || !seen.Add(token))
                return false;
            index += token.Length;
        }

        return seen.Contains("yyyy") && seen.Contains("MM") && seen.Contains("dd");
    }

    private static bool IsSafeTagPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 100
            || pattern.IndexOf("{version}", StringComparison.Ordinal) < 0
            || pattern.IndexOf("{version}", StringComparison.Ordinal) != pattern.LastIndexOf("{version}", StringComparison.Ordinal))
            return false;
        var sample = pattern.Replace("{version}", "1.2.3", StringComparison.Ordinal);
        if (sample.Length is 0 or > 200
            || sample.StartsWith("/", StringComparison.Ordinal)
            || sample.EndsWith("/", StringComparison.Ordinal)
            || sample.Contains("..", StringComparison.Ordinal)
            || !sample.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '/'))
            return false;

        // Git ref components cannot begin with '.', end with '.', or end with '.lock'.
        // Empty components are also invalid (for example, a doubled or trailing slash).
        return sample.Split('/').All(segment =>
            segment.Length > 0
            && !segment.StartsWith(".", StringComparison.Ordinal)
            && !segment.EndsWith(".", StringComparison.Ordinal)
            && !segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 500
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl))
            return false;
        if (!path.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '/'))
            return false;
        var segments = path.Split('/');
        return segments.All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }

    private static bool IsValidJsonPointer(string selector)
    {
        if (!selector.StartsWith("/", StringComparison.Ordinal))
            return false;
        return selector.Split('/').Skip(1).All(part => TryUnescapeJsonPointerPart(part, out _));
    }

    private static bool TryUnescapeJsonPointerPart(string rawPart, out string decodedPart)
    {
        decodedPart = string.Empty;
        for (var index = 0; index < rawPart.Length; index++)
        {
            if (rawPart[index] != '~')
                continue;
            if (index + 1 >= rawPart.Length || rawPart[index + 1] is not ('0' or '1'))
                return false;
            index++;
        }
        decodedPart = rawPart.Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal);
        return true;
    }
    private static string? ExtractXml(string selector, string content)
    {
        using var stringReader = new StringReader(content);
        using var reader = XmlReader.Create(stringReader, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 1_000_000
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        return document.XPathSelectElement(selector)?.Value?.Trim();
    }

    private static string? ExtractJsonPointer(string selector, string content)
    {
        using var document = JsonDocument.Parse(content);
        var current = document.RootElement;
        foreach (var part in selector.Split('/').Skip(1))
        {
            if (!TryUnescapeJsonPointerPart(part, out var key))
                return null;
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(key, out current))
                    return null;
                continue;
            }
            if (current.ValueKind == JsonValueKind.Array
                && key.Length > 0
                && (key.Length == 1 || key[0] != '0')
                && key.All(character => character is >= '0' and <= '9')
                && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index >= 0 && index < current.GetArrayLength())
            {
                current = current[index];
                continue;
            }
            return null;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString()?.Trim() : current.ToString();
    }
    private static string? ExtractToml(string selector, string content)
    {
        // Match Python tomllib's TOML 1.0 parser in the tag-triggered release gate.
        // A real parser rejects malformed documents and duplicate keys before Candidate preparation.
        var document = Toml.ToModel(content);
        object? current = document;
        foreach (var part in selector.Split('.'))
        {
            if (current is not TomlTable table || !table.TryGetValue(part, out var next))
                return null;
            current = next;
        }

        return current is string or sbyte or byte or short or ushort or int or uint or long or ulong
            ? Convert.ToString(current, CultureInfo.InvariantCulture)?.Trim()
            : null;
    }

    private static string? ExtractRegex(string selector, string content)
    {
        var match = Regex.Match(content, selector, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return !match.Success ? null
            : match.Groups["version"]?.Success == true ? match.Groups["version"].Value
            : match.Groups[1].Value;
    }
}
