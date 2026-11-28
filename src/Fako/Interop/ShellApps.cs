using System.Runtime.InteropServices;
using System.Windows.Media;
using Fako.Models;

namespace Fako.Interop;

/// <summary>
/// Windows Shell COM API（FOLDERID_AppsFolder）でインストール済みアプリの表示名・AppId・アイコンを取得する。
/// UI スレッド（STA）から呼ぶこと。
/// </summary>
internal static class ShellApps
{
    private static readonly Guid FolderIdAppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
    private static readonly Guid BhidEnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    private const uint KF_FLAG_DEFAULT = 0x00000000;

    private static readonly object CacheLock = new();
    private static IReadOnlyList<AppEntry>? s_cachedEntries;

    public static bool HasCache
    {
        get { lock (CacheLock) return s_cachedEntries != null; }
    }

    public static IReadOnlyList<AppEntry> Enumerate()
    {
        lock (CacheLock)
        {
            if (s_cachedEntries is { } cached)
                return cached;

            var results = new List<AppEntry>();
            if (!EnumerateCom(child =>
            {
                if (TryReadEntry(child, out var entry))
                    results.Add(entry);
                return true;
            }))
            {
                return results;
            }

            s_cachedEntries = Array.AsReadOnly(results.ToArray());
            return s_cachedEntries;
        }
    }

    /// <summary>
    /// 未キャッシュ時に FOLDERID_AppsFolder を 1 回だけ列挙し、エントリ一覧のキャッシュ構築と
    /// 指定条件に合う項目のアイコン取得を同時に行う。既にキャッシュがある場合はそれを返す。
    /// </summary>
    public static IReadOnlyList<AppEntry> EnumerateAndFetchIcons(
        int px,
        Func<AppEntry, bool> shouldFetchIcon,
        Action<string, ImageSource?> onIcon)
    {
        lock (CacheLock)
        {
            if (s_cachedEntries is { } cached)
                return cached;

            var results = new List<AppEntry>();
            var size = new SIZE { cx = px, cy = px };
            bool ok = EnumerateCom(child =>
            {
                if (TryReadEntry(child, out var entry))
                {
                    results.Add(entry);
                    if (px > 0 && shouldFetchIcon(entry))
                        onIcon(entry.ParsingPath, GetIconFromItem(child, size, entry.AppId));
                }
                return true;
            });

            if (!ok)
                return results;

            s_cachedEntries = Array.AsReadOnly(results.ToArray());
            return s_cachedEntries;
        }
    }

    /// <summary>
    /// 指定した AppId 集合のアイコンを FOLDERID_AppsFolder の 1 回走査で一括取得する。
    /// </summary>
    public static void FetchIcons(IReadOnlySet<string> appIds, int px, Action<string, ImageSource?> onIcon)
    {
        if (appIds.Count == 0 || px <= 0) return;

        int remaining = appIds.Count;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var size = new SIZE { cx = px, cy = px };
        EnumerateCom(child =>
        {
            var appId = ReadDisplayName(child, SIGDN.PARENTRELATIVEPARSING);
            if (!string.IsNullOrWhiteSpace(appId) && appIds.Contains(appId) && seen.Add(appId))
            {
                onIcon(appId, GetIconFromItem(child, size, appId));
                if (--remaining <= 0)
                    return false;
            }
            return true;
        });
    }

    public static string? GetDisplayName(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;

        IShellItem? item = null;
        try
        {
            var iid = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName(FolderListing.AppPrefix + appId, IntPtr.Zero, ref iid, out item) != 0
                || item == null)
                return null;
            return ReadDisplayName(item, SIGDN.NORMALDISPLAY);
        }
        catch (Exception ex)
        {
            Log.Error($"アプリ表示名の取得に失敗: {appId}", ex);
            return null;
        }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
        }
    }

    public static ImageSource? GetIcon(string appId, int px)
    {
        if (string.IsNullOrWhiteSpace(appId) || px <= 0) return null;

        IShellItem? item = null;
        try
        {
            var iid = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName(FolderListing.AppPrefix + appId, IntPtr.Zero, ref iid, out item) != 0
                || item == null)
                return null;
            return GetIconFromItem(item, new SIZE { cx = px, cy = px }, appId);
        }
        catch (Exception ex)
        {
            Log.Error($"アプリアイコンの取得に失敗: {appId}", ex);
            return null;
        }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
        }
    }

    public static void InvalidateCache()
    {
        lock (CacheLock)
            s_cachedEntries = null;
    }

    private static bool EnumerateCom(Func<IShellItem, bool> onItem)
    {
        IShellItem? appsFolder = null;
        IntPtr enumPtr = IntPtr.Zero;
        object? enumObj = null;

        try
        {
            var folderId = FolderIdAppsFolder;
            var iidShellItem = typeof(IShellItem).GUID;
            if (SHGetKnownFolderItem(ref folderId, KF_FLAG_DEFAULT, IntPtr.Zero, ref iidShellItem, out appsFolder) != 0
                || appsFolder == null)
                return false;

            var bhid = BhidEnumItems;
            var iidEnum = typeof(IEnumShellItems).GUID;
            if (appsFolder.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out enumPtr) != 0
                || enumPtr == IntPtr.Zero)
                return false;

            enumObj = Marshal.GetObjectForIUnknown(enumPtr);
            if (enumObj is not IEnumShellItems enumItems)
                return false;

            while (true)
            {
                IShellItem? child = null;
                try
                {
                    int hr = enumItems.Next(1, out child, out uint fetched);
                    if (hr != 0 || fetched == 0 || child == null)
                        break;
                    if (!onItem(child))
                        break;
                }
                finally
                {
                    if (child != null) Marshal.ReleaseComObject(child);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            // COM エラー時は取得済み分（または空）を安全に返す
            Log.Error("インストール済みアプリの列挙に失敗", ex);
            return false;
        }
        finally
        {
            if (enumObj != null) Marshal.ReleaseComObject(enumObj);
            if (enumPtr != IntPtr.Zero) Marshal.Release(enumPtr);
            if (appsFolder != null) Marshal.ReleaseComObject(appsFolder);
        }
    }

    private static bool TryReadEntry(IShellItem child, out AppEntry entry)
    {
        var displayName = ReadDisplayName(child, SIGDN.NORMALDISPLAY);
        var appId = ReadDisplayName(child, SIGDN.PARENTRELATIVEPARSING);
        if (!string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrWhiteSpace(appId))
        {
            entry = new AppEntry(appId, displayName, FolderListing.AppPrefix + appId);
            return true;
        }
        entry = null!;
        return false;
    }

    private static ImageSource? GetIconFromItem(IShellItem item, SIZE size, string appId)
    {
        try
        {
            if (item is not IShellItemImageFactory factory)
                return null;

            IntPtr hbmp = IntPtr.Zero;
            if (factory.GetImage(size, SIIGBF.RESIZETOFIT | SIIGBF.ICONONLY, out hbmp) != 0 || hbmp == IntPtr.Zero)
            {
                if (hbmp != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(hbmp);
                    hbmp = IntPtr.Zero;
                }
                if (factory.GetImage(size, SIIGBF.RESIZETOFIT, out hbmp) != 0 || hbmp == IntPtr.Zero)
                {
                    if (hbmp != IntPtr.Zero) NativeMethods.DeleteObject(hbmp);
                    return null;
                }
            }

            try
            {
                return ShellIcons.HBitmapToBitmapSource(hbmp);
            }
            finally
            {
                NativeMethods.DeleteObject(hbmp);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"アプリアイコンの取得に失敗: {appId}", ex);
            return null;
        }
    }

    private static string? ReadDisplayName(IShellItem item, SIGDN sigdn)
    {
        if (item.GetDisplayName(sigdn, out var ptr) != 0 || ptr == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    // ---- COM / Win32 定義 ----

    private enum SIGDN : uint
    {
        NORMALDISPLAY = 0x00000000,
        PARENTRELATIVEPARSING = 0x80018001,
    }

    [Flags]
    private enum SIIGBF
    {
        RESIZETOFIT = 0x00,
        ICONONLY = 0x04,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out IShellItem? rgelt, out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems ppenum);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderItem(
        ref Guid rfid,
        uint flags,
        IntPtr hToken,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? ppv);
}
