using static Fako.Interop.NativeMethods;

namespace Fako.Interop;

/// <summary>
/// 前面ウィンドウの切り替わりを監視し、「デスクトップが前面に来た／他のアプリに移った」を通知する。
/// 自プロセスのウィンドウ（フェンス自身・メニュー）への切り替えは無視する。
/// </summary>
internal sealed class ForegroundWatcher : IDisposable
{
    private readonly WinEventProc _proc; // GC されないよう保持
    private IntPtr _hook;

    /// <summary>true = デスクトップが前面、false = 他のアプリが前面。</summary>
    public event Action<bool>? DesktopForegroundChanged;

    public ForegroundWatcher()
    {
        _proc = OnEvent;
        // OUTOFCONTEXT のコールバックは、フックを登録したスレッドのメッセージループ上で呼ばれる
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc,
            0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    public static bool IsDesktopForeground() => IsDesktopWindow(GetForegroundWindow());

    private static bool IsDesktopWindow(IntPtr hwnd) => GetClassName(hwnd) is "Progman" or "WorkerW";

    private void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero) return;
        DesktopForegroundChanged?.Invoke(IsDesktopWindow(hwnd));
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
