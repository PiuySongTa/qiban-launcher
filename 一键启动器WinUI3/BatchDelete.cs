using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace QuickLauncherWinUI3;

public sealed partial class MainWindow
{
    private bool _deleteOpening;
    private async void DeleteChecked_Click(object sender, RoutedEventArgs e)
    {
        if (_deleteOpening) return;
        _deleteOpening = true;
        try
        {
            var source = CurrentCat();
            if (source == null) return;
            LauncherCore.EnsureCheckedSync(source);
            int categoryIndex = _currentCat;
            int[] indices = Enumerable.Range(0, source.Apps.Count).Where(i => source.Checked[i]).ToArray();
            if (indices.Length == 0) { StatusText.Text = "请先勾选要删除的记录。"; return; }
            var visible = (AppList.ItemsSource as IEnumerable<AppItemVM>)?.Select(v => v.Index).ToHashSet() ?? new();
            int hidden = indices.Count(i => !visible.Contains(i));
            var panel = new StackPanel { Spacing = 12, Width = 420 };
            panel.Children.Add(new TextBlock { Text = $"从「{source.Name}」移除 {indices.Length} 条勾选记录" + (hidden > 0 ? $"（含被搜索隐藏的 {hidden} 条）" : "") + "。只移除启伴中的记录，不删除原文件。" +
                (source.IsAutoScan ? "\n主目录中的记录可能因自动扫描或分组引用重新出现；不希望再次扫描，请先加入设置中的扫描排除名单。已有分组记录会保留。" : ""), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new ScrollViewer { MaxHeight = 240, Content = new TextBlock { Text = string.Join("\n\n", indices.Select(i => LauncherCore.DisplayName(source.Apps[i].Name, source.Apps[i].Path) + "\n" + source.Apps[i].Path)), TextWrapping = TextWrapping.Wrap } });
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(error);
            var dialog = new ContentDialog { Title = "删除勾选记录", Content = panel, XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme,
                PrimaryButtonText = $"删除 {indices.Length} 项", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                bool saved = false;
                try
                {
                    if (_windowClosed || !ReferenceEquals(source, CurrentCat()) || categoryIndex != _currentCat) throw new InvalidOperationException("分组已变化，请取消后重试。");
                    var draft = ConfigDraft();
                    ScanPolicy.Delete(MainCatalogModel.At(draft, categoryIndex)!, indices);
                    LauncherCore.SaveConfigChecked(draft);
                    _config.Categories = draft.Categories;
                    _config.MainCatalog = draft.MainCatalog;
                    saved = true;
                    RefreshCategories(categoryIndex);
                    RefreshApps(SearchBox.Text);
                    StatusText.Text = $"已移除 {indices.Length} 条记录，原文件保留。";
                    AppLog.Write("Batch delete completed: count=" + indices.Length);
                }
                catch (Exception ex)
                {
                    AppLog.Write("Batch delete failed", ex);
                    error.Text = saved ? "记录已保存，但界面刷新失败，请重新打开启伴。" : "删除未完成，原记录保留。请检查配置文件是否可写，然后重试。";
                    dialog.IsPrimaryButtonEnabled = !saved;
                    args.Cancel = true;
                }
            };
            await DialogService.ShowAsync(dialog);
        }
        catch (Exception ex) { AppLog.Write("Batch delete dialog failed", ex); if (!_windowClosed) StatusText.Text = "无法打开删除确认，请重试。"; }
        finally { _deleteOpening = false; }
    }
}
