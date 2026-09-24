namespace ProjectBeacon.Infrastructure.Tests;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProjectBeacon.Infrastructure.Http;

public class BeaconExceptionHandlerTests
{
    private readonly record struct CapturedLog(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<CapturedLog> Logs { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose() { }

        private sealed class CapturingLogger(CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => provider.Logs.Add(new CapturedLog(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "BeaconTests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string EnvironmentName { get; set; } = environmentName;
    }

    private sealed class TestApplicationBuilder : IApplicationBuilder
    {
        private readonly List<Func<RequestDelegate, RequestDelegate>> _middleware = new();

        public IServiceProvider ApplicationServices { get; set; } = null!;
        public IDictionary<string, object?> Properties { get; set; } = new Dictionary<string, object?>();
        public IFeatureCollection ServerFeatures { get; set; } = new FeatureCollection();
        public IFeatureCollection Features { get; set; } = new FeatureCollection();

        public IApplicationBuilder New() => new TestApplicationBuilder { ApplicationServices = ApplicationServices };

        public IApplicationBuilder Use(Func<RequestDelegate, RequestDelegate> middleware)
        {
            _middleware.Add(middleware);
            return this;
        }

        public RequestDelegate Build()
        {
            RequestDelegate pipeline = _ => Task.CompletedTask;
            for (var i = _middleware.Count - 1; i >= 0; i--)
                pipeline = _middleware[i](pipeline);
            return pipeline;
        }
    }

    private static (RequestDelegate Pipeline, CapturingLoggerProvider Logger) BuildPipeline(string? pathPrefix, string environment)
    {
        var logger = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logger));
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(environment));
        services.AddSingleton(new DiagnosticListener("Microsoft.AspNetCore"));
        services.AddMetrics();
        using var provider = services.BuildServiceProvider();
        var app = new TestApplicationBuilder { ApplicationServices = provider };
        var pipeline = app.UseBeaconExceptionHandler(pathPrefix)
            .Use(_ => _ => throw new InvalidOperationException("boom"))
            .Build();
        return (pipeline, logger);
    }

    private static DefaultHttpContext NewContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadBody(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    [Fact]
    public async Task Exception_Inside_Prefix_Returns_500_Problem_Without_Details()
    {
        var (pipeline, logger) = BuildPipeline("/v1", "Production");

        var context = NewContext("/v1/tasks");
        await pipeline(context);

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        var body = await ReadBody(context);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(500, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("An unexpected error occurred.", doc.RootElement.GetProperty("title").GetString());
        Assert.False(doc.RootElement.TryGetProperty("detail", out _));
        Assert.False(doc.RootElement.TryGetProperty("exceptionType", out _));
        Assert.DoesNotContain("boom", body);
        Assert.DoesNotContain("at ", body);

        var entry = Assert.Single(logger.Logs, l => l.Message == "Unhandled exception for GET /v1/tasks");
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsType<InvalidOperationException>(entry.Exception);
        Assert.Contains("boom", entry.Exception!.Message);
    }

    [Fact]
    public async Task Exception_Inside_Prefix_In_Development_Includes_Detail()
    {
        var (pipeline, _) = BuildPipeline("/v1", "Development");

        var context = NewContext("/v1/tasks");
        await pipeline(context);

        var body = await ReadBody(context);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("boom", doc.RootElement.GetProperty("detail").GetString());
        Assert.Equal("InvalidOperationException", doc.RootElement.GetProperty("exceptionType").GetString());
    }

    [Fact]
    public async Task Exception_Outside_Prefix_Is_Rethrown()
    {
        var (pipeline, _) = BuildPipeline("/v1", "Production");

        var context = NewContext("/dashboard");
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline(context));
        Assert.False(context.Response.HasStarted);
    }

    [Fact]
    public async Task Without_Prefix_All_Paths_Are_Handled()
    {
        var (pipeline, _) = BuildPipeline(null, "Production");

        var context = NewContext("/health");
        await pipeline(context);

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
    }
}
