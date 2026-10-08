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

/// <summary>Why a <see cref="Result"/> failed. <see cref="None"/> is success or an unclassified failure.</summary>
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

/// <summary>Success or failure of an operation, with an optional message and <see cref="ErrorKind"/>.</summary>
public record Result(bool Success, string? Error = null)
{
    public ErrorKind Kind { get; init; } = ErrorKind.None;

    public static Result Ok() => new(true);
    public static Result<T> Ok<T>(T value) => new(true, value);
    public static Result Failure(string error) => new(false, error);
    public static Result Failure(string error, ErrorKind kind) => new(false, error) { Kind = kind };
    public static Result Failure(Result failed) => new(false, failed.Error) { Kind = failed.Kind };
    public static Result Forbidden(string error = "Forbidden.") => Failure(error, ErrorKind.Forbidden);
    public static Result<T> Failure<T>(string error) => new(false, default, error);
    public static Result<T> Failure<T>(string error, ErrorKind kind) => new(false, default, error) { Kind = kind };
    public static Result<T> Failure<T>(Result failed) => new(false, default, failed.Error) { Kind = failed.Kind };
    public static Result<T> Forbidden<T>(string error = "Forbidden.") => Failure<T>(error, ErrorKind.Forbidden);
}

/// <summary>A <see cref="Result"/> that carries a value on success.</summary>
public record Result<T>(bool Success, T? Value, string? Error = null)
{
    public ErrorKind Kind { get; init; } = ErrorKind.None;
}
