using Microsoft.EntityFrameworkCore;

namespace ProjectBeacon.Application.Chat;

/// <summary>
/// Recognizes the unique violation raised when two writers claim the same part sort order.
/// The provider exception is matched by type name and message so no database provider
/// reference is needed here.
/// </summary>
internal static class ChatPartSql
{
    public static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            var name = inner.GetType().Name;
            var message = inner.Message;
            if (name == "PostgresException"
                && message.Contains("duplicate key value violates unique constraint", StringComparison.OrdinalIgnoreCase))
                return true;
            if (name == "SqliteException" && message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
