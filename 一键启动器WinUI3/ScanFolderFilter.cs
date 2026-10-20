using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QuickLauncherWinUI3;

// Each row includes a directory and all its descendants; rows are combined with OR.
public sealed class ScanFolderFilter
{
    public bool Include { get; set; } = true;
    public string Label { get; }
    public string Root { get; }
    public string Directory { get; set; }
    public ScanFolderFilter(string label, string root)
    {
        Label = label;
        Root = Normalize(root);
        Directory = Root;
    }
    public static string Normalize(string path) => path == InstalledApplications.SourceRoot ? path : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    public bool Matches(ScanHit hit) => Directory == InstalledApplications.SourceRoot ? InstalledApplications.IsEntry(hit.Path) : !InstalledApplications.IsEntry(hit.Path) && Path.GetFullPath(hit.Path).StartsWith(Normalize(Directory), StringComparison.OrdinalIgnoreCase);
    public static bool IsIncluded(IEnumerable<ScanFolderFilter> filters, ScanHit hit) =>
        filters.Where(f => f.Matches(hit)).OrderByDescending(f => Normalize(f.Directory).Length)
            .Select(f => f.Include).FirstOrDefault();

    public IEnumerable<string> Children(IEnumerable<ScanHit> hits, string directory)
    {
        if (directory == InstalledApplications.SourceRoot) return Enumerable.Empty<string>();
        string prefix = Normalize(directory);
        return hits.Where(h => !InstalledApplications.IsEntry(h.Path)).Select(h => Path.GetDirectoryName(Path.GetFullPath(h.Path))!)
            .Where(p => Normalize(p).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Length > prefix.Length ? p.Substring(prefix.Length).Split(Path.DirectorySeparatorChar)[0] : "")
            .Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase);
    }
}
