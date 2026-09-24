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

public record Result(bool Success, string? Error = null)
{
    public static Result Ok() => new(true);
    public static Result<T> Ok<T>(T value) => new(true, value);
    public static Result Failure(string error) => new(false, error);
    public static Result<T> Failure<T>(string error) => new(false, default, error);
}

public record Result<T>(bool Success, T? Value, string? Error = null);
