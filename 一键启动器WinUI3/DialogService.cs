using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace QuickLauncherWinUI3;

internal static class AppLog
{
    private static readonly object Sync = new();
    public static void Write(string context, Exception? error = null)
    {
        try
        {
            lock (Sync)
            {
                string folder = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "launcher-" + DateTime.Now.ToString("yyyyMMdd") + ".log"),
                    DateTime.Now.ToString("O") + " " + context + Environment.NewLine +
                    (error == null ? "" : error + Environment.NewLine));
            }
        }
        catch { /* Diagnostics must never interrupt the app. */ }
    }
}

internal static class DialogService
{
    private static int _showing;
    public static bool IsShowing => Volatile.Read(ref _showing) != 0;
    public static bool TransparentPanels { get; set; } = true;
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        // Reject extra clicks instead of queuing dialogs that appear after dismissal.
        if (Interlocked.CompareExchange(ref _showing, 1, 0) != 0)
        {
            AppLog.Write("Ignored overlapping dialog: " + dialog.Title);
            return ContentDialogResult.None;
        }
        try
        {
            if (dialog.RequestedTheme == ElementTheme.Default && dialog.XamlRoot?.Content is FrameworkElement root)
                dialog.RequestedTheme = root.ActualTheme;
            if (TransparentPanels && !new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
            {
                bool dark = dialog.RequestedTheme == ElementTheme.Dark;
                var background = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(216, 32, 32, 35) : Windows.UI.Color.FromArgb(216, 249, 249, 252));
                dialog.Background = background;
                dialog.Resources["ContentDialogBackground"] = background;
                dialog.Resources["ContentDialogTopOverlay"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                dialog.Resources["ContentDialogSmokeFill"] = new SolidColorBrush(Windows.UI.Color.FromArgb(65, 0, 0, 0));
            }
            AppLog.Write("Opening dialog: " + dialog.Title);
            return await dialog.ShowAsync();
        }
        catch (Exception error)
        {
            AppLog.Write("Failed to open dialog: " + dialog.Title, error);
            return ContentDialogResult.None;
        }
        finally
        {
            Interlocked.Exchange(ref _showing, 0);
        }
    }
}
