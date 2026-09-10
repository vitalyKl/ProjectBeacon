namespace ProjectBeacon.Web.Tests;

using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;

public sealed class ResourceInspect
{
    [Fact]
    public void ResourceManager_Lookup()
    {
        var assembly = typeof(ProjectBeacon.Web.Features.Dashboard.Dashboard).Assembly;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== Manifest Resources ===");
        foreach (var name in assembly.GetManifestResourceNames())
        {
            sb.AppendLine($"  {name}");
        }

        sb.AppendLine();
        sb.AppendLine("=== ResourceManager Lookup ===");
        var rm = new ResourceManager("ProjectBeacon.Web.Resources.Web", assembly);
        foreach (var culture in new[] { "en", "ru", "de", "ja" })
        {
            var ci = new CultureInfo(culture);
            try
            {
                var dashboard = rm.GetString("Dashboard", ci);
                sb.AppendLine($"  {culture}/Dashboard = '{dashboard}'");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  {culture}/Dashboard = ERROR: {ex.Message}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("=== ResourceSets ===");
        var enSet = rm.GetResourceSet(new CultureInfo("en"), true, true);
        if (enSet != null)
        {
            foreach (DictionaryEntry de in enSet)
            {
                sb.AppendLine($"  en/{de.Key} = '{de.Value}'");
            }
        }
        else
        {
            sb.AppendLine("  en Set = null");
        }

        var ruSet = rm.GetResourceSet(new CultureInfo("ru"), true, true);
        if (ruSet != null)
        {
            foreach (DictionaryEntry de in ruSet)
            {
                sb.AppendLine($"  ru/{de.Key} = '{de.Value}'");
            }
        }
        else
        {
            sb.AppendLine("  ru Set = null");
        }

        Assert.Contains(assembly.GetManifestResourceNames(), n => n.Contains("Web.resources", StringComparison.Ordinal));
    }
}
