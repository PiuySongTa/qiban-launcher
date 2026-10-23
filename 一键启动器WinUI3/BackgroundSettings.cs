using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace QuickLauncherWinUI3;

public sealed partial class MainWindow
{
    private bool _backgroundOpening;
    private string? _loadedBackground;

    private void ApplyBackground()
    {
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        bool transparent = _config.TransparentPanels && !new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        // Tint only surfaces; text, icons, and buttons stay opaque and readable.
        RootGrid.Resources["LayerFillColorDefaultBrush"] = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(transparent ? (byte)150 : (byte)255, 32, 32, 35)
            : Windows.UI.Color.FromArgb(transparent ? (byte)150 : (byte)255, 249, 249, 252));
        RootGrid.Resources["CardBackgroundFillColorDefaultBrush"] = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(transparent ? (byte)155 : (byte)255, 47, 47, 50)
            : Windows.UI.Color.FromArgb(transparent ? (byte)155 : (byte)255, 255, 255, 255));
        var color = dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
                         : Windows.UI.Color.FromArgb(255, 249, 249, 252);
        string mode = _config.BackgroundMode;
        BackgroundPicture.Visibility = Visibility.Collapsed;
        BackgroundShade.Background = null;
        RootGrid.Background = null;
        try
        {
            SystemBackdrop = mode == "Acrylic" ? new DesktopAcrylicBackdrop() : new MicaBackdrop();
            if (mode == "Solid") RootGrid.Background = new SolidColorBrush(color);
            if (mode == "Default" || mode == "Image")
            {
                string? imagePath = mode == "Default"
                    ? Path.Combine(AppContext.BaseDirectory, "Assets", "klee-logo-v1.png")
                    : _config.BackgroundImage;
                if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                {
                    AppLog.Write("Background image unavailable; using system background");
                    return;
                }
                if (_loadedBackground != imagePath)
                {
                    var bitmap = new BitmapImage { DecodePixelWidth = 2560, UriSource = new Uri(Path.GetFullPath(imagePath)) };
                    bitmap.ImageFailed += (_, _) =>
                    {
                        if (_windowClosed || !ReferenceEquals(BackgroundPicture.Source, bitmap)) return;
                        BackgroundPicture.Visibility = Visibility.Collapsed;
                        BackgroundShade.Background = null;
                        AppLog.Write("Background image decode failed; using system background");
                    };
                    BackgroundPicture.Source = bitmap;
                    _loadedBackground = imagePath;
                }
                BackgroundPicture.Visibility = Visibility.Visible;
                color.A = (byte)Math.Round(Math.Clamp(_config.BackgroundShade, 0, 100) * 2.55);
                BackgroundShade.Background = new SolidColorBrush(color);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Failed to apply background", ex);
            RootGrid.Background = new SolidColorBrush(color);
        }
    }

    private async void BackgroundSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_backgroundOpening) return;
        _backgroundOpening = true;
        try
        {
            var modes = new[] { "Default", "Acrylic", "Image", "Solid" };
            var mode = new ComboBox { Header = "背景样式", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (string label in new[] { "可莉（默认）", "透明磨砂", "自选图片", "纯色背景" }) mode.Items.Add(label);
            mode.SelectedIndex = Math.Max(0, Array.IndexOf(modes, _config.BackgroundMode));
            var shade = new Slider { Header = "图片遮罩浓度（越高，文字越清晰）", Minimum = 0, Maximum = 100,
                Value = double.IsFinite(_config.BackgroundShade) ? Math.Clamp(_config.BackgroundShade, 0, 100) : 55, StepFrequency = 5 };
            var path = new TextBlock { Text = string.IsNullOrEmpty(_config.BackgroundImage) ? "尚未选择图片" : Path.GetFileName(_config.BackgroundImage), TextWrapping = TextWrapping.Wrap };
            var choose = new Button { Content = "选择图片…" };
            var preview = new Image { Height = 110, Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
            var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var panel = new StackPanel { Spacing = 12, Width = 420 };
            panel.Children.Add(mode); panel.Children.Add(choose); panel.Children.Add(path); panel.Children.Add(preview);
            panel.Children.Add(shade);
            panel.Children.Add(new TextBlock { Text = "磨砂效果受 Windows 透明效果和节能设置影响。图片只改变背景，文字和按钮保持清晰。", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(message);
            var dialog = new ContentDialog { Title = "背景设置", Content = panel, XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme, PrimaryButtonText = "保存", CloseButtonText = "取消" };
            byte[]? selectedBytes = null;
            string extension = ".png";
            bool picking = false, closed = false;
            dialog.Closed += (_, _) => closed = true;
            void UpdateControls()
            {
                bool image = mode.SelectedIndex == 2;
                choose.IsEnabled = image && !picking;
                shade.IsEnabled = image || mode.SelectedIndex == 0;
                path.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
                preview.Visibility = image && preview.Source != null ? Visibility.Visible : Visibility.Collapsed;
            }
            mode.SelectionChanged += (_, _) => UpdateControls();
            UpdateControls();
            choose.Click += async (_, _) =>
            {
                if (picking) return;
                picking = true; UpdateControls();
                dialog.IsPrimaryButtonEnabled = false;
                try
                {
                    var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                    foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp" }) picker.FileTypeFilter.Add(ext);
                    var file = await picker.PickSingleFileAsync();
                    if (file == null || closed || _windowClosed) return;
                    var properties = await file.GetBasicPropertiesAsync();
                    if (properties.Size > 25 * 1024 * 1024) throw new InvalidOperationException("图片超过 25 MB，请选择较小的图片。");
                    using var stream = await file.OpenReadAsync();
                    var bitmap = new BitmapImage { DecodePixelWidth = 1920 };
                    await bitmap.SetSourceAsync(stream);
                    if (closed || _windowClosed) return;
                    stream.Seek(0);
                    using var memory = new MemoryStream();
                    await stream.AsStreamForRead().CopyToAsync(memory);
                    if (closed || _windowClosed) return;
                    selectedBytes = memory.ToArray(); extension = Path.GetExtension(file.Name);
                    path.Text = file.Name; preview.Source = bitmap; message.Text = "";
                }
                catch (Exception ex)
                {
                    AppLog.Write("Background image selection failed", ex);
                    if (!closed && !_windowClosed) message.Text = "无法读取图片：" + ex.Message;
                }
                finally
                {
                    picking = false;
                    if (!closed && !_windowClosed) { dialog.IsPrimaryButtonEnabled = true; UpdateControls(); }
                }
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (picking) { args.Cancel = true; return; }
                var oldMode = _config.BackgroundMode;
                var oldImage = _config.BackgroundImage;
                var oldShade = _config.BackgroundShade;
                try
                {
                    if (mode.SelectedIndex == 2 && selectedBytes == null && !File.Exists(oldImage))
                        throw new InvalidOperationException("请先选择一张图片。");
                    string? imagePath = oldImage;
                    if (selectedBytes != null && mode.SelectedIndex == 2)
                    {
                        string folder = Path.Combine(AppContext.BaseDirectory, "Backgrounds");
                        Directory.CreateDirectory(folder);
                        imagePath = Path.Combine(folder, Guid.NewGuid().ToString("N") + extension);
                        File.WriteAllBytes(imagePath, selectedBytes);
                    }
                    _config.BackgroundMode = modes[mode.SelectedIndex];
                    _config.BackgroundImage = imagePath;
                    _config.BackgroundShade = shade.Value;
                    LauncherCore.SaveConfigChecked(_config);
                    ApplyBackground();
                    AppLog.Write("Background settings saved: " + _config.BackgroundMode);
                }
                catch (Exception ex)
                {
                    _config.BackgroundMode = oldMode; _config.BackgroundImage = oldImage; _config.BackgroundShade = oldShade;
                    AppLog.Write("Background settings save failed", ex);
                    message.Text = "未保存：" + ex.Message; args.Cancel = true;
                }
            };
            await DialogService.ShowAsync(dialog);
        }
        catch (Exception ex) { AppLog.Write("Background settings failed", ex); }
        finally { _backgroundOpening = false; }
    }
}
