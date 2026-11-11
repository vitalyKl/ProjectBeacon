namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Audit;

public sealed class AuditEventTests
{
    [Fact]
    public void Create_AcceptsOnlyAllowlistedScalarDetailCodes()
    {
        var auditEvent = Create(detailsJson: """{"reasonCode":"user.disabled","count":1}""");

        Assert.Equal("""{"reasonCode":"user.disabled","count":1}""", auditEvent.DetailsJson);
    }

    [Fact]
    public void Create_AcceptsRestoreMetadataWithoutContentPayload()
    {
        var sourceVersionId = Guid.NewGuid();
        var auditEvent = Create(detailsJson:
            $"""{"sourceVersionId":"{{sourceVersionId:D}}","restoredRevision":4}""");

        Assert.Equal(
            $"""{"sourceVersionId":"{{sourceVersionId:D}}","restoredRevision":4}""",
            auditEvent.DetailsJson);
    }

    [Fact]
    public void Create_RejectsUnlistedSensitiveMetadataKeys()
    {
        Assert.Throws<ArgumentException>(() => Create(detailsJson: """{"password":"secret"}"""));
    }

    [Fact]
    public void Create_RejectsFreeFormDetailValues()
    {
        Assert.Throws<ArgumentException>(() => Create(detailsJson: """{"reasonCode":"The user asked us to reset a password"}"""));
    }

    [Fact]
    public void Create_RequiresOrganizationForProjectScopedEvents()
    {
        Assert.Throws<ArgumentException>(() => AuditEvent.Create(
            DateTime.UtcNow, AuditActorKind.Human, Guid.NewGuid(), null, null,
            "project.updated", "Project", Guid.NewGuid().ToString(), null, Guid.NewGuid(),
            AuditOutcome.Success));
    }

    [Fact]
    public void Create_RequiresTokenIdentityForTokenActor()
    {
        Assert.Throws<ArgumentException>(() => AuditEvent.Create(
            DateTime.UtcNow, AuditActorKind.ApiToken, null, null, null,
            "token.used", "ApiToken", null, null, null, AuditOutcome.Success));
    }

    private static AuditEvent Create(string? detailsJson = null) =>
        AuditEvent.Create(
            DateTime.UtcNow,
            AuditActorKind.Human,
            Guid.NewGuid(),
            null,
            null,
            "user.disabled",
            "User",
            Guid.NewGuid().ToString(),
            null,
            null,
            AuditOutcome.Success,
            detailsJson: detailsJson);
}
