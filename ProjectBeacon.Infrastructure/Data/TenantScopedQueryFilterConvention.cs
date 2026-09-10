namespace ProjectBeacon.Infrastructure.Data;

using Domain.Common;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using System.Reflection;

public static class TenantScope
{
    private static readonly AsyncLocal<Guid?> _currentOrgId = new();
    private static readonly AsyncLocal<Guid?> _currentProjectId = new();

    public static Guid? CurrentOrgId
    {
        get => _currentOrgId.Value;
        internal set => _currentOrgId.Value = value;
    }

    public static Guid? CurrentProjectId
    {
        get => _currentProjectId.Value;
        internal set => _currentProjectId.Value = value;
    }

    public static IDisposable EnterOrgScope(Guid orgId)
    {
        var previous = _currentOrgId.Value;
        _currentOrgId.Value = orgId;
        return new ScopeReset(() => _currentOrgId.Value = previous);
    }

    public static IDisposable EnterProjectScope(Guid projectId)
    {
        var previous = _currentProjectId.Value;
        _currentProjectId.Value = projectId;
        return new ScopeReset(() => _currentProjectId.Value = previous);
    }

    public static IDisposable EnterScope(Guid tenantId) => EnterProjectScope(tenantId);

    private sealed class ScopeReset : IDisposable
    {
        private readonly Action _restore;
        public ScopeReset(Action restore) => _restore = restore;
        public void Dispose() => _restore();
    }
}

internal static class TenantScopedQueryFilterConvention
{
    public static void Apply(ModelBuilder modelBuilder, BeaconDbContext context)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clr = entityType.ClrType;
            if (clr is null)
                continue;

            if (typeof(IProjectScoped).IsAssignableFrom(clr))
            {
                var method = typeof(TenantScopedQueryFilterConvention)
                    .GetMethod(nameof(SetProjectFilter), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(clr);
                method.Invoke(null, [modelBuilder, context]);
            }
            else if (typeof(IOrgScoped).IsAssignableFrom(clr))
            {
                var method = typeof(TenantScopedQueryFilterConvention)
                    .GetMethod(nameof(SetOrgFilter), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(clr);
                method.Invoke(null, [modelBuilder, context]);
            }
        }
    }

    private static void SetProjectFilter<T>(ModelBuilder modelBuilder, BeaconDbContext context)
        where T : class, IProjectScoped
    {
        modelBuilder.Entity<T>().HasQueryFilter(e =>
            context.FilterProjectId == null || e.ProjectId == context.FilterProjectId);
    }

    private static void SetOrgFilter<T>(ModelBuilder modelBuilder, BeaconDbContext context)
        where T : class, IOrgScoped
    {
        modelBuilder.Entity<T>().HasQueryFilter(e =>
            context.FilterOrgId == null || e.OrgId == context.FilterOrgId);
    }
}
