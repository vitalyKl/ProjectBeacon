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

    public const string TypePage = "24px";
    public const string TypeSection = "15px";
    public const string TypeCard = "14px";
    public const string TypeBody = "13px";
    public const string TypeSecondary = "12px";
    public const string TypeCaption = "11px";
    public const string TypeMetric = "23px";

    public const string BoardCardPad = "10px";

    public static class Dark
    {
        public const string BgApp = "#0f1218";
        public const string Surface1 = "#1c212b";
        public const string Surface2 = "#171b23";
        public const string Surface3 = "#242a35";
        public const string BgHeader = "#171b23";
        public const string BgSurfaceHover = "#242a35";
        public const string BgAccent = "#29284c";
        public const string Border = "#2b323e";
        public const string BorderSubtle = "#232a36";
        public const string BorderStrong = "#3a4351";
        public const string TextPrimary = "#edf0f5";
        public const string TextSecondary = "#a3adbb";
        public const string TextMuted = "#707b8b";
        public const string TextDisabled = "#5c6776";
        public const string Accent = "#8883ff";
        public const string AccentHover = "#aaa7ff";
        public const string AccentActive = "#6e69d6";
        public const string AccentSoft = "#29284c";
        public const string Success = "#52c69f";
        public const string SuccessBackground = "#16372f";
        public const string Warning = "#e0a64c";
        public const string WarningBackground = "#3b2d18";
        public const string Danger = "#ef747b";
        public const string DangerBackground = "#442329";
        public const string Info = "#70abe6";
        public const string InfoBackground = "#1d3248";
        public const string Sidebar = "#0a0d12";
        public const string SidebarRaised = "#11151c";
    }

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
        ("--color-on-accent", "#ffffff"),
        ("--color-on-danger", "#ffffff"),
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
        ("--type-metric", TypeMetric),
    ];

    private static readonly (string Name, string Value)[] DarkCustom =
    [
        ("--color-bg-app", Dark.BgApp),
        ("--color-surface-1", Dark.Surface1),
        ("--color-surface-2", Dark.Surface2),
        ("--color-surface-3", Dark.Surface3),
        ("--color-bg-sidebar", Dark.Sidebar),
        ("--color-bg-sidebar-raised", Dark.SidebarRaised),
        ("--color-bg-header", Dark.BgHeader),
        ("--color-bg-surface-hover", Dark.BgSurfaceHover),
        ("--color-bg-accent", Dark.BgAccent),
        ("--color-border", Dark.Border),
        ("--color-border-subtle", Dark.BorderSubtle),
        ("--color-border-strong", Dark.BorderStrong),
        ("--color-text-primary", Dark.TextPrimary),
        ("--color-text-secondary", Dark.TextSecondary),
        ("--color-text-muted", Dark.TextMuted),
        ("--color-text-disabled", Dark.TextDisabled),
        ("--color-accent", Dark.Accent),
        ("--color-accent-hover", Dark.AccentHover),
        ("--color-accent-active", Dark.AccentActive),
        ("--color-accent-soft", Dark.AccentSoft),
        ("--color-on-accent", "#10141c"),
        ("--color-on-danger", "#2a1013"),
        ("--color-success", Dark.Success),
        ("--color-success-background", Dark.SuccessBackground),
        ("--color-warning", Dark.Warning),
        ("--color-warning-background", Dark.WarningBackground),
        ("--color-danger", Dark.Danger),
        ("--color-danger-background", Dark.DangerBackground),
        ("--color-info", Dark.Info),
        ("--color-info-background", Dark.InfoBackground),
    ];

    public static readonly string RootCss = BuildRoot();

    private static string BuildRoot()
    {
        var light = string.Join('\n', Custom.Select(t => $"    {t.Name}: {t.Value};"));
        var dark = string.Join('\n', DarkCustom.Select(t => $"    {t.Name}: {t.Value};"));
        return ":root {\n" + light + "\n}\n\nbody:has(.beacon-theme-dark) {\n" + dark + "\n    background: var(--color-bg-app);\n    color: var(--color-text-primary);\n}";
    }
}
