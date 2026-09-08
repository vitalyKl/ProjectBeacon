namespace ProjectBeacon.Infrastructure.Data;

using Microsoft.EntityFrameworkCore;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;

public class BeaconDbContext : DbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<Org> Orgs => Set<Org>();
    public DbSet<OrgMember> OrgMembers => Set<OrgMember>();
    public DbSet<OrgInvite> OrgInvites => Set<OrgInvite>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<ProjectInvite> ProjectInvites => Set<ProjectInvite>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<TaskDependency> TaskDependencies => Set<TaskDependency>();
    public DbSet<TaskComment> TaskComments => Set<TaskComment>();
    public DbSet<Constraint> Constraints => Set<Constraint>();
    public DbSet<Decision> Decisions => Set<Decision>();
    public DbSet<DecisionTask> DecisionTasks => Set<DecisionTask>();

    public BeaconDbContext(DbContextOptions<BeaconDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Login).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(200);
            entity.Property(e => e.PasswordHash).IsRequired();
            entity.HasIndex(e => e.Login).IsUnique();
            entity.HasIndex(e => e.Email).IsUnique();

            entity.HasMany(e => e.Sessions)
                .WithOne(e => e.User)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany<OrgMember>()
                .WithOne(e => e.User)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany<OrgInvite>()
                .WithOne()
                .HasForeignKey(e => e.InvitedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany<ProjectMember>()
                .WithOne(e => e.User)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany<ProjectInvite>()
                .WithOne()
                .HasForeignKey(e => e.InvitedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.IpAddress).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => new { e.UserId, e.IsActive });
        });

        modelBuilder.Entity<Org>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt);

            entity.HasMany(e => e.Members)
                .WithOne(e => e.Org)
                .HasForeignKey(e => e.OrgId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Projects)
                .WithOne(e => e.Org)
                .HasForeignKey(e => e.OrgId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany<OrgInvite>()
                .WithOne(e => e.Org)
                .HasForeignKey(e => e.OrgId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrgMember>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.JoinedAt).IsRequired();
            entity.HasIndex(e => new { e.OrgId, e.UserId }).IsUnique();
            entity.HasOne(e => e.Org)
                .WithMany(e => e.Members)
                .HasForeignKey(e => e.OrgId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrgInvite>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Status).HasConversion<string>().IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.ExpiredAt).IsRequired();
            entity.HasIndex(e => new { e.OrgId, e.Email });
            entity.HasOne(e => e.Org)
                .WithMany(e => e.Invites)
                .HasForeignKey(e => e.OrgId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt);

            entity.HasOne(e => e.Org)
                .WithMany(e => e.Projects)
                .HasForeignKey(e => e.OrgId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Tasks)
                .WithOne(e => e.Project)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Labels)
                .WithOne(e => e.Project)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany<ProjectMember>()
                .WithOne(e => e.Project)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany<ProjectInvite>()
                .WithOne(e => e.Project)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany<ApiToken>()
                .WithOne(e => e.Project)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectMember>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.JoinedAt).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.UserId }).IsUnique();
            entity.HasOne(e => e.Project)
                .WithMany(e => e.Members)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectInvite>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Status).HasConversion<string>().IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.ExpiredAt).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.Email });
            entity.HasOne(e => e.Project)
                .WithMany(e => e.Invites)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApiToken>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.TokenHash).IsRequired();
            entity.Property(e => e.TokenPrefix).HasMaxLength(20);
            entity.Property(e => e.Capabilities).HasConversion<long>().IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasOne(e => e.Project)
                .WithMany(e => e.ApiTokens)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Description).HasMaxLength(5000);
            entity.Property(e => e.Status).HasConversion<string>().IsRequired();
            entity.Property(e => e.Priority).HasConversion<string>().IsRequired();
            entity.Property(e => e.Type).HasConversion<string>().IsRequired();
            entity.Property(e => e.SubStage).HasConversion<string>();
            entity.Property(e => e.ReviewNotes).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.CompletedAt);

            entity.HasOne(e => e.Project)
                .WithMany(e => e.Tasks)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Label)
                .WithMany(e => e.Tasks)
                .HasForeignKey(e => e.LabelId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Milestone)
                .WithMany(e => e.Tasks)
                .HasForeignKey(e => e.MilestoneId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(e => e.Comments)
                .WithOne(e => e.Task)
                .HasForeignKey(e => e.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Dependencies)
                .WithOne(e => e.Task)
                .HasForeignKey(e => e.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.ProjectId, e.Status });
        });

        modelBuilder.Entity<Milestone>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(5000);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt);

            entity.HasOne(e => e.Project)
                .WithMany(e => e.Milestones)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskDependency>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CreatedAt).IsRequired();

            entity.HasOne(e => e.Task)
                .WithMany(e => e.Dependencies)
                .HasForeignKey(e => e.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.DependentTask)
                .WithMany()
                .HasForeignKey(e => e.DependentTaskId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.TaskId, e.DependentTaskId }).IsUnique();
        });

        modelBuilder.Entity<TaskComment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Content).IsRequired().HasMaxLength(5000);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt);

            entity.HasOne(e => e.Task)
                .WithMany(e => e.Comments)
                .HasForeignKey(e => e.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.TaskId, e.CreatedAt });
        });

        modelBuilder.Entity<Label>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Color).IsRequired().HasMaxLength(20);
        });

        modelBuilder.Entity<Constraint>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Body).IsRequired();
            entity.Property(e => e.ScopePath).HasMaxLength(500).HasDefaultValue("");
            entity.Property(e => e.Kind).HasConversion<string>().IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.Kind, e.Status });
        });

        modelBuilder.Entity<Decision>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Context).HasMaxLength(5000);
            entity.Property(e => e.DecisionBody).IsRequired().HasMaxLength(5000);
            entity.Property(e => e.Consequences).HasMaxLength(5000);
            entity.Property(e => e.Status).HasConversion<string>().IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.Status });
            entity.HasOne(e => e.SupersededBy)
                .WithMany()
                .HasForeignKey(e => e.SupersededById)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DecisionTask>(entity =>
        {
            entity.HasKey(e => new { e.DecisionId, e.TaskId });
            entity.HasOne(e => e.Decision)
                .WithMany(e => e.RelatedTasks)
                .HasForeignKey(e => e.DecisionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Task)
                .WithMany()
                .HasForeignKey(e => e.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Apply tenant isolation to ITenantScoped entities
        // Temporarily disabled due to EF Core model resolution issue with SQLite in-memory
        // TenantScopedQueryFilterConvention.Apply(modelBuilder);
    }
}
