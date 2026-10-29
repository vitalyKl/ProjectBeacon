namespace ProjectBeacon.Web.Features.Chat;

/// <summary>The model selection and reasoning variant returned by the chat settings dialog.</summary>
public sealed record ChatModelSettingsSelection(string? Model, string? ReasoningEffort);
