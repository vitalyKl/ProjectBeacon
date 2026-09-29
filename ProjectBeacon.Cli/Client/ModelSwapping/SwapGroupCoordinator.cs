namespace ProjectBeacon.Cli.Client.ModelSwapping;

/// <summary>
/// Enforces the swap-group constraint: at most one model per group may be <see cref="BackendState.Ready"/>
/// at any time. Models marked <c>Concurrent</c> are each in their own group (no restriction).
/// </summary>
public sealed class SwapGroupCoordinator
{
    private readonly Dictionary<string, string> _modelToGroup = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeByGroup = new(StringComparer.Ordinal);

    public void Register(string modelName, bool concurrent)
    {
        _modelToGroup[modelName] = concurrent ? $"concurrent/{modelName}" : "swap";
    }

    public void Unregister(string modelName)
    {
        if (_modelToGroup.TryGetValue(modelName, out var group) && group == "swap")
            _activeByGroup.Remove("swap");
        _modelToGroup.Remove(modelName);
        _activeByGroup.Remove(modelName);
    }

    public void UnregisterAll()
    {
        _modelToGroup.Clear();
        _activeByGroup.Clear();
    }

    public bool TryActivate(string modelName)
    {
        if (!_modelToGroup.TryGetValue(modelName, out var group))
            return false;
        if (group == "swap" && _activeByGroup.Contains("swap"))
            return false;
        _activeByGroup.Add(modelName);
        if (group == "swap")
            _activeByGroup.Add("swap");
        return true;
    }

    public void Deactivate(string modelName)
    {
        _activeByGroup.Remove(modelName);
        if (_modelToGroup.TryGetValue(modelName, out var group) && group == "swap")
            _activeByGroup.Remove("swap");
    }

    public bool IsGroupBusy(string groupName) => _activeByGroup.Contains(groupName);

    public int ActiveCount => _activeByGroup.Count(k => k != "swap");
}
