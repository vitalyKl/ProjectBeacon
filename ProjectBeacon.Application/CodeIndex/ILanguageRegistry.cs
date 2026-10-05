namespace ProjectBeacon.Application.CodeIndex;

public interface ILanguageRegistry
{
    IReadOnlyCollection<LanguageDefinition> Languages { get; }
    LanguageDefinition? Resolve(string relativePath);
}
