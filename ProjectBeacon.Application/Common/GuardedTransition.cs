namespace ProjectBeacon.Application.Common;

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

/// <summary>Outcome of a compare-and-swap style state transition.</summary>
public enum GuardedTransitionResult
{
    /// <summary>The guarded update changed the row.</summary>
    Applied,
    /// <summary>A competing request already moved the row to the requested target state.</summary>
    AlreadyInTarget,
    /// <summary>The row no longer matches the source state or the requested target state.</summary>
    Lost
}

/// <summary>
/// Executes a guarded database state update and distinguishes an idempotent race from a lost race.
/// Pass the same tenant-scoped query for the update and target-state check.
/// </summary>
public static class GuardedTransition
{
    public static async Task<GuardedTransitionResult> TryAsync<TEntity>(
        IQueryable<TEntity> query,
        Expression<Func<TEntity, bool>> fromPredicate,
        Expression<Func<TEntity, bool>> targetPredicate,
        Expression<Func<SetPropertyCalls<TEntity>, SetPropertyCalls<TEntity>>> setProperties,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var updated = await query
            .Where(fromPredicate)
            .ExecuteUpdateAsync(setProperties, cancellationToken);

        if (updated > 0)
            return GuardedTransitionResult.Applied;

        return await query.AnyAsync(targetPredicate, cancellationToken)
            ? GuardedTransitionResult.AlreadyInTarget
            : GuardedTransitionResult.Lost;
    }
}
