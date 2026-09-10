namespace ProjectBeacon.Infrastructure.Data;

using Domain.Common;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using System.Reflection;

public static class TenantScope
{
    private static readonly AsyncLocal<Guid?> _currentOrgId = new();
    private static readonly AsyncLocal<Guid?> _currentProjectId = new();
    private static readonly AsyncLocal<bool> _unscoped = new();

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

    public static bool IsUnscoped => _unscoped.Value;

    // Null FilterProjectId is fail-closed. This flag is the only bypass (bootstrap, tests, migrations).
    public static IDisposable EnterUnscoped()
    {
        var previous = _unscoped.Value;
        _unscoped.Value = true;
        return new ScopeReset(() => _unscoped.Value = previous);
    }

    public static IDisposable EnterOrgScope(Guid orgId)
    {
        var previousId = _currentOrgId.Value;
        var previousUnscoped = _unscoped.Value;
        _currentOrgId.Value = orgId;
        _unscoped.Value = false;
        return new ScopeReset(() =>
        {
            _currentOrgId.Value = previousId;
            _unscoped.Value = previousUnscoped;
        });
    }

    public static IDisposable EnterProjectScope(Guid projectId)
    {
        var previousId = _currentProjectId.Value;
        var previousUnscoped = _unscoped.Value;
        _currentProjectId.Value = projectId;
        _unscoped.Value = false;
        return new ScopeReset(() =>
        {
            _currentProjectId.Value = previousId;
            _unscoped.Value = previousUnscoped;
        });
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
            context.FilterUnscoped
            || (context.FilterProjectId != null && e.ProjectId == context.FilterProjectId));
    }

    private static void SetOrgFilter<T>(ModelBuilder modelBuilder, BeaconDbContext context)
        where T : class, IOrgScoped
    {
        modelBuilder.Entity<T>().HasQueryFilter(e =>
            context.FilterUnscoped
            || (context.FilterOrgId != null && e.OrgId == context.FilterOrgId));
    }
}
