namespace ProjectBeacon.Application.Common;

public interface ICommand<out TResult>
{
}

public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken ct = default);
}

public interface IQuery<out TResult>
{
}

public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken ct = default);
}

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

public record Result<T>(bool Success, T? Value, string? Error = null)
{
    public ErrorKind Kind { get; init; } = ErrorKind.None;
}
