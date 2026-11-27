using static Fako.Interop.NativeMethods;

namespace Fako.Interop;

/// <summary>デスクトップ直上へフェンスを配置するための Z オーダー判定結果。</summary>
internal readonly struct DesktopZTarget : IEquatable<DesktopZTarget>
{
    /// <summary>すでにデスクトップ直上のフェンス帯にあるため Z オーダー変更が不要か。</summary>
    public bool AlreadyInPlace { get; }

    /// <summary><see cref="AlreadyInPlace"/> が false のときに挿入基準として使う hWndInsertAfter。</summary>
    public IntPtr InsertAfter { get; }

    private DesktopZTarget(bool alreadyInPlace, IntPtr insertAfter)
    {
        AlreadyInPlace = alreadyInPlace;
        InsertAfter = insertAfter;
    }

    public static DesktopZTarget Keep() => new(true, IntPtr.Zero);
    public static DesktopZTarget MoveAfter(IntPtr insertAfter) => new(false, insertAfter);

    public bool Equals(DesktopZTarget other) =>
        AlreadyInPlace == other.AlreadyInPlace && InsertAfter == other.InsertAfter;

    public override bool Equals(object? obj) => obj is DesktopZTarget other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(AlreadyInPlace, InsertAfter);
    public static bool operator ==(DesktopZTarget left, DesktopZTarget right) => left.Equals(right);
    public static bool operator !=(DesktopZTarget left, DesktopZTarget right) => !left.Equals(right);
    public override string ToString() => AlreadyInPlace ? "Keep" : $"MoveAfter(0x{InsertAfter.ToInt64():X})";
}

/// <summary>
/// フェンスを「デスクトップ（Progman / WorkerW）の直上、かつ他の通常アプリより背面」に保つための
/// Z オーダー決定ロジック。
/// </summary>
internal static class FenceZOrder
{
    private const int MaxWalkSteps = 512;

    /// <summary>
    /// 実行時用の高速パス（アロケーション・デリゲート呼び出し無し）。
    /// デスクトップから上方向（GW_HWNDPREV）へ辿り、自プロセスのフェンスを飛ばして基準ウィンドウを選ぶ。
    /// </summary>
    public static DesktopZTarget ResolveDesktopTarget(
        IntPtr selfHwnd,
        IntPtr desktopHwnd,
        IReadOnlyList<FenceWindow> fences)
    {
        if (desktopHwnd == IntPtr.Zero)
            return DesktopZTarget.MoveAfter(HWND_BOTTOM);

        IntPtr cur = GetWindow(desktopHwnd, GW_HWNDPREV);
        int steps = 0;
        while (cur != IntPtr.Zero && steps++ < MaxWalkSteps)
        {
            if (selfHwnd != IntPtr.Zero && cur == selfHwnd)
                return DesktopZTarget.Keep();

            if (IsFenceHandle(fences, cur))
            {
                cur = GetWindow(cur, GW_HWNDPREV);
                continue;
            }

            long ex = GetWindowLongPtr(cur, GWL_EXSTYLE).ToInt64();
            return (ex & WS_EX_TOPMOST) != 0
                ? DesktopZTarget.MoveAfter(HWND_TOP)
                : DesktopZTarget.MoveAfter(cur);
        }

        return DesktopZTarget.MoveAfter(HWND_TOP);
    }

    /// <summary>単体テスト用の純粋版。Win32 呼び出しを差し替えて境界条件を検証できる。</summary>
    public static DesktopZTarget ResolveDesktopTarget(
        IntPtr selfHwnd,
        IntPtr desktopHwnd,
        Func<IntPtr, IntPtr> getPrevWindow,
        Func<IntPtr, bool> isOwnFence,
        Func<IntPtr, bool> isTopMost)
    {
        if (desktopHwnd == IntPtr.Zero)
            return DesktopZTarget.MoveAfter(HWND_BOTTOM);

        IntPtr cur = getPrevWindow(desktopHwnd);
        int steps = 0;
        while (cur != IntPtr.Zero && steps++ < MaxWalkSteps)
        {
            if (selfHwnd != IntPtr.Zero && cur == selfHwnd)
                return DesktopZTarget.Keep();

            if (isOwnFence(cur))
            {
                cur = getPrevWindow(cur);
                continue;
            }

            return isTopMost(cur)
                ? DesktopZTarget.MoveAfter(HWND_TOP)
                : DesktopZTarget.MoveAfter(cur);
        }

        return DesktopZTarget.MoveAfter(HWND_TOP);
    }

    /// <summary>
    /// WM_WINDOWPOSCHANGING の WINDOWPOS を補正し、変更があれば true を返す。
    /// Peek 中や Z オーダー変更を含まないメッセージでは何もしない。
    /// </summary>
    public static bool TryAdjustWindowPos(ref WINDOWPOS pos, bool isPeek, DesktopZTarget target)
    {
        if (isPeek || (pos.flags & (SWP_NOZORDER | SWP_HIDEWINDOW)) != 0)
            return false;

        if (target.AlreadyInPlace)
        {
            pos.flags |= SWP_NOZORDER;
            return true;
        }

        // Peek 解除時の HWND_NOTOPMOST は WS_EX_TOPMOST を外すために維持する
        if (target.InsertAfter == HWND_TOP && pos.hwndInsertAfter == HWND_NOTOPMOST)
            return false;

        if (pos.hwndInsertAfter == target.InsertAfter)
            return false;

        pos.hwndInsertAfter = target.InsertAfter;
        return true;
    }

    private static bool IsFenceHandle(IReadOnlyList<FenceWindow> fences, IntPtr hwnd)
    {
        int count = fences.Count;
        for (int i = 0; i < count; i++)
        {
            if (fences[i].Handle == hwnd) return true;
        }
        return false;
    }
}
