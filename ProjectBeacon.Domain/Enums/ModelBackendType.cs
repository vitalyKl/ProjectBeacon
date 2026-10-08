namespace ProjectBeacon.Domain.Enums;

/// <summary>How a user-owned local model backend is launched.</summary>
    public enum ModelBackendType
{
    FreeToken,
    LlamaCpp,
    OpenAiCompatible
}
