namespace ProjectBeacon.Application.Common;

using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

/// <summary>
/// Dispatches typed Application commands to their single registered handler.
/// </summary>
public interface ICommandDispatcher
{
    Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default);
}

/// <summary>
/// Dispatches typed Application queries to their single registered handler.
/// </summary>
public interface IQueryDispatcher
{
    Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct = default);
}

/// <summary>
/// Resolves existing CQRS handlers by their request contract. This keeps call sites independent
/// of concrete handler types while preserving the existing handler interfaces and DI lifetimes.
/// </summary>
public sealed class ApplicationDispatcher(IServiceProvider services) : ICommandDispatcher, IQueryDispatcher
{
    public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return InvokeAsync<TResult>(command, typeof(ICommandHandler<,>), ct);
    }

    public Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return InvokeAsync<TResult>(query, typeof(IQueryHandler<,>), ct);
    }

    private async Task<TResult> InvokeAsync<TResult>(object request, Type handlerOpenType, CancellationToken ct)
    {
        var contract = handlerOpenType.MakeGenericType(request.GetType(), typeof(TResult));
        var handler = services.GetService(contract);
        if (handler is null)
            throw new InvalidOperationException(
                $"No handler is registered for request '{request.GetType().FullName}'.");

        var method = contract.GetMethod("HandleAsync", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Handler contract '{contract.FullName}' has no HandleAsync method.");

        // The request object retains its concrete runtime type; reflection is only used at the
        // generic dispatcher boundary, not inside business handlers.
        if (method.Invoke(handler, [request, ct]) is not Task<TResult> task)
            throw new InvalidOperationException($"Handler for '{request.GetType().FullName}' returned an incompatible task.");

        return await task.ConfigureAwait(false);
    }
}
