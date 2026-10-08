namespace ProjectBeacon.Application.CodeIndex;
/// <summary>
/// Maps a relative path to a language definition by extension.
/// </summary>
public interface ILanguageRegistry
{
    IReadOnlyCollection<LanguageDefinition> Languages { get; }
    LanguageDefinition? Resolve(string relativePath);
}
