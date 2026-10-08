namespace ProjectBeacon.Domain.Entities.Agents;

using ProjectBeacon.Domain.Common;

/// <summary>
/// A user's OpenCode provider connection. User-owned, not tenant-scoped.
/// <see cref="ApiKeyCipher"/> is stored as supplied; this type does not hash or encrypt it.
/// </summary>
public class OpenCodeConnection : Entity
{
    public OpenCodeConnection() { }

    public Guid UserId { get; private set; }
    public string ProviderId { get; private set; } = string.Empty;
    public string ModelId { get; private set; } = string.Empty;
    public string BaseUrl { get; private set; } = string.Empty;
    public string ApiKeyCipher { get; private set; } = string.Empty;
    public DateTime UpdatedAt { get; private set; }

    /// <summary>
    /// Creates a connection for <paramref name="userId"/>. <paramref name="providerId"/> is trimmed and lowercased; a null cipher is stored as empty.
    /// </summary>
    public static OpenCodeConnection Create(Guid userId, string providerId, string modelId, string? baseUrl, string? apiKeyCipher)
    {
        var row = Entity.New<OpenCodeConnection>();
        row.UserId = userId;
        row.ProviderId = providerId.Trim().ToLowerInvariant();
        row.ModelId = modelId.Trim();
        row.BaseUrl = baseUrl?.Trim() ?? string.Empty;
        row.ApiKeyCipher = apiKeyCipher ?? string.Empty;
        row.UpdatedAt = DateTime.UtcNow;
        return row;
    }

    /// <summary>
    /// Replaces the model and base URL. A null <paramref name="apiKeyCipher"/> leaves the stored cipher unchanged.
    /// </summary>
    public void Update(string modelId, string? baseUrl, string? apiKeyCipher)
    {
        ModelId = modelId.Trim();
        BaseUrl = baseUrl?.Trim() ?? string.Empty;
        if (apiKeyCipher is not null)
            ApiKeyCipher = apiKeyCipher;
        UpdatedAt = DateTime.UtcNow;
    }
}
