using System;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace QuickLauncherWinUI3;

internal static class VersionInfo
{
    public static string Current { get; } = typeof(VersionInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(VersionInfo).Assembly.GetName().Version?.ToString(3) ?? "未知";

    public static string ReadHistory()
    {
        using var stream = typeof(VersionInfo).Assembly.GetManifestResourceStream("QuickLauncherWinUI3.Changelog")
            ?? throw new FileNotFoundException("当前程序未包含更新记录。");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var text = new StringBuilder();
        while (reader.ReadLine() is string line)
            text.AppendLine(line.StartsWith('#') ? line.TrimStart('#', ' ') : line.StartsWith("- ") ? "• " + line.Substring(2) : line);
        return text.ToString();
    }
}

public sealed partial class MainWindow
{
    private bool _versionOpening;
    private async void Version_Click(object sender, RoutedEventArgs e)
    {
        if (_versionOpening) return;
        _versionOpening = true;
        try
        {
            string history;
            try { history = VersionInfo.ReadHistory(); }
            catch (Exception ex) { AppLog.Write("Unable to read version history", ex); history = "暂时无法读取更新记录。当前版本：v" + VersionInfo.Current; }
            var content = new TextBlock { Text = history, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            var dialog = new ContentDialog
            {
                Title = "启伴·一键启动器 · v" + VersionInfo.Current,
                XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme,
                Content = new ScrollViewer { Content = content, MaxHeight = 420, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                CloseButtonText = "关闭"
            };
            await DialogService.ShowAsync(dialog);
        }
        catch (Exception ex) { AppLog.Write("Version dialog failed", ex); }
        finally { _versionOpening = false; }
    }
}
