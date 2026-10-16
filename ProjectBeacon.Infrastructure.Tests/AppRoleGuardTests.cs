namespace ProjectBeacon.Infrastructure.Tests;

using Infrastructure.Data;
using Microsoft.Extensions.Logging;

/// <summary>
/// Unit coverage for the fail-closed role guard policy and diagnostic hygiene (no database).
/// </summary>
public sealed class AppRoleGuardTests
{
    [Theory]
    [InlineData(true, false, false, false)]   // verified safe role
    [InlineData(true, true, false, true)]    // SUPERUSER
    [InlineData(true, false, true, true)]    // BYPASSRLS
    [InlineData(false, false, false, true)]  // unverified
    public void IsUnsafe_MapsStatusToPolicy(
        bool verified, bool superuser, bool bypassRls, bool expectedUnsafe)
    {
        var status = new AppRoleStatus(verified, "probe_role", superuser, bypassRls,
            verified ? null : "probe failure");
        Assert.Equal(expectedUnsafe, PostgresConnection.IsUnsafe(status));
    }

    [Fact]
    public async Task Enforce_UnverifiedRole_ThrowsInProduction()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgresConnection.EnforceAppRoleAsync(UnreachableConnectionString, production: true));
        Assert.Contains("could not be verified", ex.Message);
        Assert.DoesNotContain("Host=", ex.Message);
        Assert.DoesNotContain("hunter2", ex.Message);
    }

    [Fact]
    public async Task Enforce_UnverifiedRole_WarnsInNonProduction()
    {
        var logger = new CapturingLogger();
        await PostgresConnection.EnforceAppRoleAsync(UnreachableConnectionString, production: false, logger);
        Assert.Single(logger.Warnings);
        Assert.Contains("could not be verified", logger.Warnings[0]);
    }

    [Fact]
    public async Task GetAppRoleStatus_ConnectionFailure_ReportsSafeFailureReason()
    {
        var status = await PostgresConnection.GetAppRoleStatusAsync(UnreachableConnectionString);
        Assert.False(status.Verified);
        Assert.False(string.IsNullOrEmpty(status.FailureReason));
        Assert.DoesNotContain("hunter2", status.FailureReason);
        Assert.DoesNotContain("Host=", status.FailureReason);
    }

    private const string UnreachableConnectionString =
        "Host=hunter2-host;Port=1;Database=beacon;Username=probe;Password=hunter2";

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
