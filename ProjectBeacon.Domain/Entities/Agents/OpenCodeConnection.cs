namespace ProjectBeacon.Domain.Entities.Agents;

using ProjectBeacon.Domain.Common;

public class OpenCodeConnection : Entity
{
    public OpenCodeConnection() { }

    public Guid UserId { get; private set; }
    public string ProviderId { get; private set; } = string.Empty;
    public string ModelId { get; private set; } = string.Empty;
    public string BaseUrl { get; private set; } = string.Empty;
    public string ApiKeyCipher { get; private set; } = string.Empty;
    public DateTime UpdatedAt { get; private set; }

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

    public void Update(string modelId, string? baseUrl, string? apiKeyCipher)
    {
        ModelId = modelId.Trim();
        BaseUrl = baseUrl?.Trim() ?? string.Empty;
        if (apiKeyCipher is not null)
            ApiKeyCipher = apiKeyCipher;
        UpdatedAt = DateTime.UtcNow;
    }
}
