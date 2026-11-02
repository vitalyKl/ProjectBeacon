namespace ProjectBeacon.Web.Features.Chat;

/// <summary>The model, reasoning effort, and agent mode selected in the chat settings dialog.</summary>
public sealed record ChatModelSettingsSelection(string? Model, string? ReasoningEffort, string AgentMode);
