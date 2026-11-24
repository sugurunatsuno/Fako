using System.IO;

namespace Fako.Tests;

/// <summary>テスト専用の一時ディレクトリ。ビルド出力の下に作り、テスト終了時に消す。</summary>
internal static class TestTemp
{
    public static string CreateDirectory()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "test-temp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
