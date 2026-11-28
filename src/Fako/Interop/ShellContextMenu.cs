using System.IO;
using System.Runtime.InteropServices;
using Fako.Models;

namespace Fako.Interop;

/// <summary>
/// エクスプローラーと同じ（Windows 標準の）右クリックメニューを表示する。
///
/// シェルの IContextMenu をそのまま使うので、「送る」「プログラムから開く」「削除」「プロパティ」や
/// 他アプリのシェル拡張（7-Zip、Git など）もエクスプローラーと同じように出る。
/// 「送る」「新規作成」などのサブメニューやアイコンは、メニューのメッセージを IContextMenu2/3 に
/// 渡さないと表示されないため、表示中は <see cref="Active"/> を通してオーナーウィンドウから転送する。
///
/// ※ Windows 11 の新しいメニューはエクスプローラー専用で他アプリからは出せないため、
///    出るのは「その他のオプションを確認」で表示される従来のメニュー。
/// </summary>
internal sealed class ShellContextMenu
{
    private const uint FirstShellId = 1;
    private const uint LastShellId = 0x7FFF;
    private const uint FirstExtraId = 0x8000;

    /// <summary>表示中のメニュー（オーナーウィンドウがメッセージを転送するため）。</summary>
    public static ShellContextMenu? Active { get; private set; }

    private IContextMenu2? _cm2;
    private IContextMenu3? _cm3;

    public sealed record Extra(string Text, Action Action);

    /// <summary>
    /// メニューを表示して、選ばれたコマンドを実行する（モーダル）。
    /// </summary>
    /// <param name="paths">対象。同じフォルダーにあるものだけをまとめて扱う（先頭の項目のフォルダー）。</param>
    /// <param name="extended">Shift を押しているとき: 拡張コマンド（「パスのコピー」など）も出す。</param>
    /// <returns>メニューを出せたら true（出せなければ呼び出し側で代わりのメニューを出す）。</returns>
    /// <param name="intercept">
    /// シェルに任せず自分で処理するコマンド（例: "rename"）。verb を受け取り、処理したら true。
    /// </param>
    public static bool Show(IntPtr hwnd, IReadOnlyList<string> paths, int x, int y, bool extended,
        IReadOnlyList<Extra> extras, Func<string, bool>? intercept, out string? invokedVerb)
    {
        invokedVerb = null;
        if (paths.Count == 0) return false;

        // 同じ親フォルダーの項目だけを対象にする（IContextMenu は 1 つのフォルダー内の項目しか扱えない）
        string ParentKey(string p) => FolderListing.IsShellItem(p) ? "::desktop"
            : FolderListing.IsAppItem(p) ? "::apps"
            : (Path.GetDirectoryName(p.TrimEnd('\\')) ?? p);
        var key = ParentKey(paths[0]);
        var targets = paths.Where(p => string.Equals(ParentKey(p), key, StringComparison.OrdinalIgnoreCase)).ToList();

        var absolute = new List<IntPtr>();
        IntPtr folderPtr = IntPtr.Zero, menuPtr = IntPtr.Zero, hmenu = IntPtr.Zero;
        object? folderObj = null, menuObj = null;
        try
        {
            foreach (var p in targets)
            {
                if (SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
                    absolute.Add(pidl);
            }
            if (absolute.Count == 0) return false;

            var children = new IntPtr[absolute.Count];
            var iidFolder = typeof(IShellFolder).GUID;
            if (SHBindToParent(absolute[0], ref iidFolder, out folderPtr, out children[0]) != 0) return false;
            for (int i = 1; i < absolute.Count; i++)
                children[i] = ILFindLastID(absolute[i]);

            folderObj = Marshal.GetObjectForIUnknown(folderPtr);
            var folder = (IShellFolder)folderObj;
            var iidMenu = typeof(IContextMenu).GUID;
            folder.GetUIObjectOf(hwnd, (uint)children.Length, children, ref iidMenu, IntPtr.Zero, out menuPtr);
            if (menuPtr == IntPtr.Zero) return false;

            menuObj = Marshal.GetObjectForIUnknown(menuPtr);
            var cm = (IContextMenu)menuObj;
            var instance = new ShellContextMenu
            {
                _cm3 = menuObj as IContextMenu3,
                _cm2 = menuObj as IContextMenu2,
            };

            hmenu = CreatePopupMenu();
            // CMF_CANRENAME: 「名前の変更」を出す（実行は intercept 側で自前のダイアログ）
            uint flags = CMF_NORMAL | (extended ? CMF_EXTENDEDVERBS : 0) | (intercept != null ? CMF_CANRENAME : 0);
            if (cm.QueryContextMenu(hmenu, 0, FirstShellId, LastShellId, flags) < 0) return false;

            if (extras.Count > 0)
            {
                AppendMenu(hmenu, MF_SEPARATOR, UIntPtr.Zero, null);
                for (int i = 0; i < extras.Count; i++)
                    AppendMenu(hmenu, MF_STRING, (UIntPtr)(FirstExtraId + (uint)i), extras[i].Text);
            }

            Active = instance;
            uint cmd;
            try
            {
                NativeMethods.SetForegroundWindow(hwnd); // 前面にしないとメニュー外のクリックで閉じない
                cmd = TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, x, y, hwnd, IntPtr.Zero);
            }
            finally
            {
                Active = null;
            }

            if (cmd >= FirstExtraId && cmd < FirstExtraId + extras.Count)
            {
                extras[(int)(cmd - FirstExtraId)].Action();
            }
            else if (cmd >= FirstShellId && cmd <= LastShellId)
            {
                invokedVerb = GetVerb(cm, cmd - FirstShellId);
                if (invokedVerb == null || intercept?.Invoke(invokedVerb) != true)
                    Invoke(cm, hwnd, cmd - FirstShellId, x, y);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("標準の右クリックメニューを表示できません", ex);
            return false;
        }
        finally
        {
            if (hmenu != IntPtr.Zero) DestroyMenu(hmenu);
            if (menuObj != null) Marshal.ReleaseComObject(menuObj);
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (folderObj != null) Marshal.ReleaseComObject(folderObj);
            if (folderPtr != IntPtr.Zero) Marshal.Release(folderPtr);
            foreach (var p in absolute) Marshal.FreeCoTaskMem(p);
        }
    }

    /// <summary>オーナーウィンドウのウィンドウプロシージャから呼ぶ。処理したら true。</summary>
    public bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (msg is not (WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)) return false;
        try
        {
            if (_cm3 != null)
                return _cm3.HandleMenuMsg2((uint)msg, wParam, lParam, out result) == 0;
            if (_cm2 != null && msg != WM_MENUCHAR)
                return _cm2.HandleMenuMsg((uint)msg, wParam, lParam) == 0;
        }
        catch
        {
            // シェル拡張側の失敗は無視
        }
        return false;
    }

    private static string? GetVerb(IContextMenu cm, uint offset)
    {
        var buf = Marshal.AllocHGlobal(512 * 2);
        try
        {
            return cm.GetCommandString((UIntPtr)offset, GCS_VERBW, IntPtr.Zero, buf, 512) == 0
                ? Marshal.PtrToStringUni(buf)
                : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static void Invoke(IContextMenu cm, IntPtr hwnd, uint offset, int x, int y)
    {
        uint mask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE;
        if ((GetKeyState(VK_CONTROL) & 0x8000) != 0) mask |= CMIC_MASK_CONTROL_DOWN;
        if ((GetKeyState(VK_SHIFT) & 0x8000) != 0) mask |= CMIC_MASK_SHIFT_DOWN;
        var info = new CMINVOKECOMMANDINFOEX
        {
            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
            fMask = mask,
            hwnd = hwnd,
            lpVerb = (IntPtr)offset,
            lpVerbW = (IntPtr)offset,
            nShow = SW_SHOWNORMAL,
            ptInvoke = new NativeMethods.POINT { X = x, Y = y },
        };
        cm.InvokeCommand(ref info);
    }

    // ------------------------------------------------------------------
    // Win32 / COM
    // ------------------------------------------------------------------

    public const int WM_INITMENUPOPUP = 0x0117;
    public const int WM_DRAWITEM = 0x002B;
    public const int WM_MEASUREITEM = 0x002C;
    public const int WM_MENUCHAR = 0x0120;

    private const uint CMF_NORMAL = 0x0;
    private const uint CMF_EXTENDEDVERBS = 0x100;
    private const uint CMF_CANRENAME = 0x10;
    private const uint MF_STRING = 0x0;
    private const uint MF_SEPARATOR = 0x800;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint GCS_VERBW = 0x4;
    private const uint CMIC_MASK_UNICODE = 0x00004000;
    private const uint CMIC_MASK_PTINVOKE = 0x20000000;
    private const uint CMIC_MASK_SHIFT_DOWN = 0x10000000;
    private const uint CMIC_MASK_CONTROL_DOWN = 0x40000000;
    private const int SW_SHOWNORMAL = 1;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
        public NativeMethods.POINT ptInvoke;
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint eaten, out IntPtr pidl, ref uint attrs);
        void EnumObjects(IntPtr hwnd, uint flags, out IntPtr enumIdList);
        void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        void GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        void GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr pName);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        void InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        void InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        void InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);
    [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);
}
