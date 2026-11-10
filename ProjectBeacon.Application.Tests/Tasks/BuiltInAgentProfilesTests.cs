namespace ProjectBeacon.Application.Tests;

using Application.Tasks;
using Domain.Common;

public sealed class BuiltInAgentProfilesTests
{
    [Fact]
    public void CatalogContainsNineBuiltInProfilesWithRequiredLifecycleTools()
    {
        Assert.Equal(9, BuiltInAgentProfiles.All.Count);
        var required = new[]
        {
            McpToolNames.SubtaskReportResult,
            McpToolNames.PipelineFailSubtask,
            McpToolNames.WorkspaceWrite
        };

        foreach (var profile in BuiltInAgentProfiles.All)
        {
            Assert.All(required, tool => Assert.Contains(tool, profile.RequiredTools));
            Assert.All(profile.Tools, tool =>
            {
                Assert.True(McpToolCatalog.TryResolve(tool, out var descriptor), $"Missing MCP tool {tool}.");
                Assert.NotEqual(McpToolScope.Admin, descriptor!.Scope);
            });
            Assert.DoesNotContain(profile.Tools, tool => tool is
                McpToolNames.TaskCreateSubtask
                or McpToolNames.PipelineStartActor
                or McpToolNames.PipelineLaunchSession
                or McpToolNames.PipelineStartReview);
        }
    }

    [Fact]
    public void ReadOnlyAndCommandPoliciesMatchTheBuiltInProfileIntent()
    {
        var explorer = AssertProfile("explorer");
        Assert.Equal(AgentProfileMode.Act, explorer.Mode);
        Assert.False(explorer.CommandsEnabled);
        Assert.DoesNotContain(McpToolNames.WriteFile, explorer.Tools);
        Assert.DoesNotContain(McpToolNames.ApplyPatch, explorer.Tools);

        var verifier = AssertProfile("verifier");
        Assert.Equal(AgentProfileMode.Act, verifier.Mode);
        Assert.True(verifier.CommandsEnabled);
        Assert.DoesNotContain(McpToolNames.WriteFile, verifier.Tools);
        Assert.DoesNotContain(McpToolNames.ApplyPatch, verifier.Tools);

        Assert.Contains(McpToolNames.ListContextNodes, verifier.Tools);
        Assert.Contains(McpToolNames.GetContextNode, verifier.Tools);
        Assert.Contains(McpToolNames.ListDecisions, verifier.Tools);
        Assert.Contains(McpToolNames.GetDecision, verifier.Tools);
        Assert.Contains(McpToolNames.ListConstraints, verifier.Tools);
        Assert.Contains(McpToolNames.GetConstraint, verifier.Tools);
        Assert.DoesNotContain(McpToolNames.ContextCreateDraft, verifier.Tools);
        Assert.DoesNotContain(McpToolNames.KnowledgePropose, verifier.Tools);
        Assert.DoesNotContain(McpToolNames.KnowledgeProposals, verifier.Tools);
        Assert.DoesNotContain(McpToolNames.KnowledgeWithdraw, verifier.Tools);

        Assert.DoesNotContain(McpToolNames.KnowledgePropose, explorer.Tools);
        Assert.DoesNotContain(McpToolNames.KnowledgeProposals, explorer.Tools);
        Assert.DoesNotContain(McpToolNames.KnowledgeWithdraw, explorer.Tools);

        var knowledge = AssertProfile("knowledge-curator");
        Assert.Contains(McpToolNames.ContextCreateDraft, knowledge.Tools);
        Assert.Contains(McpToolNames.ConstraintPropose, knowledge.Tools);
        Assert.Contains(McpToolNames.DecisionPropose, knowledge.Tools);
        Assert.Contains(McpToolNames.KnowledgePropose, knowledge.Tools);
        Assert.Contains(McpToolNames.KnowledgeProposals, knowledge.Tools);
        Assert.Contains(McpToolNames.KnowledgeWithdraw, knowledge.Tools);
        Assert.Contains("current baseRevision", knowledge.Instruction, StringComparison.Ordinal);
        Assert.Contains("complete target-specific payload", knowledge.Instruction, StringComparison.Ordinal);
        Assert.Contains("human accepts it", knowledge.Instruction, StringComparison.Ordinal);
        Assert.Contains("never include secrets or credentials", knowledge.Instruction, StringComparison.Ordinal);
        Assert.DoesNotContain(McpToolNames.UpsertContextNode, knowledge.Tools);
        Assert.DoesNotContain(McpToolNames.ActivateConstraint, knowledge.Tools);
        Assert.DoesNotContain(McpToolNames.AcceptDecision, knowledge.Tools);
        Assert.DoesNotContain(McpToolNames.DeprecateDecision, knowledge.Tools);
        Assert.DoesNotContain(McpToolNames.SupersedeDecision, knowledge.Tools);

        var docs = AssertProfile("docs-writer");
        Assert.False(docs.CommandsEnabled);
        Assert.Equal(AgentProfilePathPolicy.Defaults, docs.PathPolicy);
        Assert.Contains(McpToolNames.KnowledgePropose, docs.Tools);
        Assert.Contains(McpToolNames.KnowledgeProposals, docs.Tools);
        Assert.Contains(McpToolNames.KnowledgeWithdraw, docs.Tools);
        Assert.Contains(McpToolNames.HashFile, docs.Tools);
        Assert.Contains(McpToolNames.HashText, docs.Tools);
        Assert.DoesNotContain(McpToolNames.WriteFile, docs.Tools);
        Assert.DoesNotContain(McpToolNames.ApplyPatch, docs.Tools);
        Assert.Contains("must not enter the live working tree before approval", docs.Instruction, StringComparison.Ordinal);
        Assert.Contains("targetKind RepoPatch", docs.Instruction, StringComparison.Ordinal);
        Assert.Contains("hash_file", docs.Instruction, StringComparison.Ordinal);
        Assert.Contains("hash_text", docs.Instruction, StringComparison.Ordinal);
        Assert.Contains("baseRevision", docs.Instruction, StringComparison.Ordinal);
        Assert.Contains("not applied until a human accepts", docs.Instruction, StringComparison.Ordinal);
        Assert.Contains("allowed paths", docs.Instruction, StringComparison.Ordinal);
        Assert.True(BuiltInAgentProfiles.IsWithinDefaults("docs/architecture.md", docs.DefaultPaths));
        Assert.True(BuiltInAgentProfiles.IsWithinDefaults("README.md", docs.DefaultPaths));
        Assert.False(BuiltInAgentProfiles.IsWithinDefaults("../secrets.txt", docs.DefaultPaths));
        Assert.False(BuiltInAgentProfiles.IsWithinDefaults("/etc/passwd", docs.DefaultPaths));
        Assert.False(BuiltInAgentProfiles.IsSafeRelativePath("../secrets.txt"));
        Assert.False(BuiltInAgentProfiles.IsSafeRelativePath("src/../../secrets.txt"));
        Assert.False(BuiltInAgentProfiles.IsSafeRelativePath(@"C:\\temp\\secrets.txt"));
        Assert.False(BuiltInAgentProfiles.IsSafeRelativePath("/etc/passwd"));
        Assert.True(BuiltInAgentProfiles.IsSafeRelativePath("src/Feature.cs"));
        Assert.True(BuiltInAgentProfiles.IsSafeRelativePath("src/"));
    }

    [Fact]
    public void SharedPipelineToolPolicyKeepsRoleGrantsAligned()
    {
        Assert.DoesNotContain(McpToolNames.PipelineStartActor, PipelineToolPolicy.PlannerTools);
        Assert.DoesNotContain(McpToolNames.PipelineLaunchSession, PipelineToolPolicy.PlannerTools);
        Assert.Contains(McpToolNames.PipelineStartReview, PipelineToolPolicy.PlannerTools);
        Assert.Contains(McpToolNames.TaskPipelineStatus, PipelineToolPolicy.ActorWorkspaceTools);
        Assert.Contains(McpToolNames.ListContextNodes, PipelineToolPolicy.ActorWorkspaceTools);
        Assert.Contains(McpToolNames.GetContextNode, PipelineToolPolicy.ActorWorkspaceTools);
        Assert.Contains(McpToolNames.ContextCreateDraft, PipelineToolPolicy.ActorWorkspaceTools);
        Assert.Contains(McpToolNames.ConstraintPropose, PipelineToolPolicy.ActorWorkspaceTools);
        Assert.Contains(McpToolNames.DecisionPropose, PipelineToolPolicy.ActorWorkspaceTools);
        Assert.Contains(McpToolNames.HashFile, PipelineToolPolicy.ActorWorkspaceTools);
        Assert.Contains(McpToolNames.HashText, PipelineToolPolicy.ActorWorkspaceTools);
        Assert.Contains(McpToolNames.TaskPipelineStatus, PipelineToolPolicy.AgentProfileContextTools);
        Assert.Contains(McpToolNames.ListContextNodes, PipelineToolPolicy.AgentProfileContextTools);
        Assert.Contains(McpToolNames.GetContextNode, PipelineToolPolicy.AgentProfileContextTools);
        Assert.Contains(McpToolNames.GetDecision, PipelineToolPolicy.AgentProfileContextTools);
        Assert.Contains(McpToolNames.GetConstraint, PipelineToolPolicy.AgentProfileContextTools);
        Assert.Contains(McpToolNames.TaskListAttachments, PipelineToolPolicy.AgentProfileContextTools);
        Assert.Contains(McpToolNames.TaskReadAttachment, PipelineToolPolicy.AgentProfileContextTools);
        Assert.DoesNotContain(McpToolNames.ActivateConstraint, PipelineToolPolicy.AgentProfileKnowledgeWriteTools);
        Assert.DoesNotContain(McpToolNames.AcceptDecision, PipelineToolPolicy.AgentProfileKnowledgeWriteTools);
        foreach (var profile in BuiltInAgentProfiles.All)
        {
            Assert.All(profile.RequiredTools, tool => Assert.Contains(tool, PipelineToolPolicy.AgentProfileRequiredTools));
            Assert.DoesNotContain(profile.Tools, tool => tool is McpToolNames.PipelineStartActor or McpToolNames.PipelineLaunchSession);
        }
    }

    [Fact]
    public void ProfilesExposeArtifactUploadOnlyAsAnOptionalGrant()
    {
        foreach (var profile in BuiltInAgentProfiles.All)
        {
            Assert.Contains(McpToolNames.TaskAttachFile, profile.OptionalTools);
            Assert.DoesNotContain(McpToolNames.TaskAttachFile, profile.Tools);
        }
    }

    [Fact]
    public void UnknownProfileDoesNotSilentlyResolveToADefault()
    {
        Assert.Null(BuiltInAgentProfiles.Get("unknown-profile"));
    }

    private static BuiltInAgentProfile AssertProfile(string key)
    {
        var profile = BuiltInAgentProfiles.Get(key);
        Assert.NotNull(profile);
        return profile!;
    }
}
