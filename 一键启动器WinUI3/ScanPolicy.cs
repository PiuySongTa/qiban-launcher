using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QuickLauncherWinUI3;

// One policy for manual scans, additional folders, and the automatic group.
public static class ScanPolicy
{
    public static List<string> Lines(string? value) => (value ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Trim().Trim('"')).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static List<string> Roots(Config config)
    {
        var roots = new List<string>();
        if (config.ScanDefaultLocations)
            foreach (var location in new[] { Environment.SpecialFolder.Desktop, Environment.SpecialFolder.CommonDesktopDirectory,
                Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu })
                roots.Add(Environment.GetFolderPath(location));
        roots.AddRange(config.ScanDirectories ?? new());
        return roots.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool Excluded(ScanHit hit, IEnumerable<string> exclusions)
    {
        string path = hit.Path.Replace('/', '\\').TrimEnd('\\');
        foreach (string entry in exclusions)
        {
            string rule = entry.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
            if (rule.Length == 0) continue;
            // Explicit identities/paths match exactly, folders also match descendants at a separator boundary.
            if (path.Equals(rule, StringComparison.OrdinalIgnoreCase) ||
                (Path.IsPathFullyQualified(rule + (rule.EndsWith(':') ? "\\" : "")) && path.StartsWith(rule + "\\", StringComparison.OrdinalIgnoreCase))) return true;
            if (!rule.Contains('\\') && !rule.Contains(':') &&
                (Path.GetFileName(path).Equals(rule, StringComparison.OrdinalIgnoreCase) ||
                 hit.Name.Equals(rule, StringComparison.OrdinalIgnoreCase) ||
                 LauncherCore.DisplayName(hit.Name, hit.Path).Equals(rule, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    public static List<ScanHit> Filter(IEnumerable<ScanHit> hits, IEnumerable<string> exclusions)
    {
        var rules = exclusions.ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return hits.Where(h => !Excluded(h, rules) && seen.Add(h.Path)).ToList();
    }

    public static int Append(Category target, IEnumerable<ScanHit> hits)
    {
        LauncherCore.EnsureCheckedSync(target);
        var seen = new HashSet<string>(target.Apps.Select(a => a.Path), StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (var hit in hits)
            if (seen.Add(hit.Path))
            {
                target.Apps.Add(new AppItem { Name = hit.Name, Path = hit.Path });
                target.Checked.Add(false);
                added++;
            }
        return added;
    }

    public static int Delete(Category target, IEnumerable<int> indices)
    {
        int[] actual = indices.Distinct().OrderByDescending(i => i).ToArray();
        if (actual.Any(i => i < 0 || i >= target.Apps.Count)) throw new ArgumentOutOfRangeException(nameof(indices));
        LauncherCore.EnsureCheckedSync(target);
        foreach (int i in actual) { target.Apps.RemoveAt(i); target.Checked.RemoveAt(i); }
        return actual.Length;
    }
}
