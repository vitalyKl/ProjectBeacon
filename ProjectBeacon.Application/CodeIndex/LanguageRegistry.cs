namespace ProjectBeacon.Application.CodeIndex;

public sealed class LanguageRegistry : ILanguageRegistry
{
    private readonly Dictionary<string, LanguageDefinition> _byExtension;

    public LanguageRegistry(IEnumerable<LanguageDefinition> languages)
    {
        _byExtension = new Dictionary<string, LanguageDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in languages)
            foreach (var ext in language.Extensions)
            {
                var key = Normalize(ext);
                if (_byExtension.ContainsKey(key))
                    throw new ArgumentException($"Duplicate extension '{key}' (already registered for '{_byExtension[key].Id}').");
                _byExtension[key] = language;
            }
        Languages = languages.Distinct().ToArray();
    }

    public IReadOnlyCollection<LanguageDefinition> Languages { get; }

    public LanguageDefinition? Resolve(string relativePath)
    {
        var ext = Path.GetExtension(relativePath);
        if (string.IsNullOrEmpty(ext))
            return null;
        return _byExtension.TryGetValue(Normalize(ext), out var def) ? def : null;
    }

    public static ILanguageRegistry CreateDefault() => new LanguageRegistry(new[]
    {
        new LanguageDefinition(
            "csharp", "C#", [".cs"],
            SignatureBackend.Roslyn,
            Grammar: null,
            SignatureRules: null),
        new LanguageDefinition(
            "typescript", "TypeScript", [".ts", ".tsx"],
            SignatureBackend.TreeSitter,
            Grammar: "typescript",
            SignatureRules: new SignatureRules(
            [
                new NodeRule("class_declaration", "class"),
                new NodeRule("interface_declaration", "interface"),
                new NodeRule("type_alias_declaration", "type"),
                new NodeRule("function_declaration", "function"),
                new NodeRule("method_definition", "method"),
            ],
            NameField: "name")),
        new LanguageDefinition(
            "python", "Python", [".py"],
            SignatureBackend.TreeSitter,
            Grammar: "python",
            SignatureRules: new SignatureRules(
            [
                new NodeRule("class_definition", "class"),
                new NodeRule("function_definition", "function"),
            ],
            NameField: "name")),
    });

    private static string Normalize(string ext)
        => ext.StartsWith('.') ? ext.ToLowerInvariant() : "." + ext.ToLowerInvariant();
}
