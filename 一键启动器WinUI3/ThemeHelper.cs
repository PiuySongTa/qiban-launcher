using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace QuickLauncherWinUI3;

public static class ThemeHelper
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void UpdateTitleBarColors(Window window)
    {
        if (window.Content is FrameworkElement root)
        {
            root.ActualThemeChanged += (sender, args) => ApplyTitleBarColors(window);
            window.Activated += (sender, args) => ApplyTitleBarColors(window);
            ApplyTitleBarColors(window);
        }
    }

    public static void ApplyTitleBarColors(Window window)
    {
        bool dark = (window.Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        var background = dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
                              : Windows.UI.Color.FromArgb(255, 249, 249, 252);
        var foreground = dark ? Colors.White : Colors.Black;
        var inactive = dark ? Windows.UI.Color.FromArgb(255, 170, 170, 170)
                            : Windows.UI.Color.FromArgb(255, 100, 100, 100);
        var hover = dark ? Windows.UI.Color.FromArgb(255, 55, 55, 55)
                         : Windows.UI.Color.FromArgb(255, 232, 232, 237);
        // The native caption uses its own theme, separate from the XAML content.
        int nativeDark = dark ? 1 : 0;
        DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(window), 20, ref nativeDark, sizeof(int));
        var bar = window.AppWindow.TitleBar;
        bar.BackgroundColor = background;
        bar.ForegroundColor = foreground;
        bar.InactiveBackgroundColor = background;
        bar.InactiveForegroundColor = inactive;
        bar.ButtonBackgroundColor = background;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonInactiveBackgroundColor = background;
        bar.ButtonInactiveForegroundColor = inactive;
        bar.ButtonHoverBackgroundColor = hover;
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonPressedBackgroundColor = hover;
        bar.ButtonPressedForegroundColor = foreground;
    }
}
