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
        var sourceVersionId = Guid.NewGuid().ToString("D");
        var detailsJson =
            "{\"sourceVersionId\":\"" + sourceVersionId
            + "\",\"restoredRevision\":4,\"restoredTaskLinks\":2}";
        var auditEvent = Create(detailsJson);

        Assert.Equal(detailsJson, auditEvent.DetailsJson);
    }

    [Fact]
    public void Create_AcceptsRestoredTaskLinkCountAsIntegerMetadata()
    {
        const string detailsJson = """{"sourceVersionId":"abc","restoredRevision":4,"restoredTaskLinks":2}""";

        var auditEvent = Create(detailsJson);

        Assert.Equal(detailsJson, auditEvent.DetailsJson);
    }

    [Fact]
    public void Create_RejectsNonIntegerRestoredTaskLinkCount()
    {
        Assert.Throws<ArgumentException>(() =>
            Create(detailsJson: """{"restoredTaskLinks":1.5}"""));
    }

    [Fact]
    public void Create_AcceptsReleaseVersionAndMilestoneAssociationMetadata()
    {
        var previousReleaseId = Guid.NewGuid();
        var releaseId = Guid.NewGuid();
        var detailsJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            newState = "Planned",
            targetVersion = "0.7.5.9",
            targetBranch = "release/0.7.5.9",
            previousReleaseId = previousReleaseId.ToString("D"),
            releaseId = releaseId.ToString("D"),
            count = 3
        });

        var auditEvent = Create(detailsJson);

        Assert.Equal(detailsJson, auditEvent.DetailsJson);
        Assert.NotEqual(previousReleaseId, releaseId);
    }

    [Fact]
    public void Create_AcceptsReviewerDecisionSessionMetadata()
    {
        var sessionId = Guid.NewGuid();
        var detailsJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            previousState = "patch",
            newState = "Confirmed",
            reviewSessionId = sessionId.ToString("D"),
            count = 4
        });

        var auditEvent = Create(detailsJson);

        Assert.Equal(detailsJson, auditEvent.DetailsJson);
    }

    [Fact]
    public void Create_AcceptsCalendarTargetVersionAndAutoSyncPolicyMetadata()
    {
        const string detailsJson = """{"newState":"NumericComponents","policyCode":"ReleaseManagement.Enabled","reasonCode":"Escalate","resultCode":"AutoSync.Enabled","targetVersion":"2026/11/24","count":4}""";

        var auditEvent = Create(detailsJson);

        Assert.Equal(detailsJson, auditEvent.DetailsJson);
    }

    [Fact]
    public void Create_AcceptsReleaseLifecycleMetadata()
    {
        const string detailsJson = """{"previousState":"Planned","newState":"Release.FieldsUpdated","targetVersion":"0.7.5.9+build.12","targetBranch":"release/0.7.5.9","count":3}""";

        var auditEvent = Create(detailsJson);

        Assert.Equal(detailsJson, auditEvent.DetailsJson);
    }

    [Fact]
    public void Create_AcceptsValidatedGitHubPullRequestMergeMetadata()
    {
        const string json = """{"repository":"acme/project-beacon","pullRequestNumber":42,"pullRequestUrl":"https://github.com/acme/project-beacon/pull/42","mergeRequestId":"6a7c3d51-8397-49e4-86b5-4e7a4ac4f71a","resultCode":"GitHubMergeEnqueued"}""";

        var auditEvent = Create(json);

        Assert.Equal(json, auditEvent.DetailsJson);
    }

    [Theory]
    [InlineData("repository", "acme/project beacon")]
    [InlineData("repository", "https://github.com/acme/project-beacon")]
    [InlineData("pullRequestUrl", "https://github.com/acme/project-beacon/pull/42?tab=checks")]
    [InlineData("pullRequestUrl", "https://attacker.example/acme/project-beacon/pull/42")]
    public void Create_RejectsUnsafeGitHubRepositoryAndPullRequestMetadata(string key, string value)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [key] = value
        });

        Assert.Throws<ArgumentException>(() => Create(json));
    }

    [Fact]
    public void Create_AcceptsImmutableReleasePublicationMetadata()
    {
        var detailsJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            candidateId = Guid.NewGuid().ToString("D"),
            tagName = "customer/releases/0.7.5.10",
            candidateSha = new string('a', 40),
            workstationId = Guid.NewGuid().ToString("D"),
            publicationRevision = 2,
            tagPushed = true,
            workflowRunId = 12345L,
            targetVersion = "0.7.5.10",
            deployedSha = new string('b', 40)
        });

        var auditEvent = Create(detailsJson);

        Assert.Equal(detailsJson, auditEvent.DetailsJson);
    }

    [Fact]
    public void Create_RejectsMalformedImmutableReleasePublicationMetadata()
    {
        var invalidDetails = new[]
        {
            """{"candidateId":"not-a-guid"}""",
            """{"workstationId":"not-a-guid"}""",
            """{"candidateSha":"not-a-git-sha"}""",
            """{"deployedSha":"not-a-git-sha"}""",
            """{"tagName":"release/../0.7.5.10"}""",
            """{"publicationRevision":1.5}""",
            """{"workflowRunId":0}"""
        };

        foreach (var detailsJson in invalidDetails)
            Assert.Throws<ArgumentException>(() => Create(detailsJson));
    }

    [Fact]
    public void Create_RejectsFreeFormReleaseAndReviewContent()
    {
        Assert.Throws<ArgumentException>(() =>
            Create(detailsJson: """{"rationale":"This is a free-form description."}"""));
        Assert.Throws<ArgumentException>(() =>
            Create(detailsJson: """{"note":"The reviewer wrote a long note."}"""));
        Assert.Throws<ArgumentException>(() =>
            Create(detailsJson: """{"targetVersion":"version with spaces"}"""));
    }

    [Fact]
    public void Create_RejectsUnlistedSensitiveMetadataKeys()
    {
        Assert.Throws<ArgumentException>(() => Create(detailsJson: """{"password":"secret"}"""));
    }

    [Fact]
    public void Create_AcceptsLandingEvidenceWithoutFreeFormContent()
    {
        var sourceSha = new string('a', 40);
        var treeSha = new string('b', 40);
        var commandHash = new string('c', 64);
        var mergeSha = new string('d', 40);
        var details = new
        {
            previousState = "ExecutePending",
            newState = "Landed",
            resultCode = "Landed",
            sourceBranch = "beacon/task/0123456789abcdef0123456789abcdef",
            targetBranch = "main",
            sourceSha,
            reviewTreeSha = treeSha,
            reviewCommandHash = commandHash,
            targetHeadSha = new string('e', 40),
            candidateTreeSha = treeSha,
            candidateCheckOutputHash = commandHash,
            postMergeCheckOutputHash = commandHash,
            mergeCommitSha = mergeSha,
            reviewRunId = Guid.NewGuid().ToString("D"),
            deviceId = Guid.NewGuid().ToString("D")
        };
        var json = System.Text.Json.JsonSerializer.Serialize(details);

        var auditEvent = Create(detailsJson: json);

        Assert.Equal(json, auditEvent.DetailsJson);
    }

    [Fact]
    public void Create_RejectsLandingBranchNamesOutsideTheSafeBranchAlphabet()
    {
        Assert.Throws<ArgumentException>(() =>
            Create(detailsJson: """{"targetBranch":"main;rm -rf"}"""));
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
