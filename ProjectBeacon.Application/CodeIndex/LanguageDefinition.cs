namespace ProjectBeacon.Application.CodeIndex;
/// <summary>
/// How one language is recognized and which signature backend parses it.
/// </summary>
public sealed record LanguageDefinition(
    string Id,
    string DisplayName,
    string[] Extensions,
    SignatureBackend Backend,
    string? Grammar,
    SignatureRules? SignatureRules);
