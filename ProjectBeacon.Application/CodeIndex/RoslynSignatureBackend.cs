namespace ProjectBeacon.Application.CodeIndex;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
/// <summary>
/// Extracts C# signatures with Roslyn. Signature text is capped at 200 characters.
/// </summary>
public sealed class RoslynSignatureBackend : ISignatureBackend
{
    private const int MaxSignatureLength = 200;

    public SignatureBackend Kind => SignatureBackend.Roslyn;
    public string Id => "roslyn";

    public FileSignatures Extract(LanguageDefinition language, string relativePath, string content)
    {
        var symbols = new List<SymbolSignature>();
        try
        {
            var tree = CSharpSyntaxTree.ParseText(content);
            Walk(tree.GetRoot());
        }
        catch (Exception ex)
        {
            return new FileSignatures(relativePath, [], Id, ex.Message);
        }
        return new FileSignatures(relativePath, symbols, Id, null);

        void Walk(SyntaxNode node)
        {
            if (node is TypeDeclarationSyntax typeDecl)
            {
                var kind = typeDecl switch
                {
                    ClassDeclarationSyntax => "class",
                    InterfaceDeclarationSyntax => "interface",
                    StructDeclarationSyntax => "struct",
                    RecordDeclarationSyntax => "record",
                    _ => "type"
                };
                symbols.Add(new SymbolSignature(
                    typeDecl.Identifier.Text,
                    kind,
                    DeclarationText(node),
                    typeDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    null));
            }
            else if (node is EnumDeclarationSyntax enumDecl)
            {
                symbols.Add(new SymbolSignature(
                    enumDecl.Identifier.Text,
                    "enum",
                    DeclarationText(node),
                    enumDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    null));
            }
            else if (node is MethodDeclarationSyntax method)
            {
                symbols.Add(new SymbolSignature(
                    method.Identifier.Text,
                    "method",
                    DeclarationText(node),
                    method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    null));
            }
            else if (node is ConstructorDeclarationSyntax ctor)
            {
                symbols.Add(new SymbolSignature(
                    ctor.Identifier.Text,
                    "constructor",
                    DeclarationText(node),
                    ctor.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    null));
            }

            foreach (var child in node.ChildNodes())
                Walk(child);
        }
    }

    private static string DeclarationText(SyntaxNode node)
    {
        var text = node.ToString();
        var brace = text.IndexOf('{');
        var semicolon = text.IndexOf(';');
        var end = -1;
        if (brace >= 0 && (semicolon < 0 || brace < semicolon))
            end = brace;
        else if (semicolon >= 0)
            end = semicolon;

        if (end > 0)
            text = text[..end];
        text = text.Trim();
        if (text.Length > MaxSignatureLength)
            text = text[..MaxSignatureLength] + "…";
        return text;
    }
}
