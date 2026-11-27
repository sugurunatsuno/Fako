using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fako.Interop;
using Fako.Models;
using Fako.Remote;
using Microsoft.Win32;

namespace Fako;

/// <summary>SSH フェンスの接続先を入力する画面。</summary>
public partial class RemoteConnectWindow : Window
{
    /// <summary>状態表示用のブラシ。生成時に固定して、どのスレッドから参照しても安全にする。</summary>
    private static Brush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly Brush AgentOkBrush = CreateBrush(0x2E, 0x9E, 0x4F);
    private static readonly Brush AgentWarnBrush = CreateBrush(0xC8, 0x7A, 0x19);
    private static readonly Brush AgentErrorBrush = CreateBrush(0xD0, 0x40, 0x40);

    private readonly RemoteConfig _original;
    private bool _loading = true;
    private string? _selectedAgentFingerprint;
    private string _lastAgentPipe = "";
    private int _agentRefreshVersion;
    private bool _updatingAgentKeys;

    /// <summary>SSH エージェントの鍵一覧の 1 項目（先頭は「自動」）。</summary>
    private sealed class AgentKeyChoice
    {
        public string? Fingerprint { get; init; }
        public required string Label { get; init; }
    }

    /// <summary>OK で閉じたときの接続先。</summary>
    public RemoteConfig? Result { get; private set; }

    public static RemoteConfig? Edit(RemoteConfig? current, Window? owner = null)
    {
        var w = new RemoteConnectWindow(current ?? new RemoteConfig());
        if (owner != null && owner.IsVisible) w.Owner = owner;
        return w.ShowDialog() == true ? w.Result : null;
    }

    private RemoteConnectWindow(RemoteConfig cfg)
    {
        InitializeComponent();
        _original = cfg.Clone();

        var entries = SshConfigReader.Read();
        SshConfigCombo.ItemsSource = entries;
        SshConfigCombo.IsEnabled = entries.Count > 0;
        if (entries.Count == 0) SshConfigCombo.ItemsSource = new[] { "（~/.ssh/config に Host がありません）" };

        HostBox.Text = cfg.Host;
        PortBox.Text = cfg.Port.ToString();
        UserBox.Text = string.IsNullOrEmpty(cfg.User) ? Environment.UserName.ToLowerInvariant() : cfg.User;
        AgentRadio.IsChecked = cfg.Auth == RemoteAuth.Agent;
        KeyRadio.IsChecked = cfg.Auth == RemoteAuth.KeyFile;
        PasswordRadio.IsChecked = cfg.Auth == RemoteAuth.Password;
        KeyFileBox.Text = cfg.KeyFile ?? "";
        _selectedAgentFingerprint = cfg.AgentKeyFingerprint;
        AgentPipeBox.Text = cfg.AgentPipe ?? "";
        _lastAgentPipe = AgentPipeBox.Text.Trim();
        PopulateAgentKeys(null);
        SaveSecretBox.IsChecked = cfg.ProtectedSecret != null;
        if (Dpapi.Unprotect(cfg.ProtectedSecret) is { } saved) SecretBox.Password = saved;
        PathBox.Text = cfg.Path;
        ShowHiddenBox.IsChecked = cfg.ShowHidden;
        if (cfg.SshConfigAlias != null)
            SshConfigCombo.SelectedItem = entries.FirstOrDefault(e => e.Alias == cfg.SshConfigAlias);

        HostBox.TextChanged += (_, _) => UpdateHostKey();
        PortBox.TextChanged += (_, _) => UpdateHostKey();
        _loading = false;
        UpdateAuthUi();
        UpdateHostKey();
        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(HostBox.Text)) HostBox.Focus();
            else if (AgentRadio.IsChecked == true) TestButton.Focus();
            else SecretBox.Focus();
            if (AgentRadio.IsChecked == true) _ = RefreshAgentAsync();
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        // 閉じた後に届いた古い結果で UI を触らない
        _agentRefreshVersion++;
        base.OnClosed(e);
    }

    private void OnSshConfigSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SshConfigCombo.SelectedItem is not SshHostEntry entry) return;
        HostBox.Text = entry.HostName;
        PortBox.Text = entry.Port.ToString();
        if (entry.User != null) UserBox.Text = entry.User;

        if (entry.IdentityAgent is { } identityAgent &&
            !identityAgent.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            // 先にパイプを反映してから認証方法を切り替える。
            // 既にエージェント認証なら Checked が発生しないため、その場合だけ明示的に問い合わせる。
            bool alreadyAgent = AgentRadio.IsChecked == true;
            AgentPipeBox.Text = identityAgent.Equals("SSH_AUTH_SOCK", StringComparison.OrdinalIgnoreCase) ? "" : identityAgent;
            if (!alreadyAgent) AgentRadio.IsChecked = true; // OnAuthChanged 経由で 1 回だけ更新される
            else _ = RefreshAgentAsync();
            return;
        }

        if (entry.IdentityFile != null)
        {
            // 公開鍵（.pub）しか手元に無い場合は、エージェント内の同じ鍵を使う
            string? pubPath = null;
            if (entry.IdentityFile.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) && File.Exists(entry.IdentityFile))
                pubPath = entry.IdentityFile;
            else if (!File.Exists(entry.IdentityFile) && File.Exists(entry.IdentityFile + ".pub"))
                pubPath = entry.IdentityFile + ".pub";

            var fingerprint = pubPath != null ? OpenSshAgentClient.TryGetPublicKeyFingerprint(pubPath) : null;
            if (fingerprint != null)
            {
                bool alreadyAgent = AgentRadio.IsChecked == true;
                _selectedAgentFingerprint = fingerprint;
                if (!alreadyAgent) AgentRadio.IsChecked = true; // OnAuthChanged 経由で 1 回だけ更新される
                else _ = RefreshAgentAsync();
            }
            else
            {
                KeyRadio.IsChecked = true;
                KeyFileBox.Text = entry.IdentityFile;
            }
            return;
        }

        // IdentityAgent の指定が無く、ローカルに既定の鍵も無いならエージェントを使う
        if (entry.IdentityAgent is null && SshConfigReader.DefaultKeyFile() == null)
        {
            if (AgentRadio.IsChecked != true) AgentRadio.IsChecked = true; // OnAuthChanged 経由で 1 回だけ更新される
            else _ = RefreshAgentAsync();
        }
    }

    private void OnAuthChanged(object sender, RoutedEventArgs e)
    {
        UpdateAuthUi();
        if (!_loading && AgentRadio.IsChecked == true) _ = RefreshAgentAsync();
    }

    private void UpdateAuthUi()
    {
        if (_loading) return;
        bool agent = AgentRadio.IsChecked == true;
        bool key = KeyRadio.IsChecked == true;
        AgentLabel.Visibility = AgentPanel.Visibility = agent ? Visibility.Visible : Visibility.Collapsed;
        KeyLabel.Visibility = KeyPanel.Visibility = key ? Visibility.Visible : Visibility.Collapsed;
        SecretLabel.Visibility = SecretPanel.Visibility = agent ? Visibility.Collapsed : Visibility.Visible;
        SecretLabel.Text = key ? "パスフレーズ" : "パスワード";
        SecretHint.Text = key
            ? "鍵にパスフレーズが無ければ空のままで構いません。保存しない場合は、必要になったときに聞きます（Fako の終了まで覚えます）。"
            : "保存しない場合は、接続するときに聞きます（Fako の終了まで覚えます）。";
    }

    // ------------------------------------------------------------------
    // SSH エージェント
    // ------------------------------------------------------------------

    private void OnRefreshAgent(object sender, RoutedEventArgs e) => _ = RefreshAgentAsync();

    private void OnAgentPipeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (string.Equals(AgentPipeBox.Text.Trim(), _lastAgentPipe, StringComparison.Ordinal)) return;
        _ = RefreshAgentAsync();
    }

    private void OnAgentKeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAgentKeys) return;
        _selectedAgentFingerprint = SelectedAgentFingerprint();
    }

    private string? SelectedAgentFingerprint() =>
        (AgentKeyCombo.SelectedItem as AgentKeyChoice)?.Fingerprint;

    /// <summary>エージェントへの問い合わせはバックグラウンドで行い、UI スレッドを止めない。</summary>
    private async Task RefreshAgentAsync()
    {
        int version = ++_agentRefreshVersion;
        var pipe = AgentPipeBox.Text.Trim();
        _lastAgentPipe = pipe;
        RefreshAgentButton.IsEnabled = false;
        SetAgentStatus("SSH エージェントを確認しています…", null);

        IReadOnlyList<SshAgentIdentity>? identities = null;
        Exception? error = null;
        var endpoint = pipe.Length > 0 ? pipe : "自動";

        await Task.Run(() =>
        {
            try
            {
                var client = OpenSshAgentClient.Create(pipe);
                identities = client.RequestIdentities();
                endpoint = client.EndpointDisplay;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        // 閉じられた／新しい更新に置き換えられた場合は、古い結果を反映しない
        if (version != _agentRefreshVersion) return;
        RefreshAgentButton.IsEnabled = true;

        if (error != null)
        {
            SetAgentStatus(RemoteUi.Describe(error), AgentErrorBrush);
        }
        else if (identities is { Count: > 0 })
        {
            SetAgentStatus($"接続可能 — {identities.Count} 件の鍵（{endpoint}）", AgentOkBrush);
        }
        else
        {
            SetAgentStatus($"エージェントに鍵がありません（{endpoint}）— アプリのロックを解除するか鍵を登録してください",
                AgentWarnBrush);
        }

        PopulateAgentKeys(identities);
    }

    private void PopulateAgentKeys(IReadOnlyList<SshAgentIdentity>? identities)
    {
        var fingerprint = _selectedAgentFingerprint;
        var list = identities ?? Array.Empty<SshAgentIdentity>();
        var items = new List<AgentKeyChoice>
        {
            new() { Label = "自動（エージェントのすべての鍵を試す）" },
        };
        foreach (var identity in list)
            items.Add(new AgentKeyChoice { Fingerprint = identity.FingerprintSha256, Label = identity.DisplayLabel });

        // 選択中の指紋が今は見えなくても（アプリ未起動・ロック中など）、設定を消さずに選び続ける
        if (!string.IsNullOrEmpty(fingerprint) &&
            !list.Any(i => string.Equals(i.FingerprintSha256, fingerprint, StringComparison.Ordinal)))
            items.Add(new AgentKeyChoice
            {
                Fingerprint = fingerprint,
                Label = $"指定済みの鍵（現在未検出: {ShortFingerprint(fingerprint)}）",
            });

        _updatingAgentKeys = true;
        try
        {
            AgentKeyCombo.ItemsSource = items;
            AgentKeyCombo.SelectedItem = items.FirstOrDefault(
                i => string.Equals(i.Fingerprint, fingerprint, StringComparison.Ordinal)) ?? items[0];
        }
        finally
        {
            _updatingAgentKeys = false;
        }
    }

    private static string ShortFingerprint(string fingerprint) =>
        fingerprint.Length > 24 ? fingerprint[..24] + "…" : fingerprint;

    private void SetAgentStatus(string text, Brush? brush)
    {
        AgentStatusText.Text = text;
        AgentStatusText.Foreground = brush ?? SystemColors.ControlTextBrush;
    }

    private void OnBrowseKey(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "秘密鍵ファイルを選択",
            InitialDirectory = SshConfigReader.UserSshDir,
            Filter = "すべてのファイル|*.*",
        };
        if (dlg.ShowDialog(this) == true) KeyFileBox.Text = dlg.FileName;
    }

    private RemoteConfig? Read(out string? error)
    {
        error = null;
        var host = HostBox.Text.Trim();
        var user = UserBox.Text.Trim();
        if (host.Length == 0) { error = "ホストを入力してください"; return null; }
        if (user.Length == 0) { error = "ユーザー名を入力してください"; return null; }
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535) { error = "ポートは 1〜65535 で入力してください"; return null; }

        if (AgentRadio.IsChecked == true)
        {
            var pipe = AgentPipeBox.Text.Trim();
            if (pipe.Length > 0)
            {
                if (pipe.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    error = "SSH エージェント認証ではパイプ名に none を指定できません";
                    return null;
                }
                if (!pipe.Equals("SSH_AUTH_SOCK", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        OpenSshAgentClient.NormalizePipeName(pipe);
                    }
                    catch (SshAgentException ex)
                    {
                        error = ex.Message;
                        return null;
                    }
                }
            }

            return new RemoteConfig
            {
                SshConfigAlias = (SshConfigCombo.SelectedItem as SshHostEntry)?.Alias,
                Host = host,
                Port = port,
                User = user,
                Auth = RemoteAuth.Agent,
                KeyFile = null,
                ProtectedSecret = null,
                AgentKeyFingerprint = SelectedAgentFingerprint(),
                AgentPipe = pipe.Length > 0 ? pipe : null,
                Path = PathBox.Text.Trim(),
                ShowHidden = ShowHiddenBox.IsChecked == true,
            };
        }

        bool key = KeyRadio.IsChecked == true;
        var keyFile = KeyFileBox.Text.Trim();
        if (key && keyFile.Length > 0 && !System.IO.File.Exists(keyFile)) { error = "鍵ファイルが見つかりません"; return null; }
        if (key && keyFile.Length == 0 && SshConfigReader.DefaultKeyFile() == null) { error = "鍵ファイルを指定してください（~/.ssh に既定の鍵がありません）"; return null; }

        var secret = SecretBox.Password;
        return new RemoteConfig
        {
            SshConfigAlias = (SshConfigCombo.SelectedItem as SshHostEntry)?.Alias,
            Host = host,
            Port = port,
            User = user,
            Auth = key ? RemoteAuth.KeyFile : RemoteAuth.Password,
            KeyFile = key && keyFile.Length > 0 ? keyFile : null,
            ProtectedSecret = SaveSecretBox.IsChecked == true && secret.Length > 0 ? Dpapi.Protect(secret) : null,
            Path = PathBox.Text.Trim(),
            ShowHidden = ShowHiddenBox.IsChecked == true,
        };
    }

    private void RememberUnsavedSecret(RemoteConfig cfg)
    {
        if (cfg.Auth == RemoteAuth.Agent) return;
        if (cfg.ProtectedSecret == null && SecretBox.Password.Length > 0)
            SecretCache.Set(cfg, SecretBox.Password);
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var cfg = Read(out var error);
        if (cfg == null) { ShowStatus(error!, false); return; }
        RememberUnsavedSecret(cfg);

        TestButton.IsEnabled = false;
        ShowStatus(cfg.Auth == RemoteAuth.Agent
            ? "接続しています…（Bitwarden 等の承認が必要な場合はアプリ側で許可してください）"
            : "接続しています…", null);
        using var session = RemoteUi.CreateSession(cfg, this);
        try
        {
            var dir = await session.ConnectAsync(cfg.Path);
            var list = await session.ListAsync(dir);
            ShowStatus($"接続できました。{dir}（{list.Count} 項目）", true);
            App.Current.SaveSoon(); // 信頼したホスト鍵を保存
        }
        catch (Exception ex)
        {
            ShowStatus(RemoteUi.Describe(ex), false);
        }
        finally
        {
            TestButton.IsEnabled = true;
            UpdateHostKey();
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var cfg = Read(out var error);
        if (cfg == null) { ShowStatus(error!, false); return; }
        RememberUnsavedSecret(cfg);
        Result = cfg;
        DialogResult = true;
    }

    private void ShowStatus(string text, bool? ok)
    {
        StatusBorder.Visibility = Visibility.Visible;
        StatusText.Text = text;
        StatusText.Foreground = ok switch
        {
            true => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x9E, 0x4F)),
            false => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD0, 0x40, 0x40)),
            _ => SystemColors.ControlTextBrush,
        };
    }

    private string HostKeyId => $"{HostBox.Text.Trim()}:{(int.TryParse(PortBox.Text.Trim(), out var p) ? p : 22)}";

    private void UpdateHostKey()
    {
        var known = App.Current.Config.KnownHosts;
        if (known.TryGetValue(HostKeyId, out var fp))
        {
            HostKeyText.Text = fp;
            ForgetHostKeyButton.Visibility = Visibility.Visible;
        }
        else
        {
            HostKeyText.Text = "未登録（初めて接続するときに確認します）";
            ForgetHostKeyButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnForgetHostKey(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"{HostKeyId} の信頼済みのホスト鍵を削除しますか？\n次に接続するとき、改めて確認します。", "Fako",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        App.Current.Config.KnownHosts.Remove(HostKeyId);
        App.Current.SaveSoon();
        UpdateHostKey();
    }
}
