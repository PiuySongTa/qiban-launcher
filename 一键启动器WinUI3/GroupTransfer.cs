using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickLauncherWinUI3;

public static class GroupTransfer
{
    // Called on a draft configuration: persistence must succeed before publishing the draft to the UI.
    public static (int Added, int Skipped) Apply(Category source, Category target, IEnumerable<int> indices, bool move)
    {
        if (ReferenceEquals(source, target)) throw new ArgumentException("不能选择原分组。");
        var selected = indices.Distinct().OrderBy(i => i).ToArray();
        if (selected.Any(i => i < 0 || i >= source.Apps.Count)) throw new ArgumentOutOfRangeException(nameof(indices));
        LauncherCore.EnsureCheckedSync(source);
        LauncherCore.EnsureCheckedSync(target);
        var paths = new HashSet<string>(target.Apps.Select(a => a.Path), StringComparer.OrdinalIgnoreCase);
        var transferred = new List<int>();
        foreach (int index in selected)
        {
            var item = source.Apps[index];
            if (!paths.Add(item.Path)) continue;
            target.Apps.Add(new AppItem { Name = item.Name, Path = item.Path, Args = item.Args, Admin = item.Admin });
            target.Checked.Add(false);
            transferred.Add(index);
        }
        if (move)
            foreach (int index in transferred.OrderByDescending(i => i))
            {
                source.Apps.RemoveAt(index);
                source.Checked.RemoveAt(index);
            }
        return (transferred.Count, selected.Length - transferred.Count);
    }
}
