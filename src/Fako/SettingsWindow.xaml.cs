using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Fako.Interop;
using Fako.Models;

namespace Fako;

/// <summary>アプリ全体の設定画面（OK／キャンセル／適用）。</summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    public sealed record Option(object Value, string Label);

    private readonly ObservableCollection<RuleViewModel> _rules = new();
    private readonly ObservableCollection<Option> _fences = new();
    private RuleViewModel? _selected;

    public static void ShowSingle(int tab = 0)
    {
        if (_open != null)
        {
            _open.Tabs.SelectedIndex = tab;
            _open.Activate();
            return;
        }
        _open = new SettingsWindow();
        _open.Tabs.SelectedIndex = tab;
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    private SettingsWindow()
    {
        InitializeComponent();
        var cfg = App.Current.Config;

        // 全般
        AutoStartBox.IsChecked = AutoStart.IsEnabled();
        DoubleClickBox.IsChecked = cfg.DoubleClickToHide;
        ManagedRadio.IsChecked = App.Current.IsFenceManaged;
        NotManagedRadio.IsChecked = !App.Current.IsFenceManaged;

        // スナップ
        SnapBox.IsChecked = cfg.SnapEnabled;
        SnapEdgesBox.IsChecked = cfg.SnapToScreenEdges;
        SnapDistanceSlider.Value = cfg.SnapDistance;
        SnapGapSlider.Value = cfg.SnapGap;

        // 自動振り分け
        AutoSortBox.IsChecked = cfg.AutoSortEnabled;
        KindCombo.ItemsSource = Enum.GetValues<RuleKind>().Select(k => new Option(k, RuleEngine.KindLabel(k))).ToList();
        CategoryCombo.ItemsSource = Enum.GetValues<FileCategory>().Select(c => new Option(c, RuleEngine.CategoryLabel(c))).ToList();
        RefreshFenceOptions();
        TargetCombo.ItemsSource = _fences;
        foreach (var r in cfg.Rules)
            _rules.Add(NewViewModel(r.Clone()));
        RuleList.ItemsSource = _rules;
        if (_rules.Count > 0) RuleList.SelectedIndex = 0;
        UpdateRuleEditor();
        UpdateExclusionsButton();

        // 情報
        VersionText.Text = $"バージョン {typeof(App).Assembly.GetName().Version?.ToString(3)}";
        ConfigPathText.Text = $"設定: {App.Current.Store.FilePath}";
        LogPathText.Text = $"ログ: {Log.CurrentPath ?? Path.Combine(ConfigStore.Directory, "fako.log")}";
    }

    // ------------------------------------------------------------------
    // 保存
    // ------------------------------------------------------------------

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Apply()) Close();
    }

    private void OnApply(object sender, RoutedEventArgs e) => Apply();

    private bool Apply()
    {
        var invalid = _rules.FirstOrDefault(r => r.Enabled && r.HasError);
        if (invalid != null)
        {
            Tabs.SelectedIndex = 2;
            RuleList.SelectedItem = invalid;
            if (MessageBox.Show(this, $"ルール「{invalid.Name}」: {invalid.Error}\n\nこのルールは使われません。このまま保存しますか？",
                    "Fako", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return false;
        }

        var cfg = App.Current.Config;
        try { AutoStart.Set(AutoStartBox.IsChecked == true); }
        catch (Exception ex) { Log.Error("自動起動の設定に失敗", ex); }
        cfg.DoubleClickToHide = DoubleClickBox.IsChecked == true;
        cfg.IconMode = ManagedRadio.IsChecked == true ? DesktopIconMode.FenceManaged : DesktopIconMode.NotManaged;

        cfg.SnapEnabled = SnapBox.IsChecked == true;
        cfg.SnapToScreenEdges = SnapEdgesBox.IsChecked == true;
        cfg.SnapDistance = SnapDistanceSlider.Value;
        cfg.SnapGap = SnapGapSlider.Value;

        cfg.AutoSortEnabled = AutoSortBox.IsChecked == true;
        cfg.Rules = _rules.Select(r => r.ToRule()).ToList();

        App.Current.ApplySettings();
        return true;
    }

    // ------------------------------------------------------------------
    // 自動振り分けルール
    // ------------------------------------------------------------------

    private RuleViewModel NewViewModel(SortRule rule)
    {
        var vm = new RuleViewModel(rule, id => _fences.FirstOrDefault(f => (string)f.Value == id)?.Label);
        vm.PropertyChanged += OnRulePropertyChanged;
        return vm;
    }

    private void RefreshFenceOptions()
    {
        // タブのページも振り分け先にできる（タブの場合は「タブ名（フェンス 何番目のタブ）」）
        var fresh = App.Current.Config.Fences
            .SelectMany(host => host.Pages.Select((page, i) => (host, page, i)))
            .Where(x => x.page.Mode == FenceMode.Shortcuts)
            .Select(x => new Option(x.page.Id, x.host.Tabs.Count == 0
                ? x.page.Title
                : $"{x.page.Title}（タブ {x.i + 1}/{x.host.Tabs.Count + 1}）"))
            .ToList();
        _fences.Clear();
        foreach (var f in fresh) _fences.Add(f);
        foreach (var r in _rules) r.Refresh();
    }

    private void OnRulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == _selected && e.PropertyName is nameof(RuleViewModel.Kind) or nameof(RuleViewModel.Pattern))
            UpdateMatches();
    }

    private void OnRuleSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selected = RuleList.SelectedItem as RuleViewModel;
        UpdateRuleEditor();
    }

    private void UpdateRuleEditor()
    {
        RuleEditor.DataContext = _selected;
        RuleEditor.Visibility = _selected != null ? Visibility.Visible : Visibility.Collapsed;
        NoRuleText.Visibility = _selected == null ? Visibility.Visible : Visibility.Collapsed;
        int i = _selected != null ? _rules.IndexOf(_selected) : -1;
        RemoveRuleButton.IsEnabled = i >= 0;
        UpButton.IsEnabled = i > 0;
        DownButton.IsEnabled = i >= 0 && i < _rules.Count - 1;
        UpdateMatches();
    }

    /// <summary>選んでいるルールに、今のデスクトップのどの項目が当てはまるか。</summary>
    private void UpdateMatches()
    {
        if (_selected == null) return;
        var rule = _selected.ToRule();
        if (RuleEngine.Validate(rule) != null)
        {
            MatchTitle.Text = "条件を入力すると、当てはまる項目がここに表示されます";
            MatchList.Text = "";
            return;
        }
        var fenced = App.Current.AllShortcutPaths();
        var excluded = App.Current.Config.RuleExclusions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = FolderListing.List(FolderListing.UserDesktop, FolderListing.CommonDesktop)
            .Where(p => !fenced.Contains(p) && RuleEngine.Matches(rule, p))
            .ToList();
        int excludedCount = matches.Count(excluded.Contains);
        MatchTitle.Text = $"今デスクトップにある未整理の項目のうち、当てはまるもの: {matches.Count} 件"
                          + (excludedCount > 0 ? $"（うち {excludedCount} 件は手で外したため対象外）" : "");
        MatchList.Text = string.Join("、", matches.Take(12).Select(Path.GetFileName)) + (matches.Count > 12 ? " …" : "");
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        var vm = NewViewModel(new SortRule
        {
            Name = $"ルール {_rules.Count + 1}",
            TargetFenceId = _fences.Count > 0 ? (string)_fences[0].Value : "",
        });
        _rules.Add(vm);
        RuleList.SelectedItem = vm;
        RuleList.ScrollIntoView(vm);
    }

    private void OnRemoveRule(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        int i = _rules.IndexOf(_selected);
        _rules.Remove(_selected);
        if (_rules.Count > 0) RuleList.SelectedIndex = Math.Min(i, _rules.Count - 1);
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        if (_selected == null) return;
        int i = _rules.IndexOf(_selected), j = i + delta;
        if (j < 0 || j >= _rules.Count) return;
        var vm = _selected;
        _rules.Move(i, j);
        RuleList.SelectedItem = vm;
        UpdateRuleEditor();
    }

    private void OnCreateTargetFence(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var title = string.IsNullOrWhiteSpace(_selected.Name) ? "新しいフェンス" : _selected.Name;
        var fence = App.Current.NewFence(FenceMode.Shortcuts, title: title, rename: false);
        RefreshFenceOptions();
        _selected.TargetFenceId = fence.Config.Id;
    }

    private void OnRunNow(object sender, RoutedEventArgs e)
    {
        if (!Apply()) return;
        int n = App.Current.RunRules(force: true);
        RunResultText.Text = n > 0 ? $"{n} 件を振り分けました" : "振り分ける項目はありませんでした";
        UpdateMatches();
    }

    private void OnClearExclusions(object sender, RoutedEventArgs e)
    {
        App.Current.Config.RuleExclusions.Clear();
        App.Current.SaveSoon();
        UpdateExclusionsButton();
        UpdateMatches();
    }

    private void UpdateExclusionsButton()
    {
        int n = App.Current.Config.RuleExclusions.Count;
        ClearExclusionsButton.Content = $"手で外した項目の記録をクリア（{n} 件）";
        ClearExclusionsButton.IsEnabled = n > 0;
    }

    // ------------------------------------------------------------------
    // その他
    // ------------------------------------------------------------------

    private void OnEditDefaultStyle(object sender, RoutedEventArgs e) => StyleEditorWindow.EditDefault(this);

    private void OnOpenConfigFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ConfigStore.Directory);
        Process.Start("explorer.exe", $"\"{ConfigStore.Directory}\"");
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        var path = Log.CurrentPath;
        if (path != null && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
