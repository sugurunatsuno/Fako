using System.IO;
using System.Windows;
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
        w.Closed += (_, _) => _windows.Remove(w);
        if (!AllHidden)
        {
            w.Show();
            w.SetRaised(_raised);
        }
        return w;
    }

    /// <summary>そのページ（タブを含む）を持っているフェンスのウィンドウ。</summary>
    public FenceWindow? FindFence(string pageId) => _windows.FirstOrDefault(w => w.Pages.Any(p => p.Id == pageId));

    /// <summary>
    /// Tab / Shift+Tab: 左右に隣り合うフェンスへフォーカスを移す。
    /// 同じ行（縦に重なる）ものを優先し、無ければ左右にある最も近いもの。端では折り返さない。
    /// </summary>
    public void FocusAdjacentFence(FenceWindow from, int direction)
    {
        var candidates = _windows
            .Where(w => w != from && w.IsVisible && w.Handle != IntPtr.Zero)
            .Select(w => (Window: w, SameRow: VerticalOverlap(from, w)))
            .Where(x => direction > 0
                ? x.Window.Left >= from.Left + from.ActualWidth - 1
                : x.Window.Left + x.Window.ActualWidth <= from.Left + 1)
            .ToList();
        if (candidates.Count == 0) return;

        var pool = candidates.Any(x => x.SameRow) ? candidates.Where(x => x.SameRow) : candidates;
        var next = direction > 0
            ? pool.OrderBy(x => x.Window.Left).First()
            : pool.OrderByDescending(x => x.Window.Left + x.Window.ActualWidth).First();
        next.Window.FocusList();
    }

    private static bool VerticalOverlap(FenceWindow a, FenceWindow b) =>
        a.Top < b.Top + b.ActualHeight && b.Top < a.Top + a.ActualHeight;

    public IReadOnlyList<FenceWindow> FenceWindows => _windows;

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
        _windows.Where(w => w.IsVisible && w.Handle != IntPtr.Zero).Select(w => w.Handle).ToList();

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
        RunRules(force: false);
        SaveSoon();
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
