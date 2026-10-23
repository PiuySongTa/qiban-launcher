using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickLauncherWinUI3;

public static class MainCatalogModel
{
    public const int Index = -2;
    public static Category? At(Config config, int index) => index == Index ? config.MainCatalog :
        index >= 0 && index < config.Categories.Count ? config.Categories[index] : null;

    public static void Normalize(Config config)
    {
        config.MainCatalog ??= new Category();
        config.MainCatalog.Name = "主目录";
        config.MainCatalog.IsAutoScan = true;
        LauncherCore.EnsureCheckedSync(config.MainCatalog);
        // Upgrade the former automatic group without losing its records or selection.
        foreach (var group in config.Categories.Where(c => c.IsAutoScan)) Merge(config.MainCatalog, group, true);
        config.Categories.RemoveAll(c => c.IsAutoScan);
        if (config.Categories.Count == 0) config.Categories.Add(new Category { Name = "默认" });
        IncludeGroups(config);
    }

    public static void IncludeGroups(Config config)
    {
        foreach (var group in config.Categories) Merge(config.MainCatalog, group, false);
    }

    private static void Merge(Category catalog, Category source, bool selection)
    {
        LauncherCore.EnsureCheckedSync(source);
        var seen = new HashSet<string>(catalog.Apps.Select(a => a.Path), StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < source.Apps.Count; i++)
        {
            var item = source.Apps[i];
            if (!seen.Add(item.Path)) continue;
            catalog.Apps.Add(new AppItem { Name = item.Name, Path = item.Path, Args = item.Args, Admin = item.Admin });
            catalog.Checked.Add(selection && source.Checked[i]);
        }
    }

    public static HashSet<string> GroupedPaths(Config config) => new(config.Categories.SelectMany(c => c.Apps).Select(a => a.Path), StringComparer.OrdinalIgnoreCase);
    public static bool Matches(AppItem item, ISet<string> grouped, int filter) => filter switch
    {
        1 => !grouped.Contains(item.Path),
        2 => grouped.Contains(item.Path),
        _ => true
    };
}
