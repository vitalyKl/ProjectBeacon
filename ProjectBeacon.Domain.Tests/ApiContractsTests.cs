namespace ProjectBeacon.Domain.Tests;

using System.Text.Json;
using ProjectBeacon.Domain.Common;
using Xunit;

public sealed class ApiContractsTests
{
    [Fact]
    public void ClientRouteBuilders_MatchServerRouteTemplates()
    {
        var id = Guid.Parse("1f0c9e4f-98a8-4fb8-9b25-8a6dd5ecb091");

        Assert.Equal("/v1/devices/me/heartbeat", ApiRoutes.DeviceHeartbeat);
        Assert.Equal("/v1/devices/me/commands?wait=25", ApiRoutes.DeviceCommandsClaim);
        Assert.Equal("/v1/devices/me/commands?wait=1&controlOnly=true", ApiRoutes.DeviceCommandsControlClaim);
        Assert.Equal("/v1/commands/{id:guid}/complete", ApiRoutes.CommandCompleteRoute);
        Assert.Equal($"/v1/commands/{id:D}/complete", ApiRoutes.CommandComplete(id));
        Assert.Equal($"/v1/commands/{id:D}/progress", ApiRoutes.CommandProgress(id));
        Assert.Equal($"/v1/commands/{id:D}/harness-token", ApiRoutes.CommandHarnessToken(id));
        Assert.Equal($"/v1/chat/sessions/{id:D}/parts", ApiRoutes.ChatSessionParts(id));
        Assert.Equal($"/v1/chat/sessions/{id:D}/usage", ApiRoutes.ChatSessionUsage(id));
        Assert.Equal($"/v1/chat/sessions/{id:D}/idle", ApiRoutes.ChatSessionIdle(id));
    }

    [Fact]
    public void DaemonRequestDtos_SerializeWithStableWebNames()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        Assert.Equal("{}", JsonSerializer.Serialize(new EmptyRequest(), options));
        Assert.Equal(
            """{"success":true,"resultJson":"{}","error":null,"generation":4}""",
            JsonSerializer.Serialize(new CommandCompletionRequest(true, "{}", null, 4), options));
        Assert.Equal(
            """{"generation":4,"progressJson":"{}"}""",
            JsonSerializer.Serialize(new CommandProgressRequest(4, "{}"), options));
        Assert.Equal(
            """{"generation":4}""",
            JsonSerializer.Serialize(new HarnessTokenRequest(4), options));
        Assert.Equal(
            """{"probeJson":"probe","workstationJson":null,"instanceId":"daemon"}""",
            JsonSerializer.Serialize(new WorkstationHeartbeatRequest("probe", null, "daemon"), options));
        Assert.Equal(
            """{"role":"assistant","kind":"text","body":"hello","externalId":"part-1","tool":null,"toolStatus":null,"toolInput":null,"toolResult":null,"durationMs":null,"complete":false}""",
            JsonSerializer.Serialize(new ChatPartRequest("assistant", "text", "hello", "part-1"), options));
        Assert.Equal(
            """{"promptTokens":12,"completionTokens":3,"assistantMessages":1,"totalCost":0.1,"contextLimit":32768,"generationSeconds":1.5}""",
            JsonSerializer.Serialize(new ChatUsageReportRequest(12, 3, 1, 0.1, 32768, 1.5), options));
    }
}
