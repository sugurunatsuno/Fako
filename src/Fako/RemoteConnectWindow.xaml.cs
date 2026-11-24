using System.Windows;
using System.Windows.Controls;
using Fako.Interop;
using Fako.Models;
using Fako.Remote;
using Microsoft.Win32;

namespace Fako;

/// <summary>SSH フェンスの接続先を入力する画面。</summary>
public partial class RemoteConnectWindow : Window
{
    private readonly RemoteConfig _original;
    private bool _loading = true;

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
        KeyRadio.IsChecked = cfg.Auth == RemoteAuth.KeyFile;
        PasswordRadio.IsChecked = cfg.Auth == RemoteAuth.Password;
        KeyFileBox.Text = cfg.KeyFile ?? "";
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
        Loaded += (_, _) => (string.IsNullOrEmpty(HostBox.Text) ? (Control)HostBox : SecretBox).Focus();
    }

    private void OnSshConfigSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SshConfigCombo.SelectedItem is not SshHostEntry entry) return;
        HostBox.Text = entry.HostName;
        PortBox.Text = entry.Port.ToString();
        if (entry.User != null) UserBox.Text = entry.User;
        if (entry.IdentityFile != null)
        {
            KeyRadio.IsChecked = true;
            KeyFileBox.Text = entry.IdentityFile;
        }
    }

    private void OnAuthChanged(object sender, RoutedEventArgs e) => UpdateAuthUi();

    private void UpdateAuthUi()
    {
        if (_loading) return;
        bool key = KeyRadio.IsChecked == true;
        KeyLabel.Visibility = KeyPanel.Visibility = key ? Visibility.Visible : Visibility.Collapsed;
        SecretLabel.Text = key ? "パスフレーズ" : "パスワード";
        SecretHint.Text = key
            ? "鍵にパスフレーズが無ければ空のままで構いません。保存しない場合は、必要になったときに聞きます（Fako の終了まで覚えます）。"
            : "保存しない場合は、接続するときに聞きます（Fako の終了まで覚えます）。";
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
        if (cfg.ProtectedSecret == null && SecretBox.Password.Length > 0)
            SecretCache.Set(cfg, SecretBox.Password);
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var cfg = Read(out var error);
        if (cfg == null) { ShowStatus(error!, false); return; }
        RememberUnsavedSecret(cfg);

        TestButton.IsEnabled = false;
        ShowStatus("接続しています…", null);
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
