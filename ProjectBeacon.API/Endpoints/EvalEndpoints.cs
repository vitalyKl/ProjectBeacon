namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API.Auth;
using Application.Evals;
using Microsoft.AspNetCore.Mvc;

public static class EvalEndpoints
{
    public static IEndpointRouteBuilder MapEvalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/projects/{projectId:guid}/evals/pair", StartPair).RequireAuthorization().DisableAntiforgery();
        return app;
    }

    private static async Task<IResult> StartPair(
        Guid projectId,
        [FromBody] EvalPairBody body,
        EvalPairHandler handler,
        HttpContext ctx,
        CancellationToken ct)
    {
        var actor = ctx.GetActor();
        if (actor.UserId is null)
            return ProblemResults.Unauthorized();

        var result = await handler.HandleAsync(new EvalPairCommand(new EvalPairRequest(
            projectId,
            body.TaskId,
            body.DeviceId,
            actor.UserId.Value,
            body.Prompt,
            body.Path,
            body.Model,
            body.BudgetTokens,
            body.CheckCommand,
            body.Temperature,
            body.ReasoningEffort,
            body.ToolPermissions,
            body.TimeoutSeconds,
            body.RepoRevision)), ct);

        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    public record EvalPairBody(
        Guid TaskId,
        Guid DeviceId,
        string Prompt,
        string? Path = null,
        string? Model = null,
        int? BudgetTokens = null,
        string? CheckCommand = null,
        double? Temperature = null,
        string? ReasoningEffort = null,
        string? ToolPermissions = null,
        int? TimeoutSeconds = null,
        string? RepoRevision = null);
}
