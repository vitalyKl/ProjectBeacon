namespace ProjectBeacon.Cli.Tests;

using Application.Devices;
using Domain;
using Domain.Enums;
using ProjectBeacon.Cli.Client;

public sealed class CommandEnvelopeTests
{
    [Fact]
    public void NormalizeWireVersion_CurrentVersion_Accepted()
    {
        var r = CommandEnvelope.NormalizeWireVersion(CommandProtocol.CurrentVersion);
        Assert.True(r.IsOk);
        Assert.Equal(1, r.Version);
    }

    [Fact]
    public void NormalizeWireVersion_LegacyZero_NormalizedToCurrent()
    {
        var r = CommandEnvelope.NormalizeWireVersion(0);
        Assert.True(r.IsOk);
        Assert.Equal(CommandProtocol.CurrentVersion, r.Version);
    }

    [Fact]
    public void NormalizeWireVersion_AboveMax_Rejected()
    {
        var r = CommandEnvelope.NormalizeWireVersion(99);
        Assert.False(r.IsOk);
        Assert.Contains("unsupported command version", r.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeWireVersion_Negative_Rejected()
    {
        var r = CommandEnvelope.NormalizeWireVersion(-1);
        Assert.False(r.IsOk);
        Assert.Contains("non-negative", r.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HasValidPayload_MalformedJson_False()
    {
        Assert.False(CommandEnvelope.HasValidPayload("not json"));
    }

    [Fact]
    public void HasValidPayload_ValidObject_True()
    {
        Assert.True(CommandEnvelope.HasValidPayload("{}"));
        Assert.True(CommandEnvelope.HasValidPayload("{\"a\":1}"));
    }

    [Fact]
    public void HasValidPayload_EmptyString_TreatedAsEmptyObject()
    {
        Assert.True(CommandEnvelope.HasValidPayload(""));
        Assert.True(CommandEnvelope.HasValidPayload(null));
    }

    [Fact]
    public void NormalizedPayload_EmptyString_BecomesEmptyObject()
    {
        Assert.Equal("{}", CommandEnvelope.NormalizedPayload(""));
        Assert.Equal("{}", CommandEnvelope.NormalizedPayload(null));
    }

    [Fact]
    public void NormalizedPayload_PassesThroughNonEmpty()
    {
        Assert.Equal("{\"a\":1}", CommandEnvelope.NormalizedPayload("{\"a\":1}"));
    }

    [Fact]
    public async Task ExecuteAsync_VersionAboveMax_Rejected()
    {
        await using var daemon = CreateDaemon();
        var (ok, _, error) = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Id = Guid.NewGuid(),
            Kind = WorkstationCommandKind.Probe,
            Version = 99,
            PayloadJson = "{}"
        }, CancellationToken.None);
        Assert.False(ok);
        Assert.Contains("unsupported command version", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_NegativeVersion_Rejected()
    {
        await using var daemon = CreateDaemon();
        var (ok, _, error) = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Id = Guid.NewGuid(),
            Kind = WorkstationCommandKind.Probe,
            Version = -1,
            PayloadJson = "{}"
        }, CancellationToken.None);
        Assert.False(ok);
        Assert.Contains("non-negative", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_MalformedPayload_Rejected()
    {
        await using var daemon = CreateDaemon();
        var (ok, _, error) = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Id = Guid.NewGuid(),
            Kind = WorkstationCommandKind.Probe,
            Version = CommandProtocol.CurrentVersion,
            PayloadJson = "not json"
        }, CancellationToken.None);
        Assert.False(ok);
        Assert.Equal(CommandEnvelope.MalformedPayload, error);
    }

    [Fact]
    public async Task ExecuteAsync_LegacyZeroVersion_NormalizedAndAccepted()
    {
        await using var daemon = CreateDaemon();
        var (ok, result, error) = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Id = Guid.NewGuid(),
            Kind = WorkstationCommandKind.Probe,
            Version = 0,
            PayloadJson = "{}"
        }, CancellationToken.None);
        Assert.True(ok, error);
        Assert.Contains("git", result);
    }

    [Fact]
    public async Task ExecuteAsync_CurrentVersionAccepted()
    {
        await using var daemon = CreateDaemon();
        var (ok, result, error) = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Id = Guid.NewGuid(),
            Kind = WorkstationCommandKind.Probe,
            Version = CommandProtocol.CurrentVersion,
            PayloadJson = "{}"
        }, CancellationToken.None);
        Assert.True(ok, error);
        Assert.Contains("git", result);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyPayload_NormalizedToEmptyObject()
    {
        await using var daemon = CreateDaemon();
        var (ok, result, error) = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Id = Guid.NewGuid(),
            Kind = WorkstationCommandKind.Probe,
            Version = CommandProtocol.CurrentVersion,
            PayloadJson = ""
        }, CancellationToken.None);
        Assert.True(ok, error);
        Assert.Contains("git", result);
    }

    private static WorkstationDaemon CreateDaemon()
    {
        var dir = Path.Combine(Path.GetTempPath(), "beacon-envelope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        var llama = new ClientLlamaSwap
        {
            SkipRealProcess = true,
            ConfigFile = Path.Combine(dir, "config.yaml")
        };
        return new WorkstationDaemon(http, llama, () => new WorkstationSettings());
    }
}
