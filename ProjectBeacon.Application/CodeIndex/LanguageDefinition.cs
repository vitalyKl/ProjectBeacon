namespace ProjectBeacon.Application.CodeIndex;

public sealed record LanguageDefinition(
    string Id,
    string DisplayName,
    string[] Extensions,
    SignatureBackend Backend,
    string? Grammar,
    SignatureRules? SignatureRules);
