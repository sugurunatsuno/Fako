using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fako.Models;

namespace Fako;

/// <summary>
/// フェンスのタブ（1 つのフェンスに複数のページ）。
/// ページは中身（種類・アイテム・表示方法・並べ替え・タイトル）を持ち、
/// 位置・大きさ・外観・折りたたみは 1 枚目のページ（Host）のものを使う。
/// </summary>
public partial class FenceWindow
{
    private DispatcherTimer? _tabHoverTimer;
    private FenceConfig? _tabHoverTarget;

    private bool HasTabs => Host.Tabs.Count > 0;

    // ------------------------------------------------------------------
    // タブの表示
    // ------------------------------------------------------------------

    private void UpdateTabStrip()
    {
        TabStrip.Children.Clear();
        if (!HasTabs)
        {
            TabStrip.Visibility = Visibility.Collapsed;
            TitleText.Visibility = TitleEdit.IsVisible ? Visibility.Hidden : Visibility.Visible;
            return;
        }

        TitleText.Visibility = Visibility.Hidden;
        TabStrip.Visibility = TitleEdit.IsVisible ? Visibility.Hidden : Visibility.Visible;
        var s = Host.Style;
        var fg = ColorUtil.Parse(s.TitleForeground, Colors.White);
        double radius = Math.Max(0, Math.Min(6, s.CornerRadius - s.BorderThickness));

        foreach (var page in Pages)
        {
            bool active = page == Config;
            var text = new TextBlock
            {
                Text = page.Title,
                Foreground = new SolidColorBrush(fg),
                FontSize = Math.Max(9, s.TitleFontSize - 1),
                FontWeight = active && s.TitleBold ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = active ? 1 : 0.7,
                Effect = ItemTextEffect,
            };
            var tab = new Border
            {
                Child = text,
                Tag = page,
                Margin = new Thickness(1, 0, 1, 0),
                Padding = new Thickness(6, 0, 6, 0),
                CornerRadius = new CornerRadius(radius, radius, 0, 0),
                Background = active ? new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent,
                BorderBrush = active ? new SolidColorBrush(Color.FromRgb(0x60, 0xAA, 0xFF)) : Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                ToolTip = $"{page.Title}（{ModeLabel(page)}）",
                AllowDrop = true,
                Cursor = Cursors.Arrow,
            };
            tab.MouseEnter += (_, _) => { if (page != Config) tab.Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)); };
            tab.MouseLeave += (_, _) => { if (page != Config) tab.Background = Brushes.Transparent; };
            tab.MouseLeftButtonDown += OnTabMouseLeftButtonDown;
            tab.MouseDown += (_, e) =>
            {
                if (e.ChangedButton == MouseButton.Middle) { CloseTab(page); e.Handled = true; } // 中クリックで閉じる
            };
            // アイテムをドラッグしてタブの上でしばらく止めると、そのタブに切り替わる（別のタブへ移すため）
            tab.DragEnter += (_, e) => StartTabHover(page);
            tab.DragLeave += (_, e) => StopTabHover();
            TabStrip.Children.Add(tab);
        }
    }

    private static string ModeLabel(FenceConfig page) => page.Mode switch
    {
        FenceMode.Portal => "フォルダーポータル",
        FenceMode.Unsorted => "未整理",
        FenceMode.Remote => "SSH",
        _ => "ショートカット",
    };

    private void OnTabMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: FenceConfig page }) return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            ToggleRollup();
            return;
        }
        SwitchTo(page);
        // タブをつかんでドラッグすると、フェンスごと動かせる
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    /// <summary>右クリックされたのがタブなら、そのページ。</summary>
    private static FenceConfig? TabPageFromSource(object? source)
    {
        for (var d = source as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is Border { Tag: FenceConfig page }) return page;
        }
        return null;
    }

    private void StartTabHover(FenceConfig page)
    {
        if (page == Config) return;
        _tabHoverTarget = page;
        _tabHoverTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _tabHoverTimer.Tick -= OnTabHoverTick;
        _tabHoverTimer.Tick += OnTabHoverTick;
        _tabHoverTimer.Stop();
        _tabHoverTimer.Start();
    }

    private void StopTabHover()
    {
        _tabHoverTimer?.Stop();
        _tabHoverTarget = null;
    }

    private void OnTabHoverTick(object? sender, EventArgs e)
    {
        _tabHoverTimer?.Stop();
        if (_tabHoverTarget is { } page && Pages.Contains(page)) SwitchTo(page);
        _tabHoverTarget = null;
    }

    // ------------------------------------------------------------------
    // 切り替え
    // ------------------------------------------------------------------

    public void SwitchTo(FenceConfig page)
    {
        if (page == Config || !Pages.Contains(page)) return;
        EndRename(true);
        LeavePage();
        Config = page;
        Host.ActiveTab = Pages.ToList().IndexOf(page);
        ApplyView();
        SetupWatchers();
        UpdateTabStrip();
        TitleText.Text = Config.Title;
        Reload();
        App.Current.SaveSoon();
    }

    /// <summary>今のページの表示や監視を片付ける（SSH の接続は残す）。</summary>
    private void LeavePage()
    {
        _listCts?.Cancel();
        _remoteDir = null;
        _remoteEntries.Clear();
        ItemsList.UnselectAll();
        _items.Clear();
        DisposeWatchers();
    }

    private void SwitchRelative(int delta)
    {
        var pages = Pages.ToList();
        if (pages.Count < 2) return;
        int i = (pages.IndexOf(Config) + delta + pages.Count) % pages.Count;
        SwitchTo(pages[i]);
    }

    // ------------------------------------------------------------------
    // タブの追加・閉じる・切り離す・結合・並べ替え
    // ------------------------------------------------------------------

    /// <summary>
    /// ページの並びを置き換える。先頭のページがウィンドウの代表（Host）になり、
    /// 代表が変わるときは位置・大きさ・外観・折りたたみを引き継ぐ。
    /// </summary>
    private void SetPages(List<FenceConfig> pages)
    {
        var newHost = pages[0];
        if (newHost != Host)
        {
            newHost.Left = Host.Left;
            newHost.Top = Host.Top;
            newHost.Width = Host.Width;
            newHost.Height = Host.Height;
            newHost.RolledUp = Host.RolledUp;
            newHost.Style = Host.Style;
            App.Current.ReplaceHost(Host, newHost);
            Host.Tabs = new();
            Host.ActiveTab = 0;
            Host = newHost;
        }
        Host.Tabs = pages.Skip(1).ToList();
        foreach (var t in Host.Tabs) t.Tabs = new();
        Host.ActiveTab = Math.Max(0, pages.IndexOf(Config));
        UpdateTabStrip();
        App.Current.SaveSoon();
    }

    public void NewTab()
    {
        var page = new FenceConfig
        {
            Title = $"タブ {Pages.Count + 1}",
            Mode = FenceMode.Shortcuts,
            View = Config.View,
        };
        Host.Tabs.Add(page);
        SwitchTo(page);
        Dispatcher.BeginInvoke(BeginRename, DispatcherPriority.Input);
    }

    public void CloseTab(FenceConfig page, bool confirm = true)
    {
        var pages = Pages.ToList();
        if (pages.Count <= 1)
        {
            App.Current.DeleteFence(this);
            return;
        }
        var msg = $"タブ「{page.Title}」を閉じますか？" +
                  (page.Mode == FenceMode.Shortcuts && page.Items.Count > 0 ? $"\n（登録していた {page.Items.Count} 個のファイル自体は削除されません）" : "");
        if (confirm && MessageBox.Show(msg, "Fako", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        int index = pages.IndexOf(page);
        if (page == Config) SwitchTo(pages[index > 0 ? index - 1 : 1]);
        pages.Remove(page);
        DisposeSession(page.Id);
        SetPages(pages);
        App.Current.OnItemsChanged();
    }

    /// <summary>タブを、別のフェンス（ウィンドウ）として切り離す。</summary>
    public void DetachTab(FenceConfig page)
    {
        var pages = Pages.ToList();
        if (pages.Count <= 1) return;
        int index = pages.IndexOf(page);
        if (page == Config) SwitchTo(pages[index > 0 ? index - 1 : 1]);
        var style = Host.Style.Clone();
        var (left, top, width, height) = (Left, Top, Host.Width, Host.Height);
        pages.Remove(page);
        DisposeSession(page.Id);
        SetPages(pages);

        page.Tabs = new();
        page.ActiveTab = 0;
        page.Left = left + 40;
        page.Top = top + 40;
        page.Width = width;
        page.Height = height;
        page.RolledUp = false;
        page.Style = style;
        App.Current.AddFence(page);
    }

    /// <summary>他のフェンスを、そのページごとこのフェンスのタブにする。</summary>
    public void MergeFrom(FenceWindow other)
    {
        if (other == this) return;
        var incoming = other.Pages.ToList();
        App.Current.RemoveFenceWindow(other);
        var pages = Pages.ToList();
        pages.AddRange(incoming);
        SetPages(pages);
        SwitchTo(incoming[0]);
        App.Current.OnItemsChanged();
    }

    private void MoveTab(int delta)
    {
        var pages = Pages.ToList();
        int i = pages.IndexOf(Config), j = i + delta;
        if (j < 0 || j >= pages.Count) return;
        (pages[i], pages[j]) = (pages[j], pages[i]);
        SetPages(pages);
    }

    /// <summary>テスト用（FAKO_OPEN=tabscript）: タブ操作を順に行い、結果をログに残す。</summary>
    internal void DebugTabScript()
    {
        string State() =>
            $"host={Host.Title} active={Config.Title} pages=[{string.Join(", ", Pages.Select(p => p.Title))}] " +
            $"fences=[{string.Join(", ", App.Current.Config.Fences.Select(f => f.Title + (f.Tabs.Count > 0 ? $"+{f.Tabs.Count}" : "")))}] " +
            $"windows={App.Current.FenceWindows.Count} items={_items.Count} hostLeft={Host.Left:0}";
        Log.Info("[tabscript] start " + State());
        SwitchTo(Pages[1]);
        Log.Info("[tabscript] switch to 2nd: " + State());
        MoveTab(-1);
        Log.Info("[tabscript] move active to front (host change): " + State());
        var detached = Config;
        DetachTab(detached);
        Log.Info("[tabscript] detach active: " + State());
        var other = App.Current.FindFence(detached.Id)!;
        Log.Info($"[tabscript]   detached window: host={other.Host.Title} left={other.Host.Left:0} tabs={other.Host.Tabs.Count}");
        MergeFrom(other);
        Log.Info("[tabscript] merge back: " + State());
        NewTab();
        EndRename(false);
        Log.Info("[tabscript] new tab: " + State());
        CloseTab(Config, confirm: false);
        Log.Info("[tabscript] close new tab: " + State());
        CloseTab(Pages[0], confirm: false);
        Log.Info("[tabscript] close 1st tab (host change): " + State());
    }

    private MenuItem BuildTabMenu()
    {
        var tabs = new MenuItem { Header = "タブ" };
        void Add(string header, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => action();
            tabs.Items.Add(mi);
        }

        var pages = Pages.ToList();
        int index = pages.IndexOf(Config);
        Add("新しいタブ", NewTab);
        Add("このタブを閉じる（中クリック）", () => CloseTab(Config), pages.Count > 1);
        Add("このタブを別のフェンスに切り離す", () => DetachTab(Config), pages.Count > 1);
        tabs.Items.Add(new Separator());
        Add("タブを左へ", () => MoveTab(-1), index > 0);
        Add("タブを右へ", () => MoveTab(1), index >= 0 && index < pages.Count - 1);
        tabs.Items.Add(new Separator());

        var merge = new MenuItem { Header = "他のフェンスをタブとして結合" };
        foreach (var other in App.Current.FenceWindows.Where(w => w != this))
        {
            var label = other.HasTabs ? $"{other.Host.Title} ほか {other.Host.Tabs.Count} タブ" : other.Host.Title;
            var mi = new MenuItem { Header = label };
            mi.Click += (_, _) => MergeFrom(other);
            merge.Items.Add(mi);
        }
        merge.IsEnabled = merge.Items.Count > 0;
        tabs.Items.Add(merge);
        return tabs;
    }
}
