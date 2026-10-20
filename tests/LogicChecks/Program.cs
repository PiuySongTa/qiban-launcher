using System;
using System.Linq;
using QuickLauncherWinUI3;

static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
if (true)
{
    var source = new Category { Name="s", Apps=new(){
        new AppItem { Name="same", Path=@"C:\A\one.exe", Args="--test", Admin=true },
        new AppItem { Name="same", Path=@"C:\B\one.exe" },
        new AppItem { Name="store", Path="shell:AppsFolder\\Test!App" }
    }, Checked=new(){true,false,true} };
    var target = new Category { Name="t", Apps=new(){new AppItem {Name="existing",Path=@"c:\a\ONE.exe",Args="original"}}, Checked=new(){true} };
    var copied = GroupTransfer.Apply(source,target,new[]{0,1,2,2},false);
    Check(copied.Added==2 && copied.Skipped==1 && source.Apps.Count==3,"copy dedup case insensitive; distinct paths; duplicate indices");
    Check(target.Checked.SequenceEqual(new[]{true,false,false}),"new items unchecked, existing target state preserved");
    Check(target.Apps[0].Args=="original","existing duplicate settings preserved");
    target.Apps[1].Name="changed copy";
    Check(source.Apps[1].Name=="same","copy mutation does not change source record");
    var movedTarget = new Category();
    var moved=GroupTransfer.Apply(source,movedTarget,new[]{2,0},true);
    Check(moved.Added==2 && source.Apps.Count==1 && source.Apps[0].Path==@"C:\B\one.exe" && !source.Checked[0],"move uses actual indices and removes in descending order");
    Check(movedTarget.Apps[0].Admin && movedTarget.Apps[0].Args=="--test","admin and args preserved");

    var skippedTransfer=GroupTransfer.Apply(source,target,new[]{0},true);
    Check(skippedTransfer.Added==0 && skippedTransfer.Skipped==1 && source.Apps.Count==1,"duplicate move retains source");
    bool sameRejected=false;try{GroupTransfer.Apply(source,source,new[]{0},true);}catch(ArgumentException){sameRejected=true;}
    Check(sameRejected,"same group rejected");
    bool invalidRejected=false;try{GroupTransfer.Apply(source,target,new[]{0,20},true);}catch(ArgumentOutOfRangeException){invalidRejected=true;}
    Check(invalidRejected && source.Apps.Count==1,"invalid index rejected before mutation");
}
var hits = new[] {
 new ScanHit { Name="same", Path=@"C:\Desktop\Tools\same.exe" },
 new ScanHit { Name="same", Path=@"C:\Desktop\Tools\Nested\same.exe" },
 new ScanHit { Name="other", Path=@"C:\Desktop\ToolsExtra\other.exe" },
 new ScanHit { Name="root", Path=@"C:\Desktop\root.exe" }
};
var root = new ScanFolderFilter("桌面", @"C:\Desktop");
Check(hits.All(root.Matches), "whole root includes descendants and direct files");
Check(root.Children(hits, root.Root).SequenceEqual(new[]{"Tools", "ToolsExtra"}), "immediate child levels");
root.Directory = @"C:\Desktop\Tools";
Check(hits.Count(root.Matches) == 2, "folder boundary and nested descendants");
Check(root.Children(hits, root.Directory).SequenceEqual(new[]{"Nested"}), "nested folder level");
root.Directory = @"C:\Desktop\Tools\Nested";
Check(hits.Count(root.Matches) == 1, "deep folder selection");
var other = new ScanFolderFilter("另一个", @"C:\Desktop\ToolsExtra");
Check(hits.Count(h => new[]{root,other}.Any(f => f.Matches(h))) == 2, "multiple conditions union");
root.Directory = @"c:\desktop\tools";
Check(hits.Count(root.Matches) == 2, "case insensitive paths");
var parent = new ScanFolderFilter("桌面", @"C:\Desktop");
var excluded = new ScanFolderFilter("排除工具", @"C:\Desktop") { Directory=@"C:\Desktop\Tools", Include=false };
var rules = new[]{parent, excluded};
Check(hits.Count(h => ScanFolderFilter.IsIncluded(rules,h)) == 2, "child exclusion overrides selected parent");
var nested = new ScanFolderFilter("重新包含", @"C:\Desktop") { Directory=@"C:\Desktop\Tools\Nested" };
Check(hits.Count(h => ScanFolderFilter.IsIncluded(new[]{parent,excluded,nested},h)) == 3, "deeper inclusion overrides child exclusion");
Check(hits.Count(h => ScanFolderFilter.IsIncluded(new[]{excluded},h)) == 0, "excluded final branch does not restore all");
Check(hits.Count(h => ScanFolderFilter.IsIncluded(new[]{parent,nested},h)) == 4, "overlapping includes never duplicate files");
var registered = new ScanHit { Name="Test app", Path=InstalledApplications.Prefix+"Test.Package!App", Source="已安装应用" };
var installedFilter = new ScanFolderFilter("已安装应用", InstalledApplications.SourceRoot);
Check(installedFilter.Matches(registered), "installed-app source matches shell identity");
Check(!parent.Matches(registered) && !installedFilter.Matches(hits[0]), "file folders and registered app sources remain separate");
Check(!installedFilter.Children(new[]{registered},InstalledApplications.SourceRoot).Any(), "installed applications never parsed as file directories");
Check(LauncherCore.DisplayName("Test app",registered.Path)=="Test app", "registered names do not gain fake extension");
var cfg = new Config { Categories = new(){ new Category { Name="test", Apps=new(){new AppItem { Name=registered.Name, Path=registered.Path }}, Checked=new(){true} } } };
var parsed = LauncherCore.ParseConfig(System.Text.Json.JsonSerializer.Serialize(cfg));
Check(parsed!.Categories[0].Apps[0].Path==registered.Path, "registered entry survives config roundtrip");
var info = InstalledApplications.StartInfo(registered.Path);
Check(info.FileName.EndsWith("explorer.exe") && info.Arguments.Contains(registered.Path), "registered launch delegates identity to Windows");
Check(LauncherCore.TryStart(cfg.Categories[0].Apps[0],true)!=null, "unsupported elevation rejected without launching anything");

Console.WriteLine("All portable logic checks passed.");
