using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Threading;
using static Fako.Interop.NativeMethods;

namespace Fako.Interop;

/// <summary>
/// デスクトップの「何もない所」のダブルクリックを検出する。
///
/// 低レベルマウスフックは、コールバックが一定時間（LowLevelHooksTimeout）内に返らないと飛ばされ、
/// 繰り返すと Windows に黙って外される。UI スレッドに置くと、描画やアイコン読み込み、
/// UI Automation の問い合わせで詰まって取りこぼすため、
///   - フックは専用スレッド（自前のメッセージループ）で受ける
///   - 「アイコンの上ではないか」の判定（UI Automation）はスレッドプールで行う
///   - 結果だけ UI スレッドへ渡す
/// </summary>
internal sealed class DesktopDoubleClickHook : IDisposable
{
    private const int MaxIgnoredLogs = 100;

    private readonly Dispatcher _ui;
    private readonly Thread _thread;
    private LowLevelMouseProc? _proc; // GC されないよう保持
    private IntPtr _hook;
    private uint _threadId;
    private uint _lastTime;
    private POINT _lastPt;
    private bool _hasLast;
    private int _checking;
    private int _ignoredLogs;

    public event Action? DoubleClicked;

    public DesktopDoubleClickHook(Dispatcher ui)
    {
        _ui = ui;
        using var ready = new ManualResetEventSlim();
        int error = 0;
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _proc = HookProc;
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
            error = Marshal.GetLastWin32Error();
            ready.Set();
            if (_hook == IntPtr.Zero) return;

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        })
        {
            IsBackground = true,
            Name = "Fako mouse hook",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        if (_hook == IntPtr.Zero)
            throw new Win32Exception(error);
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == WM_LBUTTONDOWN)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            // 判定範囲は Windows のダブルクリック矩形（SM_CXDOUBLECLK × SM_CYDOUBLECLK）を少し広めに取る
            bool isDouble = _hasLast
                && unchecked(info.time - _lastTime) <= GetDoubleClickTime()
                && Math.Abs(info.pt.X - _lastPt.X) <= GetSystemMetrics(SM_CXDOUBLECLK)
                && Math.Abs(info.pt.Y - _lastPt.Y) <= GetSystemMetrics(SM_CYDOUBLECLK);

            if (isDouble)
            {
                _hasLast = false;
                var pt = info.pt;
                if (Interlocked.Exchange(ref _checking, 1) == 0)
                    ThreadPool.QueueUserWorkItem(_ => Check(pt));
            }
            else
            {
                _hasLast = true;
                _lastTime = info.time;
                _lastPt = info.pt;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void Check(POINT pt)
    {
        string reason = "";
        try
        {
            var hwnd = WindowFromPoint(pt);
            if (!Desktop.IsDesktopSurface(hwnd, out bool isListView))
            {
                // デスクトップ以外のダブルクリックはよくあるのでログしない
                return;
            }
            if (isListView)
            {
                var type = HitTestType(pt);
                if (type is not ("ControlType.List" or "ControlType.Pane"))
                {
                    reason = $"on item ({type})";
                    return;
                }
            }
            _ui.BeginInvoke(() => DoubleClicked?.Invoke());
        }
        catch (Exception ex)
        {
            reason = $"error {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (reason.Length > 0 && Interlocked.Increment(ref _ignoredLogs) <= MaxIgnoredLogs)
                Log.Info($"desktop double-click ignored at ({pt.X},{pt.Y}): {reason}");
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    private static string HitTestType(POINT pt)
    {
        var el = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
        return el?.Current.ControlType.ProgrammaticName ?? "(none)";
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(1));
            _threadId = 0;
        }
    }
}
