namespace ProjectBeacon.Web.Theme;

public static class DesignTokens
{
    public const string BgApp = "#f3f5f7";
    public const string Surface1 = "#f7f8fa";
    public const string Surface2 = "#ffffff";
    public const string Surface3 = "#eceff3";
    public const string BgHeader = "#ffffff";
    public const string BgSurfaceHover = "#eceff3";
    public const string BgAccent = "#ecebff";
    public const string Border = "#dfe3e8";
    public const string BorderSubtle = "#e7ebf0";
    public const string BorderStrong = "#cbd1d9";
    public const string TextPrimary = "#161b22";
    public const string TextSecondary = "#535d6c";
    public const string TextMuted = "#818b99";
    public const string TextDisabled = "#a0a8b3";
    public const string Accent = "#625cf6";
    public const string AccentHover = "#4f48dc";
    public const string AccentActive = "#3f39c4";
    public const string AccentSoft = "#ecebff";
    public const string Success = "#168567";
    public const string SuccessBackground = "#e6f5f0";
    public const string Warning = "#a56810";
    public const string WarningBackground = "#fff3da";
    public const string Danger = "#cf4d55";
    public const string DangerBackground = "#fdecee";
    public const string Info = "#2877c7";
    public const string InfoBackground = "#e7f1fb";

    public const string Sidebar = "#10141c";
    public const string SidebarRaised = "#171c26";
    public const string SidebarText = "#929cac";
    public const string SidebarTextStrong = "#eef1f5";
    public const string SidebarMuted = "#596474";
    public const string SidebarActive = "#232939";
    public const string SidebarRail = "#8f89ff";
    public const string SidebarHover = "rgba(255,255,255,.06)";
    public const string SidebarLine = "rgba(255,255,255,.08)";

    public const string FontSans = "\"Inter\", system-ui, sans-serif";
    public const string FontMono = "\"IBM Plex Mono\", ui-monospace, monospace";

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

    public const string RadiusPanel = "12px";
    public const string RadiusSmall = "8px";
    public const string RadiusControl = "5px";
    public const string RadiusPill = "9999px";

    public const string BorderHairline = "1px";

    public const string TypePage = "18px";
    public const string TypeSection = "13px";
    public const string TypeCard = "13px";
    public const string TypeBody = "13px";
    public const string TypeSecondary = "12px";
    public const string TypeCaption = "11px";

    public const string BoardCardPad = "10px";

    private static readonly (string Name, string Value)[] Custom =
    [
        ("--beacon-board-card-pad", BoardCardPad),
        ("--font-sans", FontSans),
        ("--font-mono", FontMono),
        ("--color-bg-app", BgApp),
        ("--color-surface-1", Surface1),
        ("--color-surface-2", Surface2),
        ("--color-surface-3", Surface3),
        ("--color-bg-sidebar", Sidebar),
        ("--color-bg-sidebar-raised", SidebarRaised),
        ("--color-sidebar-text", SidebarText),
        ("--color-sidebar-text-strong", SidebarTextStrong),
        ("--color-sidebar-muted", SidebarMuted),
        ("--color-sidebar-active", SidebarActive),
        ("--color-sidebar-rail", SidebarRail),
        ("--color-sidebar-hover", SidebarHover),
        ("--color-sidebar-line", SidebarLine),
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
        ("--color-accent-soft", AccentSoft),
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
        ("--radius-control", RadiusControl),
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
