namespace ProjectBeacon.API;

using Application.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

public static class ProblemResults
{
    public static IResult FromResult(this Result result, int status = StatusCodes.Status400BadRequest, string? title = null)
        => Problem(Resolve(result.Kind, status), title, result.Error);

    public static IResult FromResult<T>(this Result<T> result, int status = StatusCodes.Status400BadRequest, string? title = null)
        => Problem(Resolve(result.Kind, status), title, result.Error);

    public static IResult Unauthorized(string? detail = null) =>
        Problem(StatusCodes.Status401Unauthorized, null, detail ?? "Authentication is required.");

    public static IResult Forbidden(string? detail = null) =>
        Problem(StatusCodes.Status403Forbidden, null, detail ?? "Missing capability.");

    public static IResult TooManyRequests(string? detail = null) =>
        Problem(StatusCodes.Status429TooManyRequests, "Too Many Requests", detail ?? "Too many requests.");

    public static IResult NotFound(string? detail = null) =>
        Problem(StatusCodes.Status404NotFound, null, detail ?? "The requested resource was not found.");

    public static IResult Conflict(string? detail = null) =>
        Problem(StatusCodes.Status409Conflict, null, detail ?? "The request conflicts with the current state.");

    public static IResult Bad(string detail) =>
        Problem(StatusCodes.Status400BadRequest, null, detail);

    public static IResult Validation(IList<string> errors)
    {
        var details = Create(StatusCodes.Status400BadRequest, "Validation Failed",
            errors.Count == 1 ? errors[0] : string.Join("; ", errors));
        if (errors.Count > 1)
            details.Extensions["errors"] = errors;
        return Results.Problem(details);
    }

    private static IResult Problem(int status, string? title, string? detail) =>
        Results.Problem(Create(status, title ?? DefaultTitle(status), detail));

    private static ProblemDetails Create(int status, string title, string? detail)
    {
        var details = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Type = TypeUrl(status)
        };
        return details;
    }

    private static int Resolve(ErrorKind kind, int fallback) => kind switch
    {
        ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => fallback
    };

    private static string DefaultTitle(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "Bad Request",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not Found",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status422UnprocessableEntity => "Unprocessable Entity",
        StatusCodes.Status429TooManyRequests => "Too Many Requests",
        StatusCodes.Status500InternalServerError => "Internal Server Error",
        StatusCodes.Status503ServiceUnavailable => "Service Unavailable",
        _ => "Error"
    };

    private static string TypeUrl(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        StatusCodes.Status401Unauthorized => "https://tools.ietf.org/html/rfc9110#section-15.5.2",
        StatusCodes.Status403Forbidden => "https://tools.ietf.org/html/rfc9110#section-15.5.4",
        StatusCodes.Status404NotFound => "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        StatusCodes.Status409Conflict => "https://tools.ietf.org/html/rfc9110#section-15.5.10",
        StatusCodes.Status422UnprocessableEntity => "https://tools.ietf.org/html/rfc9110#section-15.5.21",
        StatusCodes.Status429TooManyRequests => "https://tools.ietf.org/html/rfc6585#section-4",
        StatusCodes.Status500InternalServerError => "https://tools.ietf.org/html/rfc9110#section-15.6.1",
        StatusCodes.Status503ServiceUnavailable => "https://tools.ietf.org/html/rfc9110#section-15.6.4",
        _ => "about:blank"
    };
}
