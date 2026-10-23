using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace QuickLauncherWinUI3;

// ============ 数据模型（字段名与 WinForms 版 launcher_config.json 完全兼容） ============
public class AppItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Args { get; set; } = "";
    public bool Admin { get; set; }
}

public class Category
{
    public string Name { get; set; } = "";
    public bool IsAutoScan { get; set; }
    public List<AppItem> Apps { get; set; } = new();
    public List<bool> Checked { get; set; } = new();
}

public class Config
{
    public Category MainCatalog { get; set; } = new() { Name = "主目录", IsAutoScan = true };
    public List<Category> Categories { get; set; } = new();
    public string? Theme { get; set; }
    public string BackgroundMode { get; set; } = "Default";
    public string? BackgroundImage { get; set; }
    public double BackgroundShade { get; set; } = 55;
    public List<string> ScanDirectories { get; set; } = new();
    public List<string> ScanExclusions { get; set; } = new();
    public bool ScanDefaultLocations { get; set; } = true;
    public bool ScanInstalledApps { get; set; } = true;
    public bool AutoScanEnabled { get; set; } = true;
    public bool TransparentPanels { get; set; } = true;
}

// 扫描结果一条
public class ScanHit
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Source { get; set; } = "";
    public bool IsSelected { get; set; }   // 勾选状态存在数据层，切换筛选不丢
}

// ============ 核心逻辑（从 WinForms 版移植） ============
public static class LauncherCore
{
    public const string CONFIG_FILE = "launcher_config.json";
    public const string APPGROUP_CONFIG = "AppGroup\\appgroups.json";

    private static readonly string[] ScanExts =
        { "*.lnk", "*.url", "*.exe", "*.com", "*.msi", "*.jar", "*.appref-ms", "*.bat", "*.cmd" };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // 保留自定义名称，只在末尾缺少实际文件后缀时补齐。
    public static string DisplayName(string name, string path)
    {
        if (InstalledApplications.IsEntry(path)) return name;
        string ext = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(name)) return Path.GetFileName(path);
        return string.IsNullOrEmpty(ext) || name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
            ? name : name + ext;
    }

    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, CONFIG_FILE);

    public static string AppGroupConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), APPGROUP_CONFIG);

    // ---------- 配置 ----------

    public static Config CreateDefault()
    {
        var c = new Config();
        c.Categories.Add(new Category { Name = "学习" });
        c.Categories.Add(new Category { Name = "编程" });
        c.Categories.Add(new Category { Name = "翻墙" });
        return c;
    }

    public static Config LoadConfig()
    {
        ConfigurationReadFailed = false;
        _lastConfigStamp = null;
        Config config;
        try
        {
            if (File.Exists(ConfigPath))
            {
                string configText = File.ReadAllText(ConfigPath, Encoding.UTF8);
                config = JsonSerializer.Deserialize<Config>(configText, JsonOpts) ?? new Config();
                _lastConfigStamp = Stamp(configText);
                config.Categories ??= new List<Category>();
            }
            else
            {
                config = CreateDefault();
                // 首次运行：自动从 AppGroup 迁移已有分组
                ImportFromAppGroup(config, false);
            }
        }
        catch (Exception ex)
        {
            ConfigurationReadFailed = true;
            AppLog.Write("Configuration read failed; automatic writes disabled until explicitly saved", ex);
            config = new Config();
            config.Categories = new List<Category>();
        }
        if (config.Categories.Count == 0) config.Categories.Add(new Category { Name = "默认" });
        foreach (var cat in config.Categories) EnsureCheckedSync(cat);
        MainCatalogModel.Normalize(config);
        return config;
    }

    public static bool ConfigurationReadFailed { get; private set; }
    private static string? _lastConfigStamp;
    private static string Stamp(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static bool ConfigChangedExternally()
    {
        try { return _lastConfigStamp != (File.Exists(ConfigPath) ? Stamp(File.ReadAllText(ConfigPath, Encoding.UTF8)) : null); }
        catch (Exception ex) { AppLog.Write("Cannot verify configuration before automatic update", ex); return true; }
    }

    public static void SaveConfig(Config config)
    {
        try
        {
            string text = JsonSerializer.Serialize(config, JsonOpts);
            File.WriteAllText(ConfigPath, text, Encoding.UTF8);
            _lastConfigStamp = Stamp(text);
            ConfigurationReadFailed = false;
        }
        catch { }
    }

    public static void SaveConfigChecked(Config config)
    {
        string temporary = ConfigPath + ".tmp";
        try
        {
            string text = JsonSerializer.Serialize(config, JsonOpts);
            File.WriteAllText(temporary, text, Encoding.UTF8);
            File.Move(temporary, ConfigPath, true);
            _lastConfigStamp = Stamp(text);
            ConfigurationReadFailed = false;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // 解析配置文本（用于"从备份恢复"校验）
    public static Config? ParseConfig(string json)
    {
        try
        {
            var c = JsonSerializer.Deserialize<Config>(json, JsonOpts);
            if (c == null) return null;
            c.Categories ??= new List<Category>();
            MainCatalogModel.Normalize(c);
            return c;
        }
        catch { return null; }
    }

    // 勾选列表与 Apps 保持同步
    public static void EnsureCheckedSync(Category cat)
    {
        if (cat.Checked == null || cat.Checked.Count != cat.Apps.Count)
        {
            cat.Checked = new List<bool>();
            foreach (var a in cat.Apps) cat.Checked.Add(false);
        }
    }

    // ---------- 分组操作 ----------

    public static bool AddCategory(Config c, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (c.Categories.Any(x => x.Name == name)) return false;
        c.Categories.Add(new Category { Name = name });
        return true;
    }

    public static void RenameCategory(Config c, int idx, string name)
    {
        if (idx < 0 || idx >= c.Categories.Count || string.IsNullOrWhiteSpace(name)) return;
        if (c.Categories.Any(x => x.Name == name)) return;
        c.Categories[idx].Name = name;
    }

    public static bool DeleteCategory(Config c, int idx)
    {
        if (idx < 0 || idx >= c.Categories.Count) return false;
        if (c.Categories.Count <= 1) return false; // 至少保留一个
        c.Categories.RemoveAt(idx);
        return true;
    }

    // ---------- 应用操作 ----------

    public static bool AddAppPath(Category cat, string path, out string err)
    {
        err = "";
        if (!File.Exists(path))
        {
            err = "跳过（文件不存在）：" + path;
            return false;
        }
        if (cat.Apps.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            err = "已在列表中：" + path;
            return false;
        }
        cat.Apps.Add(new AppItem
        {
            Path = path,
            Name = Path.GetFileNameWithoutExtension(path),
            Args = "",
            Admin = false,
        });
        cat.Checked.Add(true); // 手动/扫描添加默认勾选
        return true;
    }

    public static void DeleteApp(Category cat, int realIndex)
    {
        if (realIndex < 0 || realIndex >= cat.Apps.Count) return;
        cat.Apps.RemoveAt(realIndex);
        cat.Checked.RemoveAt(realIndex);
    }

    public static void SetAllChecked(Category cat, bool val)
    {
        for (int i = 0; i < cat.Checked.Count; i++) cat.Checked[i] = val;
    }

    // ---------- 扫描 ----------

    public static List<ScanHit> ScanKnownLocations()
    {
        LastScanSkipped = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ScanHit>();
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "桌面", true, list, seen);
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "公共桌面", true, list, seen);
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "开始菜单", true, list, seen);
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "公共开始菜单", true, list, seen);
        SortHits(list);
        return list;
    }

    public static List<ScanHit> ScanFolder(string dir, string source)
    {
        var list = new List<ScanHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddDir(dir, source, true, list, seen);
        SortHits(list);
        return list;
    }

    public static int LastScanSkipped { get; private set; }
    public static List<ScanHit> ScanFolderWithStatus(string dir, string source, out int skipped)
    {
        var list = new List<ScanHit>();
        int failures = 0;
        ScanDirectory(dir, source, true, list, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ref failures);
        skipped = failures;
        SortHits(list);
        return list;
    }
    private static void AddDir(string dir, string source, bool recursive, List<ScanHit> list, HashSet<string> seen)
    {
        int skipped = 0;
        ScanDirectory(dir, source, recursive, list, seen, ref skipped);
        LastScanSkipped += skipped;
    }
    private static void ScanDirectory(string dir, string source, bool recursive, List<ScanHit> list, HashSet<string> seen, ref int skipped)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        var pending = new Stack<string>();
        pending.Push(dir);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            try
            {
                foreach (string file in Directory.GetFiles(current))
                {
                    string ext = "*" + Path.GetExtension(file);
                    if (!ScanExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase) || name.StartsWith("卸载")) continue;
                    if (seen.Add(Path.GetFullPath(file))) list.Add(new ScanHit { Name = name, Path = file, Source = source });
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { skipped++; AppLog.Write("Could not scan directory files: " + current, ex); }
            if (!recursive) continue;
            try
            {
                foreach (string child in Directory.GetDirectories(current))
                {
                    try { if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    { skipped++; AppLog.Write("Could not inspect directory: " + child, ex); }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { skipped++; AppLog.Write("Could not enumerate subdirectories: " + current, ex); }
        }
    }

    private static void SortHits(List<ScanHit> list)
    {
        list.Sort(delegate (ScanHit a, ScanHit b)
        {
            int c = string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
            if (c != 0) return c;
            return string.Compare(a.Source, b.Source, StringComparison.Ordinal);
        });
    }

    // ---------- 从 AppGroup 迁移 ----------

    public static int ImportFromAppGroup(Config config, bool interactive)
    {
        try
        {
            if (!File.Exists(AppGroupConfigPath)) return 0;
            using var doc = JsonDocument.Parse(File.ReadAllText(AppGroupConfigPath, Encoding.UTF8));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return 0;

            int imported = 0;
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                if (!prop.Value.TryGetProperty("groupName", out var gnameEl)) continue;
                string gname = gnameEl.GetString() ?? "";
                if (gname.Length == 0) continue;
                if (config.Categories.Any(x => x.Name == gname)) continue; // 同名分组跳过

                var cat = new Category { Name = gname };
                if (prop.Value.TryGetProperty("path", out var pathsEl) &&
                    pathsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var pk in pathsEl.EnumerateObject())
                    {
                        string p = pk.Name;
                        if (!File.Exists(p)) continue;
                        string tooltip = null, args = "";
                        if (pk.Value.ValueKind == JsonValueKind.Object)
                        {
                            if (pk.Value.TryGetProperty("tooltip", out var t)) tooltip = t.GetString();
                            if (pk.Value.TryGetProperty("args", out var ar)) args = ar.GetString() ?? "";
                        }
                        cat.Apps.Add(new AppItem
                        {
                            Path = p,
                            Name = string.IsNullOrEmpty(tooltip)
                                ? Path.GetFileNameWithoutExtension(p) : tooltip,
                            Args = args,
                            Admin = false,
                        });
                        cat.Checked.Add(false); // 导入默认不勾选
                    }
                }
                config.Categories.Add(cat);
                imported++;
            }
            return imported;
        }
        catch
        {
            return 0;
        }
    }

    // ---------- 启动 ----------

    public static string? TryStart(AppItem a, bool admin)
    {
        try
        {
            if (InstalledApplications.IsEntry(a.Path))
            {
                if (admin) return "已安装应用通过 Windows 应用入口启动，不支持强制管理员模式。请关闭管理员开关后重试。";
                if (!string.IsNullOrEmpty(a.Args)) return "Windows 应用入口暂不支持自定义启动参数。";
                Process.Start(InstalledApplications.StartInfo(a.Path));
                return null;
            }
            if (!File.Exists(a.Path) && !a.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return "文件不存在：" + a.Path + "\n（可能已被移动或卸载，右键删除后重新添加）";
            }
            var psi = new ProcessStartInfo();
            psi.UseShellExecute = true;
            psi.FileName = a.Path;
            if (!string.IsNullOrEmpty(a.Args)) psi.Arguments = a.Args;
            if (admin) psi.Verb = "runas";
            try
            {
                string wd = Path.GetDirectoryName(a.Path);
                if (!string.IsNullOrEmpty(wd)) psi.WorkingDirectory = wd;
            }
            catch { }
            Process.Start(psi);
            return null;
        }
        catch (Exception ex)
        {
            string n = string.IsNullOrEmpty(a.Name) ? a.Path : a.Name;
            return "无法启动：" + n + "\n" + ex.Message
                + (admin ? "\n（若弹出了 UAC 窗口被你取消，也会报此错）" : "");
        }
    }

    public static (int started, List<string> fails) LaunchChecked(Category cat, bool forceAdmin = false)
    {
        int started = 0;
        var fails = new List<string>();
        for (int i = 0; i < cat.Apps.Count; i++)
        {
            if (!cat.Checked[i]) continue;
            string? err = TryStart(cat.Apps[i], forceAdmin || cat.Apps[i].Admin);
            if (err == null) started++;
            else fails.Add(err);
            Task.Delay(150).Wait(); // 稍作间隔，避免同时抢启动
        }
        return (started, fails);
    }
}
