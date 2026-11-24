using System.IO;
using System.Runtime.InteropServices;
using Fako.Models;

namespace Fako.Interop;

/// <summary>
/// v0.3 まで使っていた「ファイル属性（隠し＋システム）でデスクトップから隠す」方式の後始末。
///
/// 属性で隠すとエクスプローラーやファイル選択ダイアログからも見えなくなり、
/// 「ファイルが消えた」ように見えるため v0.4 でやめた（デスクトップ表示はフェンスで管理する方式に変更）。
/// 起動時に、Fako が付けた属性を記録に従って外す。元から付いていた属性は触らない。
/// </summary>
internal static class DesktopIconHider
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    /// <returns>元に戻した件数。</returns>
    public static int RestoreAll(AppConfig config)
    {
        int restored = 0;
        foreach (var rec in config.HiddenByFako.ToList())
        {
            try
            {
                if (File.Exists(rec.Path) || Directory.Exists(rec.Path))
                {
                    var cur = File.GetAttributes(rec.Path);
                    File.SetAttributes(rec.Path, cur & ~rec.Added);
                    Notify(rec.Path);
                    restored++;
                    Log.Info($"attributes restored: {rec.Path} (-{rec.Added})");
                }
                config.HiddenByFako.Remove(rec);
            }
            catch (Exception ex)
            {
                // 失敗したものは記録を残し、次回また試す
                Log.Error($"属性を戻せません: {rec.Path}", ex);
            }
        }
        return restored;
    }

    public static bool IsOnDesktop(string path)
    {
        var parent = Path.GetDirectoryName(path.TrimEnd('\\'));
        return parent != null
               && (Cmp.Equals(parent, FolderListing.UserDesktop) || Cmp.Equals(parent, FolderListing.CommonDesktop));
    }

    private static void Notify(string path)
    {
        const uint SHCNE_ATTRIBUTES = 0x00000800;
        const uint SHCNE_UPDATEDIR = 0x00001000;
        const uint SHCNF_PATHW = 0x0005;
        SHChangeNotify(SHCNE_ATTRIBUTES, SHCNF_PATHW, path, IntPtr.Zero);
        if (Path.GetDirectoryName(path) is { } dir)
            SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, dir, IntPtr.Zero);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, string dwItem1, IntPtr dwItem2);
}
