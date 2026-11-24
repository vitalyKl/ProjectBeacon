namespace ProjectBeacon.Cli.Tests;

using ProjectBeacon.Cli.Client;

public sealed class GitCheckoutGateTests
{
    [Fact]
    public async Task CheckoutMutation_IsExclusiveAndTryAcquireDoesNotWait()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-checkout-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var first = await GitCheckoutGate.AcquireAsync(root, CancellationToken.None);
            var blocked = await GitCheckoutGate.TryAcquireAsync(root, TimeSpan.FromMilliseconds(25), CancellationToken.None);
            Assert.Null(blocked);

            first.Dispose();
            using var next = await GitCheckoutGate.TryAcquireAsync(root, TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.NotNull(next);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckoutMutationDoesNotWaitForAgentSessionLease()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-checkout-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var session = GitRepositoryOperationGate.RegisterSession(root);
            using var checkout = await GitCheckoutGate.TryAcquireAsync(root, TimeSpan.FromMilliseconds(100), CancellationToken.None);
            Assert.NotNull(checkout);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
