using ProjectBeacon.Infrastructure.LlamaSwap;

namespace ProjectBeacon.Web.Theme;

public static class RuntimeHealth
{
    public static string From(LlamaSwapStatusDto? status)
    {
        if (status is null || !status.Available)
            return "Offline";
        var models = status.LoadedModels;
        if (models is not null)
        {
            if (models.Any(m => Is(m.State, "starting")))
                return "Starting";
            if (models.Any(m => Is(m.State, "stopping")))
                return "Stopping";
            if (models.Any(m => Is(m.State, "busy", "running")))
                return "Busy";
            if (models.Any(m => Is(m.State, "faulted", "failed", "error")))
                return models.Any(m => Is(m.State, "ready")) ? "Degraded" : "Failed";
            if (models.Count == 0)
                return string.IsNullOrWhiteSpace(status.Error) ? "Degraded" : "Failed";
        }
        if (!status.Healthy || !string.IsNullOrWhiteSpace(status.Error))
            return string.IsNullOrWhiteSpace(status.Error) ? "Failed" : "Degraded";
        return "Ready";
    }

    public static string ForDashboard(LlamaSwapStatusDto? status) =>
        status is { Available: false, Host: not null } ? "Online" : From(status);

    private static bool Is(string? state, params string[] names) =>
        names.Any(name => string.Equals(state, name, StringComparison.OrdinalIgnoreCase));
}
