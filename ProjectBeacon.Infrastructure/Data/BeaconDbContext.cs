namespace ProjectBeacon.Infrastructure.Data;

using Microsoft.EntityFrameworkCore;
using Domain.Entities.Agents;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Evals;
using Domain.Enums;
/// <summary>
/// EF context behind IBeaconDb. Tenant filters come from the bound tenant or TenantScope. A null project or org filter returns no rows unless the scope is unscoped.
/// </summary>
public class BeaconDbContext : DbContext, IBeaconDb
{
    public DbSet<User> Users => Set<User>();
    public DbSet<OpenCodeConnection> OpenCodeConnections => Set<OpenCodeConnection>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<Org> Orgs => Set<Org>();
    public DbSet<OrgMember> OrgMembers => Set<OrgMember>();
    public DbSet<OrgInvite> OrgInvites => Set<OrgInvite>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<ProjectInvite> ProjectInvites => Set<ProjectInvite>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<LabelPath> LabelPaths => Set<LabelPath>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<TaskDependency> TaskDependencies => Set<TaskDependency>();
    public DbSet<TaskComment> TaskComments => Set<TaskComment>();
    public DbSet<Constraint> Constraints => Set<Constraint>();
    public DbSet<Decision> Decisions => Set<Decision>();
    public DbSet<DecisionTask> DecisionTasks => Set<DecisionTask>();
    public DbSet<LocalModelBackend> LocalModelBackends => Set<LocalModelBackend>();
    public DbSet<AgentTemplate> AgentTemplates => Set<AgentTemplate>();
    public DbSet<TaskKind> TaskKinds => Set<TaskKind>();
    public DbSet<TaskKindPhase> TaskKindPhases => Set<TaskKindPhase>();
    public DbSet<TaskPhase> TaskPhases => Set<TaskPhase>();
    public DbSet<RoleBinding> RoleBindings => Set<RoleBinding>();
    public DbSet<Subtask> Subtasks => Set<Subtask>();
    public DbSet<PipelineSession> PipelineSessions => Set<PipelineSession>();
    public DbSet<ReviewVerdict> ReviewVerdicts => Set<ReviewVerdict>();
    public DbSet<DaemonDevice> DaemonDevices => Set<DaemonDevice>();
    public DbSet<WorkstationCommand> WorkstationCommands => Set<WorkstationCommand>();
    public DbSet<ProjectRuntime> ProjectRuntimes => Set<ProjectRuntime>();
    public DbSet<DeviceHostSample> DeviceHostSamples => Set<DeviceHostSample>();
    public DbSet<TaskStep> TaskSteps => Set<TaskStep>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatPart> ChatParts => Set<ChatPart>();

    public DbSet<EvalRun> EvalRuns => Set<EvalRun>();
    public DbSet<ReviewRun> ReviewRuns => Set<ReviewRun>();

    public DbSet<ContextSection> ContextSections => Set<ContextSection>();
    public DbSet<ContextRevision> ContextRevisions => Set<ContextRevision>();

    private readonly ITenantContext? _tenant;

    public BeaconDbContext(DbContextOptions<BeaconDbContext> options) : this(options, null) { }

    public BeaconDbContext(DbContextOptions<BeaconDbContext> options, ITenantContext? tenant) : base(options)
    {
        _tenant = tenant;
    }

    public Guid? FilterProjectId => _tenant?.ProjectId ?? TenantScope.CurrentProjectId;
    public Guid? FilterOrgId => _tenant?.OrgId ?? TenantScope.CurrentOrgId;
    public bool FilterUnscoped => (_tenant?.Unscoped ?? false) || TenantScope.IsUnscoped;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BeaconDbContext).Assembly);
        TenantScopedQueryFilterConvention.Apply(modelBuilder, this);
    }
}
