using System.Runtime.InteropServices;

namespace Fako.Interop;

/// <summary>
/// フェンスの移動・リサイズ中に、他のフェンスや画面端へ吸着させる。
/// Windows の移動／サイズ変更ループが送る WM_MOVING / WM_SIZING の RECT（物理ピクセル）を書き換える。
/// </summary>
internal static class Snapper
{
    public const int WM_SIZING = 0x0214;
    public const int WM_MOVING = 0x0216;

    // WM_SIZING の wParam（動かしている辺）
    private const int WMSZ_LEFT = 1, WMSZ_RIGHT = 2, WMSZ_TOP = 3, WMSZ_TOPLEFT = 4,
        WMSZ_TOPRIGHT = 5, WMSZ_BOTTOM = 6, WMSZ_BOTTOMLEFT = 7, WMSZ_BOTTOMRIGHT = 8;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    public sealed record Options(bool ToScreenEdges, double Distance, double Gap);

    /// <summary>移動中: 位置だけ吸着（サイズは変えない）。</summary>
    public static void SnapMove(ref RECT r, IntPtr self, IReadOnlyList<IntPtr> others, Options opt)
    {
        var (dist, gap, targets, work) = Prepare(r, self, others, opt);

        int dx = Best(new[] { r.Left, r.Right }, XCandidates(r, targets, work, gap, dist), dist);
        int dy = Best(new[] { r.Top, r.Bottom }, YCandidates(r, targets, work, gap, dist), dist);
        r.Left += dx; r.Right += dx;
        r.Top += dy; r.Bottom += dy;
    }

    /// <summary>リサイズ中: 動かしている辺だけ吸着。</summary>
    public static void SnapSize(ref RECT r, int edge, IntPtr self, IReadOnlyList<IntPtr> others, Options opt)
    {
        var (dist, gap, targets, work) = Prepare(r, self, others, opt);
        var xs = XCandidates(r, targets, work, gap, dist);
        var ys = YCandidates(r, targets, work, gap, dist);

        if (edge is WMSZ_LEFT or WMSZ_TOPLEFT or WMSZ_BOTTOMLEFT) r.Left += Best(new[] { r.Left }, xs, dist);
        if (edge is WMSZ_RIGHT or WMSZ_TOPRIGHT or WMSZ_BOTTOMRIGHT) r.Right += Best(new[] { r.Right }, xs, dist);
        if (edge is WMSZ_TOP or WMSZ_TOPLEFT or WMSZ_TOPRIGHT) r.Top += Best(new[] { r.Top }, ys, dist);
        if (edge is WMSZ_BOTTOM or WMSZ_BOTTOMLEFT or WMSZ_BOTTOMRIGHT) r.Bottom += Best(new[] { r.Bottom }, ys, dist);
    }

    private static (int dist, int gap, List<RECT> targets, RECT? work) Prepare(
        RECT r, IntPtr self, IReadOnlyList<IntPtr> others, Options opt)
    {
        double scale = GetDpiForWindow(self) / 96.0;
        if (scale <= 0) scale = 1;
        int dist = (int)Math.Round(opt.Distance * scale);
        int gap = (int)Math.Round(opt.Gap * scale);

        var targets = new List<RECT>();
        foreach (var h in others)
        {
            if (h == self || !IsWindowVisible(h)) continue;
            if (GetWindowRect(h, out var o)) targets.Add(o);
        }

        RECT? work = null;
        if (opt.ToScreenEdges)
        {
            var mon = MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(mon, ref mi)) work = mi.rcWork;
        }
        return (dist, gap, targets, work);
    }

    /// <summary>
    /// 横方向の吸着先の X 座標。
    /// 他のフェンスの左右（間隔を空けて隣に並べる／端を揃える）と、画面の作業領域の左右。
    /// 縦に離れすぎているフェンスは対象外。
    /// </summary>
    private static List<int> XCandidates(RECT r, List<RECT> targets, RECT? work, int gap, int dist)
    {
        var xs = new List<int>();
        foreach (var o in targets)
        {
            bool near = r.Top <= o.Bottom + gap + dist && r.Bottom >= o.Top - gap - dist;
            if (!near) continue;
            xs.Add(o.Right + gap); // 右隣に並べる（自分の左端）
            xs.Add(o.Left - gap);  // 左隣に並べる（自分の右端）
            xs.Add(o.Left);        // 左端を揃える
            xs.Add(o.Right);       // 右端を揃える
        }
        if (work is RECT w)
        {
            xs.Add(w.Left);
            xs.Add(w.Right);
        }
        return xs;
    }

    private static List<int> YCandidates(RECT r, List<RECT> targets, RECT? work, int gap, int dist)
    {
        var ys = new List<int>();
        foreach (var o in targets)
        {
            bool near = r.Left <= o.Right + gap + dist && r.Right >= o.Left - gap - dist;
            if (!near) continue;
            ys.Add(o.Bottom + gap);
            ys.Add(o.Top - gap);
            ys.Add(o.Top);
            ys.Add(o.Bottom);
        }
        if (work is RECT w)
        {
            ys.Add(w.Top);
            ys.Add(w.Bottom);
        }
        return ys;
    }

    /// <summary>自分の辺（edges）のどれかを候補へ合わせる最小の移動量。範囲外なら 0。</summary>
    private static int Best(int[] edges, List<int> candidates, int dist)
    {
        int best = 0, bestAbs = int.MaxValue;
        foreach (var e in edges)
        {
            foreach (var c in candidates)
            {
                int d = c - e;
                int a = Math.Abs(d);
                if (a <= dist && a < bestAbs)
                {
                    best = d;
                    bestAbs = a;
                }
            }
        }
        return best;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT r, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
}
