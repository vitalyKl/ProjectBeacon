namespace ProjectBeacon.API;

using Application.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

public static class ProblemResults
{
    public static IResult FromResult(this Result result, int status = StatusCodes.Status400BadRequest, string? title = null)
    {
        var details = new ProblemDetails
        {
            Status = status,
            Title = title ?? DefaultTitle(status),
            Detail = result.Error
        };
        details.Type = TypeUrl(status);
        return Results.Problem(details);
    }

    public static IResult FromResult<T>(this Result<T> result, int status = StatusCodes.Status400BadRequest, string? title = null)
    {
        var details = new ProblemDetails
        {
            Status = status,
            Title = title ?? DefaultTitle(status),
            Detail = result.Error
        };
        details.Type = TypeUrl(status);
        return Results.Problem(details);
    }

    public static IResult NotFound(string? detail = null)
    {
        var status = StatusCodes.Status404NotFound;
        var details = new ProblemDetails
        {
            Status = status,
            Title = "Not Found",
            Detail = detail ?? "The requested resource was not found."
        };
        details.Type = TypeUrl(status);
        return Results.Problem(details);
    }

    public static IResult Conflict(string? detail = null)
    {
        var status = StatusCodes.Status409Conflict;
        var details = new ProblemDetails
        {
            Status = status,
            Title = "Conflict",
            Detail = detail ?? "The request conflicts with the current state."
        };
        details.Type = TypeUrl(status);
        return Results.Problem(details);
    }

    public static IResult Bad(string detail)
    {
        var status = StatusCodes.Status400BadRequest;
        var details = new ProblemDetails
        {
            Status = status,
            Title = DefaultTitle(status),
            Detail = detail
        };
        details.Type = TypeUrl(status);
        return Results.Problem(details);
    }

    public static IResult Validation(IList<string> errors)
    {
        var status = StatusCodes.Status400BadRequest;
        var details = new ProblemDetails
        {
            Status = status,
            Title = "Validation Failed",
            Detail = errors.Count == 1 ? errors[0] : string.Join("; ", errors)
        };
        details.Type = TypeUrl(status);
        if (errors.Count > 1)
            details.Extensions["errors"] = errors;
        return Results.Problem(details);
    }

    private static string DefaultTitle(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "Bad Request",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not Found",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status422UnprocessableEntity => "Unprocessable Entity",
        StatusCodes.Status500InternalServerError => "Internal Server Error",
        _ => "Error"
    };

    private static string TypeUrl(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        StatusCodes.Status401Unauthorized => "https://tools.ietf.org/html/rfc9110#section-15.5.2",
        StatusCodes.Status403Forbidden => "https://tools.ietf.org/html/rfc9110#section-15.5.6",
        StatusCodes.Status404NotFound => "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        StatusCodes.Status409Conflict => "https://tools.ietf.org/html/rfc9110#section-15.5.10",
        StatusCodes.Status422UnprocessableEntity => "https://tools.ietf.org/html/rfc9110#section-15.5.11",
        StatusCodes.Status500InternalServerError => "https://tools.ietf.org/html/rfc9110#section-15.6.1",
        _ => "about:blank"
    };
}
