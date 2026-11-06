namespace ProjectBeacon.API.Endpoints;

using Microsoft.AspNetCore.Mvc;
using ProjectBeacon.API.Auth;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Attachments;

public static class TaskAttachmentEndpoints
{
    public const long MaxRequestBytes = PhysicalAttachmentService.MaxFileLength + 1024 * 1024;

    public static IEndpointRouteBuilder MapTaskAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/projects/{projectId:guid}/tasks/{taskId:guid}/attachments");
        group.MapGet("", List).RequireAuthorization().RequireHumanOrApiToken(ApiTokenCapability.TaskRead);
        group.MapPost("", Upload).RequireAuthorization().DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes))
            .RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        group.MapGet("/{attachmentId:guid}", Download).RequireAuthorization()
            .RequireHumanOrApiToken(ApiTokenCapability.TaskRead);
        return app;
    }

    private static async Task<IResult> List(
        Guid projectId, Guid taskId, IAttachmentService attachments, CancellationToken ct)
        => Results.Ok(await attachments.ListTaskAsync(projectId, taskId, ct));

    private static async Task<IResult> Upload(
        Guid projectId, Guid taskId, IFormFile? file, IAttachmentService attachments,
        HttpContext context, CancellationToken ct)
    {
        if (file is null)
            return ProblemResults.Bad("A file is required.");
        if (file.Length is <= 0 or > PhysicalAttachmentService.MaxFileLength)
            return ProblemResults.Bad($"Files must be between 1 byte and {PhysicalAttachmentService.MaxFileLength} bytes.");
        await using var stream = file.OpenReadStream();
        var result = await attachments.UploadTaskAsync(projectId, taskId, context.GetActor(),
            stream, file.FileName, file.ContentType, file.Length, ct);
        return result is null
            ? Results.NotFound()
            : Results.Created($"/v1/projects/{projectId:D}/tasks/{taskId:D}/attachments/{result.Id:D}", result);
    }

    private static async Task<IResult> Download(
        Guid projectId, Guid taskId, Guid attachmentId, IAttachmentService attachments,
        HttpContext context, CancellationToken ct)
    {
        var result = await attachments.ReadTaskAsync(projectId, taskId, attachmentId, ct);
        if (result is null)
            return Results.NotFound();
        var (metadata, bytes) = result.Value;
        var inline = context.Request.Query.ContainsKey("inline")
            && metadata.ContentType is "image/png" or "image/jpeg" or "image/gif" or "image/webp";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(bytes, metadata.ContentType, inline ? null : metadata.FileName, enableRangeProcessing: false);
    }
}
