using static Fako.Interop.NativeMethods;

namespace Fako.Interop;

/// <summary>デスクトップ（Progman / WorkerW / SHELLDLL_DefView）まわりの操作。</summary>
internal static class Desktop
{
    /// <summary>エクスプローラーの「デスクトップ アイコンの表示」切り替えコマンド。</summary>
    private const int CmdToggleDesktopIcons = 0x7402;

    public static IntPtr GetProgman() => FindWindow("Progman", null);

    /// <summary>
    /// デスクトップの土台になっているウィンドウ（Progman / WorkerW）。
    /// 前面にあるものを優先し、見つからなければ SHELLDLL_DefView の親、最後に Progman を返す。
    /// </summary>
    public static IntPtr GetDesktopRoot()
    {
        var foreground = GetForegroundWindow();
        if (GetClassName(foreground) is "Progman" or "WorkerW") return foreground;

        var defView = FindDefView();
        if (defView != IntPtr.Zero)
        {
            var root = GetParent(defView);
            if (GetClassName(root) is "Progman" or "WorkerW") return root;
        }
        return GetProgman();
    }

    public static IntPtr FindDefView()
    {
        var progman = GetProgman();
        var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero) return defView;

        // 壁紙のスライドショー等で WorkerW の下に移っていることがある
        EnumWindows((hwnd, _) =>
        {
            if (GetClassName(hwnd) != "WorkerW") return true;
            var dv = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (dv == IntPtr.Zero) return true;
            defView = dv;
            return false;
        }, IntPtr.Zero);
        return defView;
    }

    public static IntPtr FindDesktopListView()
    {
        var dv = FindDefView();
        return dv == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(dv, IntPtr.Zero, "SysListView32", null);
    }

    public static bool AreIconsVisible()
    {
        var lv = FindDesktopListView();
        return lv == IntPtr.Zero || IsWindowVisible(lv);
    }

    public static void SetIconsVisible(bool visible)
    {
        if (AreIconsVisible() == visible) return;
        var dv = FindDefView();
        if (dv != IntPtr.Zero)
            SendMessage(dv, WM_COMMAND, new IntPtr(CmdToggleDesktopIcons), IntPtr.Zero);
    }

    /// <summary>そのウィンドウがデスクトップ本体（の一部）かどうか。</summary>
    public static bool IsDesktopSurface(IntPtr hwnd, out bool isListView)
    {
        isListView = false;
        var cls = GetClassName(hwnd);
        switch (cls)
        {
            case "SysListView32":
            {
                var parent = GetParent(hwnd);
                if (GetClassName(parent) != "SHELLDLL_DefView") return false;
                var top = GetClassName(GetParent(parent));
                if (top is not ("Progman" or "WorkerW")) return false;
                isListView = true;
                return true;
            }
            case "SHELLDLL_DefView":
                return GetClassName(GetParent(hwnd)) is "Progman" or "WorkerW";
            case "Progman":
            case "WorkerW":
                return true;
            default:
                return false;
        }
    }
}
