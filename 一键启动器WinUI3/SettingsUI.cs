using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace QuickLauncherWinUI3;

public sealed partial class MainWindow
{
    private bool _settingsOpening, _settingsActionRunning;

    private bool RememberScanFolder(string path)
    {
        try
        {
            var draft = ConfigDraft();
            draft.ScanDirectories = ScanPolicy.Lines(string.Join("\n", draft.ScanDirectories ?? new()) + "\n" + path);
            LauncherCore.SaveConfigChecked(draft);
            _config.ScanDirectories = draft.ScanDirectories;
            _scanOptionsRevision++;
            return true;
        }
        catch (Exception ex) { AppLog.Write("Unable to save scan folder", ex); return false; }
    }

    private void ApplyPreferences(Config preferences)
    {
        _config.Theme = preferences.Theme;
        _config.ScanDirectories = preferences.ScanDirectories;
        _config.ScanExclusions = preferences.ScanExclusions;
        _config.ScanDefaultLocations = preferences.ScanDefaultLocations;
        _config.ScanInstalledApps = preferences.ScanInstalledApps;
        _config.AutoScanEnabled = preferences.AutoScanEnabled;
        _config.TransparentPanels = preferences.TransparentPanels;
        _scanOptionsRevision++;
        _theme = _config.Theme ?? "System";
        RootGrid.RequestedTheme = ToElementTheme(_theme);
        DialogService.TransparentPanels = _config.TransparentPanels;
        ThemeHelper.ApplyTitleBarColors(this);
        ApplyBackground();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsOpening || _settingsActionRunning) return;
        if (_scanOpening) { StatusText.Text = "扫描或添加窗口正在使用中，请关闭后再打开设置。"; return; }
        _settingsOpening = true;
        string next = "";
        bool changed = false;
        try
        {
            var panel = new StackPanel { Spacing = 14 };
            var theme = new ComboBox { Header = "主题", HorizontalAlignment = HorizontalAlignment.Stretch };
            string[] modes = { "System", "Light", "Dark" };
            foreach (string label in new[] { "跟随系统", "浅色", "深色" }) theme.Items.Add(label);
            theme.SelectedIndex = Math.Max(0, Array.IndexOf(modes, _theme));
            var transparent = new ToggleSwitch { Header = "面板与弹窗半透明", IsOn = _config.TransparentPanels };
            panel.Children.Add(theme); panel.Children.Add(transparent);
            panel.Children.Add(new TextBlock { Text = "主目录与扫描范围", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 17 });
            var auto = new ToggleSwitch { Header = "自动刷新主目录：启动时及每 5 分钟刷新", IsOn = _config.AutoScanEnabled };
            var defaults = new CheckBox { Content = "扫描桌面、公共桌面和开始菜单（含下级）", IsChecked = _config.ScanDefaultLocations };
            var installed = new CheckBox { Content = "读取已安装应用（包括 ChatGPT 等商店应用）", IsChecked = _config.ScanInstalledApps };
            panel.Children.Add(auto); panel.Children.Add(defaults); panel.Children.Add(installed);
            var folders = new TextBox { Header = "额外扫描目录（每行一个完整路径，包含全部下级）", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                MinHeight = 85, MaxHeight = 150, Text = string.Join(Environment.NewLine, _config.ScanDirectories ?? new()) };
            panel.Children.Add(folders);
            var addFolder = new Button { Content = "选择并添加扫描目录…" };
            panel.Children.Add(addFolder);
            panel.Children.Add(new TextBlock { Text = "自动收录只补充新记录，不删除已有记录；新记录默认不勾选。关闭自动刷新会保留主目录，仍可手动刷新。扫描窗口也使用这里保存的范围和排除规则。", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            var exclusions = new TextBox { Header = "非应用程序白名单（扫描排除名单）", PlaceholderText = "每行填一个文件名、完整文件/文件夹路径或已安装应用名称", AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, MinHeight = 85, MaxHeight = 150, Text = string.Join(Environment.NewLine, _config.ScanExclusions ?? new()) };
            panel.Children.Add(exclusions);
            panel.Children.Add(new TextBlock { Text = "精确匹配名称或文件名；完整目录路径排除整个下级。不会删除已经添加的记录，也不限制手动添加。不支持 * 通配符。", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            var excludeFile = new Button { Content = "选择要排除的文件…" };
            var excludeFolder = new Button { Content = "选择要排除的文件夹…" };
            var excludeButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            excludeButtons.Children.Add(excludeFile); excludeButtons.Children.Add(excludeFolder); panel.Children.Add(excludeButtons);
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(error);
            var dialog = new ContentDialog { Title = "设置", XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme,
                Content = new ScrollViewer { Content = panel, MaxHeight = 480, Width = 540, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            dialog.Resources["ContentDialogMaxWidth"] = 660d;
            bool picking = false, closed = false;
            dialog.Closed += (_, _) => closed = true;
            async Task PickAsync(TextBox destination, bool file)
            {
                if (picking || closed || _windowClosed) return;
                picking = true;
                dialog.IsPrimaryButtonEnabled = false;
                addFolder.IsEnabled = excludeFile.IsEnabled = excludeFolder.IsEnabled = false;
                try
                {
                    string? path;
                    if (file)
                    {
                        var picker = new FileOpenPicker();
                        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                        picker.FileTypeFilter.Add("*");
                        path = (await picker.PickSingleFileAsync())?.Path;
                    }
                    else
                    {
                        var picker = new FolderPicker();
                        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                        picker.FileTypeFilter.Add("*");
                        path = (await picker.PickSingleFolderAsync())?.Path;
                    }
                    if (closed || _windowClosed || path == null) return;
                    destination.Text = string.Join(Environment.NewLine, ScanPolicy.Lines(destination.Text + "\n" + path));
                    error.Text = "";
                }
                catch (Exception ex) { AppLog.Write("Settings picker failed", ex); if (!closed && !_windowClosed) error.Text = "未能选择路径，可重试或手动填写。"; }
                finally
                {
                    picking = false;
                    if (!closed && !_windowClosed) { dialog.IsPrimaryButtonEnabled = true; addFolder.IsEnabled = excludeFile.IsEnabled = excludeFolder.IsEnabled = true; }
                }
            }
            addFolder.Click += async (_, _) => await PickAsync(folders, false);
            excludeFile.Click += async (_, _) => await PickAsync(exclusions, true);
            excludeFolder.Click += async (_, _) => await PickAsync(exclusions, false);
            bool Save()
            {
                bool committed = false;
                try
                {
                    var paths = ScanPolicy.Lines(folders.Text);
                    if (paths.Any(p => !Path.IsPathFullyQualified(p))) throw new ArgumentException("扫描目录需要完整路径，例如 D:\\软件。");
                    var rules = ScanPolicy.Lines(exclusions.Text);
                    if (rules.Any(p => p.Contains('*') || p.Contains('?'))) throw new ArgumentException("排除规则暂不支持通配符，请填写准确名称或完整路径。");
                    var draft = ConfigDraft();
                    draft.Theme = modes[theme.SelectedIndex]; draft.TransparentPanels = transparent.IsOn;
                    draft.ScanDefaultLocations = defaults.IsChecked == true; draft.ScanInstalledApps = installed.IsChecked == true;
                    draft.AutoScanEnabled = auto.IsOn; draft.ScanDirectories = paths; draft.ScanExclusions = rules;
                    LauncherCore.SaveConfigChecked(draft);
                    committed = true;
                    ApplyPreferences(draft);
                    changed = true;
                    StatusText.Text = "设置已保存。";
                    return true;
                }
                catch (Exception ex)
                {
                    AppLog.Write("Settings save failed", ex);
                    error.Text = committed ? "设置已保存，但外观更新失败，请重新打开启伴。" : "未保存：" + ex.Message;
                    return false;
                }
            }
            dialog.PrimaryButtonClick += (_, args) => { if (picking || !Save()) args.Cancel = true; };
            panel.Children.Add(new TextBlock { Text = "相关功能（先保存当前设置，再打开）", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            foreach (var entry in new[] { ("background", "背景图片与磨砂设置…"), ("refresh", "立即刷新主目录"),
                ("export", "导出配置备份…"), ("import", "从配置备份恢复…"), ("appgroup", "从 AppGroup 导入分组"), ("update", "检查版本更新 / 下载安装包…"), ("version", "版本 v" + VersionInfo.Current + " · 更新记录") })
            {
                var button = new Button { Content = entry.Item2, HorizontalAlignment = HorizontalAlignment.Stretch };
                button.Click += (_, _) => { if (picking || !Save()) return; next = entry.Item1; dialog.Hide(); };
                panel.Children.Add(button);
            }
            await DialogService.ShowAsync(dialog);
        }
        catch (Exception ex) { AppLog.Write("Settings dialog failed", ex); if (!_windowClosed) StatusText.Text = "设置未能完成，可重新打开；详情见日志。"; }
        finally { _settingsOpening = false; }
        if (_windowClosed) return;
        _settingsActionRunning = true;
        try
        {
            switch (next)
            {
                case "background": BackgroundSettings_Click(sender, e); break;
                case "refresh": await RefreshAutoGroupAsync(true); break;
                case "export": await ExportConfigAsync(); break;
                case "import": await ImportConfigAsync(); break;
                case "appgroup": ImportFromAppGroup(); break;
                case "version": Version_Click(sender, e); break;
                case "update": await ShowUpdatesAsync(); break;
            }
        }
        catch (Exception ex) { AppLog.Write("Settings action failed", ex); if (!_windowClosed) StatusText.Text = "操作未完成，可重试；详情见日志。"; }
        finally { _settingsActionRunning = false; }
        if (changed && next != "refresh") await RefreshAutoGroupAsync(false);
    }
}
