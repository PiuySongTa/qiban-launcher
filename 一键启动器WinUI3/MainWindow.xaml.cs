using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace QuickLauncherWinUI3;

// Win32 窗口尺寸设置（按物理像素，绕过 AppWindow.Resize 的 DPI 换算）
internal static class NativeWin
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr icon);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    internal static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);
    internal struct RECT { public int Left, Top, Right, Bottom; }
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
}

// ---------- 分组视图模型 ----------
public class CategoryVM
{
    public Category Cat { get; }
    public CategoryVM(Category c) { Cat = c; }
    public string Name => Cat.Name;
    public int Count => Cat.Apps.Count;
}

// ---------- 软件行视图模型 ----------
public class AppItemVM : INotifyPropertyChanged
{
    public AppItem Item { get; }
    public int Index { get; }
    private readonly Category _cat;
    private bool _isChecked;
    private ImageSource? _icon;
    private bool _iconLoading;

    public AppItemVM(AppItem item, Category cat, int index)
    {
        Item = item;
        _cat = cat;
        Index = index;
        _isChecked = cat.Checked[index];
    }

    public string Name => LauncherCore.DisplayName(Item.Name, Item.Path);
    public string FullPath => Item.Path;
    public string Membership { get; set; } = "";
    public Visibility MembershipVisibility => Membership.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            _cat.Checked[Index] = value;
            OnPropertyChanged();
        }
    }

    public ImageSource? Icon
    {
        get => _icon;
        private set { _icon = value; OnPropertyChanged(); }
    }

    public Visibility AdminBadge => Item.Admin ? Visibility.Visible : Visibility.Collapsed;

    public async void LoadIconAsync()
    {
        if (_iconLoading || _icon != null) return;
        _iconLoading = true;
        try
        {
            var dq = DispatcherQueue.GetForCurrentThread();
            string? png = InstalledApplications.IsEntry(Item.Path)
                ? await InstalledApplications.GetIconAsync(Item.Path) : await IconCache.GetIconPathAsync(Item.Path);
            if (string.IsNullOrEmpty(png) || !File.Exists(png)) return;
            if (dq.HasThreadAccess) Icon = new BitmapImage(new Uri(png));
            else dq.TryEnqueue(() => Icon = new BitmapImage(new Uri(png)));
        }
        catch { }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ---------- 扫描结果视图模型 ----------
public class ScanVM : INotifyPropertyChanged
{
    public ScanHit Hit { get; }
    public ScanVM(ScanHit h, bool existing)
    {
        Hit = h;
        SourceLine = h.Source + (existing ? "（已添加）" : "");
    }
    public string Name => LauncherCore.DisplayName(Hit.Name, Hit.Path);
    public string SourceLine { get; }

    // 勾选状态代理到 ScanHit（数据层），切换筛选/搜索后不丢失
    public bool IsSelected
    {
        get => Hit.IsSelected;
        set
        {
            if (Hit.IsSelected == value) return;
            Hit.IsSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

// ---------- 主窗口 ----------
public sealed partial class MainWindow : Window
{
    private IntPtr _smallIcon, _largeIcon;
    private bool _scanOpening, _windowClosed, _bulkChecking;
    private readonly Config _config;
    private string _theme;
    private int _currentCat = -1;
    private MenuFlyout _appMenu = new();
    private ToggleMenuFlyoutItem _menuAlwaysAdmin;

    public MainWindow(Config config)
    {
        InitializeComponent();
        _config = config;
        SettingsButton.Content = "设置 · v" + VersionInfo.Current;
        Title = "启伴·一键启动器 · v" + VersionInfo.Current;
        _theme = _config.Theme ?? "System";

        // 初始窗口尺寸/居中：AppWindow.Resize 用物理像素（DPI-aware），在 Activated 后生效
        bool sized = false;
        Activated += (s, e) =>
        {
            if (sized) return;
            sized = true;
            // 设置标题栏/任务栏图标（可爱的笑脸 logo）
            try
            {
                string ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
                                if (File.Exists(ico))
                {
                    AppWindow.SetIcon(ico);
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    _smallIcon = NativeWin.LoadImage(IntPtr.Zero, ico, 1, 32, 32, 0x10);
                    _largeIcon = NativeWin.LoadImage(IntPtr.Zero, ico, 1, 256, 256, 0x10);
                    if (_smallIcon != IntPtr.Zero) NativeWin.SendMessage(hwnd, 0x80, IntPtr.Zero, _smallIcon);
                    if (_largeIcon != IntPtr.Zero) NativeWin.SendMessage(hwnd, 0x80, new IntPtr(1), _largeIcon);
                }
            }
            catch { }
            var wa = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
            int w = Math.Min(1800, wa.Width);
            int h = Math.Min(1280, wa.Height);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
            AppWindow.Move(new Windows.Graphics.PointInt32(
                wa.X + (wa.Width - w) / 2,
                wa.Y + (wa.Height - h) / 2));
        };

        if (Content is FrameworkElement root) root.RequestedTheme = ToElementTheme(_theme);
        DialogService.TransparentPanels = _config.TransparentPanels;
        ApplyBackground();

        Closed += (s, e) =>
        {
            _windowClosed = true;
            if (_smallIcon != IntPtr.Zero) NativeWin.DestroyIcon(_smallIcon);
            if (_largeIcon != IntPtr.Zero) NativeWin.DestroyIcon(_largeIcon);
        };
        _ = new ListCheckSelection<AppItemVM>(AppList, AppSelectionSurface, AppSelectionOverlay,
            vm => vm.IsChecked, (vm, value) => vm.IsChecked = value, vm => LaunchOne(vm, false));
        BuildAppMenu();
        RootGrid.ActualThemeChanged += (_, _) => ApplyBackground();
        RefreshCategories(MainCatalogModel.Index);
        InitializeAutoScan();
    }

    // ---------- 主题 ----------

    private static ElementTheme ToElementTheme(string t) => t switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private static string ThemeLabel(string t) => t switch
    {
        "Light" => "浅色",
        "Dark" => "深色",
        _ => "跟随系统",
    };

    // ---------- 列表刷新 ----------

    private Category? CurrentCat() => MainCatalogModel.At(_config, _currentCat);

    private void RefreshCategories(int select) => RefreshCatalogNavigation(select);

    private void CatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navigationSync || CatList.SelectedIndex < 0) return;
        _currentCat = CatList.SelectedIndex;
        MainCatalogButton.IsChecked = false;
        CatalogFilter.Visibility = Visibility.Collapsed;
        RefreshApps(SearchBox.Text);
    }

    private void RefreshApps(string filter)
    {
        MainCatalogModel.IncludeGroups(_config);
        var cat = CurrentCat();
        if (cat == null)
        {
            AppList.ItemsSource = null;
            AppCountText.Text = "软件 (0)";
            UpdateLaunchButton();
            return;
        }
        LauncherCore.EnsureCheckedSync(cat);
        var groupedPaths = MainCatalogModel.GroupedPaths(_config);
        var vms = new List<AppItemVM>();
        for (int i = 0; i < cat.Apps.Count; i++)
        {
            if (_currentCat == MainCatalogModel.Index && !MainCatalogModel.Matches(cat.Apps[i], groupedPaths, CatalogFilter.SelectedIndex)) continue;
            if (!string.IsNullOrEmpty(filter) &&
                !(cat.Apps[i].Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) &&
                !(cat.Apps[i].Path?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                continue;
            var vm = new AppItemVM(cat.Apps[i], cat, i);
            if (_currentCat == MainCatalogModel.Index)
            {
                var groups = _config.Categories.Where(c => c.Apps.Any(a => string.Equals(a.Path, cat.Apps[i].Path, StringComparison.OrdinalIgnoreCase))).Select(c => c.Name).ToArray();
                vm.Membership = groups.Length == 0 ? "未分组" : "分组：" + string.Join("、", groups);
            }
            vm.PropertyChanged += (sender, args) =>
            {
                if (args.PropertyName != nameof(AppItemVM.IsChecked)) return;
                if (_bulkChecking) return;
                UpdateLaunchButton();
                LauncherCore.SaveConfig(_config);
            };
            vms.Add(vm);
        }
        vms = AppSort.SelectedIndex switch
        {
            1 => vms.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(v => v.FullPath, StringComparer.OrdinalIgnoreCase).ToList(),
            2 => vms.OrderBy(v => Path.GetExtension(v.FullPath), StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            3 => vms.OrderBy(v => v.FullPath, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => vms
        };
        AppList.ItemsSource = vms;
        AppCountText.Text = "显示 " + vms.Count + " / " + cat.Apps.Count;
        MainCatalogButton.Content = "主目录 · " + _config.MainCatalog.Apps.Count;
        foreach (var vm in vms) vm.LoadIconAsync();
        UpdateLaunchButton();
    }

    private void AppSort_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_config != null && AppList != null) RefreshApps(SearchBox.Text);
    }

    private void UpdateLaunchButton()
    {
        var cat = CurrentCat();
        int n = cat == null ? 0 : cat.Checked.Count(x => x);
        LaunchBtn.Content = "启动勾选 (" + n + ")";
        LaunchBtn.IsEnabled = n > 0;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshApps(SearchBox.Text);
    }

    // ---------- 右键菜单 ----------

    private void BuildAppMenu()
    {
        _appMenu = new MenuFlyout();

        var open = new MenuFlyoutItem { Text = "打开" };
        open.Click += (s, e) => { if (AppList.SelectedItem is AppItemVM vm) LaunchOne(vm, false); };
        var runAs = new MenuFlyoutItem { Text = "以管理员身份运行" };
        runAs.Click += (s, e) => { if (AppList.SelectedItem is AppItemVM vm) LaunchOne(vm, true); };
        _menuAlwaysAdmin = new ToggleMenuFlyoutItem { Text = "每次以管理员运行" };
        _menuAlwaysAdmin.Click += (s, e) =>
        {
            if (AppList.SelectedItem is AppItemVM vm)
            {
                vm.Item.Admin = _menuAlwaysAdmin.IsChecked;
                LauncherCore.SaveConfig(_config);
                RefreshApps(SearchBox.Text); // 刷新管理员徽章
            }
        };
        var loc = new MenuFlyoutItem { Text = "打开文件位置" };
        loc.Click += (s, e) =>
        {
            if (AppList.SelectedItem is AppItemVM vm)
            {
                if (InstalledApplications.IsEntry(vm.Item.Path))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "shell:AppsFolder") { UseShellExecute = true });
                    return;
                }
                string dir = "";
                try { dir = Path.GetDirectoryName(vm.Item.Path) ?? ""; } catch { }
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
        };
        var del = new MenuFlyoutItem { Text = "删除此项" };
        del.Click += (s, e) => DeleteApp();

        _appMenu.Items.Add(open);
        _appMenu.Items.Add(runAs);
        _appMenu.Items.Add(_menuAlwaysAdmin);
        _appMenu.Items.Add(new MenuFlyoutSeparator());
        _appMenu.Items.Add(loc);
        var copyTo = new MenuFlyoutItem { Text = "复制此项到分组…" };
        copyTo.Click += (_, _) => TransferToGroup(false, true);
        var moveTo = new MenuFlyoutItem { Text = "移动此项到分组…" };
        moveTo.Click += (_, _) => TransferToGroup(true, true);
        _appMenu.Items.Add(copyTo);
        _appMenu.Items.Add(moveTo);
        _appMenu.Items.Add(new MenuFlyoutSeparator());
        _appMenu.Items.Add(del);
    }

    private void AppList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var src = e.OriginalSource as FrameworkElement;
        if (src?.DataContext is AppItemVM vm)
        {
            AppList.SelectedItem = vm;
            _menuAlwaysAdmin.IsChecked = vm.Item.Admin;
            _appMenu.ShowAt(AppList, e.GetPosition(AppList));
        }
    }

    // ---------- 启动 ----------

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        var cat = CurrentCat();
        if (cat == null) return;
        var visible = new HashSet<int>((AppList.ItemsSource as IEnumerable<AppItemVM> ?? Enumerable.Empty<AppItemVM>()).Select(v => v.Index));
        var hidden = cat.Apps.Where((app, index) => cat.Checked[index] && !visible.Contains(index)).ToList();
        if (hidden.Count > 0)
        {
            var confirm = new ContentDialog
            {
                Title = "还有被搜索或筛选隐藏的勾选项",
                Content = "本次将启动 " + cat.Checked.Count(x => x) + " 个软件，其中 " + hidden.Count + " 个不在当前显示结果中：\n\n" +
                    string.Join("\n", hidden.Take(8).Select(a => LauncherCore.DisplayName(a.Name, a.Path))) +
                    (hidden.Count > 8 ? "\n……" : "") + "\n\n是否仍要启动全部勾选的软件？",
                PrimaryButtonText = "启动全部勾选", CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot,
                RequestedTheme = ((FrameworkElement)Content).ActualTheme
            };
            if (await DialogService.ShowAsync(confirm) != ContentDialogResult.Primary) return;
        }
        var (started, fails) = LauncherCore.LaunchChecked(cat, AdminToggle.IsOn);
        if (UncheckToggle.IsOn)
        {
            LauncherCore.SetAllChecked(cat, false);
        }
        LauncherCore.SaveConfig(_config);
        RefreshApps(SearchBox.Text);
        StatusText.Text = "已启动 " + started + " 个软件（" + cat.Name + "）";
        if (fails.Count > 0)
            await ShowDialogAsync(string.Join("\n\n", fails), "部分软件启动失败");
    }

    private async void AppList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var src = e.OriginalSource as FrameworkElement;
        if (src?.DataContext is AppItemVM vm) LaunchOne(vm, false);
    }

    private async void LaunchOne(AppItemVM vm, bool forceAdmin)
    {
        string? err = LauncherCore.TryStart(vm.Item, forceAdmin || AdminToggle.IsOn || vm.Item.Admin);
        if (err != null) await ShowDialogAsync(err, "启动失败");
    }

    // ---------- 分组操作 ----------

    private async void AddCat_Click(object sender, RoutedEventArgs e)
    {
        string? name = await InputDialog.ShowAsync(this.Content.XamlRoot, "新建分组", "");
        if (string.IsNullOrEmpty(name)) return;
        if (!LauncherCore.AddCategory(_config, name))
        {
            await ShowDialogAsync("已有同名分组或名称无效。", "新建分组");
            return;
        }
        LauncherCore.SaveConfig(_config);
        RefreshCategories(_config.Categories.Count - 1);
    }

    private async void RenameCat_Click(object sender, RoutedEventArgs e)
    {
        var cat = CurrentCat();
        if (cat == null) return;
        if (_currentCat == MainCatalogModel.Index) { StatusText.Text = "主目录是固定的全量入口，不需要改名。"; return; }
        string? name = await InputDialog.ShowAsync(this.Content.XamlRoot, "重命名分组", cat.Name);
        if (string.IsNullOrEmpty(name) || name == cat.Name) return;
        LauncherCore.RenameCategory(_config, _currentCat, name);
        LauncherCore.SaveConfig(_config);
        RefreshCategories(_currentCat);
    }

    private async void DeleteCat_Click(object sender, RoutedEventArgs e)
    {
        var cat = CurrentCat();
        if (cat == null) return;
        if (_currentCat == MainCatalogModel.Index) { StatusText.Text = "主目录是固定的全量入口，不能删除。"; return; }
        if (_config.Categories.Count <= 1)
        {
            await ShowDialogAsync("至少保留一个分组。", "删除分组");
            return;
        }
        var dlg = new ContentDialog
        {
            Title = "删除分组",
            Content = "确定删除分组「" + cat.Name + "」及其全部 " + cat.Apps.Count + " 个软件？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot,
        };
        if (await DialogService.ShowAsync(dlg) == ContentDialogResult.Primary)
        {
            int idx = _currentCat;
            LauncherCore.DeleteCategory(_config, idx);
            LauncherCore.SaveConfig(_config);
            RefreshCategories(Math.Max(0, idx - 1));
        }
    }

    // ---------- 软件操作 ----------

    private async void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var cat = CurrentCat();
        if (cat == null) return;
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        foreach (var ext in new[] { ".exe", ".lnk", ".bat", ".cmd", ".url" }) picker.FileTypeFilter.Add(ext);
        var files = await picker.PickMultipleFilesAsync();
        if (files == null) return;
        bool any = false;
        foreach (var f in files)
            if (LauncherCore.AddAppPath(cat, f.Path, out _)) any = true;
        if (any) { LauncherCore.SaveConfig(_config); RefreshApps(SearchBox.Text); }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanOpening || _settingsActionRunning) return;
        _scanOpening = true;
        var button = sender as Button;
        if (button != null) button.IsEnabled = false;
        try
        {
        var cat = CurrentCat();
        if (cat == null) return;
        StatusText.Text = "正在读取软件和 Windows 应用列表…";
        var options = ConfigDraft();
        var catalog = await ScanCatalog.ReadAsync(options);
        var hits = catalog.Hits;
        string? scanWarning = catalog.Skipped > 0 || catalog.InstalledFailed
            ? "部分位置读取失败，已跳过；其他结果保留，详情见日志。" : null;

        if (_windowClosed) return;
        StatusText.Text = "软件列表已读取，可搜索名称或选择来源。";
        var existing = new HashSet<string>(cat.Apps.Select(a => a.Path), StringComparer.OrdinalIgnoreCase);
        var dlg = new ScanDialog(this.Content.XamlRoot, WinRT.Interop.WindowNative.GetWindowHandle(this), hits, existing, ((FrameworkElement)Content).ActualTheme, options);
        dlg.FolderScanned = RememberScanFolder;
        if (scanWarning != null) dlg.SetScanWarning(scanWarning);
        if (await DialogService.ShowAsync(dlg) != ContentDialogResult.Primary) return;
        if (_windowClosed) return;
        int catIndex = ReferenceEquals(cat, _config.MainCatalog) ? MainCatalogModel.Index : _config.Categories.IndexOf(cat);
        if (catIndex == -1) { StatusText.Text = "目标分组已变化，请重新扫描。"; return; }
        var draft = ConfigDraft();
        var target = MainCatalogModel.At(draft, catIndex)!;
        int added = 0;
        foreach (var a in dlg.Result)
        {
            if (target.Apps.Any(x => string.Equals(x.Path, a.Path, StringComparison.OrdinalIgnoreCase))) continue;
            target.Apps.Add(a);
            target.Checked.Add(true);
            added++;
        }
        if (added > 0)
        {
            LauncherCore.SaveConfigChecked(draft);
            cat.Apps = target.Apps;
            cat.Checked = target.Checked;
            RefreshCategories(_currentCat);
            RefreshApps(SearchBox.Text);
            StatusText.Text = "已添加 " + added + " 个软件到「" + cat.Name + "」";
        }
        }
        catch (Exception error)
        {
            AppLog.Write("Scan/add failed", error);
            StatusText.Text = "扫描或添加失败，详情已记录到程序目录的 logs 文件夹。";
        }
        finally
        {
            _scanOpening = false;
            if (button != null) button.IsEnabled = true;
        }
    }

    // ---------- 备份 / 导入 ----------

    private async Task ExportConfigAsync()
    {
        var picker = new FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        picker.SuggestedFileName = "一键启动器配置备份_" + DateTime.Now.ToString("yyyyMMdd");
        picker.FileTypeChoices.Add("JSON 配置", new List<string> { ".json" });
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        try
        {
            LauncherCore.SaveConfigChecked(_config); // 保存失败时不导出陈旧数据
            string text = File.ReadAllText(LauncherCore.ConfigPath, Encoding.UTF8);
            await FileIO.WriteTextAsync(file, text);
            StatusText.Text = "已导出配置备份：" + file.Path;
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("导出失败：" + ex.Message, "备份");
        }
    }

    private async Task ImportConfigAsync()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        string text;
        try { text = await FileIO.ReadTextAsync(file); }
        catch (Exception ex) { await ShowDialogAsync("读取失败：" + ex.Message, "恢复备份"); return; }

        var cfg = LauncherCore.ParseConfig(text);
        if (cfg == null || cfg.Categories == null || cfg.Categories.Count == 0)
        {
            await ShowDialogAsync("这个文件不是有效的配置备份（缺少分组数据）。", "恢复备份");
            return;
        }

        var dlg = new ContentDialog
        {
            Title = "恢复配置",
            Content = "将用备份文件覆盖当前配置（当前 " + _config.Categories.Count +
                      " 个分组 → 备份 " + cfg.Categories.Count + " 个分组）。确定吗？",
            PrimaryButtonText = "覆盖恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot,
        };
        if (await DialogService.ShowAsync(dlg) != ContentDialogResult.Primary) return;

        foreach (var c in cfg.Categories) LauncherCore.EnsureCheckedSync(c);
        try { LauncherCore.SaveConfigChecked(cfg); }
        catch (Exception ex) { AppLog.Write("Restore configuration failed", ex); await ShowDialogAsync("恢复未完成，当前配置保留：" + ex.Message, "恢复备份"); return; }
        _config.Categories = cfg.Categories;
        _config.MainCatalog = cfg.MainCatalog;
        ApplyPreferences(cfg);
        _config.BackgroundMode = cfg.BackgroundMode;
        _config.BackgroundImage = cfg.BackgroundImage;
        _config.BackgroundShade = cfg.BackgroundShade;
        ApplyBackground();
        if (!string.IsNullOrEmpty(cfg.Theme))
        {
            _theme = cfg.Theme;
            if (Content is FrameworkElement root) root.RequestedTheme = ToElementTheme(_theme);
            ThemeHelper.ApplyTitleBarColors(this);
            DialogService.TransparentPanels = _config.TransparentPanels;
        }
        RefreshCategories(0);
        StatusText.Text = "已从备份恢复配置（" + cfg.Categories.Count + " 个分组）";
    }

    private void ImportFromAppGroup()
    {
        int n = LauncherCore.ImportFromAppGroup(_config, false);
        if (n > 0)
        {
            LauncherCore.SaveConfig(_config);
            RefreshCategories(_currentCat);
            StatusText.Text = "已从 AppGroup 导入 " + n + " 个分组";
        }
        else
        {
            StatusText.Text = "没有可导入的新分组（AppGroup 的分组要么已导入过，要么 AppGroup 里没有分组）";
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAllChecked(true);
    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAllChecked(false);

    private void SetAllChecked(bool val)
    {
        var cat = CurrentCat();
        if (cat == null) return;
        _bulkChecking = true;
        try
        {
            if (_currentCat == MainCatalogModel.Index && AppList.ItemsSource is IEnumerable<AppItemVM> visible)
                foreach (var vm in visible) vm.IsChecked = val;
            else LauncherCore.SetAllChecked(cat, val);
        }
        finally { _bulkChecking = false; }
        RefreshApps(SearchBox.Text);
        LauncherCore.SaveConfig(_config);
    }

    private void DeleteApp_Click(object sender, RoutedEventArgs e) => DeleteApp();

    private void DeleteApp()
    {
        var cat = CurrentCat();
        if (cat == null || AppList.SelectedItem is not AppItemVM vm) return;
        LauncherCore.DeleteApp(cat, vm.Index);
        LauncherCore.SaveConfig(_config);
        RefreshApps(SearchBox.Text);
    }

    // ---------- 拖拽添加 ----------

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        var cat = CurrentCat();
        if (cat == null) return;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        bool any = false;
        foreach (var it in items)
        {
            if (it is StorageFile f && LauncherCore.AddAppPath(cat, f.Path, out _)) any = true;
        }
        if (any) { LauncherCore.SaveConfig(_config); RefreshApps(SearchBox.Text); }
    }

    // ---------- 通用弹窗 ----------

    private async Task ShowDialogAsync(string message, string title)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "确定",
            XamlRoot = this.Content.XamlRoot,
        };
        await DialogService.ShowAsync(dlg);
    }
}
