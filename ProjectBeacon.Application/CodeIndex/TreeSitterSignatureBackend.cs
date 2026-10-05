namespace ProjectBeacon.Application.CodeIndex;

using TreeSitter;

public sealed class TreeSitterSignatureBackend : ISignatureBackend
{
    private const int MaxSignatureLength = 200;

    private static readonly Dictionary<string, Language> _languageCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _languageLock = new();

    public SignatureBackend Kind => SignatureBackend.TreeSitter;
    public string Id => "tree-sitter";

    public FileSignatures Extract(LanguageDefinition language, string relativePath, string content)
    {
        var rules = language.SignatureRules;
        if (rules is null)
            return new FileSignatures(relativePath, [], Id, "no signature rules defined");

        var symbols = new List<SymbolSignature>();
        try
        {
            var lang = GetLanguage(language.Grammar!);
            using var parser = new Parser(lang);
            using var tree = parser.Parse(content);
            if (tree is not null)
                Walk(tree.RootNode, rules, symbols);
        }
        catch (Exception ex)
        {
            return new FileSignatures(relativePath, [], Id, ex.Message);
        }
        return new FileSignatures(relativePath, symbols, Id, null);
    }

    private static Language GetLanguage(string grammar)
    {
        lock (_languageLock)
        {
            if (_languageCache.TryGetValue(grammar, out var cached))
                return cached;
            var language = new Language(grammar);
            _languageCache[grammar] = language;
            return language;
        }
    }

    private static void Walk(Node node, SignatureRules rules, List<SymbolSignature> symbols)
    {
        if (node.IsNamed && rules.Matches(node.Type))
        {
            var nameNode = node.GetChildForField(rules.NameField);
            var name = nameNode?.Text;
            if (string.IsNullOrEmpty(name))
                name = "<anonymous>";
            var kind = rules.KindFor(node.Type) ?? "symbol";
            var signature = SignatureText(node.Text);
            symbols.Add(new SymbolSignature(name, kind, signature, node.StartPosition.Row + 1, null));
        }

        foreach (var child in node.NamedChildren)
            Walk(child, rules, symbols);
    }

    private static string SignatureText(string text)
    {
        var brace = text.IndexOf('{');
        if (brace >= 0)
            text = text[..brace];
        text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (text.Contains("  "))
            text = text.Replace("  ", " ");
        if (text.Length > MaxSignatureLength)
            text = text[..MaxSignatureLength] + "…";
        return text;
    }
}
