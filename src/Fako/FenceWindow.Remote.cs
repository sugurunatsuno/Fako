using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Fako.Interop;
using Fako.Models;
using Fako.Remote;

namespace Fako;

/// <summary>
/// SSH フェンス（SFTP でつないだ先のフォルダーを表示・操作する）の部分。
/// </summary>
public partial class FenceWindow
{
    /// <summary>SSH フェンスの中でドラッグするときのデータ形式（リモートのパス）。</summary>
    public const string RemoteFormat = "Fako.RemotePaths";

    private static string TempRoot => Path.Combine(Path.GetTempPath(), "Fako", "sftp");

    /// <summary>ページ（タブ）ごとの SSH 接続。タブを切り替えても切らずに持っておく。</summary>
    private readonly Dictionary<string, RemoteSession> _sessions = new();

    private RemoteSession? CurrentSession => _sessions.GetValueOrDefault(Config.Id);
    private string? _remoteDir;
    private readonly Dictionary<string, RemoteEntry> _remoteEntries = new(StringComparer.Ordinal);
    private CancellationTokenSource? _listCts;
    private DispatcherTimer? _remotePollTimer;
    private readonly Dictionary<string, EditSession> _edits = new(StringComparer.OrdinalIgnoreCase);
    private string _remoteIdleStatus = "";

    /// <summary>ダウンロードして開いたファイル。保存されたら書き戻す。</summary>
    private sealed class EditSession
    {
        public required string LocalPath { get; init; }
        public required string RemotePath { get; init; }
        public required FileSystemWatcher Watcher { get; init; }
        public required DispatcherTimer Debounce { get; init; }
        public required RemoteSession Session { get; init; }
        public required string PageId { get; init; }
        public required string Display { get; init; }
        public DateTime LastSynced { get; set; }
    }

    private bool IsRemote => Config.Mode == FenceMode.Remote && Config.Remote != null;

    private RemoteSession Session
    {
        get
        {
            if (!_sessions.TryGetValue(Config.Id, out var s))
                _sessions[Config.Id] = s = RemoteUi.CreateSession(Config.Remote!);
            return s;
        }
    }

    // ------------------------------------------------------------------
    // 表示
    // ------------------------------------------------------------------

    private void ApplyRemoteUi()
    {
        var v = IsRemote ? Visibility.Visible : Visibility.Collapsed;
        RemoteBar.Visibility = v;
        RemoteStatus.Visibility = v;
        if (IsRemote && _remotePollTimer == null)
        {
            // 他の人・他のプログラムの変更も拾えるよう、ときどき読み直す
            _remotePollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _remotePollTimer.Tick += (_, _) => { if (IsVisible && !_menuOpen) ReloadRemote(quiet: true); };
            _remotePollTimer.Start();
        }
        else if (!IsRemote && _remotePollTimer != null)
        {
            _remotePollTimer.Stop();
            _remotePollTimer = null;
        }
    }

    private void SetRemoteStatus(string text, bool idle = false)
    {
        // ドラッグでのダウンロードは、シェルのコピー処理のスレッドから呼ばれることがある
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetRemoteStatus(text, idle));
            return;
        }
        RemoteStatus.Text = text;
        RemoteStatus.ToolTip = text;
        if (idle) _remoteIdleStatus = text;
    }

    private async void ReloadRemote(bool quiet = false)
    {
        ApplyRemoteUi();
        var cfg = Config.Remote!;
        // SSH エージェント認証で未接続のときは、60 秒の定期更新で勝手に再接続しない
        // （Bitwarden 等の承認／ロック解除ダイアログが 1 分ごとに連打されるのを防ぐ）
        if (quiet && cfg.Auth == RemoteAuth.Agent && CurrentSession is not { IsConnected: true })
            return;
        _listCts?.Cancel();
        var cts = new CancellationTokenSource();
        _listCts = cts;
        if (!quiet)
        {
            if (CurrentSession == null && cfg.Auth == RemoteAuth.Agent)
                SetRemoteStatus($"{cfg.Display} に接続しています…（SSH エージェントの承認待ち）");
            else
                SetRemoteStatus(CurrentSession == null ? $"{cfg.Display} に接続しています…" : "読み込んでいます…");
        }

        try
        {
            var dir = _remoteDir ?? await Session.ConnectAsync(cfg.Path);
            var list = await Session.ListAsync(dir, cts.Token);
            if (cts.IsCancellationRequested) return;

            _remoteDir = dir;
            if (cfg.Path != dir) { cfg.Path = dir; App.Current.SaveSoon(); }
            RemotePathText.Text = dir;
            RemotePathText.ToolTip = $"{cfg.Display}:{dir}";
            RemoteUpButton.IsEnabled = dir != "/";

            var selected = ItemsList.SelectedItems.Cast<FenceItem>().Select(i => i.Path).ToHashSet();
            _remoteEntries.Clear();
            var loaded = new List<FenceItem>();
            foreach (var e in list)
            {
                if (!cfg.ShowHidden && e.Name.StartsWith('.')) continue;
                _remoteEntries[e.FullName] = e;
                loaded.Add(ShellIcons.CreateVirtualItem(e.FullName, e.Name, e.IsDirectory, e.Length, e.LastWriteTime, _iconPx));
            }
            _items.Clear();
            foreach (var item in Sort(loaded)) _items.Add(item);
            foreach (var item in _items.Where(i => selected.Contains(i.Path))) ItemsList.SelectedItems.Add(item);

            EmptyHint.Text = "このフォルダーは空です\nファイルをドロップするとアップロードします";
            EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetRemoteStatus($"{cfg.Display}  ·  {_items.Count} 項目", idle: true);
        }
        catch (Exception) when (cts.IsCancellationRequested)
        {
            // 新しい読み込み（F5・定期更新・タブの切り替え）に置き換えられた。
            // 置き換えられた側の失敗（接続のタイムアウトなど）は表示しない
        }
        catch (Exception ex)
        {
            Log.Error($"SSH フェンスの読み込みに失敗: {cfg.Display}:{_remoteDir ?? cfg.Path}", ex);
            if (quiet) return; // 定期更新の失敗では表示を消さない
            _items.Clear();
            _remoteEntries.Clear();
            if (ex is HostKeyMismatchException or CredentialCancelledException or SshAgentException) DisposeSession(Config.Id);
            EmptyHint.Text = RemoteUi.Describe(ex) + "\n\n右クリック →「接続設定…」で見直すか、F5 でやり直してください。";
            EmptyHint.Visibility = Visibility.Visible;
            SetRemoteStatus($"{cfg.Display}  ·  接続できません", idle: true);
        }
    }

    private void NavigateRemote(string dir)
    {
        _remoteDir = dir;
        Config.Remote!.Path = dir;
        App.Current.SaveSoon();
        ItemsList.UnselectAll();
        ReloadRemote();
    }

    private void OnRemoteUp(object sender, RoutedEventArgs e) => RemoteUp();

    private void RemoteUp()
    {
        if (_remoteDir is { } d && d != "/") NavigateRemote(RemoteSession.Parent(d));
    }

    private void OnRemoteRefresh(object sender, RoutedEventArgs e) => ReloadRemote();

    /// <summary>転送などの間の進み具合を、状態欄に（表示の更新は 0.1 秒に 1 回まで）。</summary>
    private IProgress<string> RemoteProgress()
    {
        var last = DateTime.MinValue;
        return new Progress<string>(text =>
        {
            if ((DateTime.Now - last).TotalMilliseconds < 100) return;
            last = DateTime.Now;
            SetRemoteStatus(text);
        });
    }

    /// <summary>リモート操作を実行し、失敗したら知らせる。終わったら読み直す。</summary>
    private async Task RunRemote(string what, Func<Task> op, bool reload = true)
    {
        try
        {
            await op();
        }
        catch (Exception ex)
        {
            Log.Error($"SSH: {what}に失敗", ex);
            MessageBox.Show($"{what}できませんでした。\n\n{RemoteUi.Describe(ex)}", "Fako — SSH",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetRemoteStatus(_remoteIdleStatus);
            if (reload) ReloadRemote(quiet: true);
        }
    }

    private List<RemoteEntry> SelectedRemoteEntries() =>
        ItemsList.SelectedItems.Cast<FenceItem>()
            .Select(i => _remoteEntries.TryGetValue(i.Path, out var e) ? e : null)
            .OfType<RemoteEntry>()
            .ToList();

    // ------------------------------------------------------------------
    // 開く・編集
    // ------------------------------------------------------------------

    private async void RemoteOpen(FenceItem item)
    {
        if (!_remoteEntries.TryGetValue(item.Path, out var entry)) return;
        if (entry.IsDirectory) { NavigateRemote(entry.FullName); return; }
        if (entry.IsSymlink)
        {
            // リンク先がフォルダーなら中へ
            try
            {
                if (await Session.IsDirectoryAsync(entry.FullName)) { NavigateRemote(entry.FullName); return; }
            }
            catch (Exception ex)
            {
                Log.Error("SSH: リンク先を調べられません", ex);
            }
        }
        await EditRemoteFile(entry);
    }

    /// <summary>一時フォルダーにダウンロードして関連付けのアプリで開き、保存されたら書き戻す。</summary>
    private async Task EditRemoteFile(RemoteEntry entry)
    {
        var dir = Path.Combine(TempRoot, Config.Id, StableHash(Config.Remote!.HostKeyId + "|" + RemoteSession.Parent(entry.FullName)));
        Directory.CreateDirectory(dir);
        var local = Path.Combine(dir, RemoteSession.SafeName(entry.Name));

        await RunRemote("ダウンロード", async () =>
        {
            StopEditWatch(local); // 自分のダウンロードで「保存された」と誤認しないよう、いったん止める
            await Session.DownloadToAsync(entry, local, RemoteProgress());
            ShellActions.Open(local);
            StartEditWatch(local, entry.FullName);
        }, reload: false);
    }

    private void StartEditWatch(string local, string remote)
    {
        var watcher = new FileSystemWatcher(Path.GetDirectoryName(local)!, Path.GetFileName(local))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        var session = new EditSession
        {
            LocalPath = local,
            RemotePath = remote,
            Watcher = watcher,
            Debounce = debounce,
            LastSynced = File.GetLastWriteTimeUtc(local),
            Session = Session,
            PageId = Config.Id,
            Display = Config.Remote!.Display,
        };
        // エディターは「一時ファイルに書いて置き換える」こともあるので、作成・名前変更も拾う
        FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
        watcher.Changed += changed;
        watcher.Created += changed;
        watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            await UploadEditedFile(session);
        };
        watcher.EnableRaisingEvents = true;
        _edits[local] = session;
    }

    private void StopEditWatch(string local)
    {
        if (!_edits.Remove(local, out var s)) return;
        s.Debounce.Stop();
        s.Watcher.Dispose();
    }

    private async Task UploadEditedFile(EditSession s)
    {
        if (!File.Exists(s.LocalPath)) return;
        var stamp = File.GetLastWriteTimeUtc(s.LocalPath);
        if (stamp <= s.LastSynced) return;
        try
        {
            await s.Session.UploadToAsync(s.LocalPath, s.RemotePath, RemoteProgress());
            s.LastSynced = stamp;
            Log.Info($"SSH: uploaded edited file {s.LocalPath} -> {s.RemotePath}");
            App.Current.ShowBalloon("SSH にアップロードしました", $"{Path.GetFileName(s.LocalPath)} → {s.Display}:{s.RemotePath}");
        }
        catch (IOException)
        {
            // まだ書き込み中（ロックされている）→ 少し待ってもう一度
            s.Debounce.Start();
        }
        catch (Exception ex)
        {
            Log.Error("SSH: 編集したファイルを書き戻せません", ex);
            MessageBox.Show($"編集した「{Path.GetFileName(s.LocalPath)}」をアップロードできませんでした。\n\n{RemoteUi.Describe(ex)}\n\n" +
                            $"手元のファイル: {s.LocalPath}", "Fako — SSH", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetRemoteStatus(_remoteIdleStatus);
            if (IsRemote && Config.Id == s.PageId) ReloadRemote(quiet: true);
        }
    }

    // ------------------------------------------------------------------
    // 転送・ファイル操作
    // ------------------------------------------------------------------

    private Task RemoteUpload(IReadOnlyList<string> localPaths, string remoteDir) =>
        RunRemote("アップロード", () => Session.UploadAsync(localPaths, remoteDir, RemoteProgress()));

    private void RemoteUploadDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "アップロードするファイルを選択", Multiselect = true };
        if (dlg.ShowDialog() == true && _remoteDir != null)
            _ = RemoteUpload(dlg.FileNames, _remoteDir);
    }

    private async void RemoteDownload(List<RemoteEntry> entries, bool showInFolder)
    {
        if (entries.Count == 0) return;
        var target = DownloadsFolder();
        List<string> saved = new();
        await RunRemote("ダウンロード", async () =>
        {
            saved = await Session.DownloadAsync(entries, target, RemoteProgress());
        }, reload: false);
        if (saved.Count == 0) return;
        App.Current.ShowBalloon("ダウンロードしました", $"{string.Join("、", saved.Take(3).Select(Path.GetFileName))}{(saved.Count > 3 ? " ほか" : "")} → {target}");
        if (showInFolder) ShellActions.OpenLocation(saved[0]);
    }

    private async void RemoteDelete(List<RemoteEntry> entries)
    {
        if (entries.Count == 0) return;
        var names = string.Join("\n", entries.Take(8).Select(e => (e.IsDirectory ? "📁 " : "") + e.Name)) + (entries.Count > 8 ? "\n…" : "");
        if (MessageBox.Show($"{Config.Remote!.Display} の次の {entries.Count} 項目を削除しますか？\nごみ箱には入らず、元に戻せません。\n\n{names}",
                "Fako — SSH", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        await RunRemote("削除", () => Session.DeleteAsync(entries, RemoteProgress()));
    }

    private string? ValidateRemoteName(string name, string? original)
    {
        if (name.Contains('/') || name.Contains('\0')) return "「/」は使えません";
        if (name is "." or "..") return "この名前は使えません";
        if (name != original && _remoteEntries.Values.Any(e => e.Name == name)) return "同じ名前のファイルまたはフォルダーがすでにあります";
        return null;
    }

    private async void RemoteRename(RemoteEntry entry)
    {
        var dlg = new RenameWindow(entry.Name, entry.IsDirectory, n => ValidateRemoteName(n, entry.Name));
        if (dlg.ShowDialog() != true) return;
        var to = RemoteSession.Combine(RemoteSession.Parent(entry.FullName), dlg.NewName);
        await RunRemote("名前の変更", () => Session.RenameAsync(entry.FullName, to));
    }

    private async void RemoteNewFolder()
    {
        if (_remoteDir == null) return;
        var name = "新しいフォルダー";
        for (int i = 2; _remoteEntries.Values.Any(e => e.Name == name); i++) name = $"新しいフォルダー ({i})";
        var dlg = new RenameWindow(name, true, n => ValidateRemoteName(n, null), "新しいフォルダー", "フォルダーの名前:");
        if (dlg.ShowDialog() != true) return;
        await RunRemote("フォルダーの作成", () => Session.CreateDirectoryAsync(RemoteSession.Combine(_remoteDir, dlg.NewName)));
    }

    private async void RemoteMove(IReadOnlyList<string> paths, string targetDir)
    {
        var moves = paths.Where(p => p != targetDir && RemoteSession.Parent(p) != targetDir).ToList();
        if (moves.Count == 0) return;
        await RunRemote("移動", async () =>
        {
            foreach (var p in moves)
                await Session.RenameAsync(p, RemoteSession.Combine(targetDir, p[(p.TrimEnd('/').LastIndexOf('/') + 1)..]));
        });
    }

    private void CopyRemotePath(IEnumerable<string> paths, bool withHost)
    {
        var cfg = Config.Remote!;
        var host = cfg.SshConfigAlias ?? cfg.Display;
        Clipboard.SetText(string.Join(Environment.NewLine, paths.Select(p => withHost ? $"{host}:{p}" : p)));
    }

    /// <summary>Windows Terminal（無ければコンソール）で ssh を開き、そのフォルダーへ cd する。</summary>
    private void OpenRemoteTerminal(string dir)
    {
        var cfg = Config.Remote!;
        var args = new List<string>();
        if (cfg.SshConfigAlias != null)
        {
            args.Add(cfg.SshConfigAlias); // ~/.ssh/config の設定をそのまま使う
        }
        else
        {
            if (cfg.Port != 22) { args.Add("-p"); args.Add(cfg.Port.ToString()); }
            if (cfg.Auth == RemoteAuth.Agent && !string.IsNullOrWhiteSpace(cfg.AgentPipe))
            {
                args.Add("-o");
                args.Add($"\"IdentityAgent={cfg.AgentPipe}\"");
            }
            if (cfg.Auth == RemoteAuth.KeyFile && cfg.KeyFile != null) { args.Add("-i"); args.Add($"\"{cfg.KeyFile}\""); }
            args.Add($"{cfg.User}@{cfg.Host}");
        }
        var quoted = "'" + dir.Replace("'", "'\\''") + "'";
        var ssh = $"-t {string.Join(" ", args)} \"cd {quoted} && exec $SHELL -l\"";
        try
        {
            var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
            if (File.Exists(wt))
                Process.Start(new ProcessStartInfo(wt, $"new-tab --title \"{cfg.Display}\" ssh {ssh}") { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo("ssh.exe", ssh) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ターミナルを開けませんでした（Windows の OpenSSH クライアントが必要です）。\n\n{ex.Message}", "Fako — SSH",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void EditRemoteSettings()
    {
        var cfg = RemoteConnectWindow.Edit(Config.Remote);
        if (cfg == null) return;
        bool defaultTitle = Config.Remote == null || Config.Title == Config.Remote.Display;
        Config.Remote = cfg;
        if (defaultTitle) Config.Title = cfg.Display;
        ResetRemote();
        ApplyAppearance();
        App.Current.SaveSoon();
        ReloadRemote();
    }

    private void ResetRemote()
    {
        _listCts?.Cancel();
        DisposeSession(Config.Id);
        _remoteDir = null;
        _remoteEntries.Clear();
    }

    private void DisposeSession(string pageId)
    {
        if (_sessions.Remove(pageId, out var s)) s.Dispose();
    }

    private void DisposeRemote()
    {
        ResetRemote();
        foreach (var id in _sessions.Keys.ToList()) DisposeSession(id);
        _remotePollTimer?.Stop();
        foreach (var local in _edits.Keys.ToList()) StopEditWatch(local);
    }

    // ------------------------------------------------------------------
    // ドラッグ＆ドロップ
    // ------------------------------------------------------------------

    /// <summary>
    /// SSH フェンスからのドラッグ。エクスプローラーへドロップすると、その時点でダウンロードして渡す（仮想ファイル）。
    /// 同じフェンスの中でフォルダーの上へ落とすと移動。
    /// </summary>
    private void StartRemoteDrag(List<FenceItem> items)
    {
        var entries = items.Select(i => _remoteEntries.TryGetValue(i.Path, out var e) ? e : null).OfType<RemoteEntry>().ToList();
        if (entries.Count == 0) return;
        var session = Session;
        var dragDir = Path.Combine(TempRoot, "drag", Guid.NewGuid().ToString("N"));

        // ここで呼ばれる処理は、エクスプローラーからの要求で UI スレッド上で同期的に実行される
        List<RemoteDataObject.VirtualFile> Expand()
        {
            SetRemoteStatus("ドラッグする項目を調べています…");
            var result = new List<RemoteDataObject.VirtualFile>();
            void Add(RemoteEntry e, string rel)
            {
                if (e.IsDirectory)
                {
                    result.Add(new(rel, e, true, 0, e.LastWriteTime));
                    foreach (var child in session.ListAsync(e.FullName).GetAwaiter().GetResult())
                        Add(child, rel + "\\" + RemoteSession.SafeName(child.Name));
                }
                else
                {
                    result.Add(new(rel, e, false, e.Length, e.LastWriteTime));
                }
            }
            try
            {
                foreach (var e in entries) Add(e, RemoteSession.SafeName(e.Name));
            }
            finally
            {
                SetRemoteStatus(_remoteIdleStatus);
            }
            return result;
        }

        string Download(RemoteEntry e)
        {
            var local = Path.Combine(dragDir, StableHash(e.FullName), RemoteSession.SafeName(e.Name));
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            SetRemoteStatus($"ダウンロード中: {e.Name}");
            try
            {
                session.DownloadToAsync(e, local).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Error($"SSH: ドラッグでのダウンロードに失敗 {e.FullName}", ex);
                throw;
            }
            finally
            {
                SetRemoteStatus(_remoteIdleStatus);
            }
            return local;
        }

        var data = new RemoteDataObject(entries.Select(e => e.FullName).ToArray(), Config.Id, Expand, Download);
        int effect = OleDrag.Run(data, OleDrag.DROPEFFECT_COPY | OleDrag.DROPEFFECT_MOVE);
        if (effect != OleDrag.DROPEFFECT_NONE) Log.Info($"SSH: dragged {entries.Count} item(s) out (effect={effect})");
    }

    private static string[]? RemotePathsOf(DragEventArgs e) =>
        RemoteDataObject.ReadText(e.Data, RemoteFormat)?.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>ドロップ先のリモートのフォルダー（フォルダーの上ならその中、そうでなければ今のフォルダー）。</summary>
    private string? RemoteDropTarget(DragEventArgs e)
    {
        var container = ItemsList.InputHitTest(e.GetPosition(ItemsList)) is DependencyObject hit ? FindAncestor<ListBoxItem>(hit) : null;
        if (container?.DataContext is FenceItem { IsFolder: true } folder) return folder.Path;
        return _remoteDir;
    }

    private DragDropEffects GetRemoteDropEffect(DragEventArgs e)
    {
        if (_remoteDir == null) return DragDropEffects.None;
        if (RemotePathsOf(e) is { } remotePaths)
        {
            // 同じ SSH フェンスの中で、フォルダーの上へ → 移動
            if (RemoteDataObject.ReadText(e.Data, SourceFenceFormat) != Config.Id) return DragDropEffects.None;
            var target = RemoteDropTarget(e);
            return target != _remoteDir && target != null && !remotePaths.Contains(target) ? DragDropEffects.Move : DragDropEffects.None;
        }
        var local = DroppedPaths(e).Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        return local.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnRemoteDrop(DragEventArgs e)
    {
        var target = RemoteDropTarget(e);
        if (target == null) return;
        if (RemotePathsOf(e) is { } remotePaths)
        {
            RemoteMove(remotePaths, target);
            return;
        }
        var local = DroppedPaths(e).Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        if (local.Length > 0) _ = RemoteUpload(local, target);
    }

    // ------------------------------------------------------------------
    // メニュー・キー
    // ------------------------------------------------------------------

    private ContextMenu BuildRemoteItemMenu(List<FenceItem> items)
    {
        var entries = SelectedRemoteEntries();
        var first = entries.FirstOrDefault();
        var m = new ContextMenu();
        if (first == null) return m;

        var open = AddItem(m, first.IsDirectory ? "開く" : "開く（保存するとアップロード）", () => RemoteOpen(items[0]));
        open.FontWeight = FontWeights.Bold;
        AddItem(m, "ダウンロード", () => RemoteDownload(entries, showInFolder: false));
        AddItem(m, "ダウンロードしてフォルダーを開く", () => RemoteDownload(entries, showInFolder: true));
        m.Items.Add(new Separator());
        AddItem(m, "名前の変更 (F2)", () => RemoteRename(first)).IsEnabled = entries.Count == 1;
        AddItem(m, entries.Count > 1 ? $"削除（{entries.Count} 個）" : "削除", () => RemoteDelete(entries));
        m.Items.Add(new Separator());
        AddItem(m, "パスをコピー", () => CopyRemotePath(entries.Select(e => e.FullName), withHost: false));
        AddItem(m, "「ホスト:パス」をコピー（scp 用）", () => CopyRemotePath(entries.Select(e => e.FullName), withHost: true));
        AddItem(m, "ターミナルで開く", () => OpenRemoteTerminal(first.IsDirectory ? first.FullName : RemoteSession.Parent(first.FullName)));
        return m;
    }

    /// <summary>何もない所の右クリックメニューに入れる、SSH フェンス用の項目。</summary>
    private void AddRemoteFenceMenuItems(ContextMenu m)
    {
        var cfg = Config.Remote!;
        AddItem(m, "上の階層へ (Backspace)", RemoteUp).IsEnabled = _remoteDir is { } d && d != "/";
        AddItem(m, "ホームへ", () => { if (CurrentSession?.Home is { } h) NavigateRemote(h); }).IsEnabled = CurrentSession?.Home != null;
        AddItem(m, "新しいフォルダー…", RemoteNewFolder).IsEnabled = _remoteDir != null;
        AddItem(m, "アップロード…", RemoteUploadDialog).IsEnabled = _remoteDir != null;
        AddItem(m, "ターミナルで開く", () => { if (_remoteDir != null) OpenRemoteTerminal(_remoteDir); }).IsEnabled = _remoteDir != null;
        AddItem(m, "このフォルダーのパスをコピー", () => { if (_remoteDir != null) CopyRemotePath(new[] { _remoteDir }, withHost: false); });
        var hidden = AddItem(m, "「.」で始まる項目を表示", () =>
        {
            cfg.ShowHidden = !cfg.ShowHidden;
            App.Current.SaveSoon();
            ReloadRemote();
        });
        hidden.IsCheckable = true;
        hidden.IsChecked = cfg.ShowHidden;
        AddItem(m, "接続設定…", EditRemoteSettings);
        m.Items.Add(new Separator());
    }

    private bool HandleRemoteKey(KeyEventArgs e)
    {
        var entries = SelectedRemoteEntries();
        switch (e.Key)
        {
            case Key.Enter when ItemsList.SelectedItem is FenceItem item:
                RemoteOpen(item);
                return true;
            case Key.Delete:
                RemoteDelete(entries);
                return true;
            case Key.F2 when entries.Count == 1:
                RemoteRename(entries[0]);
                return true;
            case Key.Back:
            case Key.Up when Keyboard.Modifiers == ModifierKeys.Alt:
                RemoteUp();
                return true;
            case Key.F5:
                ReloadRemote();
                return true;
            case Key.Apps:
            case Key.F10 when Keyboard.Modifiers == ModifierKeys.Shift:
                if (ItemsList.SelectedItems.Count > 0) OpenWpfMenu(BuildRemoteItemMenu(ItemsList.SelectedItems.Cast<FenceItem>().ToList()));
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------------

    private static string StableHash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) h = (h ^ c) * 16777619;
        return h.ToString("x8");
    }

    private static string DownloadsFolder()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) == 0)
        {
            try { return Marshal.PtrToStringUni(p)!; }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
