namespace ProjectBeacon.API.Endpoints;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using ProjectBeacon.API;
using ProjectBeacon.API.Auth;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Attachments;

public static class TaskAttachmentEndpoints
{
    public const long MaxRequestBytes = DatabaseAttachmentService.MaxFileLength + 1024 * 1024;

    public static IEndpointRouteBuilder MapTaskAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/projects/{projectId:guid}/tasks/{taskId:guid}/attachments");
        group.MapGet("", List).RequireAuthorization().RequireHumanOrApiToken(ApiTokenCapability.TaskRead);
        group.MapPost("", Upload).RequireAuthorization().DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes))
            .RequireHumanOrApiToken(ApiTokenCapability.TaskWrite)
            .RequireRateLimiting("attachment-upload");
        group.MapGet("/{attachmentId:guid}", Download).RequireAuthorization()
            .RequireHumanOrApiToken(ApiTokenCapability.TaskRead);
        return app;
    }

    private static async Task<IResult> List(
        Guid projectId, Guid taskId, IAttachmentService attachments, CancellationToken ct)
        => Results.Ok(await attachments.ListTaskAsync(projectId, taskId, ct));

    private static async Task<IResult> Upload(
        Guid projectId, Guid taskId, IAttachmentService attachments,
        HttpContext context, CancellationToken ct)
    {
        // Cookie-authenticated multipart requests have no automatic antiforgery
        // validation because the body is streamed manually. Require an exact Origin
        // match for ambient cookie credentials; a merely-present Authorization header
        // must not bypass this check when a bad bcn_ token falls back to the cookie scheme.
        if (UsesCookieAuthentication(context) && !IsSameOriginUpload(context.Request))
            return Results.Problem(
                detail: "Cookie-authenticated uploads must originate from this application origin.",
                statusCode: StatusCodes.Status403Forbidden,
                title: "Cross-origin upload rejected");

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var requestType)
            || !string.Equals(requestType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return ProblemResults.Bad("A multipart/form-data request is required.");

        var boundary = HeaderUtilities.RemoveQuotes(requestType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 128)
            return ProblemResults.Bad("The multipart boundary is missing or invalid.");

        var reader = new MultipartReader(boundary, context.Request.Body)
        {
            BodyLengthLimit = MaxRequestBytes,
            HeadersCountLimit = 16,
            HeadersLengthLimit = 16 * 1024
        };

        MultipartSection? section;
        try
        {
            section = await reader.ReadNextSectionAsync(ct);
        }
        catch (InvalidDataException ex)
        {
            return ProblemResults.Bad(ex.Message);
        }

        if (section is null
            || !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
            || !string.Equals(disposition.DispositionType.ToString(), "form-data", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(HeaderUtilities.RemoveQuotes(disposition.Name).ToString(), "file", StringComparison.OrdinalIgnoreCase))
            return ProblemResults.Bad("The multipart request must contain a file field named 'file'.");

        var fileName = GetSubmittedFileName(disposition);
        if (string.IsNullOrWhiteSpace(fileName))
            return ProblemResults.Bad("A file name is required.");

        try
        {
            // The service counts bytes while streaming and applies the same bound to
            // every caller. No IFormFile buffering or client-provided length is trusted.
            var result = await attachments.UploadTaskAsync(
                projectId, taskId, context.GetActor(), section.Body,
                fileName, section.ContentType, null, ct);

            return result is null
                ? Results.NotFound()
                : Results.Created($"/v1/projects/{projectId:D}/tasks/{taskId:D}/attachments/{result.Id:D}", result);
        }
        catch (AttachmentConcurrencyLimitException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many concurrent uploads");
        }
        catch (AttachmentQuotaExceededException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "Attachment quota exceeded");
        }
        catch (AttachmentRejectedException ex)
        {
            return ProblemResults.Bad(ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return ProblemResults.Bad(ex.Message);
        }
    }

    internal static bool UsesCookieAuthentication(HttpContext context) =>
        context.User.Identities.Any(identity =>
            identity.IsAuthenticated
            && string.Equals(
                identity.AuthenticationType,
                CookieAuthenticationDefaults.AuthenticationScheme,
                StringComparison.Ordinal));

    internal static bool IsSameOriginUpload(HttpRequest request)
    {
        var originValue = request.Headers["Origin"].ToString();
        if (!Uri.TryCreate(originValue, UriKind.Absolute, out var origin)
            || !string.IsNullOrEmpty(origin.UserInfo)
            || origin.AbsolutePath != "/"
            || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment)
            || string.IsNullOrWhiteSpace(request.Host.Host))
            return false;

        var requestPort = request.Host.Port
            ?? (string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        return string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.Host.TrimEnd('.'), request.Host.Host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            && origin.Port == requestPort;
    }

    private static string GetSubmittedFileName(ContentDispositionHeaderValue disposition)
    {
        var extended = HeaderUtilities.RemoveQuotes(disposition.FileNameStar).ToString();
        const string utf8Prefix = "UTF-8''";
        if (extended.StartsWith(utf8Prefix, StringComparison.OrdinalIgnoreCase))
            return Uri.UnescapeDataString(extended[utf8Prefix.Length..]);

        return HeaderUtilities.RemoveQuotes(disposition.FileName).ToString();
    }

    private static async Task<IResult> Download(
        Guid projectId, Guid taskId, Guid attachmentId, IAttachmentService attachments,
        HttpContext context, long? maxBytes, CancellationToken ct)
    {
        if (maxBytes is <= 0 or > DatabaseAttachmentService.MaxFileLength)
            return ProblemResults.Bad($"maxBytes must be between 1 and {DatabaseAttachmentService.MaxFileLength}.");

        (TaskAttachmentDto Attachment, byte[] Content)? result;
        try
        {
            result = await attachments.ReadTaskAsync(projectId, taskId, attachmentId, ct, maxBytes);
        }
        catch (AttachmentReadLimitExceededException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "Attachment exceeds the read limit");
        }

        if (result is null)
            return Results.NotFound();

        var (metadata, bytes) = result.Value;
        // ReadTaskAsync re-checks the payload signature, including legacy records.
        // PDF is deliberately never rendered inline; only verified raster images may be.
        var inline = context.Request.Query.ContainsKey("inline")
            && metadata.ContentType is "image/png" or "image/jpeg" or "image/gif" or "image/webp";

        var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
        disposition.SetHttpFileName(metadata.FileName);
        context.Response.Headers["Content-Disposition"] = disposition.ToString();
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
        context.Response.Headers["Cache-Control"] = "private, no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";

        return Results.File(bytes, metadata.ContentType, enableRangeProcessing: false);
    }
}
