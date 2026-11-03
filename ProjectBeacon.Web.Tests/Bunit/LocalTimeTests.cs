using ProjectBeacon.Web.Shared;

namespace ProjectBeacon.Web.Tests.Bunit;

public sealed class LocalTimeTests : BUnitRenderBase
{
    [Fact]
    public void LocalTime_ExposesUtcTimestampForBrowserTimezoneFormatting()
    {
        var value = new DateTime(2026, 11, 3, 8, 15, 0, DateTimeKind.Utc);

        var cut = Bunit.RenderComponent<LocalTime>(parameters =>
            parameters.Add(component => component.Value, value));

        Assert.Contains("data-local-time=\"2026-11-03T08:15:00.0000000Z\"", cut.Markup);
        Assert.Contains("data-local-time-format=\"g\"", cut.Markup);
    }

    [Fact]
    public void LocalTime_ConvertsOffsetTimestampToUtc()
    {
        var value = new DateTimeOffset(2026, 11, 3, 10, 15, 0, TimeSpan.FromHours(2));

        var cut = Bunit.RenderComponent<LocalTime>(parameters =>
            parameters.Add(component => component.OffsetValue, value));

        Assert.Contains("data-local-time=\"2026-11-03T08:15:00.0000000Z\"", cut.Markup);
    }
}
