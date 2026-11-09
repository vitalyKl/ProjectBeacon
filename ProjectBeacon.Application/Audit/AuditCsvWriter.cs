namespace ProjectBeacon.Application.Audit;

using System.Globalization;
using System.Text;

/// <summary>Produces bounded, spreadsheet-safe UTF-8 CSV audit exports.</summary>
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
            "Id", "AtUtc", "ActorKind", "ActorUserId", "ActorTokenId", "ActorDeviceId",
            "Action", "TargetType", "TargetId", "OrgId", "ProjectId", "Outcome",
            "Ip", "UserAgent", "TraceId", "DetailsJson");

        foreach (var row in rows)
        {
            AppendRow(csv,
                row.Id.ToString(),
                DateTime.SpecifyKind(row.At, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
                row.ActorKind.ToString(),
                row.ActorUserId?.ToString(),
                row.ActorTokenId?.ToString(),
                row.ActorDeviceId?.ToString(),
                row.Action,
                row.TargetType,
                row.TargetId,
                row.OrgId?.ToString(),
                row.ProjectId?.ToString(),
                row.Outcome.ToString(),
                row.Ip,
                row.UserAgent,
                row.TraceId,
                row.DetailsJson);
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv.ToString());
    }

    private static void AppendRow(StringBuilder csv, params string?[] values)
    {
        csv.Append(string.Join(",", values.Select(Escape)));
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
