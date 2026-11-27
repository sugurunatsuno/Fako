using System.IO;
using Fako.Interop;
using Fako.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Fako.Remote;

public sealed record RemoteEntry(string FullName, string Name, bool IsDirectory, bool IsSymlink, long Length, DateTime LastWriteTime);

/// <summary>ホスト鍵が以前と違う（なりすましの可能性）。</summary>
public sealed class HostKeyMismatchException(string message) : Exception(message);

/// <summary>パスワード／パスフレーズの入力がキャンセルされた。</summary>
public sealed class CredentialCancelledException() : Exception("資格情報の入力がキャンセルされました");

/// <summary>
/// 1 つの SSH フェンスの SFTP 接続。
/// 操作はすべてバックグラウンドで 1 つずつ順に実行する（SftpClient の同時利用を避ける）。
/// 切れていたら次の操作の前につなぎ直す。
/// </summary>
public sealed class RemoteSession : IDisposable
{
    /// <summary>ホスト鍵の確認（初回）。引数: 接続先、鍵の種類と指紋。信頼するなら true。UI スレッドで呼ばれる。</summary>
    public Func<string, string, bool>? ConfirmHostKey { get; set; }
    /// <summary>パスワード／パスフレーズの入力。引数: 説明文。キャンセルなら null。UI スレッドで呼ばれる。</summary>
    public Func<string, string?>? AskSecret { get; set; }
    /// <summary>UI スレッドで処理を実行する（ダイアログを出すため）。</summary>
    public Func<Func<object?>, object?>? InvokeOnUi { get; set; }

    /// <summary>SSH エージェントクライアントの生成（テストで差し替えるため）。</summary>
    internal Func<string?, ISshAgentClient> AgentClientFactory { get; set; } = pipe => OpenSshAgentClient.Create(pipe);

    private readonly RemoteConfig _cfg;
    private readonly Dictionary<string, string> _knownHosts;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private SftpClient? _client;
    private string? _hostKeyProblem;

    public string? Home { get; private set; }

    /// <summary>いま SFTP でつながっているか。</summary>
    public bool IsConnected => _client is { IsConnected: true };

    public RemoteSession(RemoteConfig cfg, Dictionary<string, string> knownHosts)
    {
        _cfg = cfg;
        _knownHosts = knownHosts;
    }

    // ------------------------------------------------------------------
    // 接続
    // ------------------------------------------------------------------

    private string? Secret
    {
        get => Dpapi.Unprotect(_cfg.ProtectedSecret) ?? SecretCache.Get(_cfg);
    }

    private void EnsureConnected()
    {
        if (_client is { IsConnected: true }) return;
        _client?.Dispose();
        _client = null;

        var secret = Secret;
        for (int attempt = 0; ; attempt++)
        {
            var client = new SftpClient(BuildConnectionInfo(ref secret));
            client.KeepAliveInterval = TimeSpan.FromSeconds(30);
            client.OperationTimeout = TimeSpan.FromSeconds(60);
            client.HostKeyReceived += OnHostKeyReceived;
            _hostKeyProblem = null;
            try
            {
                client.Connect();
            }
            catch (SshConnectionException) when (_hostKeyProblem != null)
            {
                client.Dispose();
                throw new HostKeyMismatchException(_hostKeyProblem);
            }
            catch (SshAuthenticationException) when (attempt < 2 && AskSecret != null && _cfg.Auth != RemoteAuth.Agent)
            {
                client.Dispose();
                // パスワード違い・未入力 → 入力してもらってやり直す
                secret = Ask(_cfg.Auth == RemoteAuth.Password
                    ? $"{_cfg.Display} のパスワードを入力してください（認証に失敗しました）"
                    : $"{_cfg.Display} への接続に失敗しました。鍵のパスフレーズを入力してください");
                continue;
            }
            catch
            {
                client.Dispose();
                throw;
            }

            _client = client;
            Home = client.WorkingDirectory;
            if (secret != null && _cfg.ProtectedSecret == null && _cfg.Auth != RemoteAuth.Agent)
                SecretCache.Set(_cfg, secret);
            Log.Info($"sftp connected: {_cfg.Display} home={Home}");
            return;
        }
    }

    private ConnectionInfo BuildConnectionInfo(ref string? secret)
    {
        AuthenticationMethod auth;
        if (_cfg.Auth == RemoteAuth.Password)
        {
            secret ??= Ask($"{_cfg.Display} のパスワードを入力してください");
            auth = new PasswordAuthenticationMethod(_cfg.User, secret);
        }
        else if (_cfg.Auth == RemoteAuth.Agent)
        {
            var client = AgentClientFactory(_cfg.AgentPipe);
            var identities = client.RequestIdentities();
            if (identities.Count == 0)
                throw new SshAgentException(
                    "SSH エージェントに鍵が登録されていません。Bitwarden や ssh-agent に SSH 鍵を追加し、ロックを解除してください。");

            var fingerprint = _cfg.AgentKeyFingerprint?.Trim();
            IPrivateKeySource[] sources;
            if (string.IsNullOrEmpty(fingerprint))
            {
                sources = new IPrivateKeySource[identities.Count];
                for (int i = 0; i < identities.Count; i++)
                    sources[i] = new SshAgentPrivateKeySource(client, identities[i]);
            }
            else
            {
                var matched = new List<IPrivateKeySource>(1);
                for (int i = 0; i < identities.Count; i++)
                {
                    if (string.Equals(identities[i].FingerprintSha256, fingerprint, StringComparison.Ordinal))
                        matched.Add(new SshAgentPrivateKeySource(client, identities[i]));
                }
                if (matched.Count == 0)
                    throw new SshAgentException(
                        $"指定された鍵（{_cfg.AgentKeyFingerprint}）が SSH エージェントに見つかりません。アプリのロック状態や鍵設定を確認してください。");
                sources = matched.ToArray();
            }

            auth = new PrivateKeyAuthenticationMethod(_cfg.User, sources);
        }
        else
        {
            var keyPath = _cfg.KeyFile ?? SshConfigReader.DefaultKeyFile()
                          ?? throw new InvalidOperationException("秘密鍵ファイルが指定されていません");
            PrivateKeyFile key;
            try
            {
                key = string.IsNullOrEmpty(secret) ? new PrivateKeyFile(keyPath) : new PrivateKeyFile(keyPath, secret);
            }
            catch (SshPassPhraseNullOrEmptyException)
            {
                secret = Ask($"鍵 {Path.GetFileName(keyPath)} のパスフレーズを入力してください");
                key = new PrivateKeyFile(keyPath, secret);
            }
            catch (SshException) when (!string.IsNullOrEmpty(secret))
            {
                // パスフレーズ違い
                secret = Ask($"鍵 {Path.GetFileName(keyPath)} のパスフレーズが違います。もう一度入力してください");
                key = new PrivateKeyFile(keyPath, secret);
            }
            auth = new PrivateKeyAuthenticationMethod(_cfg.User, key);
        }
        return new ConnectionInfo(_cfg.Host, _cfg.Port, _cfg.User, auth) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private string Ask(string prompt)
    {
        if (AskSecret == null || InvokeOnUi == null) throw new CredentialCancelledException();
        var answer = (string?)InvokeOnUi(() => AskSecret(prompt));
        return answer ?? throw new CredentialCancelledException();
    }

    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        var id = _cfg.HostKeyId;
        var fingerprint = $"{e.HostKeyName} SHA256:{e.FingerPrintSHA256}";
        lock (_knownHosts)
        {
            if (_knownHosts.TryGetValue(id, out var known))
            {
                if (known == fingerprint)
                {
                    e.CanTrust = true;
                    return;
                }
                e.CanTrust = false;
                _hostKeyProblem =
                    $"{id} のホスト鍵が、前回接続したときと違います。\n\n前回: {known}\n今回: {fingerprint}\n\n" +
                    "サーバーの再インストールなどで鍵が変わった場合を除き、なりすましの可能性があります。" +
                    "鍵が変わったことを確認できたら、接続設定の「信頼済みのホスト鍵を削除」から登録し直してください。";
                Log.Info($"host key mismatch: {id} known={known} got={fingerprint}");
                return;
            }
        }

        bool trust = InvokeOnUi != null && ConfirmHostKey != null && (bool)(InvokeOnUi(() => ConfirmHostKey(id, fingerprint)) ?? false);
        e.CanTrust = trust;
        if (trust)
        {
            lock (_knownHosts) _knownHosts[id] = fingerprint;
            Log.Info($"host key trusted: {id} {fingerprint}");
        }
        else
        {
            _hostKeyProblem = "ホスト鍵を信頼しなかったため、接続を中止しました。";
        }
    }

    /// <summary>操作を 1 つずつ順に、バックグラウンドで実行する。</summary>
    private async Task<T> Run<T>(Func<SftpClient, T> op, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                EnsureConnected();
                try
                {
                    return op(_client!);
                }
                catch (SshConnectionException)
                {
                    // 途中で切れた → 1 回だけつなぎ直してやり直す
                    _client?.Dispose();
                    _client = null;
                    EnsureConnected();
                    return op(_client!);
                }
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private Task Run(Action<SftpClient> op, CancellationToken ct = default) =>
        Run<object?>(c => { op(c); return null; }, ct);

    // ------------------------------------------------------------------
    // 操作
    // ------------------------------------------------------------------

    public static string Combine(string dir, string name) =>
        dir.EndsWith('/') ? dir + name : dir + "/" + name;

    public static string Parent(string path)
    {
        var p = path.TrimEnd('/');
        int i = p.LastIndexOf('/');
        return i <= 0 ? "/" : p[..i];
    }

    /// <summary>接続して、表示するフォルダー（空ならホーム）を返す。</summary>
    public Task<string> ConnectAsync(string? path) =>
        Run(c => string.IsNullOrEmpty(path) ? (Home ?? c.WorkingDirectory) : path);

    public Task<List<RemoteEntry>> ListAsync(string dir, CancellationToken ct = default) =>
        Run(c => c.ListDirectory(dir)
            .Where(f => f.Name is not ("." or ".."))
            .Select(f => new RemoteEntry(f.FullName, f.Name, f.IsDirectory, f.IsSymbolicLink, f.Length, f.LastWriteTime))
            .ToList(), ct);

    /// <summary>シンボリックリンクの先がフォルダーかどうか。</summary>
    public Task<bool> IsDirectoryAsync(string path) =>
        Run(c =>
        {
            try { return c.GetAttributes(path).IsDirectory; }
            catch (SftpPathNotFoundException) { return false; }
        });

    public Task CreateDirectoryAsync(string path) => Run(c => c.CreateDirectory(path));

    public Task RenameAsync(string from, string to) => Run(c => c.RenameFile(from, to));

    public Task DeleteAsync(IEnumerable<RemoteEntry> entries, IProgress<string>? progress = null) =>
        Run(c =>
        {
            foreach (var e in entries)
            {
                progress?.Report($"削除中: {e.Name}");
                if (e.IsDirectory && !e.IsSymlink) DeleteDirectoryRecursive(c, e.FullName);
                else c.DeleteFile(e.FullName);
            }
        });

    private static void DeleteDirectoryRecursive(SftpClient c, string dir)
    {
        foreach (var f in c.ListDirectory(dir))
        {
            if (f.Name is "." or "..") continue;
            if (f.IsDirectory && !f.IsSymbolicLink) DeleteDirectoryRecursive(c, f.FullName);
            else c.DeleteFile(f.FullName);
        }
        c.DeleteDirectory(dir);
    }

    /// <summary>ファイル／フォルダーをリモートの dir にアップロードする（フォルダーは中身ごと）。</summary>
    public Task UploadAsync(IEnumerable<string> localPaths, string remoteDir, IProgress<string>? progress = null) =>
        Run(c =>
        {
            foreach (var local in localPaths)
            {
                var name = Path.GetFileName(local.TrimEnd('\\'));
                if (Directory.Exists(local)) UploadDirectory(c, local, Combine(remoteDir, name), progress);
                else if (File.Exists(local)) UploadFile(c, local, Combine(remoteDir, name), progress);
            }
        });

    /// <summary>1 ファイルを指定のリモートパスへ（編集したファイルの書き戻し用）。</summary>
    public Task UploadToAsync(string localFile, string remotePath, IProgress<string>? progress = null) =>
        Run(c => UploadFile(c, localFile, remotePath, progress));

    private static void UploadFile(SftpClient c, string local, string remote, IProgress<string>? progress)
    {
        var name = Path.GetFileName(local);
        using var fs = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long total = fs.Length;
        c.UploadFile(fs, remote, canOverride: true, uploadCallback: done => progress?.Report(Percent("アップロード中", name, (long)done, total)));
    }

    private static void UploadDirectory(SftpClient c, string local, string remote, IProgress<string>? progress)
    {
        if (!c.Exists(remote)) c.CreateDirectory(remote);
        foreach (var dir in Directory.GetDirectories(local))
            UploadDirectory(c, dir, Combine(remote, Path.GetFileName(dir)), progress);
        foreach (var file in Directory.GetFiles(local))
            UploadFile(c, file, Combine(remote, Path.GetFileName(file)), progress);
    }

    /// <summary>リモートのファイル／フォルダーをローカルの dir にダウンロードする。保存先のパスを返す。</summary>
    public Task<List<string>> DownloadAsync(IEnumerable<RemoteEntry> entries, string localDir, IProgress<string>? progress = null) =>
        Run(c =>
        {
            var saved = new List<string>();
            foreach (var e in entries)
            {
                var target = UniquePath(Path.Combine(localDir, SafeName(e.Name)));
                if (e.IsDirectory) DownloadDirectory(c, e.FullName, target, progress);
                else DownloadFile(c, e.FullName, target, e.Length, progress);
                saved.Add(target);
            }
            return saved;
        });

    /// <summary>1 ファイルを指定のローカルパスへ（上書き。編集用）。</summary>
    public Task DownloadToAsync(RemoteEntry entry, string localPath, IProgress<string>? progress = null) =>
        Run(c => DownloadFile(c, entry.FullName, localPath, entry.Length, progress));

    private static void DownloadFile(SftpClient c, string remote, string local, long total, IProgress<string>? progress)
    {
        var name = Path.GetFileName(local);
        var tmp = local + ".fako-part";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            c.DownloadFile(remote, fs, done => progress?.Report(Percent("ダウンロード中", name, (long)done, total)));
        File.Move(tmp, local, overwrite: true);
    }

    private static void DownloadDirectory(SftpClient c, string remote, string local, IProgress<string>? progress)
    {
        Directory.CreateDirectory(local);
        foreach (var f in c.ListDirectory(remote))
        {
            if (f.Name is "." or "..") continue;
            var target = Path.Combine(local, SafeName(f.Name));
            if (f.IsDirectory) DownloadDirectory(c, f.FullName, target, progress);
            else if (f.IsRegularFile) DownloadFile(c, f.FullName, target, f.Length, progress);
        }
    }

    private static string Percent(string verb, string name, long done, long total) =>
        total > 0 ? $"{verb}: {name}  {Math.Min(100, done * 100 / total)}%" : $"{verb}: {name}";

    /// <summary>Windows で使えない文字を置き換える（Linux のファイル名には : や ? も使える）。</summary>
    public static string SafeName(string name)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return name.TrimEnd('.', ' ') is { Length: > 0 } n ? n : "_";
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    public void Dispose()
    {
        try { _client?.Dispose(); } catch { }
        _client = null;
    }
}

/// <summary>保存しないことにしたパスワード／パスフレーズを、Fako が動いている間だけ覚えておく。</summary>
internal static class SecretCache
{
    private static readonly Dictionary<string, string> Secrets = new();

    private static string Key(RemoteConfig c) => $"{c.User}@{c.Host}:{c.Port}|{c.Auth}|{c.KeyFile}";

    public static string? Get(RemoteConfig c)
    {
        lock (Secrets) return Secrets.TryGetValue(Key(c), out var s) ? s : null;
    }

    public static void Set(RemoteConfig c, string secret)
    {
        lock (Secrets) Secrets[Key(c)] = secret;
    }
}
