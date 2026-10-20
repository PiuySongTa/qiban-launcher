using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace QuickLauncherWinUI3;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }
    public static Config Config { get; private set; } = new();

    public App()
    {
        InitializeComponent();
        UnhandledException += (sender, args) => AppLog.Write("Unhandled UI exception", args.Exception);
        System.AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            AppLog.Write("Unhandled runtime exception", args.ExceptionObject as System.Exception);
        AppLog.Write("Application started: v" + VersionInfo.Current);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Config = LauncherCore.LoadConfig();
        MainWindow = new MainWindow(Config);
        ThemeHelper.UpdateTitleBarColors(MainWindow);
        MainWindow.Activate();
    }

    public static void ApplyTheme(ElementTheme theme)
    {
        if (MainWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
            ThemeHelper.ApplyTitleBarColors(MainWindow);
        }
    }
}
