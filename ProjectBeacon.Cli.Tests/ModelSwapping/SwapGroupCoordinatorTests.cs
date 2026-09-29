namespace ProjectBeacon.Cli.Tests.ModelSwapping;

using ProjectBeacon.Cli.Client.ModelSwapping;

public sealed class SwapGroupCoordinatorTests
{
    [Fact]
    public void TwoSwapModels_OnlyFirstActivates()
    {
        var c = new SwapGroupCoordinator();
        c.Register("a", concurrent: false);
        c.Register("b", concurrent: false);

        Assert.True(c.TryActivate("a"));
        Assert.False(c.TryActivate("b"));
        Assert.True(c.IsGroupBusy("swap"));
    }

    [Fact]
    public void AfterDeactivate_SecondCanActivate()
    {
        var c = new SwapGroupCoordinator();
        c.Register("a", concurrent: false);
        c.Register("b", concurrent: false);

        c.TryActivate("a");
        c.Deactivate("a");

        Assert.True(c.TryActivate("b"));
        Assert.True(c.IsGroupBusy("swap"));
    }

    [Fact]
    public void ConcurrentModels_DoNotBlockEachOther()
    {
        var c = new SwapGroupCoordinator();
        c.Register("x", concurrent: true);
        c.Register("y", concurrent: true);

        Assert.True(c.TryActivate("x"));
        Assert.True(c.TryActivate("y"));
    }

    [Fact]
    public void ConcurrentModels_DoNotBlockSwap()
    {
        var c = new SwapGroupCoordinator();
        c.Register("embed", concurrent: true);
        c.Register("llm", concurrent: false);

        Assert.True(c.TryActivate("embed"));
        Assert.True(c.TryActivate("llm"));
    }

    [Fact]
    public void SwapModel_BlocksOtherSwapButNotConcurrent()
    {
        var c = new SwapGroupCoordinator();
        c.Register("llm1", concurrent: false);
        c.Register("llm2", concurrent: false);
        c.Register("embed", concurrent: true);

        c.TryActivate("llm1");

        Assert.False(c.TryActivate("llm2"));
        Assert.True(c.TryActivate("embed"));
    }

    [Fact]
    public void Unregister_RemovesModel()
    {
        var c = new SwapGroupCoordinator();
        c.Register("a", concurrent: false);
        c.TryActivate("a");

        c.Unregister("a");

        Assert.False(c.IsGroupBusy("swap"));
        Assert.False(c.TryActivate("a"));
    }

    [Fact]
    public void UnregisterAll_ClearsEverything()
    {
        var c = new SwapGroupCoordinator();
        c.Register("a", concurrent: false);
        c.Register("b", concurrent: true);
        c.TryActivate("a");
        c.TryActivate("b");

        c.UnregisterAll();

        Assert.False(c.IsGroupBusy("swap"));
        Assert.Equal(0, c.ActiveCount);
    }

    [Fact]
    public void ActiveCount_ReflectsActivatedModels()
    {
        var c = new SwapGroupCoordinator();
        c.Register("a", concurrent: false);
        c.Register("b", concurrent: true);
        c.Register("c", concurrent: true);

        Assert.Equal(0, c.ActiveCount);
        c.TryActivate("a");
        Assert.Equal(1, c.ActiveCount);
        c.TryActivate("b");
        Assert.Equal(2, c.ActiveCount);
        c.Deactivate("a");
        Assert.Equal(1, c.ActiveCount);
    }

    [Fact]
    public void TryActivate_UnregisteredModel_ReturnsFalse()
    {
        var c = new SwapGroupCoordinator();
        Assert.False(c.TryActivate("ghost"));
    }
}
