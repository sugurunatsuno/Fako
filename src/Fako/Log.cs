using System.IO;
using Fako.Models;

namespace Fako;

/// <summary>
/// 簡易ログ。設定フォルダーの fako.log に書き、書けなければ exe と同じフォルダーに書く。
/// （設定フォルダーへの書き込み自体が失敗しているケースを追えるように）
/// </summary>
internal static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static string? CurrentPath { get; private set; }

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string context, Exception ex) => Write("ERROR", $"{context}\n{ex}");

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {level} {message}{Environment.NewLine}";
        lock (Gate)
        {
            foreach (var dir in new[] { ConfigStore.Directory, AppContext.BaseDirectory })
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, "fako.log");
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes)
                        File.Move(path, path + ".old", overwrite: true);
                    File.AppendAllText(path, line);
                    CurrentPath = path;
                    return;
                }
                catch
                {
                    // 次の候補へ
                }
            }
        }
    }
}
