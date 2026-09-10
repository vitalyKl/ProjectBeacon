namespace ProjectBeacon.Web.Tests;

using ProjectBeacon.Web.Theme;

public sealed class TokenBudgetMeterTests
{
    [Theory]
    [InlineData(0, 8000, 0)]
    [InlineData(129, 8000, 2)]
    [InlineData(4000, 8000, 50)]
    [InlineData(8000, 8000, 100)]
    [InlineData(12000, 8000, 100)]
    [InlineData(10, 0, 100)]
    [InlineData(0, 0, 0)]
    public void Percent_ClampsToHundred(int tokens, int budget, int expected)
    {
        Assert.Equal(expected, TokenBudgetMeter.Percent(tokens, budget));
    }
}
