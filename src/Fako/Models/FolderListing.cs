using System.IO;
using Fako.Interop;
using Microsoft.Win32;

namespace Fako.Models;

/// <summary>フォルダーの中身をエクスプローラーに近い順序（フォルダー優先・自然順）・表示規則で列挙する。</summary>
internal static class FolderListing
{
    public static string UserDesktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public static string CommonDesktop => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    /// <summary>シェル項目（ごみ箱など、ファイルではない項目）を表すパスの接頭辞。</summary>
    public const string ShellPrefix = "::";

    public static bool IsShellItem(string path) => path.StartsWith(ShellPrefix, StringComparison.Ordinal);

    /// <summary>
    /// デスクトップに表示できるシェル項目（Windows の「デスクトップ アイコンの設定」の項目）。
    /// 既定ではごみ箱だけが表示される。
    /// </summary>
    private static readonly (string Guid, bool DefaultVisible)[] DesktopShellItems =
    {
        ("{20D04FE0-3AEA-1069-A2D8-08002B30309D}", false), // PC
        ("{59031a47-3f72-44a7-89c5-5595fe6b30ee}", false), // ユーザーのファイル
        ("{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", false), // ネットワーク
        ("{645FF040-5081-101B-9F08-00AA002F954E}", true),  // ごみ箱
        ("{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", false), // コントロール パネル
    };

    /// <summary>今デスクトップに表示される設定になっているシェル項目のパス（"::{GUID}"）。</summary>
    public static IReadOnlyList<string> VisibleDesktopShellItems()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel");
        var list = new List<string>();
        foreach (var (guid, defaultVisible) in DesktopShellItems)
        {
            // 値 0 = 表示、1 = 非表示、値なし = 既定
            bool visible = key?.GetValue(guid) is int v ? v == 0 : defaultVisible;
            if (visible) list.Add(ShellPrefix + guid);
        }
        return list;
    }

    public static IReadOnlyList<string> List(params string?[] dirs)
    {
        var (showHidden, showSuperHidden) = ExplorerHiddenSettings();
        var entries = new List<FileSystemInfo>();
        foreach (var dir in dirs)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            try
            {
                entries.AddRange(new DirectoryInfo(dir).EnumerateFileSystemInfos()
                    .Where(e => IsShown(e.Attributes, showHidden, showSuperHidden)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return entries
            .OrderBy(e => e is DirectoryInfo ? 0 : 1)
            .ThenBy(e => e.Name, LogicalComparer.Instance)
            .Select(e => e.FullName)
            .ToList();
    }

    /// <summary>エクスプローラーと同じ規則: 隠しファイルは設定次第、隠し＋システム（desktop.ini など）は「保護された OS ファイル」の設定次第。</summary>
    private static bool IsShown(FileAttributes a, bool showHidden, bool showSuperHidden)
    {
        bool hidden = (a & FileAttributes.Hidden) != 0;
        bool system = (a & FileAttributes.System) != 0;
        if (hidden && system) return showSuperHidden;
        if (hidden) return showHidden;
        return true;
    }

    private static (bool ShowHidden, bool ShowSuperHidden) ExplorerHiddenSettings()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        bool hidden = key?.GetValue("Hidden") is int h && h == 1;
        bool super = key?.GetValue("ShowSuperHidden") is int s && s == 1;
        return (hidden, super);
    }

    public sealed class LogicalComparer : IComparer<string>
    {
        public static readonly LogicalComparer Instance = new();
        public int Compare(string? x, string? y) => NativeMethods.StrCmpLogicalW(x ?? "", y ?? "");
    }
}
