namespace ProjectBeacon.Web.Tests;

using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;

/// <summary>
/// Localization parity ratchet: every key in the neutral (en) Web resources must exist in
/// every supported culture. A key missing from a locale silently falls back to English at
/// runtime, so new omissions must fail this test. The pinned baseline below records the
/// keys known to be untranslated as of the last parity audit; it must shrink (as locales
/// catch up) and never grow (a new English key without translations fails the test).
/// </summary>
public sealed class LocalizationParityTests
{
    // Mirrors ServiceCollectionExtensions.SupportedCultures; "en" is the neutral base.
    private static readonly string[] SupportedCultures = ["ru", "de", "ja", "zh"];

    // Keys present in Web.resx but absent from at least one supported locale as of the
    // last parity audit. Remove entries as translations land; an empty list means full parity.
    private static readonly string[] KnownUntranslatedKeys =
    [
        "AgentChat", "Appearance", "AttentionCount",
        "CommandPalette", "CommandPaletteEmpty", "CommandPalettePlaceholder",
        "CurrentlyRunning", "CurrentProject",
        "DeleteProject", "DeleteProjectConfirm",
        "FleetCounts", "FleetDegraded", "FleetEmpty", "FleetHealth", "FleetOffline", "FleetOperational",
        "Goal", "MilestoneCounts", "MoreTasks",
        "NavGovern", "NavIntelligence", "NavOperate",
        "NeedsAttention", "NextStep", "NoAttention", "NoHostLoad", "NoRunningWork",
        "OpenProject", "ProductVersion",
        "ProjectDeleted", "ProjectManageLead", "ProjectNameRequired", "ProjectRenamed",
        "ReportTaskCount", "Reviews", "ReviewSeparate", "SearchOrCommand", "ServerCompileNoTree",
        "ShellHintAgents", "ShellHintBacklog", "ShellHintBoard", "ShellHintChat", "ShellHintConnections",
        "ShellHintContext", "ShellHintDecisions", "ShellHintNewProject", "ShellHintOverview",
        "ShellHintProject", "ShellHintReports", "ShellHintRoadmap", "ShellHintSettings",
        "ShellHintTask", "ShellHintWorkstations",
        "SubtaskReported", "SystemLoad",
        "ThemeDark", "ThemeLight", "ThemeSystem", "ThemeToDark", "ThemeToLight"
    ];

    private static readonly string ResourceBaseName = "ProjectBeacon.Web.Resources.Web";

    [Fact]
    public void NeutralKeySet_IsSubstantial()
    {
        var keys = ReadKeys(WebAssembly);
        Assert.True(keys.Count >= 700, $"sanity: expected at least 700 neutral keys, got {keys.Count}");
    }

    [Fact]
    public void EveryNeutralKey_IsTranslatedOrPinned()
    {
        var neutralKeys = ReadKeys(WebAssembly);
        var pinned = new HashSet<string>(KnownUntranslatedKeys);

        foreach (var cultureName in SupportedCultures)
        {
            var cultureKeys = ReadKeys(LoadCultureAssembly(cultureName));
            var missing = neutralKeys.Where(k => !cultureKeys.Contains(k)).ToList();
            var newDrift = missing.Where(k => !pinned.Contains(k)).OrderBy(k => k).ToList();

            Assert.True(newDrift.Count == 0,
                $"'{cultureName}' is missing newly added keys (translate them or extend the pinned baseline with justification): " +
                string.Join(", ", newDrift));
        }
    }

    [Fact]
    public void NoCulture_DefinesKeysAbsentFromEnglish()
    {
        var neutralKeys = ReadKeys(WebAssembly);

        foreach (var cultureName in SupportedCultures)
        {
            var cultureKeys = ReadKeys(LoadCultureAssembly(cultureName));
            var orphans = cultureKeys.Where(k => !neutralKeys.Contains(k)).OrderBy(k => k).ToList();

            Assert.True(orphans.Count == 0,
                $"'{cultureName}' defines keys that do not exist in the English base (add the English key first): " +
                string.Join(", ", orphans));
        }
    }

    private static Assembly WebAssembly => typeof(ProjectBeacon.Web.Features.Dashboard.Dashboard).Assembly;

    private static Assembly LoadCultureAssembly(string cultureName)
    {
        var satelliteName = WebAssembly.GetName().Name + ".resources.dll";
        var path = Path.Combine(AppContext.BaseDirectory, cultureName, satelliteName);
        Assert.True(File.Exists(path), $"satellite resource assembly for '{cultureName}' not found at {path}");

        return Assembly.LoadFile(path);
    }

    private static HashSet<string> ReadKeys(Assembly assembly)
    {
        // The neutral assembly embeds '{Base}.resources'; culture satellites embed
        // '{Base}.{culture}.resources', so resolve the name from the assembly itself.
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.StartsWith(ResourceBaseName, StringComparison.Ordinal) && n.EndsWith(".resources", StringComparison.Ordinal));
        Assert.True(resourceName != null, $"no '{ResourceBaseName}' resource in {assembly.GetName().Name}");

        var stream = assembly.GetManifestResourceStream(resourceName!);
        Assert.NotNull(stream);
        using var reader = new ResourceReader(stream!);
        var keys = new HashSet<string>();
        foreach (DictionaryEntry entry in reader)
        {
            keys.Add(entry.Key.ToString()!);
        }
        return keys;
    }
}
