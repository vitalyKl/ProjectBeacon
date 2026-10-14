namespace ProjectBeacon.API.Tests;

using Xunit.Abstractions;
using Xunit.Sdk;

/// <summary>
/// A [Fact] that reports a genuine "Skipped" status (with a reason) when the operator
/// has explicitly opted out of the PostgreSQL integration tests via
/// BEACON_TEST_SKIP_POSTGRES. xUnit v2 has no runtime SkipException, so the skip
/// decision is made at discovery time through a custom test case.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[XunitTestCaseDiscoverer("ProjectBeacon.API.Tests.PostgresFactDiscoverer", "ProjectBeacon.API.Tests")]
public sealed class PostgresFactAttribute : FactAttribute
{
}

public sealed class PostgresFactDiscoverer : FactDiscoverer
{
    public PostgresFactDiscoverer(IMessageSink diagnosticMessageSink)
        : base(diagnosticMessageSink)
    {
    }

    protected override IXunitTestCase CreateTestCase(
        ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute)
        => new PostgresTestCase(
            DiagnosticMessageSink,
            discoveryOptions.MethodDisplayOrDefault(),
            discoveryOptions.MethodDisplayOptionsOrDefault(),
            testMethod);
}

internal sealed class PostgresTestCase : XunitTestCase
{
    public const string SkipEnvVar = "BEACON_TEST_SKIP_POSTGRES";

    public const string SkipReasonText =
        "PostgreSQL integration tests explicitly skipped: BEACON_TEST_SKIP_POSTGRES is set. " +
        "Unset it to run them against Testcontainers or a BEACON_TEST_DATABASE_URL instance.";

#pragma warning disable CS0618 // Required for IXunitSerializable deserialization
    public PostgresTestCase() { }
#pragma warning restore CS0618

    public PostgresTestCase(
        IMessageSink diagnosticMessageSink,
        TestMethodDisplay defaultMethodDisplay,
        TestMethodDisplayOptions defaultMethodDisplayOptions,
        ITestMethod testMethod)
        : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod)
    {
    }

    public static bool OptedOut
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(SkipEnvVar);
            return value is "1" or "true" or "True";
        }
    }

    protected override string? GetSkipReason(IAttributeInfo factAttribute)
    {
        var explicitSkip = base.GetSkipReason(factAttribute);
        if (explicitSkip is not null)
            return explicitSkip;

        return OptedOut ? SkipReasonText : null;
    }
}
