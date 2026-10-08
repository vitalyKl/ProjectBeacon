namespace ProjectBeacon.Application.CodeIndex;
/// <summary>
/// Tree-sitter node type that counts as a symbol of the given kind.
/// </summary>
public sealed record NodeRule(string NodeType, string Kind);
/// <summary>
/// Node rules and the field that holds the symbol name.
/// </summary>
public sealed record SignatureRules(IReadOnlyList<NodeRule> Nodes, string NameField)
{
    public bool Matches(string nodeType) => Nodes.Any(n => n.NodeType == nodeType);

    public string? KindFor(string nodeType) => Nodes.FirstOrDefault(n => n.NodeType == nodeType)?.Kind;
}
