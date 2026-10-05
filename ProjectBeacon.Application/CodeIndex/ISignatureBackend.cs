namespace ProjectBeacon.Application.CodeIndex;

public interface ISignatureBackend
{
    SignatureBackend Kind { get; }
    string Id { get; }
    FileSignatures Extract(LanguageDefinition language, string relativePath, string content);
}
