namespace ProjectBeacon.Domain.Entities.Projects;

using System.Text.Json;
using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

public class LocalModelBackend : Entity
{
    public LocalModelBackend() { }

    public string Name { get; private set; } = string.Empty;
    public ModelBackendType BackendType { get; private set; }
    public string LaunchCommand { get; private set; } = string.Empty;
    public int ContextSize { get; private set; }
    public int Ttl { get; private set; }
    public bool Concurrent { get; private set; }
    public Guid UserId { get; private set; }
    public string Note { get; private set; } = string.Empty;
    public DateTime? UpdatedAt { get; private set; }

    // EF maps the JSON text backing; the ExtraFlags list view is ignored in the model.
    public string ExtraFlagsJson { get; private set; } = "[]";

    public IReadOnlyList<string> ExtraFlags
    {
        get
        {
            if (string.IsNullOrEmpty(ExtraFlagsJson))
                return [];
            try
            {
                return JsonSerializer.Deserialize<string[]>(ExtraFlagsJson) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
        private set => ExtraFlagsJson = JsonSerializer.Serialize(value ?? []);
    }

    public static LocalModelBackend Create(
        string name,
        ModelBackendType backendType,
        string launchCommand,
        int contextSize,
        int ttl,
        Guid userId,
        IReadOnlyList<string>? extraFlags = null,
        bool concurrent = false,
        string? note = null)
    {
        var backend = Entity.New<LocalModelBackend>();
        backend.Name = name;
        backend.BackendType = backendType;
        backend.LaunchCommand = launchCommand ?? string.Empty;
        backend.ContextSize = contextSize;
        backend.Ttl = ttl;
        backend.Concurrent = concurrent;
        backend.UserId = userId;
        backend.Note = note?.Trim() ?? string.Empty;
        backend.ExtraFlags = extraFlags ?? [];
        backend.UpdatedAt = DateTime.UtcNow;
        return backend;
    }

    public void Update(
        string name,
        ModelBackendType backendType,
        string launchCommand,
        int contextSize,
        int ttl,
        IReadOnlyList<string>? extraFlags = null,
        bool concurrent = false,
        string? note = null)
    {
        Name = name;
        BackendType = backendType;
        LaunchCommand = launchCommand ?? string.Empty;
        ContextSize = contextSize;
        Ttl = ttl;
        Concurrent = concurrent;
        if (extraFlags is not null)
            ExtraFlags = extraFlags;
        if (note is not null)
            Note = note.Trim();
        UpdatedAt = DateTime.UtcNow;
    }
}
