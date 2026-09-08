namespace ProjectBeacon.Infrastructure.Data;

using Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Linq.Expressions;
using System.Reflection;

/// <summary>
/// Provides a thread-scoped tenant ID for query filtering.
/// Set via TenantScope.EnterScope(). The query filters on ITenantScoped
/// entities will use this value to filter results by tenant.
/// </summary>
public static class TenantScope
{
    private static readonly AsyncLocal<long> _currentTenantId = new();

    /// <summary>
    /// The current tenant ID for this async context (0 = no scope).
    /// </summary>
    public static long CurrentTenantId
    {
        get => _currentTenantId.Value;
        internal set => _currentTenantId.Value = value;
    }

    /// <summary>
    /// Enter a tenant scope. Returns a disposable that restores the previous scope.
    /// </summary>
    public static IDisposable EnterScope(long tenantId)
    {
        var previous = _currentTenantId.Value;
        _currentTenantId.Value = tenantId;
        return new ScopeReset(previous);
    }

    private sealed class ScopeReset : IDisposable
    {
        private readonly long _previous;
        public ScopeReset(long previous) => _previous = previous;
        public void Dispose() => _currentTenantId.Value = _previous;
    }
}

/// <summary>
/// Convention for automatically applying tenant-scoped query filters to entities
/// implementing ITenantScoped.
///
/// Filters use the entity's OrgId or ProjectId (the first tenant-scoped FK property found)
/// and compare it against TenantScope.CurrentTenantId.
/// </summary>
internal static class TenantScopedQueryFilterConvention
{
    /// <summary>
    /// Apply tenant filters to all ITenantScoped entities.
    /// Uses the entity's first FK property named OrgId or ProjectId.
    /// </summary>
    public static void Apply(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
            .Where(e => e.ClrType != null && typeof(ITenantScoped).IsAssignableFrom(e.ClrType)))
        {
            // Cast to IMutableEntityType for HasQueryFilter
            var mutableType = modelBuilder.Entity(entityType.Name).Metadata;

            var parameter = Expression.Parameter(mutableType.ClrType!, "e");

            var tenantProperty = FindTenantProperty(mutableType);
            if (tenantProperty == null)
                continue;

            var scopeAccess = Expression.Property(null, typeof(TenantScope), "CurrentTenantId");
            var scopeCheck = Expression.NotEqual(scopeAccess, Expression.Constant(0L, typeof(long)));

            var tenantAccess = Expression.PropertyOrField(parameter, tenantProperty.Name);

            var tenantMatch = Expression.Equal(tenantAccess, scopeAccess);

            var body = Expression.AndAlso(scopeCheck, tenantMatch);

            modelBuilder.Entity(mutableType.Name).HasQueryFilter(Expression.Lambda(body, parameter));
        }
    }

    private static PropertyInfo? FindTenantProperty(IMutableEntityType entityType)
    {
        if (entityType.ClrType is null) return null;

        var properties = entityType.ClrType.GetProperties()
            .Where(p => p.Name is "OrgId" or "ProjectId" && p.PropertyType == typeof(Guid));

        return properties.FirstOrDefault();
    }
}