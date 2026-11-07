namespace ProjectBeacon.Domain.Common;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Versioned, durable activity snapshot exchanged by a workstation and the control plane.
/// Version 1 intentionally accepts legacy snapshots with no explicit version field.
/// </summary>
public sealed record PipelineActivityV1
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public int Version { get; init; } = CurrentVersion;
    public string? State { get; init; }
    public string? Activity { get; init; }
    public string? DeviceId { get; init; }
    public string? CommandId { get; init; }
    public string? ExternalSessionId { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public IReadOnlyList<PipelineActivityPartV1> Parts { get; init; } = [];
    public IReadOnlyList<PipelineActivityFormV1> Forms { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }

    public static PipelineActivityV1 Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new JsonException("Pipeline activity snapshot is empty.");

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Pipeline activity snapshot must be a JSON object.");

        var snapshot = JsonSerializer.Deserialize<PipelineActivityV1>(json, JsonOptions)
            ?? throw new JsonException("Pipeline activity snapshot is null.");
        if (snapshot.Version != CurrentVersion)
            throw new JsonException($"Unsupported pipeline activity version {snapshot.Version}.");

        return snapshot with
        {
            Parts = snapshot.Parts ?? [],
            Forms = snapshot.Forms ?? []
        };
    }

    public static bool TryParse(string? json, out PipelineActivityV1? snapshot)
    {
        try
        {
            snapshot = Parse(json);
            return true;
        }
        catch (JsonException)
        {
            snapshot = null;
            return false;
        }
    }

    public PipelineActivityV1 PreserveAnsweredForms(PipelineActivityV1? previous)
    {
        if (previous is null || previous.Forms.Count == 0)
            return this;

        var forms = Forms.ToList();
        var seen = forms.Select(form => form.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var answered in previous.Forms.Where(form => form.Answer is not null))
        {
            if (!string.IsNullOrWhiteSpace(answered.Id) && seen.Add(answered.Id))
            {
                forms.Add(answered with
                {
                    Fields = answered.Fields?.Clone(),
                    Answer = answered.Answer?.Clone()
                });
            }
        }

        return this with { Forms = forms };
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

public sealed record PipelineActivityPartV1
{
    public string? Kind { get; init; } = "text";
    public string? Role { get; init; } = "assistant";
    public string? Body { get; init; } = string.Empty;
    public string? ExternalId { get; init; }
    public string? Tool { get; init; }
    public string? ToolStatus { get; init; }
    public string? ToolInput { get; init; }
    public string? ToolResult { get; init; }
    public int? DurationMs { get; init; }
    public bool Complete { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

public sealed record PipelineActivityFormV1
{
    public string Id { get; init; } = string.Empty;
    public string? Title { get; init; }
    public string? Metadata { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Fields { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Answer { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}
