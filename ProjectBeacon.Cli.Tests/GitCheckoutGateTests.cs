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
    public async Task SynchronousCheckoutMutationLeaseSharesTheAsyncGate()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-checkout-gate-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var first = GitCheckoutGate.Acquire(root);
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
    public void CheckoutMutationCanProtectANotYetCreatedDirectory()
    {
        var parent = Path.Combine(Path.GetTempPath(), "beacon-checkout-not-created-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "new", "checkout");

        using var lease = GitCheckoutGate.Acquire(root);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task CheckoutAliasesShareTheSamePhysicalGate()
    {
        if (OperatingSystem.IsWindows())
            return;

        var parent = Path.Combine(Path.GetTempPath(), "beacon-checkout-alias-" + Guid.NewGuid().ToString("N"));
        var realRoot = Path.Combine(parent, "real");
        var aliasRoot = Path.Combine(parent, "alias");
        Directory.CreateDirectory(realRoot);
        try
        {
            Directory.CreateSymbolicLink(aliasRoot, realRoot);
            using var first = await GitCheckoutGate.AcquireAsync(realRoot, CancellationToken.None);
            var blocked = await GitCheckoutGate.TryAcquireAsync(
                aliasRoot, TimeSpan.FromMilliseconds(25), CancellationToken.None);

            Assert.Null(blocked);
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
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
