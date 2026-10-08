namespace ProjectBeacon.Application.CodeIndex;
/// <summary>
/// Extracts symbol signatures from one source file.
/// </summary>
public interface ISignatureBackend
{
    SignatureBackend Kind { get; }
    string Id { get; }
    FileSignatures Extract(LanguageDefinition language, string relativePath, string content);
}
