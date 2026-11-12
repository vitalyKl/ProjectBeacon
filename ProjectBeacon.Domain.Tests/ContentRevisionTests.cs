namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Content;
using ProjectBeacon.Domain.Entities.Context;
using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Entities.Roadmap;
using ProjectBeacon.Domain.Enums;

public sealed class ContentRevisionTests
{
    [Fact]
    public void ContextSection_RevisionChangesOnlyWhenTitleOrBodyChanges()
    {
        var section = ContextSection.Create(
            "goals", "Goals", "Initial", Guid.NewGuid(), ContextScopeType.Project);

        Assert.Equal(1, section.Revision);

        section.Update(title: "Goals", bodyMarkdown: "Initial");
        Assert.Equal(1, section.Revision);

        section.Update(title: "Objectives", bodyMarkdown: "Updated");
        Assert.Equal(2, section.Revision);

        section.UpdateDraft("Objectives", "Updated", null, "actor-1");
        Assert.Equal(2, section.Revision);

        section.UpdateDraft("Objectives", "New draft", null, "actor-1");
        Assert.Equal(3, section.Revision);
    }

    [Fact]
    public void ContextSection_ImportedSourceIdentityChangesCreateANewRevision()
    {
        var section = ContextSection.Create(
            "agents", "AGENTS", "Guidance", Guid.NewGuid(), ContextScopeType.Project,
            source: ContextSource.ImportedAgentsMd, sourcePath: "AGENTS.md");

        section.UpdateImportedFile(
            "AGENTS", "Guidance", ContextSource.ImportedAgentsMd, "repo/AGENTS.md", "system", "repo/AGENTS.md");
        Assert.Equal(2, section.Revision);
        Assert.Equal("repo/AGENTS.md", section.SourcePath);

        section.UpdateImportedFile(
            "AGENTS", "Guidance", ContextSource.ImportedAgentsMd, "repo/AGENTS.md", "system", "repo/AGENTS.md");
        Assert.Equal(2, section.Revision);
    }

    [Fact]
    public void Decision_ContentAndLifecycleChangesIncrementRevision()
    {
        var decision = Decision.Create("Title", "Context", "Body", Guid.NewGuid());

        Assert.Equal(1, decision.Revision);

        decision.UpdateContent("Title", "Context", "Body", null);
        Assert.Equal(1, decision.Revision);

        decision.UpdateContent("Title 2", "Context", "Body", null);
        Assert.Equal(2, decision.Revision);

        decision.Accept();
        Assert.Equal(3, decision.Revision);

        decision.Deprecate();
        Assert.Equal(4, decision.Revision);
    }

    [Fact]
    public void Constraint_BodyAndLifecycleChangesIncrementRevision()
    {
        var constraint = Constraint.Create("Must use tests", ConstraintKind.Must, Guid.NewGuid());

        Assert.Equal(1, constraint.Revision);

        constraint.UpdateBody("Must use tests");
        Assert.Equal(1, constraint.Revision);

        constraint.UpdateBody("Must use automated tests");
        Assert.Equal(2, constraint.Revision);

        constraint.Activate();
        Assert.Equal(3, constraint.Revision);

        constraint.Reject();
        Assert.Equal(4, constraint.Revision);

        constraint.SetScopePath("src/");
        Assert.Equal(5, constraint.Revision);

        constraint.SetScopePath("src/");
        Assert.Equal(5, constraint.Revision);
    }

    [Fact]
    public void Milestone_ContentAndLifecycleChangesIncrementRevision()
    {
        var milestone = Milestone.Create("Release", "Initial description", Guid.NewGuid(), 1);

        Assert.Equal(1, milestone.Revision);

        milestone.Update(name: "Release", description: "Initial description", order: 1);
        Assert.Equal(1, milestone.Revision);

        milestone.Update(description: "Updated description");
        Assert.Equal(2, milestone.Revision);

        milestone.Close();
        Assert.Equal(3, milestone.Revision);

        milestone.Reopen();
        Assert.Equal(4, milestone.Revision);

        milestone.Update(order: 2);
        Assert.Equal(5, milestone.Revision);
    }

    [Fact]
    public void Project_OnlyDescriptionChangesIncrementRevision()
    {
        var project = Project.Create("Beacon", null, Guid.NewGuid());

        Assert.Equal(1, project.Revision);

        project.Update(name: "Renamed");
        Assert.Equal(1, project.Revision);

        project.Update(description: "Project description");
        Assert.Equal(2, project.Revision);

        project.Update(description: "Project description");
        Assert.Equal(2, project.Revision);

        project.Update(description: string.Empty);
        Assert.Equal(3, project.Revision);
    }

    [Fact]
    public void ContentVersion_PreservesTargetRevisionAndProposalLink()
    {
        var projectId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();

        var version = ContentVersion.Create(
            projectId,
            ContentTargetKind.ProjectDescription,
            targetId,
            3,
            """{"description":"accepted"}""",
            "Human",
            Guid.NewGuid().ToString(),
            proposalId);

        Assert.NotEqual(Guid.Empty, version.Id);
        Assert.Equal(projectId, version.ProjectId);
        Assert.Equal(ContentTargetKind.ProjectDescription, version.TargetKind);
        Assert.Equal(targetId, version.TargetId);
        Assert.Equal(3, version.Revision);
        Assert.Equal("""{"description":"accepted"}""", version.ContentJson);
        Assert.Equal("Human", version.ChangedByType);
        Assert.Equal(proposalId, version.ProposalId);
        Assert.NotEqual(default, version.At);
    }
}
