namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Entities.Roadmap;
using ProjectBeacon.Domain.Entities.Tasks;
using Identity;

/// <summary>Org-scoped project. It has no single working-tree root; that lives on <see cref="ProjectRuntime"/>.</summary>
public class Project : Entity, IOrgScoped
{
    public Project() { }

    public int Revision { get; private set; } = 1;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string? ReviewCheckCommand { get; private set; }
    /// <summary>Encrypted JSON containing this project's MCP server definitions and credentials.</summary>
    public string McpServersCipher { get; private set; } = string.Empty;
    /// <summary>JSON map of target/operation proposal decisions; missing entries are Manual.</summary>
    public string ProposalPoliciesJson { get; private set; } = "{}";
    /// <summary>Optimistic-concurrency token for proposal policy edits, independent of content revision.</summary>
    public int ProposalPolicyRevision { get; private set; } = 1;
    /// <summary>Validated ProjectGitPolicy JSON. Defaults to manual landing, Escalate, and no auto-revert.</summary>
    public string GitPolicyJson { get; private set; } = "{\"autonomyLevel\":0,\"dirtyCheckoutBehavior\":\"Escalate\",\"protectedPaths\":[],\"maxChangedFiles\":200,\"maxChangedLines\":20000,\"autoRevertMode\":\"Off\"}";
    /// <summary>Version and release rules configured for this project.</summary>
    public string VersionPolicyJson { get; private set; } = ProjectVersionPolicyCatalogDefaults.Json;
    /// <summary>Optimistic-concurrency token for version-policy edits.</summary>
    public int VersionPolicyRevision { get; private set; } = 1;
    /// <summary>Authoritative project version. Repository manifests are projections of this value.</summary>
    public string? OfficialVersion { get; private set; }

    /// <summary>Optimistic-concurrency token for Git policy edits.</summary>
    public int GitPolicyRevision { get; private set; } = 1;
    public Guid OrgId { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    public Org Org { get; private set; } = null!;
    public ICollection<TaskItem> Tasks { get; private set; } = [];
    public ICollection<Label> Labels { get; private set; } = [];
    public ICollection<ProjectMember> Members { get; private set; } = [];
    public ICollection<ProjectInvite> Invites { get; private set; } = [];
    public ICollection<ApiToken> ApiTokens { get; private set; } = [];
    public ICollection<Milestone> Milestones { get; private set; } = [];

    public static Project Create(string name, string? description, Guid orgId)
    {
        var project = Entity.New<Project>();
        project.Name = name;
        project.Description = description;
        project.OrgId = orgId;
        project.CreatedAt = DateTime.UtcNow;
        return project;
    }

    public void SetMcpServersCipher(string cipher)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        McpServersCipher = cipher;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Replaces the project proposal policy map without changing the project description revision.</summary>
    public void SetProposalPoliciesJson(string policiesJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policiesJson);
        ProposalPoliciesJson = policiesJson;
        ProposalPolicyRevision++;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Replaces the canonical Git workflow policy without changing project content revision.</summary>
    public void SetGitPolicyJson(string policyJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyJson);
        GitPolicyJson = policyJson;
        GitPolicyRevision++;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Replaces the project version policy and increments its concurrency revision.</summary>
    public void SetVersionPolicyJson(string policyJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyJson);
        VersionPolicyJson = policyJson;
        VersionPolicyRevision++;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Initializes the authoritative version once. Bumping it requires a release transaction.</summary>
    public void InitializeOfficialVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (OfficialVersion is not null)
            throw new InvalidOperationException("The official version is already initialized. Use ReleasePrepare to change it.");
        OfficialVersion = version.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Changes the authoritative version as part of a server-authorized release transaction.</summary>
    public void SetOfficialVersionFromRelease(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        OfficialVersion = version.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Restores the nullable description from a historical content snapshot.</summary>
    public void RestoreDescriptionFromHistory(string? description)
    {
        if (string.Equals(Description, description, StringComparison.Ordinal))
            return;

        Description = description;
        Revision++;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Update(string? name = null, string? description = null, string? reviewCheckCommand = null)
    {
        if (name is not null) Name = name;
        if (description is not null)
        {
            if (!string.Equals(Description, description, StringComparison.Ordinal))
                Revision++;
            Description = description;
        }
        if (reviewCheckCommand is not null)
            ReviewCheckCommand = string.IsNullOrWhiteSpace(reviewCheckCommand) ? null : reviewCheckCommand.Trim();
        UpdatedAt = DateTime.UtcNow;
    }
}
