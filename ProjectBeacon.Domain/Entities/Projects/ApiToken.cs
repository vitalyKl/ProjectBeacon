namespace ProjectBeacon.Domain.Entities.Projects;

using Common;
using Enums;

public class ApiToken : Entity, IProjectScoped
{
    public ApiToken() { }

    public string Name { get; private set; } = string.Empty;
    public string TokenHash { get; private set; } = string.Empty;
    public string? TokenPrefix { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid? CreatedByUserId { get; private set; }
    public ApiTokenCapability Capabilities { get; private set; }
    public DateTime? ExpiresAt { get; private set; }
    public DateTime? LastUsedAt { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public Project Project { get; private set; } = null!;

    public static ApiToken Create(string name, string tokenHash, string tokenPrefix, Guid projectId, ApiTokenCapability capabilities, DateTime? expiresAt, Guid? createdByUserId)
    {
        var token = Entity.New<ApiToken>();
        token.Name = name;
        token.TokenHash = tokenHash;
        token.TokenPrefix = tokenPrefix;
        token.ProjectId = projectId;
        token.Capabilities = capabilities;
        token.ExpiresAt = expiresAt;
        token.CreatedByUserId = createdByUserId;
        token.CreatedAt = DateTime.UtcNow;
        return token;
    }

    public void RecordUsage()
    {
        LastUsedAt = DateTime.UtcNow;
    }

    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value < DateTime.UtcNow;

    public bool HasCapability(ApiTokenCapability capability)
    {
        return (Capabilities & capability) == capability;
    }
}
