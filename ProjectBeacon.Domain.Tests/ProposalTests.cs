namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Content;
using ProjectBeacon.Domain.Enums;

public sealed class ProposalTests
{
    private static readonly DateTime CreatedAt = new(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc);
    private const string BaseHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static Proposal Create(Guid? subtaskId = null, Guid? sessionId = null)
        => Proposal.Create(
            Guid.NewGuid(),
            taskId: subtaskId.HasValue ? Guid.NewGuid() : null,
            subtaskId,
            sessionId,
            ContentTargetKind.ProjectDescription,
            Guid.NewGuid(),
            ProposalOperation.Update,
            4,
            BaseHash,
            """{"description":"New description"}""",
            "Clarifies the project purpose.",
            ProposalAuthorKind.Human,
            Guid.NewGuid(),
            null,
            null,
            "Human",
            Guid.NewGuid().ToString("N"),
            CreatedAt);

    [Fact]
    public void Create_StartsPendingAndUsesFixedThirtyDayExpiry()
    {
        var proposal = Create();

        Assert.NotEqual(Guid.Empty, proposal.Id);
        Assert.Equal(ProposalStatus.Pending, proposal.Status);
        Assert.Equal(4, proposal.BaseRevision);
        Assert.Equal(BaseHash, proposal.BaseHash);
        Assert.Equal(CreatedAt, proposal.CreatedAt);
        Assert.Equal(CreatedAt.AddDays(30), proposal.ExpiresAt);
        Assert.Equal(1, proposal.StateRevision);
        Assert.Null(proposal.AppliedRevision);
        Assert.Null(proposal.DecidedByUserId);
    }

    [Fact]
    public void Accept_StoresDecisionAndAppliedRevision_AndCannotBeRepeatedAsTransition()
    {
        var proposal = Create();
        var humanId = Guid.NewGuid();
        var acceptedPayload = """{"description":"Edited by reviewer"}""";

        proposal.Accept(humanId, 5, acceptedPayload, CreatedAt.AddMinutes(10));

        Assert.Equal(ProposalStatus.Accepted, proposal.Status);
        Assert.Equal(humanId, proposal.DecidedByUserId);
        Assert.Equal(5, proposal.AppliedRevision);
        Assert.Equal(acceptedPayload, proposal.AcceptedPayloadJson);
        Assert.Equal(CreatedAt.AddMinutes(10), proposal.DecidedAt);
        Assert.Equal(2, proposal.StateRevision);
        Assert.Throws<InvalidOperationException>(() =>
            proposal.Accept(humanId, 6, null, CreatedAt.AddMinutes(11)));
    }

    [Fact]
    public void Reject_RequiresHumanAndStoresOptionalNote()
    {
        var proposal = Create();
        var humanId = Guid.NewGuid();

        proposal.Reject(humanId, "Duplicate proposal.", CreatedAt.AddHours(1));

        Assert.Equal(ProposalStatus.Rejected, proposal.Status);
        Assert.Equal(humanId, proposal.DecidedByUserId);
        Assert.Equal("Duplicate proposal.", proposal.DecisionNote);
        Assert.Equal(2, proposal.StateRevision);
    }

    [Fact]
    public void WithdrawAndSupersede_AreTerminalPendingTransitions()
    {
        var withdrawn = Create();
        withdrawn.Withdraw(CreatedAt.AddHours(1));
        Assert.Equal(ProposalStatus.Withdrawn, withdrawn.Status);

        var superseded = Create(Guid.NewGuid(), Guid.NewGuid());
        var replacementId = Guid.NewGuid();
        superseded.Supersede(replacementId, CreatedAt.AddHours(2));
        Assert.Equal(ProposalStatus.Superseded, superseded.Status);
        Assert.Equal(replacementId, superseded.SupersededById);
        Assert.Throws<InvalidOperationException>(() =>
            superseded.Withdraw(CreatedAt.AddHours(3)));
    }

    [Fact]
    public void Expire_ChangesOnlyPendingItemsAtOrAfterExpiry()
    {
        var proposal = Create();

        Assert.False(proposal.Expire(proposal.ExpiresAt.AddTicks(-1)));
        Assert.Equal(ProposalStatus.Pending, proposal.Status);

        Assert.True(proposal.Expire(proposal.ExpiresAt));
        Assert.Equal(ProposalStatus.Expired, proposal.Status);
        Assert.Equal(proposal.ExpiresAt, proposal.DecidedAt);
        Assert.False(proposal.Expire(proposal.ExpiresAt.AddDays(1)));
    }

    [Fact]
    public void MarkConflicted_PreservesObservedRevisionAndReason()
    {
        var proposal = Create();

        proposal.MarkConflicted(7, "BaseRevisionChanged", CreatedAt.AddMinutes(5));

        Assert.Equal(ProposalStatus.Conflicted, proposal.Status);
        Assert.Equal(7, proposal.ConflictRevision);
        Assert.Equal("BaseRevisionChanged", proposal.ConflictReason);
        Assert.Equal(2, proposal.StateRevision);
    }

    [Fact]
    public void CreateAndDelete_ValidateSupportedTargetOperationPairs()
    {
        const string emptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        var reservedMilestoneId = Guid.NewGuid();
        var milestoneCreate = Proposal.Create(
            Guid.NewGuid(), null, null, null,
            ContentTargetKind.Milestone, reservedMilestoneId,
            ProposalOperation.Create, 0, emptyHash,
            """{"name":"Alpha","description":null,"order":1}""",
            "Adds the first roadmap milestone.",
            ProposalAuthorKind.Human, Guid.NewGuid(), null, null, "Human",
            "milestone-create-" + Guid.NewGuid().ToString("N"), CreatedAt);

        Assert.Equal(ProposalOperation.Create, milestoneCreate.Operation);
        Assert.Equal(reservedMilestoneId, milestoneCreate.TargetId);
        Assert.Equal(0, milestoneCreate.BaseRevision);
        Assert.Equal(ProposalStatus.Pending, milestoneCreate.Status);

        var sectionDelete = Proposal.Create(
            Guid.NewGuid(), null, null, null,
            ContentTargetKind.ContextSection, Guid.NewGuid(),
            ProposalOperation.Delete, 4, BaseHash, "{}",
            "Removes the obsolete reviewed section.",
            ProposalAuthorKind.Human, Guid.NewGuid(), null, null, "Human",
            "section-delete-" + Guid.NewGuid().ToString("N"), CreatedAt);
        var decisionDelete = Proposal.Create(
            Guid.NewGuid(), null, null, null,
            ContentTargetKind.Decision, Guid.NewGuid(),
            ProposalOperation.Delete, 2, BaseHash, "{}",
            "Deprecates the obsolete accepted decision.",
            ProposalAuthorKind.Human, Guid.NewGuid(), null, null, "Human",
            "decision-delete-" + Guid.NewGuid().ToString("N"), CreatedAt);
        var constraintDelete = Proposal.Create(
            Guid.NewGuid(), null, null, null,
            ContentTargetKind.Constraint, Guid.NewGuid(),
            ProposalOperation.Delete, 3, BaseHash, "{}",
            "Rejects the obsolete active constraint.",
            ProposalAuthorKind.Human, Guid.NewGuid(), null, null, "Human",
            "constraint-delete-" + Guid.NewGuid().ToString("N"), CreatedAt);
        var milestoneDelete = Proposal.Create(
            Guid.NewGuid(), null, null, null,
            ContentTargetKind.Milestone, Guid.NewGuid(),
            ProposalOperation.Delete, 5, BaseHash, "{}",
            "Removes the completed milestone.",
            ProposalAuthorKind.Human, Guid.NewGuid(), null, null, "Human",
            "milestone-delete-" + Guid.NewGuid().ToString("N"), CreatedAt);

        Assert.All(
            new[] { sectionDelete, decisionDelete, constraintDelete, milestoneDelete },
            proposal => Assert.Equal(ProposalOperation.Delete, proposal.Operation));

        Assert.Throws<ArgumentException>(() => Proposal.Create(
            Guid.NewGuid(), null, null, null,
            ContentTargetKind.ProjectDescription, Guid.NewGuid(),
            ProposalOperation.Create, 0, emptyHash,
            """{"description":"new"}""", "Creates unsupported target.",
            ProposalAuthorKind.Human, Guid.NewGuid(), null, null, "Human",
            "bad-create-" + Guid.NewGuid().ToString("N"), CreatedAt));
        Assert.Throws<ArgumentException>(() => Proposal.Create(
            Guid.NewGuid(), null, null, null,
            ContentTargetKind.TaskDescription, Guid.NewGuid(),
            ProposalOperation.Delete, 1, BaseHash, "{}",
            "Deletes unsupported target.",
            ProposalAuthorKind.Human, Guid.NewGuid(), null, null, "Human",
            "bad-delete-" + Guid.NewGuid().ToString("N"), CreatedAt));
        Assert.Throws<ArgumentException>(() => Proposal.Create(
            Guid.NewGuid(), null, null, null,
            ContentTargetKind.Milestone, Guid.NewGuid(),
            ProposalOperation.Create, 1, BaseHash,
            """{"name":"Alpha","description":null,"order":1}""",
            "Create must use revision zero.",
            ProposalAuthorKind.Human, Guid.NewGuid(), null, null, "Human",
            "bad-create-revision-" + Guid.NewGuid().ToString("N"), CreatedAt));
    }

    [Fact]
    public void Create_RequiresValidJsonObjectAndBoundedRationale()
    {
        Assert.Throws<ArgumentException>(() => Proposal.ValidatePayload("[]"));
        Assert.Throws<ArgumentException>(() => Proposal.ValidatePayload("not-json"));
        Assert.Throws<ArgumentException>(() => Proposal.ValidatePayload(new string('x', Proposal.MaxPayloadLength + 1)));
        Assert.Throws<ArgumentException>(() => Proposal.Create(
            Guid.NewGuid(), null, null, null, ContentTargetKind.ProjectDescription, Guid.NewGuid(),
            ProposalOperation.Update, 1, BaseHash, """{"description":"x"}""",
            new string('r', Proposal.MaxRationaleLength + 1),
            ProposalAuthorKind.Human, Guid.NewGuid(), null, null, "Human",
            Guid.NewGuid().ToString("N"), CreatedAt));
    }
}
