using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace Fako;

/// <summary>GitHub Releases を見て、新しいバージョンの有無を確認する。</summary>
internal sealed record UpdateInfo(Version Version, string Tag, string ReleaseUrl, string ExeUrl, string? Sha256Url);

/// <summary>
/// 公開リポジトリ sugurunatsuno/Fako の最新リリースを確認し、ダウンロードして自分自身を置き換える。
/// 認証は使わない（公開リポジトリの Releases API。レート制限は 1 時間 60 回）。
/// </summary>
internal static class UpdateService
{
    private const string Owner = "sugurunatsuno";
    private const string Repository = "Fako";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    /// <summary>アプリのバージョン（1.0.0.0 → 1.0.0）。</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd($"Fako/{CurrentVersion} (+https://github.com/{Owner}/{Repository})");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await Http.SendAsync(request, timeout.Token);
        // リリースがまだ 1 つも無い場合は 404。エラーではなく「更新なし」として扱う。
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "" : "";
        if (!TryParseVersion(tag, out var version)) return null;
        if (version <= CurrentVersion) return null;

        string releaseUrl = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "";
        string? exeUrl = null;
        string? shaUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                if (name.Equals("Fako.exe", StringComparison.OrdinalIgnoreCase)) exeUrl = url;
                else if (name.Equals("Fako.exe.sha256", StringComparison.OrdinalIgnoreCase)) shaUrl = url;
            }
        }

        if (exeUrl is null)
            throw new InvalidOperationException($"リリース {tag} に Fako.exe が見つかりません。");

        return new UpdateInfo(version, tag, releaseUrl, exeUrl, shaUrl);
    }

    public static bool TryParseVersion(string tag, out Version version)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        int cut = text.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) text = text[..cut];
        return Version.TryParse(text, out version!);
    }

    /// <summary>新しい Fako.exe を一時フォルダーにダウンロードする。進捗は 0〜1。</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress,
        CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Fako", "update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"Fako-{info.Version}.exe");

        using (var response = await Http.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }
        progress?.Report(1.0);

        if (info.Sha256Url is { } shaUrl)
        {
            var expected = (await Http.GetStringAsync(shaUrl, ct))
                .Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (expected is { Length: 64 })
            {
                await using var stream = File.OpenRead(path);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(path);
                    throw new InvalidOperationException("ダウンロードしたファイルの検証（SHA256）に失敗しました。");
                }
            }
        }
        return path;
    }

    /// <summary>
    /// 新しい exe を --apply-update モードで起動する。呼び出し側はこの後アプリを終了する。
    /// 新しい exe が旧プロセスの終了を待って、インストール先の exe を置き換える。
    /// </summary>
    public static void ApplyAndRestart(string newExe)
    {
        var target = Environment.ProcessPath
                     ?? throw new InvalidOperationException("実行ファイルのパスが分かりません。");
        var psi = new ProcessStartInfo(newExe)
        {
            UseShellExecute = false,
            Arguments = $"--apply-update \"{target}\" {Environment.ProcessId}",
        };
        if (Process.Start(psi) is null)
            throw new InvalidOperationException("アップデート用のプロセスを開始できませんでした。");
        Log.Info($"update: started {newExe} to replace {target}");
    }

    /// <summary>--apply-update &lt;target&gt; &lt;pid&gt; の処理。通常起動なら false。</summary>
    public static bool TryApplyUpdate(string[] args)
    {
        if (args.Length < 3 ||
            !args[0].Equals("--apply-update", StringComparison.OrdinalIgnoreCase))
            return false;

        var target = args[1];
        int pid = int.TryParse(args[2], out var parsed) ? parsed : 0;
        try
        {
            if (pid > 0)
            {
                try
                {
                    using var old = Process.GetProcessById(pid);
                    if (!old.WaitForExit(30_000))
                        Log.Info("update: old process did not exit in 30s");
                }
                catch (ArgumentException)
                {
                    // すでに終了している
                }
            }

            var self = Environment.ProcessPath!;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                bool copied = false;
                for (int i = 0; i < 10 && !copied; i++)
                {
                    try
                    {
                        File.Copy(self, target, overwrite: true);
                        copied = true;
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(500);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        Thread.Sleep(500);
                    }
                }
                if (!copied)
                    throw new IOException($"ファイルを置き換えられませんでした: {target}");
            }

            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            Log.Info($"update applied: {target}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("アップデートの適用に失敗", ex);
            MessageBox.Show(
                $"アップデートを適用できませんでした。\n{ex.Message}\n\n新しいファイル: {Environment.ProcessPath}",
                "Fako", MessageBoxButton.OK, MessageBoxImage.Warning);
            return true;
        }
    }
}
