using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace QuickLauncherWinUI3;

public sealed partial class InputDialog : ContentDialog
{
    public InputDialog()
    {
        InitializeComponent();
    }

    public static async Task<string?> ShowAsync(XamlRoot xr, string title, string initial)
    {
        var dlg = new InputDialog
        {
            Title = title,
            XamlRoot = xr,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        dlg.NameBox.Text = initial;
        dlg.NameBox.SelectAll();
        if (await DialogService.ShowAsync(dlg) == ContentDialogResult.Primary)
            return dlg.NameBox.Text.Trim();
        return null;
    }
}
