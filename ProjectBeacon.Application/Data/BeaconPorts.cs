namespace ProjectBeacon.Infrastructure.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Domain.Entities.Agents;
using Domain.Entities.Devices;
using Domain.Entities.Evals;
using Domain.Entities.Identity;
using Domain.Entities.Projects;

// Owned by Application so handlers do not reference the Infrastructure assembly.
// The EF context in Infrastructure implements this port.
/// <summary>Application port over the EF context so handlers do not reference the Infrastructure assembly.</summary>
public interface IBeaconDb : IAsyncDisposable
{
    DbSet<User> Users { get; }
    DbSet<OpenCodeConnection> OpenCodeConnections { get; }
    DbSet<UserSession> Sessions { get; }
    DbSet<Org> Orgs { get; }
    DbSet<OrgMember> OrgMembers { get; }
    DbSet<OrgInvite> OrgInvites { get; }
    DbSet<PasswordResetToken> PasswordResetTokens { get; }
    DbSet<Project> Projects { get; }
    DbSet<ProjectMember> ProjectMembers { get; }
    DbSet<ProjectInvite> ProjectInvites { get; }
    DbSet<ApiToken> ApiTokens { get; }
    DbSet<TaskItem> Tasks { get; }
    DbSet<Label> Labels { get; }
    DbSet<LabelPath> LabelPaths { get; }
    DbSet<Report> Reports { get; }
    DbSet<Milestone> Milestones { get; }
    DbSet<TaskDependency> TaskDependencies { get; }
    DbSet<TaskComment> TaskComments { get; }
    DbSet<Constraint> Constraints { get; }
    DbSet<Decision> Decisions { get; }
    DbSet<DecisionTask> DecisionTasks { get; }
    DbSet<LocalModelBackend> LocalModelBackends { get; }
    DbSet<AgentTemplate> AgentTemplates { get; }
    DbSet<TaskKind> TaskKinds { get; }
    DbSet<TaskKindPhase> TaskKindPhases { get; }
    DbSet<TaskPhase> TaskPhases { get; }
    DbSet<RoleBinding> RoleBindings { get; }
    DbSet<Subtask> Subtasks { get; }
    DbSet<PipelineSession> PipelineSessions { get; }
    DbSet<ReviewVerdict> ReviewVerdicts { get; }
    DbSet<DaemonDevice> DaemonDevices { get; }
    DbSet<WorkstationCommand> WorkstationCommands { get; }
    DbSet<ProjectRuntime> ProjectRuntimes { get; }
    DbSet<DeviceHostSample> DeviceHostSamples { get; }
    DbSet<TaskStep> TaskSteps { get; }
    DbSet<ChatSession> ChatSessions { get; }
    DbSet<ChatPart> ChatParts { get; }
    DbSet<EvalRun> EvalRuns { get; }
    DbSet<ReviewRun> ReviewRuns { get; }
    DbSet<ContextSection> ContextSections { get; }
    DbSet<ContextRevision> ContextRevisions { get; }

    Guid? FilterProjectId { get; }
    Guid? FilterOrgId { get; }
    bool FilterUnscoped { get; }
    DatabaseFacade Database { get; }
    EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class;
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Creates <see cref="IBeaconDb"/> instances.</summary>
public interface IBeaconDbFactory
{
    IBeaconDb CreateDbContext();
    Task<IBeaconDb> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}

/// <summary>Project and org ids, plus an unscoped flag, assigned onto a database session.</summary>
public interface ITenantContext
{
    Guid? ProjectId { get; }
    Guid? OrgId { get; }
    bool Unscoped { get; }
    void Assign(Guid? projectId, Guid? orgId, bool unscoped);
}

/// <summary>Mutable <see cref="ITenantContext"/> whose <c>Assign</c> replaces the project, org, and unscoped flag.</summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? ProjectId { get; private set; }
    public Guid? OrgId { get; private set; }
    public bool Unscoped { get; private set; }

    public void Assign(Guid? projectId, Guid? orgId, bool unscoped)
    {
        ProjectId = projectId;
        OrgId = orgId;
        Unscoped = unscoped;
    }
}
