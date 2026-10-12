namespace ProjectBeacon.Application.Tests;

using Application.Devices;
using Application.Tasks;
using Domain.Entities.Devices;
using Domain.Entities.Evals;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.LlamaSwap;
using Microsoft.Data.Sqlite;

public sealed partial class PipelineHandlerTests
{
    [Fact]
    public async Task SelfReportedPass_DoesNotProve()
    {
        var (projectId, task) = await SeedTaskAsync("Check");
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        using (TenantScope.EnterUnscoped())
        {
            _db.ReviewRuns.Add(ReviewRun.Start(projectId, task.Id, ReviewerType.Agent, Guid.NewGuid()));
            await _db.SaveChangesAsync();
        }

        await ProveReviewAsync(projectId, task.Id, factory, 3);

        using (TenantScope.EnterUnscoped())
        {
            var run = _db.ReviewRuns.Single(r => r.TaskId == task.Id);
            await _db.Entry(run).ReloadAsync();
            Assert.Equal(ReviewRunStatus.Failed, run.Status);
            Assert.False(run.IsCheckProof());
        }
    }

    [Fact]
    public async Task HappyPath_PlannerActorReviewApprove_ClosesTaskDone()
    {
        var (projectId, task) = await SeedTaskAsync("Ship the report", "Generate the monthly report");
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));

        var start = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)));
        Assert.True(start.Success, start.Error);
        Assert.Equal(PipelineRole.Planner, start.Value!.Role);
        Assert.Equal(SessionStatus.Ready, start.Value.Status);
        Assert.Contains(PipelineRole.Planner, spawner.Calls.Select(c => c.Role));

        var sub1 = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "Collect data", new[] { "beacon_search_code" }, new[] { "data/" })));
        Assert.True(sub1.Success, sub1.Error);
        var sub2 = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "Render charts")));
        Assert.True(sub2.Success, sub2.Error);

        var actor1 = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub1.Value!.Id)));
        Assert.True(actor1.Success, actor1.Error);
        Assert.Equal(PipelineRole.Actor, actor1.Value!.Role);
        Assert.Equal(sub1.Value.Id, actor1.Value.SubtaskId);

        var launched1 = await new LaunchSessionHandler(factory)
            .HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(actor1.Value.Id)));
        Assert.True(launched1.Success, launched1.Error);
        Assert.Equal(SessionStatus.Active, launched1.Value!.Status);
        Assert.NotNull(launched1.Value.LaunchedAt);

        var res1 = await new ReportSubtaskResultHandler(factory)
            .HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(task.Id, sub1.Value.Id, "diff://sub1", "Data collected")));
        Assert.True(res1.Success, res1.Error);
        Assert.Contains(res1.Value!.Subtasks, s => s.Id == sub1.Value!.Id && s.Status == SubtaskStatus.Done);
        Assert.Contains(res1.Value.Sessions, s => s.Id == actor1.Value!.Id && s.Status == SessionStatus.Closed);

        var actor2 = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub2.Value!.Id)));
        Assert.True(actor2.Success, actor2.Error);
        var res2 = await new ReportSubtaskResultHandler(factory)
            .HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(task.Id, sub2.Value!.Id, "diff://sub2", "Charts rendered")));
        Assert.True(res2.Success, res2.Error);

        var review = await new StartReviewHandler(factory, spawner)
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(task.Id)));
        Assert.True(review.Success, review.Error);
        Assert.Equal(PipelineRole.Review, review.Value!.Role);

        var verdict = await new RecordReviewVerdictHandler(factory)
            .HandleAsync(new RecordReviewVerdictCommand(new RecordReviewVerdictRequest(task.Id, ReviewVerdictKind.Approve, "Looks solid")));
        Assert.True(verdict.Success, verdict.Error);
        Assert.Equal(TaskPipelineStage.Approved, verdict.Value!.Stage);
        Assert.Equal(TaskItemStatus.InProgress, verdict.Value.Status);

        var blocked = await new ApprovePipelineHandler(factory)
            .HandleAsync(new ApprovePipelineCommand(new ApprovePipelineRequest(task.Id)));
        Assert.False(blocked.Success);
        Assert.Equal("Close requires a completed review check.", blocked.Error);

        await ProveReviewAsync(projectId, task.Id, factory, 0);

        var closed = await new ApprovePipelineHandler(factory)
            .HandleAsync(new ApprovePipelineCommand(new ApprovePipelineRequest(task.Id)));
        Assert.True(closed.Success, closed.Error);
        Assert.Equal(TaskPipelineStage.Closed, closed.Value!.Stage);
        Assert.Equal(TaskItemStatus.Done, closed.Value.Status);
        Assert.Equal("Looks solid", closed.Value.ReviewNotes);

        var state = await new GetPipelineHandler(factory)
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)));
        Assert.True(state.Success, state.Error);
        Assert.Equal(TaskPipelineStage.Closed, state.Value!.Stage);
    }
    [Fact]
    public async Task Prompts_AreIsolated_PerRole()
    {
        var (projectId, task) = await SeedTaskAsync("Secret task title", "Secret task description");
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));

        var start = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)));
        Assert.True(start.Success, start.Error);
        Assert.Contains("PipelineProject", start.Value!.PromptContext);
        Assert.Contains("Secret task title", start.Value.PromptContext);
        Assert.Contains("Secret task description", start.Value.PromptContext);

        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "Do the thing", new[] { "beacon_search_code" }, new[] { "src/" })));
        Assert.True(sub.Success, sub.Error);

        var actor = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value!.Id)));
        Assert.True(actor.Success, actor.Error);
        var actorPrompt = actor.Value!.PromptContext;
        Assert.Contains("Do the thing", actorPrompt);
        Assert.Contains("beacon_search_code", actorPrompt);
        Assert.Contains("src/", actorPrompt);
        Assert.DoesNotContain("Secret task title", actorPrompt);
        Assert.DoesNotContain("PipelineProject", actorPrompt);

        var reported = await new ReportSubtaskResultHandler(factory)
            .HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(task.Id, sub.Value.Id, "diff://a", "Done it")));
        Assert.True(reported.Success, reported.Error);

        var review = await new StartReviewHandler(factory, spawner)
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(task.Id)));
        Assert.True(review.Success, review.Error);
        var reviewPrompt = review.Value!.PromptContext;
        Assert.Contains("Secret task title", reviewPrompt);
        Assert.Contains("[DONE]", reviewPrompt);
        Assert.Contains("diff://a", reviewPrompt);
        Assert.DoesNotContain("Role: actor", reviewPrompt);
    }

    [Fact]
    public async Task ActorSession_BindsBackendAndBuildsLlamaCppLaunchSpec()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var backend = LocalModelBackend.Create("local-model", ModelBackendType.LlamaCpp, "llama-server -m m.gguf --port ${PORT}", 4096, 60, projectId);
        using (TenantScope.EnterUnscoped())
        {
            _db.LocalModelBackends.Add(backend);
            _db.RoleBindings.Add(RoleBinding.Create(PipelineRole.Actor, backend.Id, projectId));
            await _db.SaveChangesAsync();
        }
        var spawner = new ManualSessionSpawner(new LlamaSwapCatalog(8123));
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));

        var start = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)));
        Assert.True(start.Success, start.Error);
        Assert.Null(start.Value!.ModelBackendId);
        Assert.Null(start.Value.LaunchSpec);

        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work")));
        Assert.True(sub.Success, sub.Error);
        var actor = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value!.Id)));
        Assert.True(actor.Success, actor.Error);
        Assert.Equal(backend.Id, actor.Value!.ModelBackendId);
        Assert.Contains("--port 8123", actor.Value.LaunchSpec!);
        Assert.Contains("--ctx-size 4096", actor.Value.LaunchSpec);
        Assert.Contains("--no-reasoning-preserve", actor.Value.LaunchSpec);
    }

    [Fact]
    public async Task LaunchSpec_OpenAiCompatible_OmitsReasoningFlag()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var backend = LocalModelBackend.Create("openai-compat", ModelBackendType.OpenAiCompatible, "server --port ${PORT}", 0, 0, projectId);
        using (TenantScope.EnterUnscoped())
        {
            _db.LocalModelBackends.Add(backend);
            _db.RoleBindings.Add(RoleBinding.Create(PipelineRole.Actor, backend.Id, projectId));
            await _db.SaveChangesAsync();
        }
        var spawner = new ManualSessionSpawner(new LlamaSwapCatalog(8123));
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));

        await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)));
        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work")));
        Assert.True(sub.Success, sub.Error);
        var actor = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value!.Id)));
        Assert.True(actor.Success, actor.Error);
        Assert.Equal(backend.Id, actor.Value!.ModelBackendId);
        Assert.Contains("--port 8123", actor.Value.LaunchSpec!);
        Assert.DoesNotContain("--no-reasoning-preserve", actor.Value.LaunchSpec);
        Assert.DoesNotContain("--ctx-size", actor.Value.LaunchSpec);
    }
}
