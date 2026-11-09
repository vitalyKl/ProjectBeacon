namespace ProjectBeacon.Application.Audit;

using System.Globalization;
using System.Text;

/// <summary>Creates spreadsheet-safe UTF-8 CSV exports from audit entries.</summary>
public static class AuditCsvWriter
{
    public const int MaxRows = 10_000;

    public static byte[] Encode(IReadOnlyCollection<AuditEntry> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count > MaxRows)
            throw new ArgumentOutOfRangeException(nameof(rows), $"Audit export is limited to {MaxRows} rows.");

        var csv = new StringBuilder();
        AppendRow(csv,
            "AtUtc", "ActorKind", "ActorUserId", "ActorTokenId", "ActorDeviceId",
            "Action", "TargetType", "TargetId", "OrgId", "ProjectId", "Outcome",
            "Ip", "UserAgent", "TraceId", "DetailsJson");
        foreach (var entry in rows)
        {
            AppendRow(csv,
                DateTime.SpecifyKind(entry.At, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
                entry.ActorKind.ToString(),
                entry.ActorUserId?.ToString(),
                entry.ActorTokenId?.ToString(),
                entry.ActorDeviceId?.ToString(),
                entry.Action,
                entry.TargetType,
                entry.TargetId,
                entry.OrgId?.ToString(),
                entry.ProjectId?.ToString(),
                entry.Outcome.ToString(),
                entry.Ip,
                entry.UserAgent,
                entry.TraceId,
                entry.DetailsJson);
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv.ToString());
    }

    private static void AppendRow(StringBuilder csv, params string?[] values)
    {
        csv.AppendJoin(',', values.Select(Escape));
        csv.Append("\r\n");
    }

    private static string Escape(string? value)
    {
        value ??= string.Empty;
        var firstMeaningful = value.FirstOrDefault(character => !char.IsWhiteSpace(character));
        if (firstMeaningful is '=' or '+' or '-' or '@')
            value = "'" + value;

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
