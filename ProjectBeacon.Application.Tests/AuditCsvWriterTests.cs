namespace ProjectBeacon.Application.Tests;

using System.Text;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Domain.Entities.Audit;

public sealed class AuditCsvWriterTests
{
    [Fact]
    public void Encode_PrefixesSpreadsheetFormulasAndPreservesUtf8Bom()
    {
        var entry = new AuditEntry(
            Guid.NewGuid(),
            DateTime.UtcNow,
            AuditActorKind.Human,
            Guid.NewGuid(),
            null,
            null,
            "account.login.succeeded",
            "User",
            Guid.NewGuid().ToString(),
            null,
            null,
            AuditOutcome.Success,
            null,
            "  =SUM(1,1)",
            null,
            """{"authMethod":"password"}""");

        var csv = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
            .GetString(AuditCsvWriter.Encode([entry]));

        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains("\"Id\",\"AtUtc\",\"ActorKind\"", csv);
        Assert.Contains("\"'  =SUM(1,1)\"", csv);
        Assert.Contains("account.login.succeeded", csv);
    }

    [Fact]
    public void Encode_RejectsMoreThanTheConfiguredMaximum()
    {
        var entry = new AuditEntry(
            Guid.NewGuid(), DateTime.UtcNow, AuditActorKind.System,
            null, null, null, "platform.bootstrap.completed", "User", null,
            null, null, AuditOutcome.Success, null, null, null, null);
        var rows = Enumerable.Repeat(entry, AuditCsvWriter.MaxRows + 1).ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(() => AuditCsvWriter.Encode(rows));
    }
}
