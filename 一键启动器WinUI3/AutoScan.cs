using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace QuickLauncherWinUI3;

internal sealed record CatalogResult(List<ScanHit> Hits, int Skipped, bool InstalledFailed);

internal static class ScanCatalog
{
    public static async Task<CatalogResult> ReadAsync(Config options)
    {
        string[] roots = ScanPolicy.Roots(options).ToArray();
        int skipped = 0;
        var hits = await Task.Run(() =>
        {
            var found = new List<ScanHit>();
            foreach (string root in roots)
            {
                found.AddRange(LauncherCore.ScanFolderWithStatus(root, Label(root), out int count));
                skipped += count;
            }
            return found;
        });
        bool failed = false;
        if (options.ScanInstalledApps)
        {
            try { hits.AddRange(await InstalledApplications.ReadAsync()); }
            catch (Exception ex) { AppLog.Write("Installed apps scan failed", ex); failed = true; }
        }
        return new CatalogResult(ScanPolicy.Filter(hits, options.ScanExclusions ?? new()), skipped, failed);
    }

    internal static string Label(string path)
    {
        foreach (var pair in new[] { (Environment.SpecialFolder.Desktop, "桌面"), (Environment.SpecialFolder.CommonDesktopDirectory, "公共桌面"),
            (Environment.SpecialFolder.StartMenu, "开始菜单"), (Environment.SpecialFolder.CommonStartMenu, "公共开始菜单") })
            if (string.Equals(path, Environment.GetFolderPath(pair.Item1), StringComparison.OrdinalIgnoreCase)) return pair.Item2;
        return Path.GetFileName(path.TrimEnd('\\', '/')) is string name && name.Length > 0 ? name : path;
    }
}

public sealed partial class MainWindow
{
    private DispatcherTimer? _autoScanTimer;
    private bool _autoRefreshRunning;
    private int _scanOptionsRevision;

    private Config ConfigDraft() => LauncherCore.ParseConfig(JsonSerializer.Serialize(_config))
        ?? throw new InvalidOperationException("无法准备配置草稿。");

    private void InitializeAutoScan()
    {
        _autoScanTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _autoScanTimer.Tick += async (_, _) => await RefreshAutoGroupAsync(false);
        _autoScanTimer.Start();
        RootGrid.Loaded += async (_, _) => await RefreshAutoGroupAsync(false);
        Closed += (_, _) => _autoScanTimer.Stop();
    }

    private async Task RefreshAutoGroupAsync(bool manual)
    {
        if (_windowClosed) return;
        if (_autoRefreshRunning) { if (manual) StatusText.Text = "主目录正在刷新，请稍后再试。"; return; }
        if (!manual && (!_config.AutoScanEnabled || DialogService.IsShowing || _scanOpening || _settingsOpening || _settingsActionRunning)) return;
        if (LauncherCore.ConfigurationReadFailed || LauncherCore.ConfigChangedExternally())
        {
            StatusText.Text = "配置读取失败或被其他程序修改，已暂停主目录自动刷新以保护数据。请检查备份并重新打开启伴。";
            AppLog.Write("Automatic update skipped: invalid or externally modified configuration");
            return;
        }
        _autoRefreshRunning = true;
        int revision = _scanOptionsRevision;
        try
        {
            var options = ConfigDraft();
            AppLog.Write("Automatic group scan started");
            if (manual) StatusText.Text = "正在刷新主目录…";
            var result = await ScanCatalog.ReadAsync(options);
            // Ignore results belonging to an old configuration, a closed window, or an active editing dialog.
            if (_windowClosed || revision != _scanOptionsRevision || DialogService.IsShowing || _scanOpening) return;
            if (LauncherCore.ConfigChangedExternally())
            {
                StatusText.Text = "配置在扫描期间被其他程序修改，本次结果未写入。请重新打开启伴。";
                AppLog.Write("Automatic update skipped: configuration changed during scan");
                return;
            }
            var draft = ConfigDraft();
            var target = draft.MainCatalog;
            int added = ScanPolicy.Append(target, result.Hits);
            if (added > 0 || !File.Exists(LauncherCore.ConfigPath))
            {
                LauncherCore.SaveConfigChecked(draft);
                _config.MainCatalog.Apps = target.Apps;
                _config.MainCatalog.Checked = target.Checked;
                int select = manual ? MainCatalogModel.Index : _currentCat;
                RefreshCategories(select);
                RefreshApps(SearchBox.Text);
            }
            string warning = result.Skipped > 0 || result.InstalledFailed ? " 部分位置读取失败，已有记录保留，详情见日志。" : "";
            if (manual || added > 0) StatusText.Text = $"「主目录」刷新完成，新增 {added} 项（默认不勾选）。" + warning;
            if (manual && added == 0) RefreshCategories(MainCatalogModel.Index);
            AppLog.Write($"Automatic group scan completed: added={added}, skipped={result.Skipped}, installedFailed={result.InstalledFailed}");
        }
        catch (Exception ex)
        {
            AppLog.Write("Automatic group refresh failed; existing records retained", ex);
            if (!_windowClosed) StatusText.Text = "主目录刷新未完成，原有记录保留；可在设置中重试，详情见日志。";
        }
        finally { _autoRefreshRunning = false; }
    }
}
