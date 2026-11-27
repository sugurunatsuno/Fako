namespace Fako;

/// <summary>
/// フェードアウト完了後の遅延 Hide() を世代番号で管理する。
/// Opacity == 0 の最終フレームが合成されてから Hide() することで残像を防ぎ、
/// 待機中に再表示（FadeTo(true)）やウィンドウ終了が起きた場合は古い Hide() 予約を確実に破棄する。
/// </summary>
internal sealed class FadeHideCoordinator
{
    public const int DefaultPostFadeFrames = 2;

    public int Generation { get; private set; }
    public bool IsHidePending { get; private set; }

    private int _pendingGeneration;
    private int _remainingFrames;

    /// <summary>新しいフェードを開始し、進行中の Hide 予約を無効化して新しい世代番号を返す。</summary>
    public int BeginFade()
    {
        Generation++;
        IsHidePending = false;
        _remainingFrames = 0;
        return Generation;
    }

    /// <summary>
    /// フェードアウト完了時に呼ぶ。世代が現在と一致していれば、
    /// 指定フレーム数だけ描画が進んだ後に Hide() する予約を立てて true を返す。
    /// </summary>
    public bool TryScheduleHide(int generation, int framesToWait = DefaultPostFadeFrames)
    {
        if (generation != Generation) return false;
        _pendingGeneration = generation;
        _remainingFrames = Math.Max(1, framesToWait);
        IsHidePending = true;
        return true;
    }

    /// <summary>
    /// 描画フレームが 1 回進むごとに呼ぶ。
    /// 最終フレームが提示され Hide() を実行すべきタイミングになったら true を返す。
    /// </summary>
    public bool OnFrameRendered()
    {
        if (!IsHidePending) return false;
        if (_pendingGeneration != Generation)
        {
            IsHidePending = false;
            _remainingFrames = 0;
            return false;
        }
        if (--_remainingFrames > 0) return false;
        IsHidePending = false;
        return true;
    }

    /// <summary>待機中の Hide 予約をキャンセルする（再表示・ウィンドウ終了時）。</summary>
    public void Cancel()
    {
        Generation++;
        IsHidePending = false;
        _remainingFrames = 0;
    }
}
