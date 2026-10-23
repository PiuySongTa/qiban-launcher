using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace QuickLauncherWinUI3;

public sealed partial class MainWindow
{
    private bool _navigationSync;
    private void MainCatalog_Click(object sender, RoutedEventArgs e) => RefreshCategories(MainCatalogModel.Index);
    private void CatalogFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_config != null && AppList != null) RefreshApps(SearchBox.Text);
    }

    private void RefreshCatalogNavigation(int select)
    {
        _navigationSync = true;
        try
        {
            MainCatalogModel.IncludeGroups(_config);
            CatList.ItemsSource = _config.Categories.Select(c => new CategoryVM(c)).ToList();
            _currentCat = select == MainCatalogModel.Index ? select : select >= 0 && select < _config.Categories.Count ? select : MainCatalogModel.Index;
            CatList.SelectedIndex = _currentCat >= 0 ? _currentCat : -1;
            MainCatalogButton.IsChecked = _currentCat == MainCatalogModel.Index;
            MainCatalogButton.Content = "主目录 · " + _config.MainCatalog.Apps.Count;
            CatalogFilter.Visibility = _currentCat == MainCatalogModel.Index ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _navigationSync = false; }
        RefreshApps(SearchBox.Text);
    }

}
