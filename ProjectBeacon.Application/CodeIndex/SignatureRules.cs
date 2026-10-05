namespace ProjectBeacon.Application.CodeIndex;

public sealed record NodeRule(string NodeType, string Kind);

public sealed record SignatureRules(IReadOnlyList<NodeRule> Nodes, string NameField)
{
    public bool Matches(string nodeType) => Nodes.Any(n => n.NodeType == nodeType);

    public string? KindFor(string nodeType) => Nodes.FirstOrDefault(n => n.NodeType == nodeType)?.Kind;
}
