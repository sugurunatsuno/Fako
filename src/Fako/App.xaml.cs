using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Fako.Interop;
using Fako.Models;

namespace Fako;

public partial class App : Application
{
    private const string MutexName = @"Local\Fako.SingleInstance";

    public static new App Current => (App)Application.Current;

    public ConfigStore Store { get; } = new();
    public AppConfig Config { get; private set; } = new();
    public bool AllHidden { get; private set; }

    private readonly List<FenceWindow> _windows = new();
    private Mutex? _mutex;
    private TrayIcon? _tray;
    private DesktopDoubleClickHook? _hook;
    private DispatcherTimer? _saveTimer;
    private HwndSource? _hotkeySource;
    private bool _peeking;
    private bool _peekWasHidden;
    private const int PeekHotkeyId = 0x0F4A;
    private const int PaletteHotkeyId = 0x0F4B;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        // 設定フォルダーごとに 1 つ（FAKO_HOME でテスト用の別インスタンスを並べて動かせる）
        // （string.GetHashCode はプロセスごとに値が変わるので使えない → FNV-1a）
        uint hash = 2166136261;
        foreach (char ch in ConfigStore.Directory.ToUpperInvariant()) hash = (hash ^ ch) * 16777619;
        var mutexName = $"{MutexName}.{hash:x8}";
        _mutex = new Mutex(true, mutexName, out bool created);
        if (!created)
        {
            MessageBox.Show("Fako はすでに起動しています。\nタスクトレイのアイコンから操作できます。", "Fako",
                MessageBoxButton.OK, MessageBoxImage.Information);
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };

        int integrity = ProcessIntegrity.Current();
        Log.Info($"start {typeof(App).Assembly.GetName().Version} exe={Environment.ProcessPath} integrity=0x{integrity:X} config={Store.FilePath} exists={File.Exists(Store.FilePath)}");
        if (integrity >= 0 && integrity < ProcessIntegrity.Medium)
        {
            MessageBox.Show(
                "Fako が「低い整合性レベル」で起動しています。\n" +
                "このままではタスクトレイに表示されず、設定も保存されません。\n\n" +
                "exe が、低整合性ラベル付きのフォルダー（サンドボックス用の作業フォルダーなど）に置かれている可能性があります。\n" +
                "Fako.exe を %LOCALAPPDATA%\\Programs\\Fako などにコピーして、そこから起動してください。\n\n" +
                $"現在の場所: {Environment.ProcessPath}",
                "Fako", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        try { AutoStart.Migrate(); AutoStart.RefreshPath(); }
        catch (Exception ex) { Log.Error("自動起動の更新に失敗", ex); }
        Config = Store.Load();

        // v0.3 までの「属性で隠す」方式で隠したファイルを元に戻す（未整理フェンスの一覧より先に）
        if (DesktopIconHider.RestoreAll(Config) > 0) SaveNow();

        if (Config.Fences.Count == 0)
        {
            var wa = SystemParameters.WorkArea;
            Config.Fences.Add(new FenceConfig
            {
                Title = "はじめてのフェンス",
                Left = wa.Left + 80,
                Top = wa.Top + 80,
            });
            SaveNow();
        }

        // 未整理フェンスは他のフェンスの中身に依存するので最後に作る
        foreach (var fc in Config.Fences.OrderBy(f => f.Mode == FenceMode.Unsorted ? 1 : 0))
            ShowFence(fc);

        try { _tray = new TrayIcon(this); }
        catch (Exception ex) { Log.Error("トレイアイコンの作成に失敗", ex); }
        SetupHotkeys();
        ApplyDoubleClickHook();
        ApplyIconMode();
        SetupDesktopWatchers();
        RunRules(force: false);

        // テスト用: FAKO_OPEN=settings[:タブ番号] / style で起動時に画面を開く（旧名 KAKOI_OPEN も可）
        var openRequest = Environment.GetEnvironmentVariable("FAKO_OPEN")
            ?? Environment.GetEnvironmentVariable("KAKOI_OPEN");
        switch (openRequest?.Split(':'))
        {
            case ["settings", var tab]: OpenSettings(int.TryParse(tab, out var t) ? t : 0); break;
            case ["settings"]: OpenSettings(); break;
            case ["style"] when _windows.Count > 0: StyleEditorWindow.ShowFor(_windows[0]); break;
            case ["tabscript"]:
                Dispatcher.BeginInvoke(() => _windows.FirstOrDefault(w => w.Pages.Count >= 3)?.DebugTabScript(),
                    DispatcherPriority.ApplicationIdle);
                break;
            case ["remote"]:
                Dispatcher.BeginInvoke(() => RemoteConnectWindow.Edit(Config.AllPages().FirstOrDefault(f => f.Remote != null)?.Remote),
                    DispatcherPriority.ApplicationIdle);
                break;
            case ["palette"]:
                Dispatcher.BeginInvoke(TogglePalette, DispatcherPriority.ApplicationIdle);
                break;
            case ["paletteopen"]:
                Dispatcher.BeginInvoke(() =>
                {
                    TogglePalette();
                    var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    t.Tick += (_, _) => { t.Stop(); PaletteWindow.DebugOpenFirst(); };
                    t.Start();
                }, DispatcherPriority.ApplicationIdle);
                break;
            case ["search"]:
                Dispatcher.BeginInvoke(() => _windows.FirstOrDefault()?.ToggleSearch(),
                    DispatcherPriority.ApplicationIdle);
                break;
            case ["shellmenu"]:
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    _windows.FirstOrDefault(w => w.Config.Mode == FenceMode.Shortcuts && w.Config.Items.Count > 0)
                        ?.DebugShowFirstItemMenu();
                };
                timer.Start();
                break;
        }

        _foreground = new ForegroundWatcher();
        _foreground.DesktopForegroundChanged += SetFencesRaised;
        SetFencesRaised(ForegroundWatcher.IsDesktopForeground());
    }

    private ForegroundWatcher? _foreground;
    private bool _raised;

    /// <summary>デスクトップが前面のときはフェンスをその上に、他のアプリが前面なら最背面へ。</summary>
    private void SetFencesRaised(bool raised)
    {
        _raised = raised;
        foreach (var w in _windows)
            w.SetRaised(raised);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_hotkeySource != null)
        {
            NativeMethods.UnregisterHotKey(_hotkeySource.Handle, PeekHotkeyId);
            NativeMethods.UnregisterHotKey(_hotkeySource.Handle, PaletteHotkeyId);
            _hotkeySource.Dispose();
            _hotkeySource = null;
        }
        _hook?.Dispose();
        _foreground?.Dispose();
        foreach (var w in _desktopWatchers) w.Dispose();
        _tray?.Dispose();
        // Fako が隠した Windows 標準のデスクトップアイコンは、終了時に戻す（デスクトップが空に見えないように）
        if (_iconsHiddenByFako)
        {
            try { Desktop.SetIconsVisible(true); }
            catch (Exception ex) { Log.Error("デスクトップアイコンを戻せません", ex); }
        }
        if (_mutex != null)
        {
            SaveNow();
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
        base.OnExit(e);
    }

    // ------------------------------------------------------------------
    // フェンス管理
    // ------------------------------------------------------------------

    private FenceWindow ShowFence(FenceConfig config)
    {
        var w = new FenceWindow(config);
        _windows.Add(w);
        w.Closed += (_, _) =>
        {
            _windows.Remove(w);
            _groupSelection.Remove(w);
        };
        if (!AllHidden)
        {
            w.Show();
            w.SetRaised(_raised);
            if (_peeking) w.SetPeek(true);
        }
        return w;
    }

    /// <summary>指定したタイトルのショートカットフェンスを再利用するか、新しく作る（プリセット用）。</summary>
    public FenceConfig EnsureShortcutFence(string title)
    {
        var existing = Config.AllPages().FirstOrDefault(p =>
            p.Mode == FenceMode.Shortcuts &&
            string.Equals(p.Title, title, StringComparison.CurrentCultureIgnoreCase));
        if (existing != null) return existing;
        var w = NewFence(FenceMode.Shortcuts, title: title, rename: false);
        return w.Host;
    }

    /// <summary>そのページ（タブを含む）を持っているフェンスのウィンドウ。</summary>
    public FenceWindow? FindFence(string pageId) => _windows.FirstOrDefault(w => w.Pages.Any(p => p.Id == pageId));

    /// <summary>
    /// Tab / Shift+Tab: 左右に隣り合うフェンスへフォーカスを移す。
    /// 同じ行（縦に重なる）ものを優先し、無ければ左右にある最も近いもの。端では折り返さない。
    /// </summary>
    public void FocusAdjacentFence(FenceWindow from, int direction)
    {
        var fromRect = ScreenRect(from);
        var candidates = _windows
            .Where(w => w != from && w.IsVisible && w.Handle != IntPtr.Zero)
            .Select(w => (Window: w, Rect: ScreenRect(w)))
            .Where(x => direction > 0
                ? CenterX(x.Rect) > CenterX(fromRect)
                : CenterX(x.Rect) < CenterX(fromRect))
            .Select(x => (x.Window, x.Rect, SameRow: VerticalOverlap(fromRect, x.Rect)))
            .ToList();
        if (candidates.Count == 0) return;

        // 同じ行（縦に重なる）ものを優先。無ければ左右にある中で最も近いもの。
        var pool = candidates.Any(x => x.SameRow) ? candidates.Where(x => x.SameRow) : candidates;
        var next = pool.OrderBy(x => Math.Abs(CenterX(x.Rect) - CenterX(fromRect))).First();
        next.Window.FocusList();
    }

    private static double CenterX(Rect r) => r.Left + r.Width / 2;

    private static bool VerticalOverlap(Rect a, Rect b) => a.Top < b.Bottom && b.Top < a.Bottom;

    /// <summary>DPI の違うモニターでも比較できるよう、物理ピクセルの矩形にする。</summary>
    private static Rect ScreenRect(FenceWindow w)
    {
        var p = w.PointToScreen(new Point(0, 0));
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
        double height = Math.Max(w.ActualHeight, w.Host.Height);
        return new Rect(p.X, p.Y, w.ActualWidth * dpi.DpiScaleX, height * dpi.DpiScaleY);
    }

    // ------------------------------------------------------------------
    // 複数フェンスのまとめ移動
    // ------------------------------------------------------------------

    private readonly HashSet<FenceWindow> _groupSelection = new();
    private List<(FenceWindow Window, double Left, double Top)>? _groupDrag;
    private FenceWindow? _groupDragSource;

    public bool IsGroupSelected(FenceWindow w) => _groupSelection.Contains(w);

    /// <summary>Ctrl+Shift+クリック、またはフェンスの右クリックから選択を切り替える。</summary>
    public void ToggleGroup(FenceWindow w)
    {
        if (!_groupSelection.Remove(w)) _groupSelection.Add(w);
        w.SetGroupSelected(_groupSelection.Contains(w));
        if (_groupSelection.Count == 0) Log.Info("group selection cleared");
    }

    /// <summary>グループに入っているフェンスをドラッグし始めたら、相対位置を記録する。</summary>
    public void BeginGroupDrag(FenceWindow from)
    {
        if (!_groupSelection.Contains(from))
        {
            _groupDrag = null;
            _groupDragSource = null;
            return;
        }
        _groupDragSource = from;
        _groupDrag = _groupSelection
            .Where(w => w.IsVisible)
            .Select(w => (Window: w, w.Left, w.Top))
            .ToList();
    }

    /// <summary>ドラッグ中のフェンスの移動量を、選択中の他のフェンスにも同じだけ反映する。</summary>
    public void GroupDragMoved(FenceWindow from)
    {
        if (_groupDrag is null || _groupDragSource != from) return;
        var origin = _groupDrag.FirstOrDefault(x => x.Window == from);
        if (origin.Window is null) return;
        var dpiFrom = System.Windows.Media.VisualTreeHelper.GetDpi(from);
        // 物理ピクセルの移動量にしてから、各フェンスの DPI で割り直す（混在 DPI 対応）
        double dx = (from.Left - origin.Left) * dpiFrom.DpiScaleX;
        double dy = (from.Top - origin.Top) * dpiFrom.DpiScaleY;
        if (dx == 0 && dy == 0) return;
        foreach (var (w, left, top) in _groupDrag)
        {
            if (w == from) continue;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
            w.Left = left + dx / dpi.DpiScaleX;
            w.Top = top + dy / dpi.DpiScaleY;
        }
    }

    public void EndGroupDrag()
    {
        _groupDrag = null;
        _groupDragSource = null;
    }

    public IReadOnlyList<FenceWindow> FenceWindows => _windows;

    // ------------------------------------------------------------------
    // 元に戻す（フェンスからの削除・フェンス間の移動）
    // ------------------------------------------------------------------

    private sealed record UndoEntry(string Kind, string PageId, string? TargetPageId,
        List<(string Path, int Index)> Items);

    private readonly List<UndoEntry> _undo = new();
    private const int UndoLimit = 20;

    public bool CanUndo => _undo.Count > 0;

    private void PushUndo(UndoEntry entry)
    {
        _undo.Add(entry);
        if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
    }

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>フェンスから外す直前に呼ぶ。元の並び位置も覚えておく。</summary>
    public void RecordRemoval(FenceConfig page, IEnumerable<string> paths)
    {
        if (page.Mode != FenceMode.Shortcuts) return;
        var items = paths
            .Select(p => (Path: p, Index: page.Items.FindIndex(x => PathComparer.Equals(x, p))))
            .Where(x => x.Index >= 0)
            .ToList();
        if (items.Count > 0) PushUndo(new UndoEntry("remove", page.Id, null, items));
    }

    /// <summary>フェンス間の移動直前に呼ぶ。</summary>
    public void RecordMove(string fromPageId, FenceConfig to, IEnumerable<string> paths)
    {
        if (to.Mode != FenceMode.Shortcuts) return;
        if (Config.FindPage(fromPageId) is not { Mode: FenceMode.Shortcuts } from) return;
        var items = paths
            .Select(p => (Path: p, Index: from.Items.FindIndex(x => PathComparer.Equals(x, p))))
            .Where(x => x.Index >= 0)
            .ToList();
        if (items.Count > 0) PushUndo(new UndoEntry("move", from.Id, to.Id, items));
    }

    /// <summary>直前のフェンス操作を戻す。</summary>
    public void UndoLast()
    {
        if (_undo.Count == 0) return;
        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        var page = Config.FindPage(entry.PageId);
        if (page is null) return;

        if (entry.Kind == "move" && entry.TargetPageId is { } targetId
            && Config.FindPage(targetId) is { } target)
        {
            var set = entry.Items.Select(i => i.Path).ToHashSet(PathComparer);
            target.Items.RemoveAll(set.Contains);
        }

        foreach (var (path, index) in entry.Items.OrderBy(i => i.Index))
        {
            if (page.Items.Contains(path, PathComparer)) continue;
            page.Items.Insert(Math.Clamp(index, 0, page.Items.Count), path);
        }

        var restored = entry.Items.Select(i => i.Path).ToHashSet(PathComparer);
        Config.RuleExclusions.RemoveAll(restored.Contains);

        foreach (var w in _windows)
            if (w.Pages.Any(p => p.Id == entry.PageId || p.Id == entry.TargetPageId))
                w.Reload();
        SaveSoon();
        _tray?.ShowBalloon("Fako", "元に戻しました");
        Log.Info($"undo: {entry.Kind} {entry.Items.Count} items");
    }

    /// <summary>ページからアイテムを外す（ドラッグで別のフェンス・タブへ移したとき）。</summary>
    public void RemoveFromPage(string pageId, IEnumerable<string> paths)
    {
        var page = Config.FindPage(pageId);
        if (page is not { Mode: FenceMode.Shortcuts }) return;
        var set = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (page.Items.RemoveAll(set.Contains) == 0) return;
        if (FindFence(pageId) is { } w && w.Config == page) w.Reload();
        SaveSoon();
    }

    /// <summary>タブの並べ替えなどで、フェンスの代表（1 枚目のページ）が変わった。</summary>
    public void ReplaceHost(FenceConfig oldHost, FenceConfig newHost)
    {
        int i = Config.Fences.IndexOf(oldHost);
        if (i >= 0) Config.Fences[i] = newHost;
        SaveSoon();
    }

    /// <summary>切り離したタブなどを、新しいフェンスのウィンドウとして出す。</summary>
    public FenceWindow AddFence(FenceConfig config)
    {
        Config.Fences.Add(config);
        var w = ShowFence(config);
        SaveSoon();
        return w;
    }

    /// <summary>ウィンドウを閉じて設定からも外す（ページは呼び出し側が引き取る。結合用）。</summary>
    public void RemoveFenceWindow(FenceWindow w)
    {
        Config.Fences.Remove(w.Host);
        w.Close();
        SaveSoon();
    }

    public HashSet<string> AllShortcutPaths() =>
        Config.AllPages().Where(f => f.Mode == FenceMode.Shortcuts)
            .SelectMany(f => f.Items)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public FenceWindow NewFence(FenceMode mode = FenceMode.Shortcuts, FenceWindow? near = null, string? portalPath = null,
        string? title = null, bool rename = true, RemoteConfig? remote = null)
    {
        if (AllHidden) SetAllHidden(false);

        var wa = SystemParameters.WorkArea;
        // 既存のフェンスと重ならないよう、少しずつずらす
        int offset = 30 * (Config.Fences.Count % 8);
        var config = new FenceConfig
        {
            Mode = mode,
            PortalPath = portalPath,
            Remote = remote,
            Style = Config.DefaultStyle.Clone(),
            Left = near != null ? near.Left + 30 : wa.Left + wa.Width / 2 - 170 + offset,
            Top = near != null ? near.Top + 30 : wa.Top + wa.Height / 2 - 110 + offset,
        };
        if (mode == FenceMode.Unsorted) config.Title = "未整理";
        if (mode == FenceMode.Recent)
        {
            config.Title = "最近のファイル";
            config.SortKey = SortKey.Modified;
            config.SortDescending = true;
        }
        if (portalPath != null)
            config.Title = Path.GetFileName(portalPath.TrimEnd('\\')) is { Length: > 0 } n ? n : portalPath;
        if (title != null) config.Title = title;

        Config.Fences.Add(config);
        var w = ShowFence(config);
        SaveSoon();
        if (mode == FenceMode.Shortcuts && rename)
            w.Dispatcher.BeginInvoke(w.BeginRename, DispatcherPriority.Input);
        return w;
    }

    /// <summary>スナップの相手になるフェンスのウィンドウハンドル。</summary>
    public IReadOnlyList<IntPtr> FenceHandles() =>
        _windows.Where(w => w.IsVisible && w.Handle != IntPtr.Zero
                            && !(_groupDrag != null && _groupSelection.Contains(w)))
            .Select(w => w.Handle).ToList();

    public void ApplyStyleToAll(FenceStyle style)
    {
        foreach (var w in _windows)
        {
            w.Host.Style = style.Clone();
            w.ApplyAppearance();
        }
        SaveSoon();
    }

    /// <summary>設定画面の OK／適用。</summary>
    public void ApplySettings()
    {
        ApplyDoubleClickHook();
        ApplyIconMode();
        foreach (var w in _windows) w.RefreshPrivacy();
        RunRules(force: false);
        SaveSoon();
    }

    /// <summary>トレイ・設定画面からプライバシーモードを切り替える。</summary>
    public void SetPrivacyMode(bool enabled)
    {
        Config.PrivacyMode = enabled;
        foreach (var w in _windows) w.RefreshPrivacy();
        SaveSoon();
    }

    // ------------------------------------------------------------------
    // Peek（ホットキーでフェンスを一時的に前面へ）
    // ------------------------------------------------------------------

    public bool IsPeeking => _peeking;

    private void SetupHotkeys()
    {
        var p = new HwndSourceParameters("FakoHotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ExtendedWindowStyle = 0x00000080,          // WS_EX_TOOLWINDOW
        };
        _hotkeySource = new HwndSource(p);
        _hotkeySource.AddHook(HotkeyWndProc);

        bool ok = NativeMethods.RegisterHotKey(_hotkeySource.Handle, PeekHotkeyId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
            NativeMethods.VK_SPACE);
        if (ok) Log.Info("peek hotkey registered: Ctrl+Alt+Space");
        else Log.Info($"peek hotkey registration failed (error={Marshal.GetLastWin32Error()})");

        bool okPalette = NativeMethods.RegisterHotKey(_hotkeySource.Handle, PaletteHotkeyId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
            0x46 /* F */);
        if (okPalette) Log.Info("palette hotkey registered: Ctrl+Alt+F");
        else Log.Info($"palette hotkey registration failed (error={Marshal.GetLastWin32Error()})");
    }

    private IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY) return IntPtr.Zero;
        switch (wParam.ToInt32())
        {
            case PeekHotkeyId:
                TogglePeek();
                handled = true;
                break;
            case PaletteHotkeyId:
                TogglePalette();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Ctrl+Alt+Space: フェンスを一時的に全ウィンドウの上へ出す。もう一度押すと元に戻る。
    /// 一括で隠しているときは、その間だけ表示する。
    /// </summary>
    public void TogglePeek()
    {
        if (_windows.Count == 0) return;
        _peeking = !_peeking;
        if (_peeking)
        {
            _peekWasHidden = AllHidden;
            if (AllHidden) SetAllHidden(false);
            foreach (var w in _windows) w.SetPeek(true);
            Log.Info("peek on");
        }
        else
        {
            foreach (var w in _windows) w.SetPeek(false);
            if (_peekWasHidden) SetAllHidden(true);
            _peekWasHidden = false;
            Log.Info("peek off");
        }
    }

    /// <summary>Ctrl+Alt+F: 全フェンス横断の検索パレット。</summary>
    public void TogglePalette() => PaletteWindow.Toggle();

    /// <summary>検索パレット用: 全フェンスの表示中アイテム。</summary>
    public IEnumerable<(FenceWindow Fence, FenceItem Item)> EnumerateItems()
    {
        foreach (var w in _windows.ToList())
            foreach (var item in w.VisibleItems)
                yield return (w, item);
    }

    /// <summary>書き出したレイアウトを適用する（現在のフェンスを閉じて作り直す）。</summary>
    public bool ReplaceLayout(AppConfig incoming)
    {
        try
        {
            // 読み込み後に問題があっても戻せるよう、今の設定を退避する
            SaveNow();
            if (File.Exists(Store.FilePath))
            {
                var backup = Path.Combine(ConfigStore.Directory,
                    $"config.before-import-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.Copy(Store.FilePath, backup, overwrite: true);
                Log.Info($"layout backup: {backup}");
            }

            SetAllHidden(false);
            foreach (var w in _windows.ToList()) w.Close();
            _windows.Clear();
            _undo.Clear();
            _groupSelection.Clear();

            Config = incoming;
            if (Config.Fences.Count == 0)
            {
                var wa = SystemParameters.WorkArea;
                Config.Fences.Add(new FenceConfig
                {
                    Title = "はじめてのフェンス",
                    Left = wa.Left + 80,
                    Top = wa.Top + 80,
                    Style = Config.DefaultStyle.Clone(),
                });
            }
            foreach (var fc in Config.Fences.OrderBy(f => f.Mode == FenceMode.Unsorted ? 1 : 0))
                ShowFence(fc);

            ApplyIconMode();
            ApplyDoubleClickHook();
            foreach (var w in _windows) w.RefreshPrivacy();
            SaveSoon();
            Log.Info($"layout imported: fences={Config.Fences.Count}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("レイアウトの読み込みに失敗", ex);
            return false;
        }
    }

    public void OpenSettings(int tab = 0) => SettingsWindow.ShowSingle(tab);

    // ------------------------------------------------------------------
    // 自動振り分け
    // ------------------------------------------------------------------

    private readonly List<FileSystemWatcher> _desktopWatchers = new();
    private DispatcherTimer? _rulesTimer;

    /// <summary>手でフェンスから外した項目を記録し、ルールで戻さないようにする。</summary>
    public void ExcludeFromRules(IEnumerable<string> paths)
    {
        var set = Config.RuleExclusions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            if (DesktopIconHider.IsOnDesktop(p) && set.Add(p))
                Config.RuleExclusions.Add(p);
        }
        SaveSoon();
    }

    /// <summary>ルールを当てる。force = 自動振り分けがオフでも実行（「今すぐ振り分ける」）。</summary>
    /// <returns>振り分けた件数。</returns>
    public int RunRules(bool force)
    {
        bool enabled = Config.AutoSortEnabled;
        if (force) Config.AutoSortEnabled = true;
        List<(string Path, string FenceId)> moved;
        try { moved = RuleEngine.Apply(Config); }
        finally { Config.AutoSortEnabled = enabled; }

        if (moved.Count == 0) return 0;
        foreach (var fenceId in moved.Select(m => m.FenceId).Distinct())
        {
            if (FindFence(fenceId) is { } w && w.Config.Id == fenceId) w.Reload();
        }
        Log.Info($"auto-sorted {moved.Count} item(s): " +
                 string.Join(" | ", moved.Select(m => $"{Path.GetFileName(m.Path)} → {Config.FindPage(m.FenceId)?.Title}")));
        OnItemsChanged();
        return moved.Count;
    }

    /// <summary>デスクトップに項目が増えたら、少し待ってからルールを当てる。</summary>
    private void SetupDesktopWatchers()
    {
        _rulesTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _rulesTimer.Tick += (_, _) => { _rulesTimer.Stop(); RunRules(force: false); };

        foreach (var dir in new[] { FolderListing.UserDesktop, FolderListing.CommonDesktop }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                var w = new FileSystemWatcher(dir)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    IncludeSubdirectories = false,
                };
                w.Created += OnDesktopChanged;
                w.Renamed += OnDesktopChanged;
                w.EnableRaisingEvents = true;
                _desktopWatchers.Add(w);
            }
            catch (Exception ex)
            {
                Log.Error($"デスクトップを監視できません: {dir}", ex);
            }
        }
    }

    private void OnDesktopChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!Config.AutoSortEnabled || Config.Rules.Count == 0) return;
            _rulesTimer?.Stop();
            _rulesTimer?.Start();
        });
    }

    /// <summary>SSH フェンスを追加する（接続先を入力してもらう）。</summary>
    public void NewRemoteFence()
    {
        var remote = RemoteConnectWindow.Edit(null);
        if (remote == null) return;
        var w = NewFence(FenceMode.Remote, title: remote.Display, rename: false, remote: remote);
        w.Host.Width = Math.Max(w.Host.Width, 420);
        w.Width = w.Host.Width;
        w.SetView(FenceView.Details); // 更新日時・サイズも見たいので詳細表示から
        SaveSoon();
    }

    public void ShowBalloon(string title, string text) => _tray?.ShowBalloon(title, text);

    public void NewPortalFence()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "フェンスに表示するフォルダーを選択" };
        if (dlg.ShowDialog() == true)
            NewFence(FenceMode.Portal, portalPath: dlg.FolderName);
    }

    public void DeleteFence(FenceWindow w)
    {
        int registered = w.Pages.Where(p => p.Mode == FenceMode.Shortcuts).Sum(p => p.Items.Count);
        var name = w.Pages.Count > 1 ? $"「{w.Host.Title}」（{w.Pages.Count} タブ）" : $"「{w.Host.Title}」";
        var msg = registered > 0
            ? $"フェンス{name}を削除しますか？\n（登録していた {registered} 個のファイル自体は削除されません）"
            : $"フェンス{name}を削除しますか？";
        bool lastUnsorted = w.Pages.Any(p => p.Mode == FenceMode.Unsorted) && IsFenceManaged
                            && Config.AllPages().Count(f => f.Mode == FenceMode.Unsorted) == w.Pages.Count(p => p.Mode == FenceMode.Unsorted);
        if (lastUnsorted)
            msg += "\n\n「フェンスで管理する」モードのため、どのフェンスにも入っていない項目はデスクトップに表示されなくなります" +
                   "（設定で「管理しない」に切り替えると、Windows 標準のアイコンが表示されます）。";
        if (MessageBox.Show(msg, "Fako", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        Config.Fences.Remove(w.Host);
        w.Close();
        OnItemsChanged();
    }

    /// <summary>名前を変えたファイルの登録を、すべてのフェンス・ルールの記録で付け替える。</summary>
    public void ReplacePath(string oldPath, string newPath)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        foreach (var f in Config.AllPages())
        {
            for (int i = 0; i < f.Items.Count; i++)
                if (cmp.Equals(f.Items[i], oldPath)) f.Items[i] = newPath;
        }
        for (int i = 0; i < Config.RuleExclusions.Count; i++)
            if (cmp.Equals(Config.RuleExclusions[i], oldPath)) Config.RuleExclusions[i] = newPath;

        ShellIcons.Invalidate(new[] { oldPath, newPath });
        foreach (var w in _windows) w.Reload();
        SaveSoon();
    }

    /// <summary>どこかのショートカットフェンスの中身が変わった。</summary>
    public void OnItemsChanged()
    {
        foreach (var w in _windows.Where(w => w.Config.Mode == FenceMode.Unsorted))
            w.Reload();
        SaveSoon();
    }

    // ------------------------------------------------------------------
    // デスクトップのアイコンの管理
    // ------------------------------------------------------------------

    /// <summary>Windows 標準のデスクトップアイコンを、Fako が隠しているか。</summary>
    private bool _iconsHiddenByFako;

    public bool IsFenceManaged => Config.IconMode != DesktopIconMode.NotManaged;

    /// <summary>
    /// 「フェンスで管理する」なら Windows 標準のアイコンを隠し、未整理フェンスを用意する。
    /// 「管理しない」なら（Fako が隠していた場合だけ）標準のアイコンを戻す。
    /// ファイルそのものには一切触らないので、エクスプローラーでは普通に見える。
    /// </summary>
    private void ApplyIconMode()
    {
        try
        {
            if (IsFenceManaged)
            {
                EnsureUnsortedFence();
                if (Desktop.AreIconsVisible())
                {
                    Desktop.SetIconsVisible(false);
                    Log.Info("desktop icons hidden (fence-managed mode)");
                }
                _iconsHiddenByFako = true;
            }
            else if (_iconsHiddenByFako)
            {
                Desktop.SetIconsVisible(true);
                _iconsHiddenByFako = false;
                Log.Info("desktop icons shown (not managed)");
            }
        }
        catch (Exception ex)
        {
            Log.Error("デスクトップアイコンの切り替えに失敗", ex);
        }
    }

    public void SetIconMode(DesktopIconMode mode)
    {
        Config.IconMode = mode;
        ApplyIconMode();
        SaveSoon();
    }

    /// <summary>どのフェンスにも入っていない項目の行き場として、未整理フェンスが無ければ作る。</summary>
    private void EnsureUnsortedFence()
    {
        if (Config.AllPages().Any(f => f.Mode == FenceMode.Unsorted)) return;
        var wa = SystemParameters.WorkArea;
        var config = new FenceConfig
        {
            Mode = FenceMode.Unsorted,
            Title = "未整理",
            Style = Config.DefaultStyle.Clone(),
            Width = 420,
            Height = 280,
            Left = wa.Left + 24,
            Top = wa.Bottom - 280 - 24,
        };
        Config.Fences.Add(config);
        ShowFence(config);
        SaveSoon();
        Log.Info("unsorted fence created");
    }

    // ------------------------------------------------------------------
    // 表示切り替え
    // ------------------------------------------------------------------

    public void ToggleAllHidden() => SetAllHidden(!AllHidden);

    public void SetAllHidden(bool hidden)
    {
        AllHidden = hidden;
        foreach (var w in _windows.ToList())
            w.FadeTo(!hidden);
    }

    public void SetDoubleClickToHide(bool enabled)
    {
        Config.DoubleClickToHide = enabled;
        ApplyDoubleClickHook();
        SaveSoon();
    }

    private void ApplyDoubleClickHook()
    {
        if (Config.DoubleClickToHide && _hook == null)
        {
            try
            {
                _hook = new DesktopDoubleClickHook(Dispatcher);
                _hook.DoubleClicked += ToggleAllHidden;
                Log.Info("mouse hook installed");
            }
            catch (Exception ex)
            {
                Log.Error("マウスフックの登録に失敗", ex);
            }
        }
        else if (!Config.DoubleClickToHide && _hook != null)
        {
            _hook.Dispose();
            _hook = null;
        }
    }

    // ------------------------------------------------------------------
    // 保存・エラー
    // ------------------------------------------------------------------

    public void SaveSoon()
    {
        if (_saveTimer == null) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private bool _saveFailedReported;

    private void SaveNow()
    {
        try
        {
            Store.Save(Config);
        }
        catch (Exception ex)
        {
            Log.Error($"設定の保存に失敗: {Store.FilePath}", ex);
            if (!_saveFailedReported)
            {
                _saveFailedReported = true;
                _tray?.ShowBalloon("設定を保存できません", $"{ex.Message}\nログ: {Log.CurrentPath}");
            }
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("未処理の例外", e.Exception);
        MessageBox.Show($"エラーが発生しました。\n{e.Exception.Message}\n\n詳細: {Log.CurrentPath}",
            "Fako", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
