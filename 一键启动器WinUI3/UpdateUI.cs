using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace QuickLauncherWinUI3;

public sealed partial class MainWindow
{
    private bool _updateOpening;

    private async Task ShowUpdatesAsync()
    {
        if (_updateOpening || _windowClosed) return;
        _updateOpening = true;
        using var lifetime = new CancellationTokenSource();
        Task operation = Task.CompletedTask;
        bool closed = false, busy = false;
        Windows.Foundation.TypedEventHandler<object, WindowEventArgs> onWindowClosed = (_, _) => lifetime.Cancel();
        Closed += onWindowClosed;
        try
        {
            UpdateRelease? available = null;
            var panel = new StackPanel { Spacing = 12, Width = 500 };
            panel.Children.Add(new TextBlock { Text = "当前版本 v" + VersionInfo.Current + "\n更新来源：" + AppUpdateClient.Repository, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "从公开仓库读取更新，无需登录 GitHub 或填写令牌。检查时只请求发行信息，不上传你的软件清单和配置。", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            var prerelease = new CheckBox { Content = "包含测试版（beta / rc）" };
            var check = new Button { Content = "检查更新" };
            var status = new TextBlock { Text = "点击检查后读取最新发布记录。", TextWrapping = TextWrapping.Wrap };
            var progress = new ProgressBar { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
            var notes = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            panel.Children.Add(prerelease); panel.Children.Add(check); panel.Children.Add(status); panel.Children.Add(progress);
            panel.Children.Add(new ScrollViewer { Content = notes, MaxHeight = 160 });
            panel.Children.Add(new HyperlinkButton { Content = "打开 GitHub 发布页", NavigateUri = new Uri(AppUpdateClient.ReleasesPage) });
            panel.Children.Add(new TextBlock { Text = "下载安装包后，关闭启伴，备份 launcher_config.json 与 Backgrounds，再把包内完整程序文件夹合并到原位置，保留配置、背景和日志。当前采用手动安装，不在运行时替换文件。", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            var dialog = new ContentDialog { Title = "版本更新", Content = new ScrollViewer { Content = panel, MaxHeight = 480, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme,
                PrimaryButtonText = "下载安装包", IsPrimaryButtonEnabled = false, CloseButtonText = "关闭", DefaultButton = ContentDialogButton.Close };
            dialog.Resources["ContentDialogMaxWidth"] = 620d;
            void Busy(bool value)
            {
                busy = value;
                if (closed || _windowClosed) return;
                check.IsEnabled = prerelease.IsEnabled = !value;
                dialog.IsPrimaryButtonEnabled = !value && available != null;
                dialog.CloseButtonText = value ? "取消并关闭" : "关闭";
            }
            void Reset() { available = null; dialog.IsPrimaryButtonEnabled = false; notes.Text = ""; status.Text = "条件已改变，请重新检查。"; }
            prerelease.Click += (_, _) => Reset();
            async Task CheckAsync()
            {
                Busy(true); available = null; notes.Text = ""; status.Text = "正在连接 GitHub…";
                try
                {
                    using var client = new AppUpdateClient();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(45));
                    var result = await client.CheckAsync(VersionInfo.Current, prerelease.IsChecked == true, timeout.Token);
                    if (closed || _windowClosed) return;
                    available = result;
                    status.Text = result == null ? "未发现比当前版本更新、且提供 Windows x64 安装包的发行版。" : $"发现 {result.Tag}，安装包 {result.Package.Size / 1024d / 1024d:F1} MB。";
                    notes.Text = result?.Notes ?? "正式版与测试版按你选择的范围检查；较旧版本不会被视为更新。";
                    AppLog.Write("Update check completed: newer=" + (result != null));
                }
                catch (OperationCanceledException) { if (!closed && !_windowClosed) status.Text = "检查超时或已取消，可以重试。"; }
                catch (Exception ex) { if (!closed && !_windowClosed) status.Text = "检查未完成：" + (ex is System.Net.Http.HttpRequestException ? "无法连接 GitHub，请检查网络后重试。" : ex.Message); AppLog.Write("Update check failed: " + ex.GetType().Name); }
                finally { Busy(false); }
            }
            async Task DownloadAsync(UpdateRelease release)
            {
                Busy(true);
                try
                {
                    var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.Downloads, SuggestedFileName = Path.GetFileNameWithoutExtension(release.Package.Name) };
                    picker.FileTypeChoices.Add("ZIP 安装包", new[] { ".zip" });
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                    var file = await picker.PickSaveFileAsync();
                    if (file == null || closed || _windowClosed) return;
                    status.Text = "正在下载并校验安装包…"; progress.Value = 0; progress.Visibility = Visibility.Visible;
                    using var client = new AppUpdateClient();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromMinutes(10));
                    await client.DownloadAsync(release, file.Path, new Progress<double>(value => { if (!closed && !_windowClosed) progress.Value = value; }), timeout.Token);
                    if (closed || _windowClosed) return;
                    status.Text = "下载完成，SHA256 校验通过。安装包：" + file.Path + "\n请按下方步骤关闭软件后手动安装。";
                    AppLog.Write("Update package downloaded and verified");
                }
                catch (OperationCanceledException) { if (!closed && !_windowClosed) status.Text = "下载已取消，可以重试。"; }
                catch (Exception ex) { if (!closed && !_windowClosed) status.Text = "下载未完成：" + (ex is System.Net.Http.HttpRequestException ? "网络连接失败，请重试。" : ex.Message); AppLog.Write("Update download failed: " + ex.GetType().Name); }
                finally { Busy(false); }
            }
            check.Click += async (_, _) => { if (busy) return; operation = CheckAsync(); await operation; };
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                args.Cancel = true;
                if (busy || available == null) return;
                operation = DownloadAsync(available); await operation;
            };
            dialog.Closed += (_, _) => { closed = true; lifetime.Cancel(); };
            await DialogService.ShowAsync(dialog);
        }
        catch (Exception ex) { AppLog.Write("Update dialog failed: " + ex.GetType().Name); if (!_windowClosed) StatusText.Text = "无法打开版本更新，请重试。"; }
        finally
        {
            closed = true; lifetime.Cancel();
            Closed -= onWindowClosed;
            try { await operation; } catch (OperationCanceledException) { }
            _updateOpening = false;
        }
    }
}
