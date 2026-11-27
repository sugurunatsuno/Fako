using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using Fako.Models;
using Renci.SshNet.Common;

namespace Fako.Remote;

/// <summary>SSH 接続まわりの共通 UI（ホスト鍵の確認・パスワード入力・エラー文言）。</summary>
internal static class RemoteUi
{
    public static RemoteSession CreateSession(RemoteConfig cfg, Window? owner = null) => new(cfg, App.Current.Config.KnownHosts)
    {
        InvokeOnUi = f => Application.Current.Dispatcher.Invoke(f),
        ConfirmHostKey = (id, fp) => ConfirmHostKey(owner, id, fp),
        AskSecret = prompt => AskSecret(owner, prompt),
    };

    public static bool ConfirmHostKey(Window? owner, string id, string fingerprint)
    {
        var msg = $"初めて接続するサーバーです。\n\n{id}\n{fingerprint}\n\n" +
                  "この指紋がサーバーのものと一致することを確認できたら「はい」を押してください。\n" +
                  "（サーバー上で ssh-keygen -lf /etc/ssh/ssh_host_*_key.pub で確認できます）";
        var result = owner is { IsVisible: true }
            ? MessageBox.Show(owner, msg, "Fako — ホスト鍵の確認", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(msg, "Fako — ホスト鍵の確認", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No,
                MessageBoxOptions.DefaultDesktopOnly);
        return result == MessageBoxResult.Yes;
    }

    /// <summary>パスワード／パスフレーズを聞く小さなダイアログ。キャンセルなら null。</summary>
    public static string? AskSecret(Window? owner, string prompt)
    {
        var box = new PasswordBox { Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "OK", MinWidth = 96, IsDefault = true };
        var cancel = new Button { Content = "キャンセル", MinWidth = 96, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        if (Application.Current.TryFindResource("AccentButtonStyle") is Style accent) ok.Style = accent;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var w = new Window
        {
            Title = "Fako — SSH",
            Content = panel,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Topmost = owner is not { IsVisible: true },
            FontFamily = new System.Windows.Media.FontFamily("Yu Gothic UI"),
            FontSize = 13,
            ThemeMode = ThemeMode.System,
        };
        if (owner is { IsVisible: true }) w.Owner = owner;
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => box.Focus();
        return w.ShowDialog() == true ? box.Password : null;
    }

    /// <summary>例外を、利用者向けの説明に直す。</summary>
    public static string Describe(Exception ex)
    {
        if (ex is AggregateException { InnerException: { } inner }) ex = inner;
        return ex switch
        {
            HostKeyMismatchException => ex.Message,
            CredentialCancelledException => "資格情報の入力がキャンセルされました。",
            SshAgentUnavailableException =>
                "SSH エージェントに接続できません。Bitwarden・1Password・OpenSSH エージェントなどが起動しているか確認してください。\n" +
                "（Windows 標準の OpenSSH Authentication Agent サービスと競合している場合は停止してください）",
            SshAgentOperationRefusedException =>
                "SSH エージェントによって署名が拒否されました。アプリのロックを解除するか、承認ダイアログで許可してからやり直してください。",
            SshAgentException => ex.Message,
            SshAuthenticationException => $"認証に失敗しました。ユーザー名・鍵・パスワードを確認してください。\n（{ex.Message}）",
            SshPassPhraseNullOrEmptyException => "鍵にパスフレーズが必要です。",
            SocketException se => $"サーバーに接続できません。ホスト名・ポート・ネットワークを確認してください。\n（{se.Message}）",
            SshOperationTimeoutException => "タイムアウトしました。サーバーまたはネットワークの応答がありません。",
            SshConnectionException => $"接続が切れました。\n（{ex.Message}）",
            SftpPermissionDeniedException => $"権限がありません。\n（{ex.Message}）",
            SftpPathNotFoundException => $"見つかりません。\n（{ex.Message}）",
            _ => ex.Message,
        };
    }
}
