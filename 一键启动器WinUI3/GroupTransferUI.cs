using System;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace QuickLauncherWinUI3;

public sealed partial class MainWindow
{
    private bool _transferOpening;

    private async void TransferToGroup(bool move, bool single)
    {
        if (_transferOpening) return;
        _transferOpening = true;
        try
        {
            var source = CurrentCat();
            if (source == null) return;
            int sourceIndex = _currentCat;
            int[] indices = single
                ? (AppList.SelectedItem is AppItemVM vm ? new[] { vm.Index } : Array.Empty<int>())
                : Enumerable.Range(0, source.Apps.Count).Where(i => source.Checked[i]).ToArray();
            if (indices.Length == 0) { StatusText.Text = "请先勾选要整理的软件。"; return; }
            var targets = _config.Categories.Select((c, i) => (Category: c, Index: i)).Where(x => x.Index != sourceIndex).ToArray();
            if (targets.Length == 0) { StatusText.Text = "请先新建另一个分组。"; return; }
            string action = move ? "移动" : "复制";
            int hidden = single ? 0 : indices.Count(i => !(AppList.ItemsSource is System.Collections.Generic.IEnumerable<AppItemVM> shown) || !shown.Any(v => v.Index == i));
            var target = new ComboBox { Header = "目标分组", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var entry in targets) target.Items.Add(entry.Category.Name);
            target.SelectedIndex = 0;
            var panel = new StackPanel { Spacing = 12, Width = 420 };
            panel.Children.Add(new TextBlock { Text = $"从「{source.Name}」{action} {indices.Length} 项" + (hidden > 0 ? $"，其中 {hidden} 项被搜索隐藏。" : "。"), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(target);
            panel.Children.Add(new TextBlock { Text = "目标中已有相同路径的条目会跳过；同名不同路径仍可添加。" + (move ? "跳过的条目会保留在原分组。" : "原分组保留全部记录。") + "\n名称、路径、启动参数和管理员设置保留；目标中新条目默认不勾选。", TextWrapping = TextWrapping.Wrap });
            var list = new ListView { MaxHeight = 180, SelectionMode = ListViewSelectionMode.None };
            foreach (int i in indices)
                list.Items.Add(new TextBlock { Text = LauncherCore.DisplayName(source.Apps[i].Name, source.Apps[i].Path) + "\n" + source.Apps[i].Path, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            panel.Children.Add(list);
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(error);
            var dialog = new ContentDialog { Title = action + "到分组", Content = panel, XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme, PrimaryButtonText = "确认" + action, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                bool committed = false;
                try
                {
                    if (_windowClosed || _currentCat != sourceIndex || !ReferenceEquals(CurrentCat(), source))
                        throw new InvalidOperationException("原分组已变化，请取消后重试。");
                    // Prepare and save an independent draft; a failed write never removes source records.
                    var draft = LauncherCore.ParseConfig(JsonSerializer.Serialize(_config))
                        ?? throw new InvalidOperationException("无法准备分组数据。");
                    int targetIndex = targets[target.SelectedIndex].Index;
                    var result = GroupTransfer.Apply(draft.Categories[sourceIndex], draft.Categories[targetIndex], indices, move);
                    if (result.Added > 0)
                    {
                        LauncherCore.SaveConfigChecked(draft);
                        _config.Categories = draft.Categories;
                        committed = true;
                        RefreshCategories(sourceIndex);
                        RefreshApps(SearchBox.Text);
                    }
                    StatusText.Text = $"已{action} {result.Added} 项到「{targets[target.SelectedIndex].Category.Name}」；跳过 {result.Skipped} 项相同路径记录" + (move && result.Skipped > 0 ? "（仍保留在原组）" : "") + "。";
                    AppLog.Write($"Group transfer completed: move={move}, added={result.Added}, skipped={result.Skipped}");
                }
                catch (Exception ex)
                {
                    AppLog.Write("Group transfer failed", ex);
                    error.Text = committed
                        ? "分组数据已保存，但界面刷新失败。请关闭此弹窗并重新打开启伴。"
                        : (ex is System.IO.IOException || ex is UnauthorizedAccessException
                            ? "保存失败，请检查配置文件是否只读或被其他程序占用。原分组记录已保留。"
                            : "未完成：" + ex.Message + " 原分组记录已保留。");
                    if (committed) dialog.IsPrimaryButtonEnabled = false;
                    args.Cancel = true;
                }
            };
            await DialogService.ShowAsync(dialog);
        }
        catch (Exception ex) { AppLog.Write("Group transfer dialog failed", ex); StatusText.Text = "无法打开分组整理，请重试。"; }
        finally { _transferOpening = false; }
    }

    private void CopyChecked_Click(object sender, RoutedEventArgs e) => TransferToGroup(false, false);
    private void MoveChecked_Click(object sender, RoutedEventArgs e) => TransferToGroup(true, false);
}
