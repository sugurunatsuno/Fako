using Fako.Interop;
using Xunit;
using static Fako.Interop.NativeMethods;

namespace Fako.Tests;

public class FenceZOrderAndFadeTests
{
    private static readonly IntPtr Progman = new(0x1000);
    private static readonly IntPtr Notepad = new(0x2000);
    private static readonly IntPtr Browser = new(0x2001);
    private static readonly IntPtr Settings = new(0x2002);
    private static readonly IntPtr Taskbar = new(0x3000);
    private static readonly IntPtr Fence1 = new(0x4001);
    private static readonly IntPtr Fence2 = new(0x4002);
    private static readonly IntPtr Fence3 = new(0x4003);

    /// <summary>上から下への Z オーダー配列から GW_HWNDPREV 参照を作る。</summary>
    private static Func<IntPtr, IntPtr> PrevFromTopToBottom(params IntPtr[] zOrderTopToBottom)
    {
        var prevMap = new Dictionary<IntPtr, IntPtr>();
        for (int i = 0; i < zOrderTopToBottom.Length; i++)
            prevMap[zOrderTopToBottom[i]] = i == 0 ? IntPtr.Zero : zOrderTopToBottom[i - 1];
        return hwnd => prevMap.TryGetValue(hwnd, out var prev) ? prev : IntPtr.Zero;
    }

    // ------------------------------------------------------------------
    // FenceZOrder.ResolveDesktopTarget
    // ------------------------------------------------------------------

    [Fact]
    public void ResolveDesktopTarget_DesktopMissing_FallsBackToHwndBottom()
    {
        var target = FenceZOrder.ResolveDesktopTarget(
            Fence1,
            IntPtr.Zero,
            _ => IntPtr.Zero,
            _ => false,
            _ => false);

        Assert.Equal(DesktopZTarget.MoveAfter(HWND_BOTTOM), target);
    }

    [Fact]
    public void ResolveDesktopTarget_NormalAppAboveDesktop_InsertsBelowAppNotTop()
    {
        // Z-order: Browser -> Notepad -> Progman -> Fence1(hidden/behind)
        var prev = PrevFromTopToBottom(Browser, Notepad, Progman, Fence1);

        var target = FenceZOrder.ResolveDesktopTarget(
            Fence1,
            Progman,
            prev,
            h => h == Fence1,
            _ => false);

        // v1.0.2 の修正維持: デスクトップクリック等でも Notepad より前面に出さない
        Assert.Equal(DesktopZTarget.MoveAfter(Notepad), target);
    }

    [Fact]
    public void ResolveDesktopTarget_OnlyTopMostAboveDesktop_UsesHwndTop()
    {
        // Win+D 等で Progman が通常アプリより前に来た状態: Taskbar(TOPMOST) -> Progman -> Notepad -> Fence1
        var prev = PrevFromTopToBottom(Taskbar, Progman, Notepad, Fence1);

        var target = FenceZOrder.ResolveDesktopTarget(
            Fence1,
            Progman,
            prev,
            h => h == Fence1,
            h => h == Taskbar);

        Assert.Equal(DesktopZTarget.MoveAfter(HWND_TOP), target);
    }

    [Fact]
    public void ResolveDesktopTarget_NothingAboveDesktop_UsesHwndTop()
    {
        var prev = PrevFromTopToBottom(Progman, Fence1);

        var target = FenceZOrder.ResolveDesktopTarget(
            Fence1,
            Progman,
            prev,
            h => h == Fence1,
            _ => false);

        Assert.Equal(DesktopZTarget.MoveAfter(HWND_TOP), target);
    }

    [Fact]
    public void ResolveDesktopTarget_MultipleFencesAlreadyAboveDesktop_KeepsOrderWithoutSwapping()
    {
        // Z-order: Notepad -> Fence1 -> Fence2 -> Fence3 -> Progman
        var prev = PrevFromTopToBottom(Notepad, Fence1, Fence2, Fence3, Progman);
        bool IsOwn(IntPtr h) => h == Fence1 || h == Fence2 || h == Fence3;

        Assert.Equal(DesktopZTarget.Keep(), FenceZOrder.ResolveDesktopTarget(Fence1, Progman, prev, IsOwn, _ => false));
        Assert.Equal(DesktopZTarget.Keep(), FenceZOrder.ResolveDesktopTarget(Fence2, Progman, prev, IsOwn, _ => false));
        Assert.Equal(DesktopZTarget.Keep(), FenceZOrder.ResolveDesktopTarget(Fence3, Progman, prev, IsOwn, _ => false));
    }

    [Fact]
    public void ResolveDesktopTarget_SkipsOtherOwnFences_DoesNotUseSiblingFenceAsAnchor()
    {
        // Z-order: Notepad -> Fence1 -> Progman -> Fence2
        // Fence2 を再配置するとき、Fence1 ではなく Notepad を基準に選ぶ
        var prev = PrevFromTopToBottom(Notepad, Fence1, Progman, Fence2);
        bool IsOwn(IntPtr h) => h == Fence1 || h == Fence2;

        var target = FenceZOrder.ResolveDesktopTarget(Fence2, Progman, prev, IsOwn, _ => false);

        Assert.Equal(DesktopZTarget.MoveAfter(Notepad), target);
    }

    [Fact]
    public void ResolveDesktopTarget_DoesNotSkipNonFenceOwnWindowsLikeSettings()
    {
        // Z-order: Settings -> Fence1 -> Progman -> Fence2
        // 設定ウィンドウやパレットはフェンスではないので、フェンスはその背面に留まる
        var prev = PrevFromTopToBottom(Settings, Fence1, Progman, Fence2);
        bool IsOwnFence(IntPtr h) => h == Fence1 || h == Fence2;

        var target = FenceZOrder.ResolveDesktopTarget(Fence2, Progman, prev, IsOwnFence, _ => false);

        Assert.Equal(DesktopZTarget.MoveAfter(Settings), target);
    }

    [Fact]
    public void ResolveDesktopTarget_SiblingFencesBelowTopMost_ReturnsHwndTop()
    {
        // Z-order: Taskbar(TOPMOST) -> Fence1 -> Progman -> Fence2
        var prev = PrevFromTopToBottom(Taskbar, Fence1, Progman, Fence2);
        bool IsOwnFence(IntPtr h) => h == Fence1 || h == Fence2;

        var target = FenceZOrder.ResolveDesktopTarget(Fence2, Progman, prev, IsOwnFence, h => h == Taskbar);

        Assert.Equal(DesktopZTarget.MoveAfter(HWND_TOP), target);
    }

    [Fact]
    public void ResolveDesktopTarget_CycleInWindowList_TerminatesSafely()
    {
        // 壊れた循環リンクでも無限ループしない
        IntPtr prev(IntPtr h) => h == Progman ? Fence2 : Fence2;
        var target = FenceZOrder.ResolveDesktopTarget(Fence1, Progman, prev, h => h == Fence2, _ => false);

        Assert.Equal(DesktopZTarget.MoveAfter(HWND_TOP), target);
    }

    // ------------------------------------------------------------------
    // FenceZOrder.TryAdjustWindowPos (WM_WINDOWPOSCHANGING)
    // ------------------------------------------------------------------

    [Fact]
    public void TryAdjustWindowPos_DuringPeek_DoesNotModify()
    {
        var pos = new WINDOWPOS { hwndInsertAfter = HWND_TOPMOST, flags = 0 };

        bool changed = FenceZOrder.TryAdjustWindowPos(ref pos, isPeek: true, DesktopZTarget.MoveAfter(Notepad));

        Assert.False(changed);
        Assert.Equal(HWND_TOPMOST, pos.hwndInsertAfter);
    }

    [Fact]
    public void TryAdjustWindowPos_NoZOrderOrHideWindow_DoesNotModify()
    {
        var moveOnly = new WINDOWPOS { hwndInsertAfter = HWND_TOP, flags = SWP_NOZORDER };
        Assert.False(FenceZOrder.TryAdjustWindowPos(ref moveOnly, isPeek: false, DesktopZTarget.MoveAfter(Notepad)));
        Assert.Equal(HWND_TOP, moveOnly.hwndInsertAfter);

        var hiding = new WINDOWPOS { hwndInsertAfter = HWND_TOP, flags = SWP_HIDEWINDOW };
        Assert.False(FenceZOrder.TryAdjustWindowPos(ref hiding, isPeek: false, DesktopZTarget.MoveAfter(Notepad)));
        Assert.Equal(HWND_TOP, hiding.hwndInsertAfter);
    }

    [Fact]
    public void TryAdjustWindowPos_AlreadyInPlace_SetsSwpNoZOrder()
    {
        // フェンスをクリックした等で Windows が HWND_TOP へ持ち上げようとしても、Z オーダー変更を打ち消す
        var pos = new WINDOWPOS { hwndInsertAfter = HWND_TOP, flags = SWP_NOMOVE | SWP_NOSIZE };

        bool changed = FenceZOrder.TryAdjustWindowPos(ref pos, isPeek: false, DesktopZTarget.Keep());

        Assert.True(changed);
        Assert.NotEqual(0u, pos.flags & SWP_NOZORDER);
    }

    [Fact]
    public void TryAdjustWindowPos_OnShow_RewritesHwndTopToDesktopAnchor()
    {
        // Show() による HWND_TOP への挿入を、表示の瞬間から Notepad の直下へ書き換える
        var pos = new WINDOWPOS { hwndInsertAfter = HWND_TOP, flags = SWP_NOACTIVATE };

        bool changed = FenceZOrder.TryAdjustWindowPos(ref pos, isPeek: false, DesktopZTarget.MoveAfter(Notepad));

        Assert.True(changed);
        Assert.Equal(Notepad, pos.hwndInsertAfter);
        Assert.Equal(0u, pos.flags & SWP_NOZORDER);
    }

    [Fact]
    public void TryAdjustWindowPos_AlreadyMatchingTarget_ReturnsFalse()
    {
        var pos = new WINDOWPOS { hwndInsertAfter = Notepad, flags = SWP_NOMOVE | SWP_NOSIZE };

        bool changed = FenceZOrder.TryAdjustWindowPos(ref pos, isPeek: false, DesktopZTarget.MoveAfter(Notepad));

        Assert.False(changed);
        Assert.Equal(Notepad, pos.hwndInsertAfter);
    }

    [Fact]
    public void TryAdjustWindowPos_PeekEndWithHwndNoTopMost_PreservesNoTopMost()
    {
        var pos = new WINDOWPOS { hwndInsertAfter = HWND_NOTOPMOST, flags = SWP_NOMOVE | SWP_NOSIZE };

        bool changed = FenceZOrder.TryAdjustWindowPos(ref pos, isPeek: false, DesktopZTarget.MoveAfter(HWND_TOP));

        Assert.False(changed);
        Assert.Equal(HWND_NOTOPMOST, pos.hwndInsertAfter);
    }

    // ------------------------------------------------------------------
    // FadeHideCoordinator (遅延 Hide とキャンセル)
    // ------------------------------------------------------------------

    [Fact]
    public void FadeHideCoordinator_WaitsForRenderedFramesBeforeHiding()
    {
        var coord = new FadeHideCoordinator();
        int gen = coord.BeginFade();

        Assert.True(coord.TryScheduleHide(gen, framesToWait: 2));
        Assert.True(coord.IsHidePending);

        // 1 フレーム目: まだ Opacity == 0 のフレームを提示中なので Hide しない
        Assert.False(coord.OnFrameRendered());
        Assert.True(coord.IsHidePending);

        // 2 フレーム目: 最終フレームが提示されたので Hide する
        Assert.True(coord.OnFrameRendered());
        Assert.False(coord.IsHidePending);

        // 以降は再度発火しない
        Assert.False(coord.OnFrameRendered());
    }

    [Fact]
    public void FadeHideCoordinator_ShowDuringFadeOut_IgnoresStaleCompletedCallback()
    {
        var coord = new FadeHideCoordinator();
        int hideGen = coord.BeginFade();

        // フェードアウト完了前に再表示（FadeTo(true)）が呼ばれた
        _ = coord.BeginFade();

        // 古いフェードアウトの Completed が遅れて届いても Hide を予約しない
        Assert.False(coord.TryScheduleHide(hideGen));
        Assert.False(coord.IsHidePending);
        Assert.False(coord.OnFrameRendered());
    }

    [Fact]
    public void FadeHideCoordinator_ShowDuringPendingHideWait_CancelsHide()
    {
        var coord = new FadeHideCoordinator();
        int hideGen = coord.BeginFade();
        Assert.True(coord.TryScheduleHide(hideGen, framesToWait: 2));
        Assert.False(coord.OnFrameRendered()); // 1 フレーム経過

        // Hide() 実行直前に再表示が呼ばれた
        _ = coord.BeginFade();
        Assert.False(coord.IsHidePending);

        // 次のフレームが来ても Hide されない
        Assert.False(coord.OnFrameRendered());
        Assert.False(coord.OnFrameRendered());
    }

    [Fact]
    public void FadeHideCoordinator_CancelOnWindowClose_ClearsPendingHide()
    {
        var coord = new FadeHideCoordinator();
        int hideGen = coord.BeginFade();
        Assert.True(coord.TryScheduleHide(hideGen, framesToWait: 2));

        coord.Cancel();

        Assert.False(coord.IsHidePending);
        Assert.False(coord.OnFrameRendered());
    }

    [Fact]
    public void NativeMethods_IsDesktopRootClass_ZeroHandleReturnsFalse()
    {
        Assert.False(IsDesktopRootClass(IntPtr.Zero));
        Assert.False(IsWindowClass(IntPtr.Zero, "WorkerW"));
    }
}
