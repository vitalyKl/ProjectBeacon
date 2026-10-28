namespace ProjectBeacon.Application.Tests.Common;

using Application;
using Application.Common;
using Application.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class ApplicationDispatcherTests
{
    [Fact]
    public async Task SendAsync_ResolvesHandlerByTypedCommandContract()
    {
        var services = new ServiceCollection();
        services.AddTransient<ICommandHandler<EchoCommand, Result<string>>, EchoHandler>();
        services.AddTransient<ICommandDispatcher, ApplicationDispatcher>();
        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        var result = await dispatcher.SendAsync<Result<string>>(new EchoCommand("dispatched"));

        Assert.True(result.Success);
        Assert.Equal("dispatched", result.Value);
    }

    [Fact]
    public async Task QueryAsync_ResolvesHandlerByTypedQueryContract()
    {
        var services = new ServiceCollection();
        services.AddTransient<IQueryHandler<EchoQuery, Result<string>>, EchoQueryHandler>();
        services.AddTransient<IQueryDispatcher, ApplicationDispatcher>();
        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        var result = await dispatcher.QueryAsync<Result<string>>(new EchoQuery("queried"));

        Assert.True(result.Success);
        Assert.Equal("queried", result.Value);
    }

    [Fact]
    public void AddApplicationHandlers_RegistersConcreteHandlersByTheirContracts()
    {
        var services = new ServiceCollection();

        services.AddApplicationHandlers();

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(ICommandHandler<CreateTaskCommand, Result<TaskItemDto>>)
            && descriptor.ImplementationType == typeof(CreateTaskHandler));
    }

    [Fact]
    public async Task SendAsync_WhenHandlerIsMissing_FailsWithRequestType()
    {
        var dispatcher = new ApplicationDispatcher(new ServiceCollection().BuildServiceProvider());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.SendAsync<Result<string>>(new EchoCommand("missing")));

        Assert.Contains(nameof(EchoCommand), exception.Message);
    }

    private sealed record EchoCommand(string Value) : ICommand<Result<string>>;

    private sealed record EchoQuery(string Value) : IQuery<Result<string>>;

    private sealed class EchoHandler : ICommandHandler<EchoCommand, Result<string>>
    {
        public Task<Result<string>> HandleAsync(EchoCommand command, CancellationToken ct = default)
            => Task.FromResult(Result.Ok(command.Value));
    }

    private sealed class EchoQueryHandler : IQueryHandler<EchoQuery, Result<string>>
    {
        public Task<Result<string>> HandleAsync(EchoQuery query, CancellationToken ct = default)
            => Task.FromResult(Result.Ok(query.Value));
    }
}
