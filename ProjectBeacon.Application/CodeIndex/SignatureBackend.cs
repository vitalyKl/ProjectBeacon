namespace ProjectBeacon.Application.CodeIndex;
/// <summary>
/// Which extractor produced a signature: Roslyn or Tree-sitter.
/// </summary>
public enum SignatureBackend
{
    Roslyn,
    TreeSitter
}
