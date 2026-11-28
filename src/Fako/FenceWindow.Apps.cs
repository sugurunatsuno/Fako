using System.Windows;
using System.Windows.Controls;
using Fako.Interop;
using Fako.Models;

namespace Fako;

/// <summary>
/// アプリ一覧（Launchpad 風）仮想フェンスの部分。
/// </summary>
public partial class FenceWindow
{
    private bool IsApps => Config.Mode == FenceMode.Apps;
    private bool ShouldKeepSearchBarOpen => IsApps && Config.AppsAlwaysShowSearch;
    private bool EffectiveSingleClickOpen => App.Current.Config.SingleClickOpen || (IsApps && Config.AppsSingleClick);

    // ------------------------------------------------------------------
    // 表示
    // ------------------------------------------------------------------

    private void ApplyAppsUi()
    {
        SearchPlaceholder.Text = IsApps ? "アプリを検索..." : "絞り込み...";
        UpdateSearchPlaceholder();
        if (ShouldKeepSearchBarOpen)
        {
            SearchBar.Visibility = Visibility.Visible;
            SearchCloseButton.ToolTip = "検索をクリア (Esc)";
        }
        else
        {
            SearchCloseButton.ToolTip = "検索を閉じる (Esc)";
            if (string.IsNullOrEmpty(SearchBox.Text))
                SearchBar.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateSearchPlaceholder()
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ReloadApps()
    {
        ApplyRemoteUi();
        ApplyAppsUi();

        var selected = ItemsList.SelectedItems.Cast<FenceItem>().Select(i => i.Path).ToHashSet(PathComparer);
        var loaded = ShellIcons.CreateAppItems(Config.AppsHideNoise, _iconPx);
        // AppsListing.Filter が既に DisplayName の自然順（昇順）で整列済み
        IEnumerable<FenceItem> ordered = EffectiveSortKey == SortKey.Name && !Config.SortDescending
            ? loaded
            : Sort(loaded);
        _items.Clear();
        foreach (var item in ordered)
            _items.Add(item);
        foreach (var item in _items.Where(i => selected.Contains(i.Path)))
            ItemsList.SelectedItems.Add(item);
        if (_searchText.Length > 0 && ItemsList.SelectedItem is null)
            ItemsList.SelectedItem = FirstVisibleItem();

        EmptyHint.Text = DefaultEmptyHint();
        UpdateEmptyHint();
    }

    // ------------------------------------------------------------------
    // メニュー
    // ------------------------------------------------------------------

    private ContextMenu BuildAppItemMenu(List<FenceItem> items)
    {
        var m = new ContextMenu();
        if (items.Count == 0) return m;

        var open = AddItem(m, "開く", () => items.ForEach(i => ShellActions.Open(i.Path)));
        open.FontWeight = FontWeights.Bold;
        AddItem(m, "アプリ ID をコピー", () =>
        {
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, items.Select(i => FolderListing.ExtractAppId(i.Path))));
            }
            catch (Exception ex)
            {
                Log.Error("クリップボードへのコピーに失敗", ex);
            }
        });
        m.Items.Add(new Separator());
        AddItem(m, "「アプリと機能」を開く…", ShellActions.OpenAppsSettings);
        return m;
    }

    /// <summary>何もない所の右クリックメニューに入れる、アプリ一覧フェンス用の項目。</summary>
    private void AddAppsFenceMenuItems(ContextMenu m)
    {
        var hideNoise = AddItem(m, "アンインストーラー・マニュアル等を隠す", () =>
        {
            Config.AppsHideNoise = !Config.AppsHideNoise;
            Reload();
            App.Current.SaveSoon();
        });
        hideNoise.IsCheckable = true;
        hideNoise.IsChecked = Config.AppsHideNoise;

        bool globalSingle = App.Current.Config.SingleClickOpen;
        var singleClick = AddItem(m,
            globalSingle ? "シングルクリックで起動（全体設定が有効）" : "シングルクリックで起動",
            () =>
            {
                Config.AppsSingleClick = !Config.AppsSingleClick;
                App.Current.SaveSoon();
            });
        singleClick.IsCheckable = true;
        singleClick.IsChecked = globalSingle || Config.AppsSingleClick;
        singleClick.IsEnabled = !globalSingle;

        var alwaysSearch = AddItem(m, "検索バーを常に表示", () =>
        {
            Config.AppsAlwaysShowSearch = !Config.AppsAlwaysShowSearch;
            ApplyAppsUi();
            App.Current.SaveSoon();
        });
        alwaysSearch.IsCheckable = true;
        alwaysSearch.IsChecked = Config.AppsAlwaysShowSearch;

        AddItem(m, "「アプリと機能」を開く…", ShellActions.OpenAppsSettings);
        m.Items.Add(new Separator());
    }
}
