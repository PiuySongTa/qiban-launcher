using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace QuickLauncherWinUI3;

public sealed partial class ScanDialog : ContentDialog
{
    public List<AppItem> Result { get; } = new();
    private readonly List<ScanHit> _all;
    private readonly HashSet<string> _existing;
    private readonly IntPtr _hwnd;
    private readonly List<(string label, string path)> _roots = new();
    private readonly List<ScanFolderFilter> _filters = new();
    private readonly List<(CheckBox check, string path)> _folderChecks = new();


    public ScanDialog(XamlRoot xr, IntPtr hwnd, List<ScanHit> hits, HashSet<string> existing, ElementTheme theme)
    {
        InitializeComponent();
        XamlRoot = xr;
        RequestedTheme = theme;
        _hwnd = hwnd;
        _all = hits;
        _existing = existing;
        _ = new ListCheckSelection<ScanVM>(ScanList, ScanSelectionSurface, ScanSelectionOverlay,
            vm => vm.IsSelected, (vm, value) => vm.IsSelected = value);
        PrimaryButtonClick += ScanDialog_PrimaryButtonClick;
        Closed += (s, e) => _closed = true;

        ToolTipService.SetToolTip(AddFolderBtn,
            "扫描 4 个默认位置以外的文件夹（比如 D 盘的绿色软件目录、Program Files），\n会递归查找里面的 .exe / 快捷方式");

        AddRoot("已安装应用", InstalledApplications.SourceRoot);
        AddRoot("桌面", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        AddRoot("公共桌面", Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
        AddRoot("开始菜单", Environment.GetFolderPath(Environment.SpecialFolder.StartMenu));
        AddRoot("公共开始菜单", Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu));
        Refresh();
    }

    private void AddRoot(string label, string path)
    {
        if (!string.IsNullOrEmpty(path) && !_roots.Any(r => string.Equals(r.path, path, StringComparison.OrdinalIgnoreCase)))
            _roots.Add((label, path));
    }

    private void AddFilter_Click(object sender, RoutedEventArgs e)
    {
        _folderChecks.Clear();
        var tree = new TreeView { SelectionMode = TreeViewSelectionMode.None, Height = 310, Width = 540 };
        // RootNodes supplies TreeViewNode objects; explicitly present each node's Content.
        tree.ItemTemplate = (DataTemplate)Resources["FolderNodeTemplate"];
        foreach (var root in _roots)
            tree.RootNodes.Add(CreateFolderNode(root.label, root.path, root.path, true));
        var panel = new StackPanel { Spacing = 8, RequestedTheme = RequestedTheme };
        panel.Children.Add(new TextBlock { Text = "勾选父目录全选下级；取消子目录可排除，父目录显示半选",
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Resources["FilterTextBrush"] });
        panel.Children.Add(tree);
        var done = new Button { Content = "完成", HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(done);
        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, RequestedTheme));
        var flyout = new Flyout { Content = panel, FlyoutPresenterStyle = presenterStyle };
        done.Click += (s, a) => flyout.Hide();
        flyout.ShowAt((FrameworkElement)sender);
    }

    private TreeViewNode CreateFolderNode(string label, string path, string root, bool expanded)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = "\uE8B7", FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        var check = new CheckBox { Content = label, IsChecked = FolderState(path) };
        _folderChecks.Add((check, path));
        ToolTipService.SetToolTip(check, path);
        content.Children.Add(check);
        check.Click += (s, e) =>
        {
            bool include = check.IsChecked == true;
            string prefix = ScanFolderFilter.Normalize(path);
            _filters.RemoveAll(f => ScanFolderFilter.Normalize(f.Directory).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            _filters.Add(new ScanFolderFilter(label, root) { Directory = path, Include = include });
            foreach (var item in _folderChecks) item.check.IsChecked = FolderState(item.path);
            RenderFilters(); Refresh();
        };        var node = new TreeViewNode { Content = content, IsExpanded = expanded };
        var filter = new ScanFolderFilter(label, root);
        foreach (string child in filter.Children(_all, path))
            node.Children.Add(CreateFolderNode(child, Path.Combine(path, child), root, false));
        return node;
    }
    private bool? FolderState(string path)
    {
        var scope = new ScanFolderFilter("", path);
        var hits = _all.Where(scope.Matches).ToList();
        int included = hits.Count(h => ScanFolderFilter.IsIncluded(_filters, h));
        if (included == 0) return false;
        return included == hits.Count ? true : null;
    }
    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _filters.Clear();
        RenderFilters();
        Refresh();
    }

    private void RenderFilters()
    {
        FilterRows.Children.Clear();
        foreach (var filter in _filters)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            string rootLabel = _roots.FirstOrDefault(r => string.Equals(ScanFolderFilter.Normalize(r.path), filter.Root, StringComparison.OrdinalIgnoreCase)).label ?? filter.Label;
            string relative = filter.Root == InstalledApplications.SourceRoot ? "." : Path.GetRelativePath(filter.Root, filter.Directory);
            var text = new TextBlock { Text = (filter.Include ? "包含：" : "排除：") + rootLabel + (relative == "." ? "" : " › " + relative.Replace("\\", " › ")),
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            text.Foreground = (Microsoft.UI.Xaml.Media.Brush)Resources["FilterTextBrush"];
            ToolTipService.SetToolTip(text, filter.Directory);
            row.Children.Add(text);
            var remove = new Button { Content = "移除" };
            Grid.SetColumn(remove, 1);
            remove.Click += (s, e) => { _filters.Remove(filter); RenderFilters(); Refresh(); };
            row.Children.Add(remove);
            FilterRows.Children.Add(row);
        }
    }    private void ScanSort_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_all != null && ScanList != null) Refresh();
    }

    private void Refresh()
    {
        string q = FilterBox.Text.Trim();



        var list = new List<ScanVM>();
        foreach (var h in _all)
        {
            if (_filters.Count > 0 && !ScanFolderFilter.IsIncluded(_filters, h)) continue;
            if (q.Length > 0 && LauncherCore.DisplayName(h.Name, h.Path).IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && h.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var vm = new ScanVM(h, _existing.Contains(h.Path));
            vm.PropertyChanged += Vm_PropertyChanged;
            list.Add(vm);
        }
        list = ScanSort.SelectedIndex switch
        {
            1 => list.OrderBy(v => Path.GetExtension(v.Hit.Path), StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            2 => list.OrderBy(v => v.Hit.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => list.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(v => v.Hit.Path, StringComparer.OrdinalIgnoreCase).ToList()
        };
        ScanList.ItemsSource = list;
        UpdateCount();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScanVM.IsSelected)) UpdateCount();
    }

    private void UpdateCount()
    {
        if (ScanList.ItemsSource is IEnumerable<ScanVM> list)
        {
            int total = 0, sel = 0;
            foreach (var vm in list) { total++; if (vm.IsSelected) sel++; }
            CountText.Text = "共 " + total + " 项，已勾选 " + sel + " 项";
        }
        else
        {
            CountText.Text = "";
        }
    }

    public void SetScanWarning(string warning) { ScanWarning.Text = warning; ScanWarning.Visibility = Visibility.Visible; }

    private bool _folderOpening, _closed;
    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_folderOpening) return;
        _folderOpening = true;
        AddFolderBtn.IsEnabled = false;
        try
        {
            var fp = new FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(fp, _hwnd);
            fp.FileTypeFilter.Add("*");
            var folder = await fp.PickSingleFolderAsync();
            if (folder == null || _closed) return;
            SetScanWarning("正在扫描文件夹…");
            int skipped = 0;
            var hits = await System.Threading.Tasks.Task.Run(() => LauncherCore.ScanFolderWithStatus(folder.Path, folder.Name, out skipped));
            if (_closed) return;
            var seen = new HashSet<string>(_all.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var h in hits) if (seen.Add(h.Path)) _all.Add(h);
            AddRoot(folder.Name, folder.Path);
            if (hits.Count > 0)
            {
                _filters.Add(new ScanFolderFilter(folder.Name, folder.Path));
                RenderFilters(); Refresh();
            }
            SetScanWarning(skipped > 0 ? "有 " + skipped + " 处目录读取受限，已跳过；可用结果保留。商店应用请选择“已安装应用”来源。" :
                hits.Count == 0 ? "没有找到支持的软件文件。商店应用可从目录树的“已安装应用”选择。" : "文件夹扫描完成。");
        }
        catch (Exception error) { AppLog.Write("Folder scan failed", error); if (!_closed) SetScanWarning("扫描未完成，详情已记入日志，可重试。"); }
        finally { _folderOpening = false; if (!_closed) AddFolderBtn.IsEnabled = true; }
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();
    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAll(true);
    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAll(false);

    // 只作用于当前筛选后可见的项
    private void SetAll(bool v)
    {
        if (ScanList.ItemsSource is IEnumerable<ScanVM> list)
            foreach (var vm in list) vm.IsSelected = v;
    }

    private void ScanDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Result.Clear();
        foreach (var h in _all)
        {
            if (!h.IsSelected) continue;
            if (_existing.Contains(h.Path)) continue;
            Result.Add(new AppItem { Name = h.Name, Path = h.Path, Args = "", Admin = false });
        }
    }
}
