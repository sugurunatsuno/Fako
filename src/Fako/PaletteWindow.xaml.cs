using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fako.Models;

namespace Fako;

/// <summary>
/// 全フェンス横断の検索・ランチャー（Ctrl+Alt+F）。
/// インデックスを使わず、いま表示している一覧から名前で絞り込む。
/// </summary>
public partial class PaletteWindow : Window
{
    private static PaletteWindow? _open;

    private readonly ObservableCollection<Entry> _shown = new();
    private List<Entry> _all = new();
    private bool _closing;

    public sealed record Entry(FenceWindow Owner, FenceItem Item)
    {
        public string Name => Item.Name;
        public string Fence => Owner.PageTitle;
        public ImageSource? Icon => Item.Icon;
    }

    public static void Toggle()
    {
        if (_open != null)
        {
            _open.CloseOnce();
            return;
        }
        ShowSingle();
    }

    /// <summary>閉じる処理は 1 回だけ。Close 後の Deactivated で再度 Close すると例外になる。</summary>
    private void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    public static void ShowSingle()
    {
        if (_open != null)
        {
            _open.Activate();
            _open.QueryBox.Focus();
            _open.QueryBox.SelectAll();
            return;
        }
        _open = new PaletteWindow();
        _open.Closed += (_, _) =>
        {
            _open = null;
            Log.Info("palette closed");
        };
        _open.Show();
        _open.Activate();
        _open.QueryBox.Focus();
        Log.Info($"palette opened: {_open._shown.Count} items");
    }

    private PaletteWindow()
    {
        InitializeComponent();
        ResultList.ItemsSource = _shown;
        Reload();
        Deactivated += (_, _) => CloseOnce();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void Reload()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _all = App.Current.EnumerateItems()
            .Where(x => x.Item.Exists && seen.Add(x.Item.Path))
            .Select(x => new Entry(x.Fence, x.Item))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        ApplyFilter();
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = QueryBox.Text.Trim();
        IEnumerable<Entry> source = _all;
        if (query.Length > 0)
        {
            var compare = System.Globalization.CultureInfo.CurrentCulture.CompareInfo;
            const System.Globalization.CompareOptions options =
                System.Globalization.CompareOptions.IgnoreCase
                | System.Globalization.CompareOptions.IgnoreKanaType
                | System.Globalization.CompareOptions.IgnoreWidth;
            source = _all.Where(e => compare.IndexOf(e.Name, query, options) >= 0);
        }

        _shown.Clear();
        foreach (var entry in source.Take(200)) _shown.Add(entry);
        CountText.Text = _shown.Count == 0 ? "一致なし" : $"{_shown.Count} 件";
        if (_shown.Count > 0) ResultList.SelectedIndex = 0;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseOnce();
                e.Handled = true;
                break;
            case Key.Enter:
                OpenSelected();
                e.Handled = true;
                break;
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_shown.Count == 0) return;
        ResultList.SelectedIndex = Math.Clamp(ResultList.SelectedIndex + delta, 0, _shown.Count - 1);
        if (ResultList.SelectedItem is Entry entry) ResultList.ScrollIntoView(entry);
    }

    private void OpenSelected()
    {
        if (ResultList.SelectedItem is not Entry entry) return;
        CloseOnce();
        entry.Owner.OpenItem(entry.Item);
    }

    /// <summary>テスト用（FAKO_OPEN=paletteopen）: 先頭の検索結果を開く。</summary>
    internal static void DebugOpenFirst()
    {
        if (_open is null) return;
        _open.ResultList.SelectedIndex = 0;
        _open.OpenSelected();
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();
}
