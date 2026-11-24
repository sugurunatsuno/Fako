using System.Windows;

namespace Fako;

/// <summary>アップデートのダウンロードと適用の進捗を表示する。</summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _info;
    private readonly CancellationTokenSource _cts = new();
    private bool _closed;

    internal UpdateWindow(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;
        VersionText.Text = $"Fako {info.Version} に更新します";
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _cts.Cancel();
        };
    }

    private async Task RunAsync()
    {
        try
        {
            StatusText.Text = "ダウンロードしています…";
            var progress = new Progress<double>(p =>
            {
                Bar.Value = p * 100;
                PercentText.Text = $"{p * 100:0}%";
            });
            var path = await UpdateService.DownloadAsync(_info, progress, _cts.Token);
            if (_closed || _cts.IsCancellationRequested) return;

            StatusText.Text = "適用しています。Fako を再起動します…";
            Bar.Value = 100;
            UpdateService.ApplyAndRestart(path);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            if (!_closed) Close();
        }
        catch (Exception ex)
        {
            Log.Error("アップデートのダウンロードに失敗", ex);
            StatusText.Text = "更新できませんでした";
            MessageBox.Show(this, $"アップデートに失敗しました。\n{ex.Message}", "Fako",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            if (!_closed) Close();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        Close();
    }
}
