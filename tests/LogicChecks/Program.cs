using System;
using System.Linq;
using QuickLauncherWinUI3;

static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
try
{
if (args.Length == 2 && args[0] == "--public-update-probe")
{
    using var client = new AppUpdateClient();
    using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
    var release = await client.CheckAsync("0.3.0", false, timeout.Token) ?? throw new Exception("No newer public Windows x64 release was found");
    Check(AppReleaseVersion.Parse(release.Tag).CompareTo(AppReleaseVersion.Parse("0.3.0")) > 0,"anonymous public release check finds an upgrade");
    await client.DownloadAsync(release, args[1], null, timeout.Token);
    Check(new System.IO.FileInfo(args[1]).Length == release.Package.Size,"actual public package download completes with SHA256 verification");
    Console.WriteLine("PUBLIC_RELEASE="+release.Tag);
    return;
}
if (args.Length == 2 && args[0] == "--migration-snapshot")
{
    string snapshot = System.IO.File.ReadAllText(args[1]);
    using var old = System.Text.Json.JsonDocument.Parse(snapshot);
    var rawGroups = old.RootElement.GetProperty("Categories").EnumerateArray().ToArray();
    var expectedPaths = rawGroups.SelectMany(c => c.GetProperty("Apps").EnumerateArray()).Select(a => a.GetProperty("Path").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var migration = LauncherCore.ParseConfig(snapshot) ?? throw new Exception("Snapshot parsing failed");
    Check(expectedPaths.IsSubsetOf(migration.MainCatalog.Apps.Select(a=>a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)),"snapshot migration retains every existing program in main catalog");
    var normalGroups = rawGroups.Where(c => !c.TryGetProperty("IsAutoScan",out var autoFlag) || !autoFlag.GetBoolean()).ToArray();
    Check(migration.Categories.Count==normalGroups.Length,"snapshot migration keeps every ordinary group");
    for (int i=0; i<normalGroups.Length; i++)
    {
        var before=System.Text.Json.JsonSerializer.Deserialize<Category>(normalGroups[i].GetRawText())!;
        Check(System.Text.Json.JsonSerializer.Serialize(before.Apps)==System.Text.Json.JsonSerializer.Serialize(migration.Categories[i].Apps) && before.Checked.SequenceEqual(migration.Categories[i].Checked),"snapshot ordinary group records, settings and checks preserved #"+(i+1));
    }
    Console.WriteLine("Snapshot migration verified without saving or launching anything.");
    return;
}
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

var scanSamples = new[] {
    new ScanHit { Name="tool",Path=@"C:\Tools\tool.exe" },
    new ScanHit { Name="tool",Path=@"C:\Other\tool.exe" },
    new ScanHit { Name="nested",Path=@"C:\Tools\Sub\nested.exe" },
    new ScanHit { Name="neighbor",Path=@"C:\ToolsExtra\neighbor.exe" },
    new ScanHit { Name="TOOL",Path=@"c:\TOOLS\TOOL.exe" },
    registered
};
Check(ScanPolicy.Filter(scanSamples,Array.Empty<string>()).Count==5,"scan dedup uses full path and ignores case");
Check(ScanPolicy.Filter(scanSamples,new[]{@"C:\Tools"}).Count==3,"directory exclusions include descendants but keep sibling prefix");
Check(ScanPolicy.Filter(scanSamples,new[]{"tool.exe"}).Count==3,"filename exclusion applies across directories and case");
Check(ScanPolicy.Filter(scanSamples,new[]{@"C:\Other\tool.exe"}).Count==4,"full file exclusion preserves same name elsewhere");
Check(ScanPolicy.Excluded(registered,new[]{"Test app"}) && ScanPolicy.Excluded(registered,new[]{registered.Path}),"installed apps excluded by name or full shell identity");
Check(ScanPolicy.Filter(scanSamples,new[]{@"C:\"}).Count==1,"drive root exclusion preserves registered apps");
Check(!ScanPolicy.Excluded(scanSamples[0],new[]{""}),"blank exclusion never excludes everything");
Check(ScanPolicy.Lines("\r\n C:\\Tools \r\n\"c:\\tools\"\nC:\\Other").Count==2,"settings trim quotes, blank lines, and duplicate scopes");
var autoGroup = new Category { IsAutoScan=true, Apps=new(){new AppItem{Name="custom",Path=@"C:\Tools\tool.exe",Admin=true,Args="keep"}}, Checked=new(){true} };
Check(ScanPolicy.Append(autoGroup,scanSamples)==4,"automatic refresh merges distinct identities");
Check(autoGroup.Checked.SequenceEqual(new[]{true,false,false,false,false}) && autoGroup.Apps[0].Args=="keep" && autoGroup.Apps[0].Admin,"refresh preserves checks and user settings; new entries unchecked");
Check(ScanPolicy.Append(autoGroup,Array.Empty<ScanHit>())==0 && autoGroup.Apps.Count==5,"empty or unavailable scan never removes existing entries");
Check(ScanPolicy.Append(autoGroup,scanSamples)==0,"repeated automatic refresh is idempotent");
ScanPolicy.Delete(autoGroup,new[]{3,1,3});
Check(autoGroup.Apps.Count==3 && autoGroup.Apps[0].Path==@"C:\Tools\tool.exe" && autoGroup.Apps[1].Path==@"C:\Tools\Sub\nested.exe" && autoGroup.Apps[2].Path==registered.Path && autoGroup.Checked.SequenceEqual(new[]{true,false,false}),"batch delete uses original indices and preserves remaining state");
bool deleteRejected=false;try {ScanPolicy.Delete(autoGroup,new[]{0,99});}catch(ArgumentOutOfRangeException){deleteRejected=true;}
Check(deleteRejected && autoGroup.Apps.Count==3,"invalid batch indices fail before deleting any record");
autoGroup.IsAutoScan = false;
var settingsConfig = new Config { Categories=new(){autoGroup}, ScanDirectories=new(){@"C:\Tools"},ScanExclusions=new(){"tool.exe"},ScanDefaultLocations=false,ScanInstalledApps=false,AutoScanEnabled=false,TransparentPanels=false };
var settingsRoundtrip = LauncherCore.ParseConfig(System.Text.Json.JsonSerializer.Serialize(settingsConfig))!;
Check(settingsRoundtrip.MainCatalog.IsAutoScan && !settingsRoundtrip.AutoScanEnabled && !settingsRoundtrip.TransparentPanels && settingsRoundtrip.ScanExclusions.Single()=="tool.exe", "new preferences and auto-group identity survive roundtrip");
Check(ScanPolicy.Roots(settingsRoundtrip).SequenceEqual(new[]{@"C:\Tools"}),"disabled default locations leaves only configured roots");
var oldConfig = LauncherCore.ParseConfig("{\"Categories\":[{\"Name\":\"old\"}]}")!;
Check(oldConfig.ScanDirectories.Count==0 && oldConfig.ScanDefaultLocations && oldConfig.ScanInstalledApps && oldConfig.TransparentPanels,"old config receives compatible defaults without migration overwrite");
string fixture = System.IO.Path.Combine(AppContext.BaseDirectory,"scan-fixture-"+Guid.NewGuid().ToString("N"));
try
{
    System.IO.Directory.CreateDirectory(System.IO.Path.Combine(fixture,"Nested"));
    System.IO.File.WriteAllText(System.IO.Path.Combine(fixture,"keep.exe"),"");
    System.IO.File.WriteAllText(System.IO.Path.Combine(fixture,"ignore.txt"),"");
    System.IO.File.WriteAllText(System.IO.Path.Combine(fixture,"Nested","keep.exe"),"");
    var actualScan = LauncherCore.ScanFolderWithStatus(fixture,"fixture",out int skipped);
    Check(actualScan.Count==2 && skipped==0,"real folder scan includes nested executables and ignores non-program extensions");
    Check(ScanPolicy.Filter(actualScan,new[]{System.IO.Path.Combine(fixture,"Nested")}).Count==1,"real scanned hits respect saved folder exclusions");
    Check(ScanPolicy.Filter(actualScan,new[]{"keep.exe"}).Count==0,"real scanned hits respect filename exclusions");
}
finally { if (!System.IO.Path.GetFullPath(fixture).StartsWith(System.IO.Path.GetFullPath(AppContext.BaseDirectory),StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup"); System.IO.Directory.Delete(fixture,true); }
// ConfigPath here is the independent LogicChecks output folder, never the release folder.
Check(!System.IO.File.Exists(LauncherCore.ConfigPath),"test output has no pre-existing user configuration");
try
{
    LauncherCore.SaveConfigChecked(settingsConfig);
    string original = System.IO.File.ReadAllText(LauncherCore.ConfigPath);
    var deleteDraft = LauncherCore.ParseConfig(original)!;
    ScanPolicy.Delete(deleteDraft.Categories[0],new[]{0,2});
    bool writeFailed=false;
    using (var locked = new System.IO.FileStream(LauncherCore.ConfigPath,System.IO.FileMode.Open,System.IO.FileAccess.Read,System.IO.FileShare.None))
    {
        try { LauncherCore.SaveConfigChecked(deleteDraft); }
        catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { writeFailed=true; }
    }
    Check(writeFailed && System.IO.File.ReadAllText(LauncherCore.ConfigPath)==original,"failed batch save leaves on-disk configuration intact");
    Check(settingsConfig.Categories[0].Apps.Count==3 && !System.IO.File.Exists(LauncherCore.ConfigPath+".tmp"),"failed draft save preserves live source and cleans temporary file");
    Check(!LauncherCore.ConfigChangedExternally(),"own successful save updates automatic-refresh configuration stamp");
    System.IO.File.AppendAllText(LauncherCore.ConfigPath,"\n ");
    Check(LauncherCore.ConfigChangedExternally(),"external configuration modification is detected before automatic overwrite");
}
finally { System.IO.File.Delete(LauncherCore.ConfigPath); }
var legacyAuto = new Category { Name="自动收录",IsAutoScan=true,Apps=new(){new AppItem{Name="old",Path=@"C:\Old\app.exe",Admin=true,Args="keep"}},Checked=new(){true} };
var catalogConfig = new Config { Categories=new(){ legacyAuto, new Category{Name="work",Apps=new(){new AppItem{Name="old elsewhere",Path=@"C:\Other\app.exe"},new AppItem{Name="duplicate",Path=@"c:\OLD\APP.exe"}},Checked=new(){true,true}} } };
MainCatalogModel.Normalize(catalogConfig);
Check(catalogConfig.Categories.Count==1 && catalogConfig.MainCatalog.Apps.Count==2,"legacy automatic group migrates to independent deduplicated main catalog");
Check(catalogConfig.MainCatalog.Checked.SequenceEqual(new[]{true,false}) && catalogConfig.MainCatalog.Apps[0].Admin && catalogConfig.MainCatalog.Apps[0].Args=="keep","catalog migration preserves legacy selection and launch settings, group checks stay independent");
var memberships=MainCatalogModel.GroupedPaths(catalogConfig);
Check(catalogConfig.MainCatalog.Apps.All(a=>MainCatalogModel.Matches(a,memberships,2)),"group membership matches full paths ignoring case");
catalogConfig.MainCatalog.Apps.Add(new AppItem{Name="unassigned",Path=@"C:\New\app.exe"});catalogConfig.MainCatalog.Checked.Add(true);
Check(catalogConfig.MainCatalog.Apps.Count(a=>MainCatalogModel.Matches(a,memberships,1))==1,"ungrouped filter identifies only unassigned catalog entries");
var assigned=GroupTransfer.Apply(catalogConfig.MainCatalog,catalogConfig.Categories[0],new[]{2},false);
Check(assigned.Added==1 && catalogConfig.MainCatalog.Apps.Count==3 && !MainCatalogModel.Matches(catalogConfig.MainCatalog.Apps[2],MainCatalogModel.GroupedPaths(catalogConfig),1),"assigning from catalog preserves full catalog and removes item from ungrouped view");
MainCatalogModel.Normalize(catalogConfig); MainCatalogModel.Normalize(catalogConfig);
Check(catalogConfig.MainCatalog.Apps.Count==3 && catalogConfig.Categories.Count==1,"repeated normalization is idempotent");
Check(ReferenceEquals(MainCatalogModel.At(catalogConfig,MainCatalogModel.Index),catalogConfig.MainCatalog) && MainCatalogModel.At(catalogConfig,-1)==null,"special catalog identity never aliases an ordinary category index");
var catalogRoundtrip=LauncherCore.ParseConfig(System.Text.Json.JsonSerializer.Serialize(catalogConfig))!;
Check(catalogRoundtrip.MainCatalog.Apps.Count==3 && catalogRoundtrip.Categories.Count==1,"independent main catalog survives save and restore");
await UpdateChecks.Run(Check);
Console.WriteLine("All portable logic checks passed.");
}
catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); Environment.ExitCode=1; }
