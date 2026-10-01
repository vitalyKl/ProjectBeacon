namespace ProjectBeacon.Web.Theme;

public static class DesignTokens
{
    // Color tokens (dark-only)
    public const string BgApp = "#181818";
    public const string Surface1 = "#141414";
    public const string Surface2 = "#1A1A1A";
    public const string BgHeader = BgApp;
    public const string BgSurfaceHover = "#202020";
    public const string BgAccent = "#1a2740";
    public const string Border = "#2A2A2A";
    public const string BorderSubtle = "#222222";
    public const string BorderStrong = "#333333";
    public const string TextPrimary = "#F5F5F5";
    public const string TextSecondary = "#B5B5B5";
    public const string TextMuted = "#8A8A8A";
    public const string TextDisabled = "#5C5C5C";
    public const string Accent = "#3B82F6";
    public const string AccentHover = "#2563EB";
    public const string AccentActive = "#1D4ED8";
    public const string Success = "#34d399";
    public const string SuccessBackground = "#0f2a22";
    public const string Warning = "#fbbf24";
    public const string WarningBackground = "#2a2210";
    public const string Danger = "#f87171";
    public const string DangerBackground = "#2a1416";
    public const string Info = "#60a5fa";
    public const string InfoBackground = "#152033";

    // Spacing scale
    public const string Space1 = "4px";
    public const string Space2 = "8px";
    public const string Space3 = "12px";
    public const string Space4 = "16px";
    public const string Space5 = "20px";
    public const string Space6 = "24px";
    public const string Space8 = "32px";
    public const string Space10 = "40px";
    public const string Space12 = "48px";
    public const string Space16 = "64px";

    // Radii
    public const string RadiusPanel = "12px";
    public const string RadiusSmall = "8px";
    public const string RadiusPill = "9999px";

    public const string BorderHairline = "0.5px";

    public const string TypePage = "24px";
    public const string TypeSection = "16px";
    public const string TypeCard = "14px";
    public const string TypeBody = "14px";
    public const string TypeSecondary = "13px";
    public const string TypeCaption = "12px";

    // Component geometry
    public const string BoardCardPad = "10px";

    private static readonly (string Name, string Value)[] Custom =
    [
        ("--beacon-board-card-pad", BoardCardPad),
        ("--color-bg-app", BgApp),
        ("--color-surface-1", Surface1),
        ("--color-surface-2", Surface2),
        ("--color-bg-sidebar", "var(--color-surface-1)"),
        ("--color-bg-header", BgHeader),
        ("--color-bg-surface", "var(--color-surface-2)"),
        ("--color-bg-surface-hover", BgSurfaceHover),
        ("--color-bg-accent", BgAccent),
        ("--color-border", Border),
        ("--color-border-subtle", BorderSubtle),
        ("--color-border-strong", BorderStrong),
        ("--color-border-accent", "var(--color-accent)"),
        ("--color-text-primary", TextPrimary),
        ("--color-text-secondary", TextSecondary),
        ("--color-text-muted", TextMuted),
        ("--color-text-disabled", TextDisabled),
        ("--color-accent", Accent),
        ("--color-accent-hover", AccentHover),
        ("--color-accent-active", AccentActive),
        ("--color-success", Success),
        ("--color-success-background", SuccessBackground),
        ("--color-warning", Warning),
        ("--color-warning-background", WarningBackground),
        ("--color-danger", Danger),
        ("--color-danger-background", DangerBackground),
        ("--color-info", Info),
        ("--color-info-background", InfoBackground),
        ("--space-1", Space1),
        ("--space-2", Space2),
        ("--space-3", Space3),
        ("--space-4", Space4),
        ("--space-5", Space5),
        ("--space-6", Space6),
        ("--space-8", Space8),
        ("--space-10", Space10),
        ("--space-12", Space12),
        ("--space-16", Space16),
        ("--radius-panel", RadiusPanel),
        ("--radius-small", RadiusSmall),
        ("--radius-pill", RadiusPill),
        ("--border-hairline", BorderHairline),
        ("--type-page", TypePage),
        ("--type-section", TypeSection),
        ("--type-card", TypeCard),
        ("--type-body", TypeBody),
        ("--type-secondary", TypeSecondary),
        ("--type-caption", TypeCaption),
    ];

    public static readonly string RootCss = BuildRoot();

    private static string BuildRoot()
    {
        var lines = Custom.Select(t => $"    {t.Name}: {t.Value};");
        return ":root {\n" + string.Join('\n', lines) + "\n}";
    }
}
