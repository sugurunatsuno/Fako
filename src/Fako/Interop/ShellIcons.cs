using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fako.Models;

namespace Fako.Interop;

/// <summary>
/// シェル（エクスプローラー）と同じ表示名・種類・アイコン／サムネイルを取得する。
/// UI スレッド（STA）から呼ぶこと。
/// </summary>
internal static class ShellIcons
{
    private sealed record Meta(string Name, bool Exists, bool IsFolder, string TypeName, long? Size, DateTime? Modified);

    private static readonly Dictionary<string, Meta> MetaCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Dictionary<int, ImageSource?>> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public static FenceItem CreateItem(string path, int iconPx)
    {
        var meta = GetMeta(path);
        var icon = GetIcon(path, iconPx, meta.Exists);
        return new FenceItem(path, meta.Name, meta.Exists, meta.IsFolder, meta.TypeName, meta.Size, meta.Modified, icon);
    }

    private const int BulkIconFetchThreshold = 48;

    public static FenceItem CreateAppItem(AppEntry entry, int iconPx)
    {
        MetaCache[entry.ParsingPath] = new Meta(entry.DisplayName, true, false, "アプリケーション", null, null);
        var icon = GetIcon(entry.ParsingPath, iconPx, exists: true);
        return new FenceItem(entry.ParsingPath, entry.DisplayName, true, false, "アプリケーション", null, null, icon);
    }

    /// <summary>
    /// アプリ一覧をまとめて FenceItem 化する。未キャッシュのアイコンが多数ある場合は
    /// FOLDERID_AppsFolder の 1 回走査で直接取得して SHCreateItemFromParsingName の再呼び出しを省く。
    /// </summary>
    public static List<FenceItem> CreateAppItems(bool hideNoise, int iconPx)
    {
        IReadOnlyList<AppEntry> entries;
        if (!ShellApps.HasCache && iconPx > 0)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var raw = ShellApps.EnumerateAndFetchIcons(
                iconPx,
                entry => seen.Add(entry.AppId)
                         && (!hideNoise || !AppsListing.IsNoiseApp(entry.DisplayName, entry.AppId))
                         && !HasAppIcon(entry.ParsingPath, iconPx),
                (parsingPath, icon) => SetAppIcon(parsingPath, iconPx, icon));
            entries = AppsListing.Filter(raw, hideNoise);
        }
        else
        {
            entries = AppsListing.ListInstalled(hideNoise);
            if (iconPx > 0)
            {
                var missingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in entries)
                {
                    if (!HasAppIcon(entry.ParsingPath, iconPx))
                        missingIds.Add(entry.AppId);
                }

                if (missingIds.Count >= BulkIconFetchThreshold)
                {
                    ShellApps.FetchIcons(missingIds, iconPx, (appId, icon) =>
                        SetAppIcon(FolderListing.AppPrefix + appId, iconPx, icon));
                }
            }
        }

        var items = new List<FenceItem>(entries.Count);
        foreach (var entry in entries)
            items.Add(CreateAppItem(entry, iconPx));
        return items;
    }

    private static bool HasAppIcon(string parsingPath, int px) =>
        IconCache.TryGetValue(parsingPath, out var bySize) && bySize.ContainsKey(px);

    private static void SetAppIcon(string parsingPath, int px, ImageSource? icon)
    {
        if (!IconCache.TryGetValue(parsingPath, out var bySize))
            IconCache[parsingPath] = bySize = new Dictionary<int, ImageSource?>();
        bySize[px] = icon;
    }

    public static string GetName(string path) => GetMeta(path).Name;

    // ------------------------------------------------------------------
    // 手元に無い項目（SSH 先のファイルなど）: 拡張子から種類名とアイコンを決める
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, string> VirtualTypeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageSource?> VirtualIconCache = new(StringComparer.OrdinalIgnoreCase);

    public static FenceItem CreateVirtualItem(string id, string name, bool isFolder, long? size, DateTime? modified, int iconPx)
    {
        var ext = isFolder ? "<folder>" : System.IO.Path.GetExtension(name);
        var probe = isFolder ? "folder" : "file" + ext; // SHGetFileInfo に渡す架空の名前

        if (!VirtualTypeCache.TryGetValue(ext, out var type))
        {
            var info = new SHFILEINFO();
            SHGetFileInfo(probe, isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL, ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
            type = string.IsNullOrEmpty(info.szTypeName) ? (isFolder ? "ファイル フォルダー" : "ファイル") : info.szTypeName;
            VirtualTypeCache[ext] = type;
        }

        var listId = iconPx <= 32 ? SHIL_LARGE : iconPx <= 64 ? SHIL_EXTRALARGE : SHIL_JUMBO;
        var iconKey = $"{listId}|{ext}";
        if (!VirtualIconCache.TryGetValue(iconKey, out var icon))
        {
            icon = GetSystemImageListIcon(probe, isFolder, listId);
            VirtualIconCache[iconKey] = icon;
        }
        return new FenceItem(id, name, true, isFolder, type, isFolder ? null : size, modified, icon);
    }

    /// <summary>システムのイメージリスト（32／48／256px）から、拡張子に対応するアイコンを取り出す。</summary>
    private static ImageSource? GetSystemImageListIcon(string probe, bool isFolder, int listId)
    {
        var info = new SHFILEINFO();
        if (SHGetFileInfo(probe, isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL, ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES) == IntPtr.Zero)
            return null;

        var iid = typeof(IImageList).GUID;
        if (SHGetImageList(listId, ref iid, out var list) != 0 || list == null) return null;
        try
        {
            if (list.GetIcon(info.iIcon, ILD_TRANSPARENT, out var hIcon) != 0 || hIcon == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                NativeMethods.DestroyIcon(hIcon);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(list);
        }
    }

    private const int SHIL_LARGE = 0x0;       // 32px
    private const int SHIL_EXTRALARGE = 0x2;  // 48px
    private const int SHIL_JUMBO = 0x4;       // 256px
    private const int ILD_TRANSPARENT = 0x1;
    private const uint SHGFI_SYSICONINDEX = 0x000004000;

    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }

    [DllImport("shell32.dll", EntryPoint = "#727")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    public static void Invalidate(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            MetaCache.Remove(p);
            IconCache.Remove(p);
        }
    }

    /// <summary>アプリ一覧項目（shell:AppsFolder\...）のメタデータ・アイコンキャッシュを破棄する。</summary>
    public static void InvalidateApps()
    {
        foreach (var key in MetaCache.Keys.Where(FolderListing.IsAppItem).ToList())
            MetaCache.Remove(key);
        foreach (var key in IconCache.Keys.Where(FolderListing.IsAppItem).ToList())
            IconCache.Remove(key);
    }

    // ------------------------------------------------------------------

    private static Meta GetMeta(string path)
    {
        // ごみ箱・PC などのシェル項目（"::{GUID}"）。ファイルではないので、シェルに名前を聞けたら「ある」とみなす
        if (FolderListing.IsShellItem(path))
        {
            if (MetaCache.TryGetValue(path, out var shellCached)) return shellCached;
            var shellName = GetDisplayName(path);
            var shellMeta = new Meta(shellName ?? path, shellName != null, true, "システム フォルダー", null, null);
            MetaCache[path] = shellMeta;
            return shellMeta;
        }

        // インストール済みアプリ（"shell:AppsFolder\<AppId>"）
        if (FolderListing.IsAppItem(path))
        {
            if (MetaCache.TryGetValue(path, out var appCached)) return appCached;
            var appId = FolderListing.ExtractAppId(path);
            var appName = ShellApps.GetDisplayName(appId);
            var appMeta = new Meta(appName ?? appId, appName != null, false, "アプリケーション", null, null);
            MetaCache[path] = appMeta;
            return appMeta;
        }

        bool isFolder = Directory.Exists(path);
        bool exists = isFolder || File.Exists(path);
        if (MetaCache.TryGetValue(path, out var cached) && cached.Exists == exists)
            return cached;

        string name = (exists ? GetDisplayName(path) : null) ?? FallbackName(path);
        string type = GetTypeName(path, exists, isFolder);
        long? size = null;
        DateTime? modified = null;
        try
        {
            if (exists && !isFolder)
            {
                var fi = new FileInfo(path);
                size = fi.Length;
                modified = fi.LastWriteTime;
            }
            else if (isFolder)
            {
                modified = new DirectoryInfo(path).LastWriteTime;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var meta = new Meta(name, exists, isFolder, type, size, modified);
        MetaCache[path] = meta;
        return meta;
    }

    private static ImageSource? GetIcon(string path, int px, bool exists)
    {
        if (!IconCache.TryGetValue(path, out var bySize))
            IconCache[path] = bySize = new Dictionary<int, ImageSource?>();
        if (bySize.TryGetValue(px, out var cached))
            return cached;

        ImageSource? icon = FolderListing.IsAppItem(path)
            ? ShellApps.GetIcon(FolderListing.ExtractAppId(path), px)
            : (exists ? GetShellImage(path, px) : null) ?? GetIconByExtension(path);
        bySize[px] = icon;
        return icon;
    }

    private static string FallbackName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(name)) return path;
        var ext = Path.GetExtension(name);
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileNameWithoutExtension(name);
        return name;
    }

    private static string? GetDisplayName(string path)
    {
        IShellItem? item = null;
        try
        {
            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item);
            item.GetDisplayName(SIGDN.NORMALDISPLAY, out var pName);
            try { return Marshal.PtrToStringUni(pName); }
            finally { Marshal.FreeCoTaskMem(pName); }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
        }
    }

    private static ImageSource? GetShellImage(string path, int px)
    {
        IShellItem? item = null;
        try
        {
            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item);
            if (item is not IShellItemImageFactory factory) return null;

            var size = new SIZE { cx = px, cy = px };
            if (factory.GetImage(size, SIIGBF.RESIZETOFIT, out var hbmp) != 0 || hbmp == IntPtr.Zero)
                return null;
            try { return HBitmapToBitmapSource(hbmp); }
            finally { NativeMethods.DeleteObject(hbmp); }
        }
        catch
        {
            // アクセス不可・ネットワーク切断など。フォールバックに任せる
            return null;
        }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
        }
    }

    private static string GetTypeName(string path, bool exists, bool isFolder)
    {
        var info = new SHFILEINFO();
        uint flags = SHGFI_TYPENAME;
        uint attr = 0;
        if (!exists)
        {
            flags |= SHGFI_USEFILEATTRIBUTES;
            attr = Path.HasExtension(path) ? FILE_ATTRIBUTE_NORMAL : FILE_ATTRIBUTE_DIRECTORY;
        }
        if (SHGetFileInfo(path, attr, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) != IntPtr.Zero
            && !string.IsNullOrEmpty(info.szTypeName))
            return info.szTypeName;
        return isFolder ? "ファイル フォルダー" : "ファイル";
    }

    private static readonly int DibSectionSize = Marshal.SizeOf<DIBSECTION>();

    /// <summary>
    /// IShellItemImageFactory の HBITMAP は 32bpp のアルファ付き DIB。
    /// Imaging.CreateBitmapSourceFromHBitmap はアルファを落とすことがあるので自前で変換する。
    /// </summary>
    internal static BitmapSource? HBitmapToBitmapSource(IntPtr hbmp)
    {
        if (GetObject(hbmp, DibSectionSize, out DIBSECTION ds) == DibSectionSize
            && ds.dsBm.bmBitsPixel == 32 && ds.dsBm.bmBits != IntPtr.Zero)
        {
            int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;
            if (w > 0 && h > 0 && stride >= w * 4)
            {
                int totalBytes = checked(stride * h);
                var buf = ArrayPool<byte>.Shared.Rent(totalBytes);
                try
                {
                    if (ds.dsBmih.biHeight > 0) // ボトムアップ DIB → 行を逆順に直接コピー
                    {
                        for (int y = 0; y < h; y++)
                        {
                            Marshal.Copy(IntPtr.Add(ds.dsBm.bmBits, (h - 1 - y) * stride), buf, y * stride, stride);
                        }
                    }
                    else
                    {
                        Marshal.Copy(ds.dsBm.bmBits, buf, 0, totalBytes);
                    }

                    bool hasAlpha = HasNonZeroAlpha(buf.AsSpan(0, totalBytes));
                    var src = BitmapSource.Create(w, h, 96, 96,
                        hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, buf, stride);
                    src.Freeze();
                    return src;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }
        }

        var fallback = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        fallback.Freeze();
        return fallback;
    }

    private static bool HasNonZeroAlpha(ReadOnlySpan<byte> bytes)
    {
        if ((bytes.Length & 3) == 0)
        {
            foreach (uint pixel in MemoryMarshal.Cast<byte, uint>(bytes))
            {
                if ((pixel & 0xFF000000u) != 0)
                    return true;
            }
            return false;
        }

        for (int i = 3; i < bytes.Length; i += 4)
        {
            if (bytes[i] != 0) return true;
        }
        return false;
    }

    /// <summary>ファイルが存在しなくても拡張子からアイコンを得る。</summary>
    private static ImageSource? GetIconByExtension(string path)
    {
        var info = new SHFILEINFO();
        const uint flags = SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES;
        uint attr = Path.HasExtension(path) ? FILE_ATTRIBUTE_NORMAL : FILE_ATTRIBUTE_DIRECTORY;
        if (SHGetFileInfo(path, attr, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) == IntPtr.Zero
            || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            NativeMethods.DestroyIcon(info.hIcon);
        }
    }

    // ---- COM / Win32 定義 ----

    private enum SIGDN : uint
    {
        NORMALDISPLAY = 0x00000000,
    }

    [Flags]
    private enum SIIGBF
    {
        RESIZETOFIT = 0x00,
        BIGGERSIZEOK = 0x01,
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
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0;
        public uint dsBitfields1;
        public uint dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(IntPtr h, int c, out DIBSECTION pv);

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_TYPENAME = 0x000000400;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
}
