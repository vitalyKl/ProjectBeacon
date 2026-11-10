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
