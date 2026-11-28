using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Fako.Interop;
using Fako.Models;
using Microsoft.Win32;
using static Fako.Interop.NativeMethods;

namespace Fako;

public partial class FenceWindow : Window
{
    /// <summary>フェンス間ドラッグで「どのフェンスから来たか」を運ぶデータ形式。</summary>
    public const string SourceFenceFormat = "Fako.SourceFence";
    /// <summary>フェンス間ドラッグで運ぶパス（ごみ箱などのシェル項目も含む）。</summary>
    public const string PathsFormat = "Fako.Paths";
    private const double MinExpandedHeight = 70;

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    internal static readonly (string Name, string Color)[] ColorPresets =
    {
        ("グラファイト", "#A0202428"),
        ("ミッドナイト", "#A0182640"),
        ("フォレスト", "#A0183A28"),
        ("ワイン", "#A0401A24"),
        ("プラム", "#A0302040"),
        ("アンバー", "#A0503A10"),
        ("スレート", "#A0485868"),
    };

    private static readonly (string Name, byte Alpha)[] OpacityPresets =
    {
        ("ほぼ透明", 0x30), ("薄め", 0x60), ("標準", 0xA0), ("濃いめ", 0xD0), ("不透明", 0xF0),
    };

    private static readonly (FenceView View, string Label)[] ViewLabels =
    {
        (FenceView.ExtraLarge, "特大アイコン"),
        (FenceView.Large, "大アイコン"),
        (FenceView.Medium, "中アイコン"),
        (FenceView.Small, "小アイコン"),
        (FenceView.List, "一覧"),
        (FenceView.Details, "詳細"),
        (FenceView.Tiles, "並べて表示"),
    };

    private static readonly (SortKey Key, string Label)[] SortLabels =
    {
        (SortKey.Name, "名前"),
        (SortKey.Modified, "更新日時"),
        (SortKey.Type, "種類"),
        (SortKey.Size, "サイズ"),
    };

    // ---- テンプレートから参照する表示用の値 ----
    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(FenceWindow), new PropertyMetadata(40.0));
    public static readonly DependencyProperty TileWidthProperty =
        DependencyProperty.Register(nameof(TileWidth), typeof(double), typeof(FenceWindow), new PropertyMetadata(72.0));
    public static readonly DependencyProperty DateColWidthProperty =
        DependencyProperty.Register(nameof(DateColWidth), typeof(double), typeof(FenceWindow), new PropertyMetadata(0.0));
    public static readonly DependencyProperty TypeColWidthProperty =
        DependencyProperty.Register(nameof(TypeColWidth), typeof(double), typeof(FenceWindow), new PropertyMetadata(0.0));
    public static readonly DependencyProperty SizeColWidthProperty =
        DependencyProperty.Register(nameof(SizeColWidth), typeof(double), typeof(FenceWindow), new PropertyMetadata(0.0));

    public double IconSize { get => (double)GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }
    public double TileWidth { get => (double)GetValue(TileWidthProperty); set => SetValue(TileWidthProperty, value); }
    public double DateColWidth { get => (double)GetValue(DateColWidthProperty); set => SetValue(DateColWidthProperty, value); }
    public double TypeColWidth { get => (double)GetValue(TypeColWidthProperty); set => SetValue(TypeColWidthProperty, value); }
    public double SizeColWidth { get => (double)GetValue(SizeColWidthProperty); set => SetValue(SizeColWidthProperty, value); }

    /// <summary>
    /// このウィンドウの代表（1 枚目のページ）。位置・大きさ・外観・折りたたみはここに保存する。
    /// タブを閉じたり切り離したりして 1 枚目が変わると差し替わる。
    /// </summary>
    public FenceConfig Host { get; private set; }

    /// <summary>表示中のページ（タブが無ければ Host と同じ）。中身・表示方法・並べ替えはここ。</summary>
    public FenceConfig Config { get; private set; }

    public IReadOnlyList<FenceConfig> Pages => Host.Pages.ToList();

    /// <summary>取得するアイコン画像のピクセル数（表示サイズ × 高 DPI の余裕）。</summary>
    private int _iconPx = 64;

    private readonly ObservableCollection<FenceItem> _items = new();
    private readonly ICollectionView _view;
    private string _searchText = "";
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _afterShellCommandTimer;
    private readonly List<FileSystemWatcher> _watchers = new();
    private bool _hoverExpanded;
    private bool _menuOpen;
    private Point _dragStart;
    private FenceItem? _dragItem;

    // ドラッグによる範囲選択（マーキー）
    private Point _marqueeStart;
    private bool _marqueeActive;
    private bool _marqueeVisible;
    private bool _marqueeAdditive;
    private readonly HashSet<FenceItem> _marqueeBaseSelection = new();
    private const double MarqueeThreshold = 4;

    /// <summary>タイプアヘッド: 直近に入力した文字列と、その最後の入力時刻。</summary>
    private string _typeBuffer = "";
    private DateTime _typeBufferAt;
    private static readonly TimeSpan TypeBufferTimeout = TimeSpan.FromMilliseconds(900);

    /// <summary>シングルクリックで開いた直後のダブルクリックで二重に開かないための記録。</summary>
    private string? _lastMouseOpenPath;
    private DateTime _lastMouseOpenAt;

    private static readonly System.Windows.Media.Effects.BlurEffect PrivacyBlur = CreatePrivacyBlur();

    public FenceWindow(FenceConfig config)
    {
        InitializeComponent();
        Host = config;
        Config = config.Pages.ElementAt(Math.Clamp(config.ActiveTab, 0, config.Tabs.Count));
        EnsureOnScreen();

        Left = config.Left;
        Top = config.Top;
        Width = config.Width;
        Height = config.Height;
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = o => o is FenceItem item && MatchesSearch(item);
        ItemsList.ItemsSource = _view;

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (IsMouseOver || IsKeyboardFocusWithin || _menuOpen || TitleEdit.IsVisible) return;
            _hoverExpanded = false;
            ApplyRollup();
        };

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); Reload(); };

        _afterShellCommandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _afterShellCommandTimer.Tick += (_, _) =>
        {
            _afterShellCommandTimer.Stop();
            AfterShellCommand();
        };

        SourceInitialized += OnSourceInitialized;
        LocationChanged += OnLocationChanged;
        SizeChanged += OnSizeChanged;
        MouseEnter += (_, _) => { HoverExpand(); ApplyPrivacy(); };
        MouseLeave += (_, _) =>
        {
            ScheduleCollapse();
            Dispatcher.BeginInvoke((Action)ApplyPrivacy);
        };
        Activated += (_, _) =>
        {
            // フェンスをクリックしたら、そのまま矢印キー・文字入力で操作できるようにする（検索入力中は奪わない）
            if (!ItemsList.IsKeyboardFocusWithin && !SearchBox.IsKeyboardFocusWithin && !TitleEdit.IsVisible)
                ItemsList.Focus();
        };
        IsKeyboardFocusWithinChanged += (_, _) =>
        {
            ApplyPrivacy();
            if (IsKeyboardFocusWithin) return;
            _typeBuffer = "";
            if (Host.RolledUp && !IsMouseOver && !_menuOpen && !TitleEdit.IsVisible)
            {
                _hoverExpanded = false;
                ApplyRollup();
            }
        };
        Closed += (_, _) =>
        {
            CancelPendingHide();
            DisposeWatchers();
            DisposeRemote();
        };

        InitializeDesktopBackground();
        ApplyAppearance();
        ApplyView();
        SetupWatchers();
        Reload();
        ApplyRollup();
    }

    // ------------------------------------------------------------------
    // 表示方法（アイコン／一覧／詳細…）
    // ------------------------------------------------------------------

    private void ApplyView()
    {
        var (iconSize, tileWidth, template, panel, style, px) = Config.View switch
        {
            FenceView.ExtraLarge => (96.0, 124.0, "IconTemplate", "WrapPanelH", "IconItemStyle", 192),
            FenceView.Large => (64.0, 96.0, "IconTemplate", "WrapPanelH", "IconItemStyle", 128),
            FenceView.Small => (16.0, 170.0, "RowTemplate", "WrapPanelH", "RowItemStyle", 32),
            FenceView.List => (16.0, 190.0, "RowTemplate", "WrapPanelV", "RowItemStyle", 32),
            FenceView.Details => (16.0, 0.0, "DetailsTemplate", "StackPanelV", "RowItemStyle", 32),
            FenceView.Tiles => (40.0, 230.0, "TilesTemplate", "WrapPanelH", "IconItemStyle", 64),
            _ => (40.0, 72.0, "IconTemplate", "WrapPanelH", "IconItemStyle", 64),
        };
        IconSize = iconSize;
        TileWidth = tileWidth;
        _iconPx = px;

        // 生成済みのコンテナに新しいスタイルを当てるため、いったん外して付け直す
        ItemsList.ItemsSource = null;
        ItemsList.ItemTemplate = (DataTemplate)FindResource(template);
        ItemsList.ItemsPanel = (ItemsPanelTemplate)FindResource(panel);
        ItemsList.ItemContainerStyle = (Style)FindResource(style);
        bool horizontal = Config.View == FenceView.List;
        ScrollViewer.SetHorizontalScrollBarVisibility(ItemsList, horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(ItemsList, horizontal ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        ItemsList.ItemsSource = _view;

        DetailsHeader.Visibility = Config.View == FenceView.Details ? Visibility.Visible : Visibility.Collapsed;
        UpdateDetailColumns();
        UpdateHeaderLabels();
    }

    public void SetView(FenceView view)
    {
        if (Config.View == view) return;
        Config.View = view;
        ApplyView();
        Reload();
        App.Current.SaveSoon();
    }

    /// <summary>詳細表示の列を、フェンスの幅に収まる分だけ出す（名前 → 更新日時 → サイズ → 種類の優先順）。</summary>
    private void UpdateDetailColumns()
    {
        if (Config.View != FenceView.Details) return;
        double rest = Math.Max(0, (ActualWidth > 0 ? ActualWidth : Width) - 40) - 150; // 名前に最低 150
        DateColWidth = rest >= 125 ? 125 : 0;
        rest -= DateColWidth;
        SizeColWidth = rest >= 80 ? 80 : 0;
        rest -= SizeColWidth;
        TypeColWidth = rest >= 120 ? Math.Min(180, rest) : 0;
    }

    /// <summary>並べ替えの実効値（ポータル／未整理には「手動」が無いので名前順）。</summary>
    private SortKey EffectiveSortKey =>
        Config.Mode == FenceMode.Shortcuts || Config.SortKey != SortKey.Manual ? Config.SortKey : SortKey.Name;

    private IEnumerable<FenceItem> Sort(List<FenceItem> items)
    {
        var key = EffectiveSortKey;
        if (key == SortKey.Manual) return items;

        bool desc = Config.SortDescending;
        var logical = FolderListing.LogicalComparer.Instance;
        // エクスプローラーと同じく、フォルダーを先にまとめる（降順なら後ろ）
        // ごみ箱などのシェル項目は常に先頭（エクスプローラーのデスクトップと同じ）
        var q = items.OrderBy(i => FolderListing.IsShellItem(i.Path) ? 0 : 1)
            .ThenBy(i => desc ? (i.IsFolder ? 1 : 0) : (i.IsFolder ? 0 : 1));
        q = key switch
        {
            SortKey.Modified => desc ? q.ThenByDescending(i => i.Modified ?? DateTime.MinValue) : q.ThenBy(i => i.Modified ?? DateTime.MinValue),
            SortKey.Type => desc ? q.ThenByDescending(i => i.TypeName, StringComparer.CurrentCultureIgnoreCase) : q.ThenBy(i => i.TypeName, StringComparer.CurrentCultureIgnoreCase),
            SortKey.Size => desc ? q.ThenByDescending(i => i.Size ?? -1) : q.ThenBy(i => i.Size ?? -1),
            _ => desc ? q.ThenByDescending(i => i.Name, logical) : q.ThenBy(i => i.Name, logical),
        };
        return key is SortKey.Modified or SortKey.Type or SortKey.Size
            ? q.ThenBy(i => i.Name, logical)
            : q;
    }

    private void SetSort(SortKey key, bool? descending = null)
    {
        if (descending is bool d)
        {
            Config.SortDescending = d;
        }
        else if (EffectiveSortKey == key)
        {
            Config.SortDescending = !Config.SortDescending; // 同じ列をもう一度 → 昇順／降順の切り替え
        }
        else
        {
            Config.SortDescending = key is SortKey.Modified or SortKey.Size; // 日付・サイズは新しい／大きい順から
        }
        Config.SortKey = key;
        UpdateHeaderLabels();
        Reload();
        App.Current.SaveSoon();
    }

    private void UpdateHeaderLabels()
    {
        var key = EffectiveSortKey;
        string Label(SortKey k, string text) => key == k ? $"{text} {(Config.SortDescending ? "▼" : "▲")}" : text;
        HeaderName.Content = Label(SortKey.Name, "名前");
        HeaderDate.Content = Label(SortKey.Modified, "更新日時");
        HeaderType.Content = Label(SortKey.Type, "種類");
        HeaderSize.Content = Label(SortKey.Size, "サイズ");
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<SortKey>(tag, out var key))
            SetSort(key);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Ctrl+ホイール: エクスプローラーと同じく表示を切り替える
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            int i = Array.FindIndex(ViewLabels, v => v.View == Config.View);
            i = Math.Clamp(i + (e.Delta > 0 ? -1 : 1), 0, ViewLabels.Length - 1);
            SetView(ViewLabels[i].View);
            e.Handled = true;
            return;
        }

        // 一覧表示は横に並ぶので、ホイールで横スクロールする
        if (Config.View == FenceView.List && FindDescendant<ScrollViewer>(ItemsList) is { } sv)
        {
            for (int n = 0; n < 3; n++)
            {
                if (e.Delta > 0) sv.LineLeft();
                else sv.LineRight();
            }
            e.Handled = true;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }

    // ------------------------------------------------------------------
    // デスクトップへの張り付き
    // ------------------------------------------------------------------

    // 張り付きの方針:
    //   常にデスクトップ（Progman / WorkerW）の直上、かつ他の通常アプリより背面に固定する。
    //   HWND_BOTTOM を使うとデスクトップ（Progman）の背後に回って見えなくなるため使わない。
    //   また HWND_TOP を使うと他のアプリより前面に出てしまうため、デスクトップから上方向へ辿り
    //   自プロセスのフェンスを除いた最初のウィンドウを基準にする（無ければ HWND_TOP）。
    //   Show() 前および WM_WINDOWPOSCHANGING でこの Z オーダーを強制することで、
    //   表示の瞬間に他のウィンドウの前面へ 1 フレーム出るちらつきを防ぐ。
    //
    // ※ Progman を所有者にする定番の手は、Windows 10/11 対応を manifest で宣言した
    //   プロセスでは SetWindowLongPtr(GWLP_HWNDPARENT) が ERROR_INVALID_PARAMETER で拒否されるため使わない。
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // タスクバー／Alt+Tab に出さない
        long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        ex = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        // 初回 Show() で可視化される前にデスクトップ直上へ配置しておく
        if (!_peek)
            ApplyDesktopZOrder(requireVisible: false);
    }

    private IntPtr _hwnd;
    private bool _peek;
    private bool _peekExpanded;
    private readonly FadeHideCoordinator _fadeHide = new();
    private bool _hideRenderHooked;

    public IntPtr Handle => _hwnd;

    /// <summary>デスクトップの直上（他アプリより背面）に配置する。</summary>
    public void SetRaised(bool raised) => ApplyDesktopZOrder(requireVisible: true);

    private void ApplyDesktopZOrder(bool requireVisible)
    {
        if (_hwnd == IntPtr.Zero || _peek) return;
        if (requireVisible && !IsVisible) return;

        var target = ResolveDesktopTarget();
        if (target.AlreadyInPlace || target.InsertAfter == _hwnd) return;
        SetWindowPos(_hwnd, target.InsertAfter, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    private DesktopZTarget ResolveDesktopTarget() =>
        FenceZOrder.ResolveDesktopTarget(_hwnd, Desktop.GetDesktopRoot(), App.Current.FenceWindows);

    /// <summary>Peek 中は全ウィンドウの上（最前面）に出し、終わったら通常の Z オーダーへ戻す。</summary>
    public void SetPeek(bool on)
    {
        if (_peek == on) return;
        _peek = on;

        if (on)
        {
            if (Host.RolledUp && !_hoverExpanded)
            {
                _peekExpanded = true;
                _hoverExpanded = true;
                ApplyRollup();
            }
        }
        else if (_peekExpanded)
        {
            _peekExpanded = false;
            if (!IsMouseOver)
            {
                _hoverExpanded = false;
                ApplyRollup();
            }
        }

        if (_hwnd == IntPtr.Zero || !IsVisible) return;
        if (on)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
        else
        {
            // WS_EX_TOPMOST を確実に解除してから、通常のデスクトップ直上へ戻す
            SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            ApplyDesktopZOrder(requireVisible: true);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 標準の右クリックメニュー表示中: サブメニュー（送る・新規作成など）やアイコンの描画をシェルに任せる
        if (ShellContextMenu.Active is { } shellMenu && shellMenu.HandleMessage(msg, wParam, lParam, out var menuResult))
        {
            handled = true;
            return menuResult;
        }

        if (msg == WM_WINDOWPOSCHANGING && !_peek)
        {
            // Z オーダーが変わるときは常にデスクトップ直上へ補正（Show() 直後やクリック時も他アプリの前に出さない）
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & (SWP_NOZORDER | SWP_HIDEWINDOW)) == 0)
            {
                var target = ResolveDesktopTarget();
                if (FenceZOrder.TryAdjustWindowPos(ref pos, isPeek: false, target))
                    Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        else if ((msg == Snapper.WM_MOVING || msg == Snapper.WM_SIZING) && App.Current.Config.SnapEnabled
                 && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) // Alt を押している間は吸着しない
        {
            var cfg = App.Current.Config;
            var opt = new Snapper.Options(cfg.SnapToScreenEdges, cfg.SnapDistance, cfg.SnapGap);
            var r = Marshal.PtrToStructure<Snapper.RECT>(lParam);
            var others = App.Current.FenceHandles();
            if (msg == Snapper.WM_MOVING) Snapper.SnapMove(ref r, hwnd, others, opt);
            else Snapper.SnapSize(ref r, wParam.ToInt32(), hwnd, others, opt);
            Marshal.StructureToPtr(r, lParam, false);
            handled = true;
            return new IntPtr(1);
        }
        return IntPtr.Zero;
    }

    private void EnsureOnScreen()
    {
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;
        bool visible = Host.Left + 60 < vr && Host.Left + Host.Width - 60 > vl
                       && Host.Top >= vt - 10 && Host.Top + TitleHeight < vb;
        if (!visible)
        {
            var wa = SystemParameters.WorkArea;
            Host.Left = wa.Left + 80;
            Host.Top = wa.Top + 80;
        }
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        Host.Left = Left;
        Host.Top = Top;
        App.Current.SaveSoon();
        App.Current.GroupDragMoved(this);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Host.Width = ActualWidth;
        bool collapsed = Host.RolledUp && !_hoverExpanded;
        if (!collapsed && ActualHeight > TitleHeight + 2)
            Host.Height = ActualHeight;
        if (e.WidthChanged) UpdateDetailColumns();
        App.Current.SaveSoon();
    }

    // ------------------------------------------------------------------
    // 見た目・折りたたみ
    // ------------------------------------------------------------------

    // ---- 外観（FenceStyle）から作る、テンプレート用の値 ----
    public static readonly DependencyProperty ItemForegroundProperty =
        DependencyProperty.Register(nameof(ItemForeground), typeof(Brush), typeof(FenceWindow), new PropertyMetadata(Brushes.White));
    public static readonly DependencyProperty ItemSubForegroundProperty =
        DependencyProperty.Register(nameof(ItemSubForeground), typeof(Brush), typeof(FenceWindow), new PropertyMetadata(Brushes.White));
    public static readonly DependencyProperty ItemFontSizeProperty =
        DependencyProperty.Register(nameof(ItemFontSize), typeof(double), typeof(FenceWindow), new PropertyMetadata(12.0));
    public static readonly DependencyProperty ItemSubFontSizeProperty =
        DependencyProperty.Register(nameof(ItemSubFontSize), typeof(double), typeof(FenceWindow), new PropertyMetadata(11.0));
    public static readonly DependencyProperty ItemNameMaxHeightProperty =
        DependencyProperty.Register(nameof(ItemNameMaxHeight), typeof(double), typeof(FenceWindow), new PropertyMetadata(32.0));
    public static readonly DependencyProperty ItemTextEffectProperty =
        DependencyProperty.Register(nameof(ItemTextEffect), typeof(System.Windows.Media.Effects.Effect), typeof(FenceWindow));

    public Brush ItemForeground { get => (Brush)GetValue(ItemForegroundProperty); set => SetValue(ItemForegroundProperty, value); }
    public Brush ItemSubForeground { get => (Brush)GetValue(ItemSubForegroundProperty); set => SetValue(ItemSubForegroundProperty, value); }
    public double ItemFontSize { get => (double)GetValue(ItemFontSizeProperty); set => SetValue(ItemFontSizeProperty, value); }
    public double ItemSubFontSize { get => (double)GetValue(ItemSubFontSizeProperty); set => SetValue(ItemSubFontSizeProperty, value); }
    public double ItemNameMaxHeight { get => (double)GetValue(ItemNameMaxHeightProperty); set => SetValue(ItemNameMaxHeightProperty, value); }
    public System.Windows.Media.Effects.Effect? ItemTextEffect
    {
        get => (System.Windows.Media.Effects.Effect?)GetValue(ItemTextEffectProperty);
        set => SetValue(ItemTextEffectProperty, value);
    }

    /// <summary>折りたたんだときの高さ（タイトルバー＋上下の枠）。</summary>
    private double TitleHeight => TitleBar.Height + Host.Style.BorderThickness * 2;

    /// <summary>外観設定をウィンドウに反映する（外観の設定画面からのプレビューにも使う）。</summary>
    public void ApplyAppearance()
    {
        var s = Host.Style;
        Root.BorderBrush = new SolidColorBrush(ColorUtil.Parse(s.BorderColor, Colors.Transparent));
        Root.BorderThickness = new Thickness(s.BorderThickness);
        Root.CornerRadius = new CornerRadius(s.CornerRadius);
        UpdateDesktopBackground();

        TitleBar.Height = Math.Round(Math.Max(20, s.TitleFontSize * 2));
        TitleBar.Background = s.TitleBackground is { Length: > 0 } tb
            ? new SolidColorBrush(ColorUtil.Parse(tb, Colors.Transparent))
            : new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        TitleText.Text = Config.Title;
        TitleText.Foreground = new SolidColorBrush(ColorUtil.Parse(s.TitleForeground, Colors.White));
        TitleText.FontSize = s.TitleFontSize;
        TitleText.FontWeight = s.TitleBold ? FontWeights.SemiBold : FontWeights.Normal;
        TitleText.HorizontalAlignment = s.TitleAlignment switch
        {
            TitleAlign.Left => HorizontalAlignment.Left,
            TitleAlign.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };

        var text = ColorUtil.Parse(s.TextColor, Colors.White);
        ItemForeground = new SolidColorBrush(text);
        ItemSubForeground = new SolidColorBrush(Color.FromArgb((byte)(text.A * 0.78), text.R, text.G, text.B));
        ItemFontSize = s.ItemFontSize;
        ItemSubFontSize = Math.Max(8, s.ItemFontSize - 1);
        ItemNameMaxHeight = Math.Ceiling(s.ItemFontSize * 1.34 * 2) + 1; // 2 行まで
        ItemTextEffect = s.TextShadow ? (System.Windows.Media.Effects.Effect)FindResource("TextShadow") : null;

        UpdateTabStrip();
        ApplyRollup();
        ApplyPrivacy();
    }

    private static System.Windows.Media.Effects.BlurEffect CreatePrivacyBlur()
    {
        var blur = new System.Windows.Media.Effects.BlurEffect
        {
            Radius = 14,
            KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
            RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
        };
        blur.Freeze();
        return blur;
    }

    /// <summary>設定画面・トレイからプライバシーモードを切り替えたときに呼ぶ。</summary>
    public void RefreshPrivacy() => ApplyPrivacy();

    /// <summary>
    /// プライバシーモード: マウスもキーボードも乗っていない間は、中身と名前をぼかす。
    /// 画面共有中にデスクトップを映しても、ファイル名やアイコンが読めないようにする。
    /// </summary>
    private void ApplyPrivacy()
    {
        bool hide = App.Current.Config.PrivacyMode
                    && !IsMouseOver && !IsKeyboardFocusWithin && !_menuOpen && !TitleEdit.IsVisible;
        ContentArea.Effect = hide ? PrivacyBlur : null;
        ContentArea.Opacity = hide ? 0.35 : 1.0;
        TabStrip.Effect = hide ? PrivacyBlur : null;
        TabStrip.Opacity = hide ? 0.35 : 1.0;
        TitleText.Text = hide && !TabStrip.IsVisible ? "•••" : Config.Title;
    }

    private void ApplyRollup()
    {
        bool collapsed = Host.RolledUp && !_hoverExpanded;
        ContentArea.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        double inner = Math.Max(0, Host.Style.CornerRadius - Host.Style.BorderThickness);
        TitleBar.CornerRadius = collapsed ? new CornerRadius(inner) : new CornerRadius(inner, inner, 0, 0);
        if (collapsed)
        {
            MinHeight = TitleHeight;
            MaxHeight = TitleHeight;
            Height = TitleHeight;
        }
        else
        {
            MaxHeight = double.PositiveInfinity;
            Height = Math.Max(MinExpandedHeight, Host.Height);
            MinHeight = MinExpandedHeight;
        }
    }

    private void HoverExpand()
    {
        _collapseTimer.Stop();
        if (Host.RolledUp && !_hoverExpanded)
        {
            _hoverExpanded = true;
            ApplyRollup();
        }
    }

    private void ScheduleCollapse()
    {
        if (Host.RolledUp && _hoverExpanded)
        {
            _collapseTimer.Stop();
            _collapseTimer.Start();
        }
    }

    public void ToggleRollup()
    {
        Host.RolledUp = !Host.RolledUp;
        _hoverExpanded = false;
        ApplyRollup();
        App.Current.SaveSoon();
    }

    public void FadeTo(bool visible)
    {
        int gen = _fadeHide.BeginFade();
        UnhookPendingHideRender();

        if (!visible && !IsVisible) return;

        var anim = new DoubleAnimation(visible ? 1.0 : 0.0, TimeSpan.FromMilliseconds(160));
        if (visible)
        {
            if (!IsVisible)
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = 0;
                if (_hwnd != IntPtr.Zero && !_peek)
                    ApplyDesktopZOrder(requireVisible: false);
                Show();
            }
            if (!_peek)
                ApplyDesktopZOrder(requireVisible: true);
        }
        else
        {
            anim.Completed += (_, _) =>
            {
                // Opacity == 0 の最終フレームが合成・提示されてから Hide() する
                if (Opacity == 0 && _fadeHide.TryScheduleHide(gen))
                    HookPendingHideRender();
            };
        }
        BeginAnimation(OpacityProperty, anim);
    }

    private void HookPendingHideRender()
    {
        if (_hideRenderHooked) return;
        _hideRenderHooked = true;
        CompositionTarget.Rendering += OnPendingHideRender;
    }

    private void UnhookPendingHideRender()
    {
        if (!_hideRenderHooked) return;
        _hideRenderHooked = false;
        CompositionTarget.Rendering -= OnPendingHideRender;
    }

    private void CancelPendingHide()
    {
        _fadeHide.Cancel();
        UnhookPendingHideRender();
    }

    private void OnPendingHideRender(object? sender, EventArgs e)
    {
        if (!_fadeHide.OnFrameRendered())
        {
            if (!_fadeHide.IsHidePending) UnhookPendingHideRender();
            return;
        }
        UnhookPendingHideRender();
        if (IsVisible && Opacity == 0) Hide();
    }

    // ------------------------------------------------------------------
    // 中身
    // ------------------------------------------------------------------

    public void Reload()
    {
        _typeBuffer = "";
        if (IsRemote)
        {
            ApplyAppsUi();
            ReloadRemote();
            return;
        }
        if (IsApps)
        {
            ReloadApps();
            return;
        }
        ApplyRemoteUi();
        ApplyAppsUi();

        IEnumerable<string> paths = Config.Mode switch
        {
            FenceMode.Portal => FolderListing.List(Config.PortalPath),
            FenceMode.Unsorted => UnsortedPaths(),
            FenceMode.Recent => FolderListing.RecentFiles(Config.RecentHours, 100),
            _ => Config.Items,
        };

        var selected = ItemsList.SelectedItems.Cast<FenceItem>().Select(i => i.Path).ToHashSet(PathComparer);
        var loaded = paths.Select(p => ShellIcons.CreateItem(p, _iconPx)).ToList();
        _items.Clear();
        foreach (var item in Sort(loaded))
            _items.Add(item);
        foreach (var item in _items.Where(i => selected.Contains(i.Path)))
            ItemsList.SelectedItems.Add(item);

        EmptyHint.Text = DefaultEmptyHint();
        UpdateEmptyHint();
    }

    private string DefaultEmptyHint() => Config.Mode switch
    {
        FenceMode.Portal when string.IsNullOrEmpty(Config.PortalPath) || !Directory.Exists(Config.PortalPath)
            => "フォルダーが見つかりません\n右クリック →「種類」から選び直してください",
        FenceMode.Portal => "このフォルダーは空です",
        FenceMode.Unsorted => "デスクトップは片付いています ✨",
        FenceMode.Recent => "最近のファイルはありません",
        FenceMode.Apps => "インストール済みのアプリが見つかりません",
        _ => "ここにファイルやフォルダーをドロップ\n（元の場所からは移動しません）",
    };

    /// <summary>空のヒント。絞り込み中は「一致なし」を出す。</summary>
    private void UpdateEmptyHint()
    {
        if (_items.Count == 0)
        {
            EmptyHint.Text = DefaultEmptyHint();
            EmptyHint.Visibility = Visibility.Visible;
        }
        else if (_view.IsEmpty)
        {
            EmptyHint.Text = _searchText.Length == 0
                ? DefaultEmptyHint()
                : $"「{_searchText}」に一致する項目がありません";
            EmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyHint.Visibility = Visibility.Collapsed;
        }
    }

    // ------------------------------------------------------------------
    // 検索（Ctrl+F）
    // ------------------------------------------------------------------

    private bool MatchesSearch(FenceItem item)
    {
        if (_searchText.Length == 0) return true;
        var compare = System.Globalization.CultureInfo.CurrentCulture.CompareInfo;
        const System.Globalization.CompareOptions options =
            System.Globalization.CompareOptions.IgnoreCase
            | System.Globalization.CompareOptions.IgnoreKanaType
            | System.Globalization.CompareOptions.IgnoreWidth;
        return compare.IndexOf(item.Name, _searchText, options) >= 0;
    }

    public void ToggleSearch()
    {
        if (SearchBar.IsVisible)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            return;
        }
        SearchBar.Visibility = Visibility.Visible;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void CloseSearch()
    {
        if (ShouldKeepSearchBarOpen)
        {
            if (SearchBox.Text.Length > 0) SearchBox.Text = "";
            _searchText = "";
            _view.Refresh();
            UpdateSearchPlaceholder();
            UpdateEmptyHint();
            ItemsList.Focus();
            return;
        }
        SearchBar.Visibility = Visibility.Collapsed;
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        _searchText = "";
        _view.Refresh();
        UpdateSearchPlaceholder();
        UpdateEmptyHint();
        ItemsList.Focus();
    }

    private FenceItem? FirstVisibleItem()
    {
        foreach (var o in _view)
        {
            if (o is FenceItem item) return item;
        }
        return null;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchPlaceholder();
        _searchText = SearchBox.Text.Trim();
        _view.Refresh();
        if (ItemsList.SelectedItem is FenceItem selected && MatchesSearch(selected) && _view.Contains(selected))
        {
            ItemsList.ScrollIntoView(selected);
        }
        else
        {
            ItemsList.SelectedItem = FirstVisibleItem();
            if (ItemsList.SelectedItem is FenceItem first) ItemsList.ScrollIntoView(first);
        }
        UpdateEmptyHint();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && HasTabs)
        {
            SwitchRelative(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            if (IsApps)
            {
                ShellApps.InvalidateCache();
                ShellIcons.InvalidateApps();
            }
            else
            {
                ShellIcons.Invalidate(_items.Select(i => i.Path));
            }
            Reload();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (ItemsList.SelectedItem is FenceItem item)
            {
                OpenItem(item);
                if (IsApps)
                {
                    if (ShouldKeepSearchBarOpen)
                    {
                        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
                    }
                    else
                    {
                        CloseSearch();
                    }
                }
            }
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Up)
        {
            MoveSelection(e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
        }
    }

    private void OnCloseSearch(object sender, RoutedEventArgs e) => CloseSearch();

    private void MoveSelection(int delta)
    {
        int count = ItemsList.Items.Count;
        if (count == 0) return;
        ItemsList.SelectedIndex = Math.Clamp(ItemsList.SelectedIndex + delta, 0, count - 1);
        if (ItemsList.SelectedItem is FenceItem item) ItemsList.ScrollIntoView(item);
    }

    private static IEnumerable<string> UnsortedPaths()
    {
        var fenced = App.Current.AllShortcutPaths();
        // ごみ箱・PC など（Windows の「デスクトップ アイコンの設定」で表示にしているもの）も含める
        return FolderListing.VisibleDesktopShellItems()
            .Concat(FolderListing.List(FolderListing.UserDesktop, FolderListing.CommonDesktop))
            .Where(p => !fenced.Contains(p));
    }

    private IEnumerable<string> WatchedDirectories() => Config.Mode switch
    {
        FenceMode.Portal when !string.IsNullOrEmpty(Config.PortalPath) => new[] { Config.PortalPath! },
        FenceMode.Unsorted => new[] { FolderListing.UserDesktop, FolderListing.CommonDesktop },
        FenceMode.Recent => FolderListing.RecentRoots(),
        FenceMode.Apps => FolderListing.StartMenuProgramRoots(),
        _ => Array.Empty<string>(),
    };

    private void SetupWatchers()
    {
        DisposeWatchers();
        foreach (var dir in WatchedDirectories().Distinct(PathComparer))
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                var w = new FileSystemWatcher(dir)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes
                                   | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = Config.Mode == FenceMode.Apps,
                };
                w.Created += OnFileSystemChanged;
                w.Deleted += OnFileSystemChanged;
                w.Renamed += OnFileSystemChanged;
                w.Changed += OnFileSystemChanged;
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception)
            {
                // 監視できなくても表示はできる（手動で更新）
            }
        }
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        var changedPaths = e is RenamedEventArgs r ? new[] { r.OldFullPath, r.FullPath } : new[] { e.FullPath };

        Dispatcher.BeginInvoke(() =>
        {
            if (IsApps)
            {
                ShellApps.InvalidateCache();
                ShellIcons.InvalidateApps();
            }
            else
            {
                ShellIcons.Invalidate(changedPaths);
            }
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }

    private void DisposeWatchers()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }

    /// <summary>他のフェンスへ移されたアイテムをこのフェンスから外す。</summary>
    public void RemovePaths(IEnumerable<string> paths)
    {
        if (Config.Mode != FenceMode.Shortcuts) return;
        var set = paths.ToHashSet(PathComparer);
        if (Config.Items.RemoveAll(set.Contains) > 0)
            Reload();
    }

    private void RemoveSelected()
    {
        if (Config.Mode != FenceMode.Shortcuts) return;
        var paths = ItemsList.SelectedItems.Cast<FenceItem>().Select(i => i.Path).ToList();
        if (paths.Count == 0) return;
        App.Current.RecordRemoval(Config, paths);
        RemovePaths(paths);
        App.Current.ExcludeFromRules(paths); // 手で外したものは自動振り分けで戻さない
        App.Current.OnItemsChanged();
    }

    private void ChangeMode(FenceMode mode)
    {
        if (mode == FenceMode.Portal)
        {
            var dlg = new OpenFolderDialog { Title = "フェンスに表示するフォルダーを選択" };
            if (!string.IsNullOrEmpty(Config.PortalPath)) dlg.InitialDirectory = Config.PortalPath;
            if (dlg.ShowDialog() != true) return;
            Config.PortalPath = dlg.FolderName;
            Config.Title = System.IO.Path.GetFileName(dlg.FolderName.TrimEnd('\\')) is { Length: > 0 } n ? n : dlg.FolderName;
        }
        else if (mode == FenceMode.Unsorted && Config.Mode != FenceMode.Unsorted)
        {
            Config.Title = "未整理";
        }
        else if (mode == FenceMode.Recent)
        {
            Config.Title = "最近のファイル";
            Config.SortKey = SortKey.Modified;
            Config.SortDescending = true;
        }
        else if (mode == FenceMode.Apps)
        {
            Config.Title = "アプリ";
            Config.SortKey = SortKey.Name;
            Config.SortDescending = false;
            if (Config.View is FenceView.Details or FenceView.List or FenceView.Small)
                Config.View = FenceView.Medium;
        }
        else if (mode == FenceMode.Remote)
        {
            var cfg = RemoteConnectWindow.Edit(Config.Remote);
            if (cfg == null) return;
            Config.Remote = cfg;
            Config.Title = cfg.Display;
        }

        // ショートカットの一覧は残しておく（種類を戻したときに復活する）
        ResetRemote();
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        _searchText = "";
        Config.Mode = mode;
        ApplyAppearance();
        ApplyView();
        SetupWatchers();
        Reload();
        App.Current.OnItemsChanged();
    }

    // ------------------------------------------------------------------
    // タイトルバー
    // ------------------------------------------------------------------

    /// <summary>まとめ移動の選択状態を表示する。</summary>
    public void SetGroupSelected(bool on) =>
        GroupRing.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TitleEdit.IsVisible) return;
        if (e.ClickCount == 1 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            App.Current.ToggleGroup(this);
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2)
        {
            ToggleRollup();
            e.Handled = true;
            return;
        }
        App.Current.BeginGroupDrag(this);
        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally { App.Current.EndGroupDrag(); }
    }

    public void BeginRename()
    {
        Activate();
        HoverExpand();
        TitleEdit.Text = Config.Title;
        TitleText.Visibility = Visibility.Hidden;
        TabStrip.Visibility = Visibility.Hidden;
        TitleEdit.Visibility = Visibility.Visible;
        TitleEdit.Focus();
        TitleEdit.SelectAll();
        ApplyPrivacy();
    }

    private void EndRename(bool commit)
    {
        if (!TitleEdit.IsVisible) return;
        var text = TitleEdit.Text.Trim();
        if (commit && text.Length > 0)
        {
            Config.Title = text;
            App.Current.SaveSoon();
        }
        TitleEdit.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        ApplyAppearance();
        ScheduleCollapse();
    }

    private void OnTitleEditKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { EndRename(true); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndRename(false); e.Handled = true; }
    }

    private void OnTitleEditLostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndRename(true);

    // ------------------------------------------------------------------
    // アイテム操作（クリック・キー・ドラッグ開始）
    // ------------------------------------------------------------------

    private static FenceItem? ItemFromSource(object? source)
    {
        var d = source as DependencyObject;
        while (d != null && d is not ListBoxItem)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return (d as ListBoxItem)?.DataContext as FenceItem;
    }

    private void OnListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _typeBuffer = "";
        _dragStart = e.GetPosition(this);
        _dragItem = ItemFromSource(e.OriginalSource);

        // 項目の上ではない所を押したら、範囲選択を開始する（スクロールバーは除く）
        if (_dragItem == null && !IsScrollBarPart(e.OriginalSource))
        {
            BeginMarquee(e.GetPosition(ItemsList),
                (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0);
            e.Handled = true;
        }
    }

    private void OnListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_marqueeActive)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndMarquee();
                return;
            }

            var pos = e.GetPosition(ItemsList);
            if (!_marqueeVisible
                && Math.Abs(pos.X - _marqueeStart.X) < MarqueeThreshold
                && Math.Abs(pos.Y - _marqueeStart.Y) < MarqueeThreshold)
                return;

            _marqueeVisible = true;
            var rect = ClampToItemsList(new Rect(_marqueeStart, pos));
            ShowMarqueeRect(rect);
            UpdateMarqueeSelection(rect);
            e.Handled = true;
            return;
        }

        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var dragPos = e.GetPosition(this);
        if (Math.Abs(dragPos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(dragPos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var items = ItemsList.SelectedItems.Cast<FenceItem>().ToList();
        if (!items.Contains(_dragItem)) items = new List<FenceItem> { _dragItem };
        _dragItem = null;

        if (IsRemote)
        {
            StartRemoteDrag(items);
            return;
        }

        var paths = items.Select(i => i.Path).ToArray();
        var data = new DataObject();
        // 外のアプリには実ファイルだけを渡す（ごみ箱などのシェル項目やアプリ項目はフェンス間の移動用にだけ載せる）
        var files = paths.Where(p => !FolderListing.IsShellItem(p) && !FolderListing.IsAppItem(p)).ToArray();
        if (files.Length > 0) data.SetData(DataFormats.FileDrop, files);
        data.SetData(PathsFormat, paths);
        data.SetData(SourceFenceFormat, Config.Id);

        // エクスプローラーへドロップされたときに実体が「移動」されないよう、コピー／リンクのみ許可
        DragDrop.DoDragDrop(ItemsList, data, DragDropEffects.Copy | DragDropEffects.Link);
    }

    private static bool IsScrollBarPart(object? source)
    {
        var d = source as DependencyObject;
        while (d != null && d is not ListBox)
        {
            if (d is System.Windows.Controls.Primitives.ScrollBar
                or System.Windows.Controls.Primitives.Thumb
                or System.Windows.Controls.Primitives.RepeatButton)
                return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void BeginMarquee(Point start, bool additive)
    {
        _marqueeStart = start;
        _marqueeActive = true;
        _marqueeVisible = false;
        _marqueeAdditive = additive;

        _marqueeBaseSelection.Clear();
        if (additive)
        {
            foreach (var item in ItemsList.SelectedItems.Cast<FenceItem>())
                _marqueeBaseSelection.Add(item);
        }
        else
        {
            ItemsList.UnselectAll();
        }

        MarqueeRect.Visibility = Visibility.Collapsed;
        ItemsList.CaptureMouse();
        ItemsList.Focus();
    }

    private void EndMarquee()
    {
        if (!_marqueeActive) return;
        _marqueeActive = false;
        _marqueeVisible = false;
        _marqueeBaseSelection.Clear();
        MarqueeRect.Visibility = Visibility.Collapsed;
        if (ItemsList.IsMouseCaptured) ItemsList.ReleaseMouseCapture();
    }

    private void OnListLostMouseCapture(object sender, MouseEventArgs e) => EndMarquee();

    private Rect ClampToItemsList(Rect rect)
    {
        double maxX = Math.Max(0, ItemsList.ActualWidth);
        double maxY = Math.Max(0, ItemsList.ActualHeight);
        double left = Math.Clamp(Math.Min(rect.Left, rect.Right), 0, maxX);
        double top = Math.Clamp(Math.Min(rect.Top, rect.Bottom), 0, maxY);
        double right = Math.Clamp(Math.Max(rect.Left, rect.Right), 0, maxX);
        double bottom = Math.Clamp(Math.Max(rect.Top, rect.Bottom), 0, maxY);
        return new Rect(new Point(left, top), new Point(right, bottom));
    }

    private void ShowMarqueeRect(Rect rect)
    {
        Canvas.SetLeft(MarqueeRect, rect.Left);
        Canvas.SetTop(MarqueeRect, rect.Top);
        MarqueeRect.Width = rect.Width;
        MarqueeRect.Height = rect.Height;
        MarqueeRect.Visibility = Visibility.Visible;
    }

    private void UpdateMarqueeSelection(Rect rect)
    {
        var desired = new HashSet<FenceItem>();
        for (int i = 0; i < ItemsList.Items.Count; i++)
        {
            if (ItemsList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container
                || !container.IsVisible
                || container.DataContext is not FenceItem item)
                continue;

            var bounds = container.TransformToAncestor(ItemsList)
                .TransformBounds(new Rect(container.RenderSize));
            if (rect.IntersectsWith(bounds)) desired.Add(item);
        }
        if (_marqueeAdditive) desired.UnionWith(_marqueeBaseSelection);

        // 差分だけ反映する（毎回すべて外して入れ直すとちらつくため）
        foreach (var item in ItemsList.SelectedItems.Cast<FenceItem>().ToList())
        {
            if (!desired.Contains(item)) ItemsList.SelectedItems.Remove(item);
        }
        foreach (var item in desired)
        {
            if (!ItemsList.SelectedItems.Contains(item)) ItemsList.SelectedItems.Add(item);
        }
    }

    private void OnListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (ItemFromSource(e.OriginalSource) is not { } item) return;
        OpenItemFromMouse(item);
    }

    /// <summary>シングルクリックで開く設定のとき、ドラッグにならなかったクリックで開く。</summary>
    private void OnListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_marqueeActive)
        {
            EndMarquee();
            e.Handled = true;
            return;
        }

        var item = _dragItem;
        _dragItem = null;
        if (!EffectiveSingleClickOpen || item is null) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (ItemFromSource(e.OriginalSource) != item) return;
        OpenItemFromMouse(item);
    }

    /// <summary>ダブルクリック相当の時間内に同じ項目を二重に開かない。</summary>
    private void OpenItemFromMouse(FenceItem item)
    {
        var now = DateTime.UtcNow;
        if (string.Equals(_lastMouseOpenPath, item.Path, StringComparison.OrdinalIgnoreCase)
            && (now - _lastMouseOpenAt).TotalMilliseconds < NativeMethods.GetDoubleClickTime())
            return;
        _lastMouseOpenPath = item.Path;
        _lastMouseOpenAt = now;
        OpenItem(item);
    }

    /// <summary>このフェンスの項目を開く（ローカル／SSH を問わず）。</summary>
    public void OpenItem(FenceItem item)
    {
        if (IsRemote) RemoteOpen(item);
        else ShellActions.Open(item.Path);
    }

    /// <summary>検索・コマンドパレット用（今のページの項目）。</summary>
    public IEnumerable<FenceItem> VisibleItems => _items;
    public string PageTitle => Config.Title;

    /// <summary>
    /// Tab / Shift+Tab を先に拾う。ListBox の既定のフォーカス移動より前に処理しないと、
    /// 同じフェンス内の項目間移動になってしまう。
    /// </summary>
    private void OnListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ToggleSearch();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            App.Current.UndoLast();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && SearchBar.IsVisible)
        {
            CloseSearch();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Tab) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (HasTabs)
            {
                SwitchRelative(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
            }
            return;
        }
        // タブなしの Tab / Shift+Tab は、左右に隣り合うフェンスへ（端では折り返さない）
        int direction = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
        App.Current.FocusAdjacentFence(this, direction);
        e.Handled = true;
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        // ナビゲーションしたらタイプアヘッドはやり直し
        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right
            or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            _typeBuffer = "";

        if (e.Key == Key.Back && _typeBuffer.Length > 0)
        {
            _typeBuffer = _typeBuffer[..^1];
            if (_typeBuffer.Length > 0 && FindByPrefix(_typeBuffer) is { } back) SelectAndReveal(back);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _typeBuffer.Length > 0)
        {
            _typeBuffer = "";
            e.Handled = true;
            return;
        }

        if (IsRemote)
        {
            if (HandleRemoteKey(e)) e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Enter:
                foreach (var item in ItemsList.SelectedItems.Cast<FenceItem>().ToList())
                    ShellActions.Open(item.Path);
                e.Handled = true;
                break;
            case Key.Delete:
                RemoveSelected();
                e.Handled = true;
                break;
            case Key.F5:
                if (IsApps)
                {
                    ShellApps.InvalidateCache();
                    ShellIcons.InvalidateApps();
                }
                else
                {
                    ShellIcons.Invalidate(_items.Select(i => i.Path));
                }
                Reload();
                e.Handled = true;
                break;
            case Key.Apps: // アプリケーションキー
            case Key.F10 when Keyboard.Modifiers == ModifierKeys.Shift:
                if (IsApps)
                {
                    if (ItemsList.SelectedItems.Count > 0)
                        OpenWpfMenu(BuildAppItemMenu(ItemsList.SelectedItems.Cast<FenceItem>().ToList()));
                }
                else
                {
                    ShowItemMenu(cursor: false);
                }
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// タイプアヘッド: 文字を打つと名前が前方一致する項目へ移動する。
    /// 同じ文字を続けて打つと、その文字で始まる次の項目へ（エクスプローラー風）。
    /// </summary>
    private void OnListTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        // Ctrl / Alt 付きはショートカットなので拾わない（Shift は大文字入力に使える）
        if ((Keyboard.Modifiers & ~ModifierKeys.Shift) != ModifierKeys.None) return;

        if (IsApps)
        {
            SearchBar.Visibility = Visibility.Visible;
            SearchBox.Focus();
            SearchBox.Text += e.Text;
            SearchBox.CaretIndex = SearchBox.Text.Length;
            e.Handled = true;
            return;
        }

        var now = DateTime.UtcNow;
        bool newBuffer = _typeBuffer.Length == 0 || now - _typeBufferAt > TypeBufferTimeout;
        if (newBuffer) _typeBuffer = "";
        _typeBufferAt = now;

        // まず「今の文字列＋新しい文字」で前方一致を探す（app → Apple も打てる）
        var extended = _typeBuffer + e.Text;
        var match = FindByPrefix(extended, afterCurrent: newBuffer);
        var nextBuffer = extended;

        // 同じ 1 文字の連打で、その文字を含む次の候補へ（aa に一致が無い場合だけ）
        if (match is null && !newBuffer && _typeBuffer.Length == 1
            && e.Text.Length == 1 && _typeBuffer[0] == e.Text[0])
        {
            match = FindByPrefix(_typeBuffer, afterCurrent: true);
            nextBuffer = _typeBuffer;
        }

        // それでも無ければ、最後の文字で探し直す
        if (match is null && extended.Length > 1)
        {
            nextBuffer = e.Text;
            match = FindByPrefix(nextBuffer);
        }

        if (match is not null)
        {
            _typeBuffer = nextBuffer;
            SelectAndReveal(match);
        }
        e.Handled = true;
    }

    /// <summary>名前が prefix で始まる項目を探す。afterCurrent なら選択中の次から（末尾は先頭へ）。</summary>
    private FenceItem? FindByPrefix(string prefix, bool afterCurrent = false)
    {
        if (_items.Count == 0 || prefix.Length == 0) return null;
        var compare = System.Globalization.CultureInfo.CurrentCulture.CompareInfo;
        const System.Globalization.CompareOptions options =
            System.Globalization.CompareOptions.IgnoreCase
            | System.Globalization.CompareOptions.IgnoreKanaType
            | System.Globalization.CompareOptions.IgnoreWidth;

        int start = 0;
        if (afterCurrent && ItemsList.SelectedItem is FenceItem current)
        {
            int index = _items.IndexOf(current);
            if (index >= 0) start = index + 1;
        }
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[(start + i) % _items.Count];
            if (compare.IndexOf(item.Name, prefix, options) == 0) return item;
        }
        return null;
    }

    private void SelectAndReveal(FenceItem item)
    {
        ItemsList.SelectedItem = item;
        ItemsList.ScrollIntoView(item);
        if (ItemsList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
        {
            container.BringIntoView();
            // 矢印キーの起点は「フォーカス中のコンテナ」。ListBox 本体にフォーカスを残すと
            // 最初の項目から移動してしまうため、選択した項目へフォーカスも移す。
            container.Focus();
        }
    }

    private void FocusSelectedItem()
    {
        ItemsList.UpdateLayout(); // ScrollIntoView でコンテナが作られるのを待つ
        if (ItemsList.SelectedItem is FenceItem item
            && ItemsList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container
            && container.Focus())
            return;
        ItemsList.Focus();
    }

    /// <summary>矢印キーで選択が画面外へ進んだときも、必ず見える位置までスクロールする。</summary>
    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ItemsList.IsKeyboardFocusWithin) return;
        if (InputManager.Current.MostRecentInputDevice is not KeyboardDevice) return;
        if (ItemsList.SelectedItem is FenceItem item)
            ItemsList.ScrollIntoView(item);
    }

    /// <summary>他のフェンスからキーボードで移ってきたとき、このフェンスにフォーカスを渡す。</summary>
    public void FocusList()
    {
        if (Host.RolledUp && !_hoverExpanded)
        {
            _hoverExpanded = true;
            ApplyRollup();
        }
        if (!Activate()) Log.Info("FocusList: Activate failed");
        if (ItemsList.SelectedItem is FenceItem selected)
        {
            ItemsList.ScrollIntoView(selected);
            FocusSelectedItem();
        }
        else if (_items.Count > 0)
        {
            SelectAndReveal(_items[0]);
        }
        else
        {
            ItemsList.Focus();
        }
    }

    // ------------------------------------------------------------------
    // ドロップ
    // ------------------------------------------------------------------

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        HoverExpand();
        OnDragOver(sender, e);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDropEffect(e);
        e.Handled = true;
        // ドラッグ中は MouseLeave が来ないので、離れたら閉じる用にタイマーを仕掛け直す
        ScheduleCollapse();
    }

    /// <summary>ドラッグされてきたパス（フェンス間なら独自形式、外からならファイルドロップ）。</summary>
    private static string[] DroppedPaths(DragEventArgs e) =>
        e.Data.GetData(PathsFormat) as string[]
        ?? e.Data.GetData(DataFormats.FileDrop) as string[]
        ?? Array.Empty<string>();

    /// <summary>
    /// 外からのドロップをデスクトップフォルダーへ入れるときの効果。エクスプローラーと同じ規則:
    /// Ctrl = コピー、Shift = 移動、何も押さなければ同じドライブなら移動・別ドライブならコピー。
    /// </summary>
    private static DragDropEffects DesktopDropEffect(DragEventArgs e, string[] paths)
    {
        var desktop = FolderListing.UserDesktop;
        var real = paths.Where(p => !FolderListing.IsShellItem(p) && (File.Exists(p) || Directory.Exists(p))).ToList();
        if (real.Count == 0) return DragDropEffects.None;
        if (real.All(p => string.Equals(System.IO.Path.GetDirectoryName(p.TrimEnd('\\')), desktop, StringComparison.OrdinalIgnoreCase)))
            return DragDropEffects.None; // もともとデスクトップにある

        bool ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        bool shift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
        bool sameVolume = string.Equals(System.IO.Path.GetPathRoot(real[0]), System.IO.Path.GetPathRoot(desktop),
            StringComparison.OrdinalIgnoreCase);
        var wanted = ctrl ? DragDropEffects.Copy : shift ? DragDropEffects.Move
            : sameVolume ? DragDropEffects.Move : DragDropEffects.Copy;
        if ((e.AllowedEffects & wanted) == 0)
        {
            wanted = (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
                : (e.AllowedEffects & DragDropEffects.Move) != 0 ? DragDropEffects.Move
                : DragDropEffects.None;
        }
        return wanted;
    }

    private DragDropEffects GetDropEffect(DragEventArgs e)
    {
        if (IsRemote) return GetRemoteDropEffect(e);
        var paths = DroppedPaths(e);
        if (paths.Length == 0) return DragDropEffects.None;
        var source = e.Data.GetData(SourceFenceFormat) as string;

        switch (Config.Mode)
        {
            case FenceMode.Shortcuts:
                return source != null ? DragDropEffects.Copy : DragDropEffects.Link;
            case FenceMode.Unsorted:
                if (source != null)
                {
                    // 他のフェンスから「未整理に戻す」
                    return source != Config.Id && App.Current.Config.FindPage(source)?.Mode == FenceMode.Shortcuts
                        ? DragDropEffects.Copy
                        : DragDropEffects.None;
                }
                // エクスプローラーなどから: デスクトップの代わりとして、デスクトップフォルダーへ移動／コピー
                return DesktopDropEffect(e, paths);
            default:
                return DragDropEffects.None;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (GetDropEffect(e) == DragDropEffects.None) return;
        if (IsRemote)
        {
            OnRemoteDrop(e);
            return;
        }

        var paths = DroppedPaths(e);
        var source = e.Data.GetData(SourceFenceFormat) as string;

        if (Config.Mode == FenceMode.Unsorted && source == null)
        {
            // 外からのドロップ: デスクトップへ（エクスプローラーと同じ確認ダイアログ付き）。表示は監視で更新される
            bool copy = DesktopDropEffect(e, paths) == DragDropEffects.Copy;
            ShellActions.MoveOrCopyInto(paths, FolderListing.UserDesktop, copy);
            return;
        }

        if (Config.Mode == FenceMode.Unsorted)
        {
            if (App.Current.Config.FindPage(source!) is { } from)
                App.Current.RecordRemoval(from, paths);
            App.Current.RemoveFromPage(source!, paths);
            App.Current.ExcludeFromRules(paths); // 未整理に戻したものは自動振り分けで戻さない
            App.Current.OnItemsChanged();
            return;
        }

        bool sorted = EffectiveSortKey != SortKey.Manual;
        if (source == Config.Id && sorted)
        {
            // 並べ替え表示中に手で動かしたら、今の見た目の順を起点に「手動」へ切り替える
            Config.Items = _items.Select(i => i.Path)
                .Concat(Config.Items.Where(p => !_items.Any(i => PathComparer.Equals(i.Path, p))))
                .ToList();
            Config.SortKey = SortKey.Manual;
            UpdateHeaderLabels();
            sorted = false;
        }

        var list = Config.Items;
        int insert = sorted ? -1 : IndexAt(e.GetPosition(ItemsList)); // 並べ替え表示中は位置に意味がないので末尾へ
        if (insert < 0 || insert > list.Count) insert = list.Count;

        if (source == Config.Id)
        {
            // 並べ替え
            var moving = paths.Where(p => list.Contains(p, PathComparer)).ToList();
            int before = moving.Count(p => list.FindIndex(x => PathComparer.Equals(x, p)) < insert);
            var set = moving.ToHashSet(PathComparer);
            list.RemoveAll(set.Contains);
            list.InsertRange(Math.Clamp(insert - before, 0, list.Count), moving);
        }
        else
        {
            if (source != null)
                App.Current.RecordMove(source, Config, paths);
            foreach (var p in paths)
            {
                if (list.Contains(p, PathComparer)) continue;
                list.Insert(insert++, p);
            }
            if (source != null)
                App.Current.RemoveFromPage(source, paths);
        }

        Reload();
        App.Current.OnItemsChanged();
    }

    /// <summary>ドロップ位置に対応する挿入インデックス。アイテム外なら -1。</summary>
    private int IndexAt(Point p)
    {
        var container = ItemsList.InputHitTest(p) is DependencyObject hit ? FindAncestor<ListBoxItem>(hit) : null;
        if (container == null) return -1;
        int index = ItemsList.ItemContainerGenerator.IndexFromContainer(container);
        if (index < 0) return -1;
        var local = ItemsList.TranslatePoint(p, container);
        bool vertical = Config.View is FenceView.Details or FenceView.List; // 縦に並ぶ表示は上下で判定
        bool after = vertical ? local.Y > container.ActualHeight / 2 : local.X > container.ActualWidth / 2;
        return after ? index + 1 : index;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    // ------------------------------------------------------------------
    // 右クリックメニュー
    // ------------------------------------------------------------------

    private void OnPreviewRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (TitleEdit.IsKeyboardFocusWithin) return;
        if (FindAncestor<TextBox>(e.OriginalSource as DependencyObject) == SearchBox) return;

        if (TabPageFromSource(e.OriginalSource) is { } tabPage) SwitchTo(tabPage);
        var item = ItemFromSource(e.OriginalSource);
        e.Handled = true;
        if (item != null)
        {
            if (!ItemsList.SelectedItems.Contains(item))
            {
                ItemsList.UnselectAll();
                ItemsList.SelectedItem = item;
            }
            if (IsRemote)
            {
                OpenWpfMenu(BuildRemoteItemMenu(ItemsList.SelectedItems.Cast<FenceItem>().ToList()));
                return;
            }
            if (IsApps)
            {
                OpenWpfMenu(BuildAppItemMenu(ItemsList.SelectedItems.Cast<FenceItem>().ToList()));
                return;
            }
            // マウス操作の処理が終わってから（モーダルのメニューを出すため）
            Dispatcher.BeginInvoke(() => ShowItemMenu(cursor: true), DispatcherPriority.Input);
            return;
        }
        OpenWpfMenu(BuildFenceMenu());
    }

    /// <summary>
    /// アイテムの右クリック: Windows 標準（エクスプローラーと同じ）のメニュー。
    /// 出せない項目（見つからないファイルなど）のときは Fako の簡易メニュー。
    /// </summary>
    private void ShowItemMenu(bool cursor)
    {
        var items = ItemsList.SelectedItems.Cast<FenceItem>().ToList();
        if (items.Count == 0) return;

        // 表示位置: マウスの位置、キーボード（アプリケーションキー）なら選んでいる項目の位置
        int x, y;
        if (!cursor && ItemsList.ItemContainerGenerator.ContainerFromItem(items[0]) is ListBoxItem container)
        {
            var p = container.PointToScreen(new Point(container.ActualWidth / 2, container.ActualHeight / 2));
            (x, y) = ((int)p.X, (int)p.Y);
        }
        else
        {
            GetCursorPos(out var pt);
            (x, y) = (pt.X, pt.Y);
        }

        var live = items.Where(i => i.Exists).Select(i => i.Path).ToList();
        var extras = new List<ShellContextMenu.Extra>();
        if (Config.Mode == FenceMode.Shortcuts)
            extras.Add(new(items.Count > 1 ? $"フェンスから外す（{items.Count} 個）" : "フェンスから外す", RemoveSelected));

        bool extended = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _menuOpen = true;
        bool shown;
        string? verb = null;
        // 「名前の変更」はエクスプローラーの画面内で行う機能なので、シェルに任せず自前のダイアログで行う
        bool Intercept(string v)
        {
            if (!string.Equals(v, "rename", StringComparison.OrdinalIgnoreCase)) return false;
            var target = live.FirstOrDefault(p => !FolderListing.IsShellItem(p) && !FolderListing.IsAppItem(p));
            if (target != null) Dispatcher.BeginInvoke(() => RenameItem(target));
            return true;
        }

        try
        {
            shown = live.Count > 0 && ShellContextMenu.Show(_hwnd, live, x, y, extended, extras, Intercept, out verb);
        }
        finally
        {
            _menuOpen = false;
        }

        if (!shown)
        {
            OpenWpfMenu(BuildItemMenu(items));
            return;
        }

        if (verb != null) Log.Info($"shell verb '{verb}' on {string.Join(" | ", live)}");
        // 削除・切り取りなどで状態が変わるかもしれないので、少し待ってから表示を更新する
        _lastShellVerb = verb;
        _lastShellPaths = live;
        ShellIcons.Invalidate(live);
        _afterShellCommandTimer.Stop();
        _afterShellCommandTimer.Start();
        ScheduleCollapse();
    }

    private string? _lastShellVerb;
    private IReadOnlyList<string> _lastShellPaths = Array.Empty<string>();

    /// <summary>標準メニューのコマンドの後始末（削除されたものはショートカット型フェンスから外す）。</summary>
    private void AfterShellCommand()
    {
        if (string.Equals(_lastShellVerb, "delete", StringComparison.OrdinalIgnoreCase) && Config.Mode == FenceMode.Shortcuts)
        {
            var gone = _lastShellPaths.Where(p =>
                !FolderListing.IsShellItem(p)
                && (FolderListing.IsAppItem(p)
                    ? ShellApps.GetDisplayName(FolderListing.ExtractAppId(p)) is null
                    : !File.Exists(p) && !Directory.Exists(p))).ToList();
            if (gone.Count > 0)
            {
                RemovePaths(gone);
                App.Current.OnItemsChanged();
            }
        }
        _lastShellVerb = null;
        ShellIcons.Invalidate(_items.Select(i => i.Path));
        Reload();
    }

    private void RenameItem(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        var dlg = new RenameWindow(path);
        if (dlg.ShowDialog() != true) return;

        var newPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path.TrimEnd('\\'))!, dlg.NewName);
        try
        {
            if (Directory.Exists(path)) Microsoft.VisualBasic.FileIO.FileSystem.RenameDirectory(path, dlg.NewName);
            else Microsoft.VisualBasic.FileIO.FileSystem.RenameFile(path, dlg.NewName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"名前を変更できませんでした。\n\n{ex.Message}", "Fako", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Log.Info($"renamed: {path} -> {newPath}");
        App.Current.ReplacePath(path, newPath);
    }

    /// <summary>テスト用（FAKO_OPEN=shellmenu）: 先頭のアイテムの右クリックメニューを出す。</summary>
    internal void DebugShowFirstItemMenu()
    {
        if (_items.Count == 0) return;
        ItemsList.SelectedItem = _items[0];
        ShowItemMenu(cursor: false);
    }

    private void OpenWpfMenu(ContextMenu menu)
    {
        menu.PlacementTarget = this;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.Opened += (_, _) => { _menuOpen = true; ApplyPrivacy(); };
        menu.Closed += (_, _) => { _menuOpen = false; ScheduleCollapse(); ApplyPrivacy(); };
        menu.IsOpen = true;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    private ContextMenu BuildItemMenu(List<FenceItem> items)
    {
        var first = items[0];
        bool isApp = FolderListing.IsAppItem(first.Path);
        var m = new ContextMenu();
        var open = AddItem(m, "開く", () => items.ForEach(i => ShellActions.Open(i.Path)));
        open.FontWeight = FontWeights.Bold;
        AddItem(m, "ファイルの場所を開く", () => ShellActions.OpenLocation(first.Path)).IsEnabled =
            !FolderListing.IsShellItem(first.Path) && !isApp;
        AddItem(m, "パスをコピー", () =>
        {
            try { Clipboard.SetText(string.Join(Environment.NewLine, items.Select(i => i.Path))); }
            catch (Exception ex) { Log.Error("クリップボードへのコピーに失敗", ex); }
        });
        m.Items.Add(new Separator());
        if (Config.Mode == FenceMode.Shortcuts)
            AddItem(m, items.Count > 1 ? $"フェンスから外す（{items.Count} 個）" : "フェンスから外す", RemoveSelected);
        AddItem(m, "プロパティ", () => ShellActions.ShowProperties(first.Path)).IsEnabled = first.Exists && !isApp;
        return m;
    }

    private ContextMenu BuildFenceMenu()
    {
        var m = new ContextMenu();
        if (IsRemote) AddRemoteFenceMenuItems(m);
        if (IsApps) AddAppsFenceMenuItems(m);

        // エクスプローラーのデスクトップメニューと同じ並び: 表示 → 並べ替え
        var view = new MenuItem { Header = "表示", InputGestureText = "Ctrl+ホイール" };
        foreach (var (v, label) in ViewLabels)
        {
            var mi = new MenuItem { Header = label, IsChecked = Config.View == v };
            mi.Click += (_, _) => SetView(v);
            view.Items.Add(mi);
        }
        m.Items.Add(view);

        var sort = new MenuItem { Header = "並べ替え" };
        var effective = EffectiveSortKey;
        if (Config.Mode == FenceMode.Shortcuts)
        {
            var manual = new MenuItem { Header = "手動（ドラッグで並べた順）", IsChecked = effective == SortKey.Manual };
            manual.Click += (_, _) => SetSort(SortKey.Manual, false);
            sort.Items.Add(manual);
            sort.Items.Add(new Separator());
        }
        foreach (var (k, label) in SortLabels)
        {
            var mi = new MenuItem { Header = label, IsChecked = effective == k };
            mi.Click += (_, _) => SetSort(k, k == effective ? Config.SortDescending : null);
            sort.Items.Add(mi);
        }
        if (effective != SortKey.Manual)
        {
            sort.Items.Add(new Separator());
            var asc = new MenuItem { Header = "昇順", IsChecked = !Config.SortDescending };
            asc.Click += (_, _) => SetSort(effective, false);
            var desc = new MenuItem { Header = "降順", IsChecked = Config.SortDescending };
            desc.Click += (_, _) => SetSort(effective, true);
            sort.Items.Add(asc);
            sort.Items.Add(desc);
        }
        m.Items.Add(sort);
        AddItem(m, "最新の情報に更新 (F5)", () =>
        {
            if (IsApps)
            {
                ShellApps.InvalidateCache();
                ShellIcons.InvalidateApps();
            }
            else
            {
                ShellIcons.Invalidate(_items.Select(i => i.Path));
            }
            Reload();
        });
        m.Items.Add(new Separator());

        AddItem(m, "新しいフェンス", () => App.Current.NewFence(FenceMode.Shortcuts, near: this));
        AddItem(m, HasTabs ? "タブ名の変更" : "名前の変更", BeginRename);
        m.Items.Add(BuildTabMenu());
        var roll = AddItem(m, "折りたたむ（タイトルをダブルクリック）", ToggleRollup);
        roll.IsCheckable = true;
        roll.IsChecked = Host.RolledUp;

        var color = new MenuItem { Header = "色" };
        var current = ColorUtil.Parse(Host.Style.Background, ColorUtil.DefaultBackground);
        foreach (var (name, hex) in ColorPresets)
        {
            var c = ColorUtil.Parse(hex, ColorUtil.DefaultBackground);
            var swatch = new Rectangle
            {
                Width = 14, Height = 14, RadiusX = 3, RadiusY = 3,
                Fill = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)),
                Stroke = Brushes.Gray, StrokeThickness = 0.5,
            };
            var mi = new MenuItem
            {
                Header = name, Icon = swatch,
                IsChecked = c.R == current.R && c.G == current.G && c.B == current.B,
            };
            mi.Click += (_, _) =>
            {
                var keep = ColorUtil.Parse(Host.Style.Background, ColorUtil.DefaultBackground).A;
                Host.Style.Background = ColorUtil.ToHex(ColorUtil.WithAlpha(c, keep));
                ApplyAppearance();
                App.Current.SaveSoon();
            };
            color.Items.Add(mi);
        }
        color.Items.Add(new Separator());
        foreach (var (name, alpha) in OpacityPresets)
        {
            var mi = new MenuItem { Header = $"濃さ: {name}", IsChecked = Math.Abs(current.A - alpha) < 0x18 };
            mi.Click += (_, _) =>
            {
                var c = ColorUtil.Parse(Host.Style.Background, ColorUtil.DefaultBackground);
                Host.Style.Background = ColorUtil.ToHex(ColorUtil.WithAlpha(c, alpha));
                ApplyAppearance();
                App.Current.SaveSoon();
            };
            color.Items.Add(mi);
        }
        m.Items.Add(color);
        var design = AddItem(m, "外観の設定…", () => StyleEditorWindow.ShowFor(this));
        design.FontWeight = FontWeights.SemiBold;
        AddItem(m, App.Current.IsGroupSelected(this) ? "まとめ移動から外す" : "まとめ移動に追加（Ctrl+Shift+クリック）",
            () => App.Current.ToggleGroup(this));

        var kind = new MenuItem { Header = "種類" };
        AddMode(kind, "ショートカット（ドロップしたものを並べる）", FenceMode.Shortcuts);
        AddMode(kind, Config.Mode == FenceMode.Portal ? "フォルダーポータル（フォルダーを変更…）" : "フォルダーポータル…", FenceMode.Portal);
        AddMode(kind, "未整理（どのフェンスにも無いデスクトップ項目）", FenceMode.Unsorted);
        AddMode(kind, "最近のファイル（仮想）", FenceMode.Recent);
        AddMode(kind, "アプリ一覧（Launchpad）", FenceMode.Apps);
        AddMode(kind, Config.Mode == FenceMode.Remote ? "SSH（接続先を変更…）" : "SSH（リモートのフォルダー）…", FenceMode.Remote);
        m.Items.Add(kind);

        if (Config.Mode == FenceMode.Portal && !string.IsNullOrEmpty(Config.PortalPath))
            AddItem(m, "フォルダーを開く", () => ShellActions.Open(Config.PortalPath!));
        m.Items.Add(new Separator());
        AddItem(m, "Fako の設定…", () => App.Current.OpenSettings());
        AddItem(m, "フェンスを削除", () => App.Current.DeleteFence(this));
        return m;
    }

    private void AddMode(MenuItem parent, string header, FenceMode mode)
    {
        var mi = new MenuItem { Header = header, IsChecked = Config.Mode == mode };
        mi.Click += (_, _) =>
        {
            if (mode == FenceMode.Remote && Config.Mode == FenceMode.Remote) EditRemoteSettings();
            else if (mode != Config.Mode || mode == FenceMode.Portal) ChangeMode(mode);
        };
        parent.Items.Add(mi);
    }

    private static MenuItem AddItem(ContextMenu menu, string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => action();
        menu.Items.Add(mi);
        return mi;
    }
}
