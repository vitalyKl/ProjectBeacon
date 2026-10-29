namespace ProjectBeacon.Application.Common;

/// <summary>Marker for a command whose handler returns <typeparamref name="TResult"/>.</summary>
public interface ICommand<out TResult>
{
}

/// <summary>Handles one command type.</summary>
public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken ct = default);
}

/// <summary>Marker for a query whose handler returns <typeparamref name="TResult"/>.</summary>
public interface IQuery<out TResult>
{
}

/// <summary>Handles one query type.</summary>
public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken ct = default);
}

/// <summary>Why an operation failed.</summary>
public enum ErrorKind
{
    None,
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    Unavailable
}

/// <summary>Stable machine-readable code for a business failure.</summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string Unavailable = "unavailable";
    public const string InternalError = "internal_error";
    public const string Unclassified = "operation_failed";

    public static string For(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => ValidationFailed,
        ErrorKind.Unauthorized => Unauthorized,
        ErrorKind.Forbidden => Forbidden,
        ErrorKind.NotFound => NotFound,
        ErrorKind.Conflict => Conflict,
        ErrorKind.Unavailable => Unavailable,
        _ => Unclassified
    };
}

/// <summary>Success or failure of an operation, with a stable error code for failures.</summary>
public record Result(bool Success, string? Error = null)
{
    public ErrorKind Kind { get; init; } = ErrorKind.None;
    public string? ErrorCode { get; init; }

    public static Result Ok() => new(true);
    public static Result<T> Ok<T>(T value) => new(true, value);
    public static Result Failure(string error) => Failure(error, ErrorKind.Validation);
    public static Result Failure(string error, ErrorKind kind) =>
        new(false, error) { Kind = kind, ErrorCode = ErrorCodes.For(kind) };
    public static Result Failure(string error, string errorCode, ErrorKind kind = ErrorKind.Validation) =>
        new(false, error) { Kind = kind, ErrorCode = NormalizeCode(errorCode) };
    public static Result Failure(Result failed) =>
        new(false, failed.Error) { Kind = failed.Kind, ErrorCode = failed.ErrorCode ?? ErrorCodes.For(failed.Kind) };
    public static Result Forbidden(string error = "Forbidden.") => Failure(error, ErrorKind.Forbidden);
    public static Result<T> Failure<T>(string error) => Failure<T>(error, ErrorKind.Validation);
    public static Result<T> Failure<T>(string error, ErrorKind kind) =>
        new(false, default, error) { Kind = kind, ErrorCode = ErrorCodes.For(kind) };
    public static Result<T> Failure<T>(string error, string errorCode, ErrorKind kind = ErrorKind.Validation) =>
        new(false, default, error) { Kind = kind, ErrorCode = NormalizeCode(errorCode) };
    public static Result<T> Failure<T>(Result failed) =>
        new(false, default, failed.Error) { Kind = failed.Kind, ErrorCode = failed.ErrorCode ?? ErrorCodes.For(failed.Kind) };
    public static Result<T> Forbidden<T>(string error = "Forbidden.") => Failure<T>(error, ErrorKind.Forbidden);

    private static string NormalizeCode(string code)
    {
        var normalized = code.Trim().ToLowerInvariant();
        if (normalized.Length is 0 or > 100 || normalized.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')))
            throw new ArgumentException("Error code must be 1-100 ASCII letters, digits, '.', '_' or '-'.", nameof(code));
        return normalized;
    }
}

/// <summary>A result that carries a value on success, with a stable error code on failure.</summary>
public record Result<T>(bool Success, T? Value, string? Error = null)
{
    public ErrorKind Kind { get; init; } = ErrorKind.None;
    public string? ErrorCode { get; init; }
}
