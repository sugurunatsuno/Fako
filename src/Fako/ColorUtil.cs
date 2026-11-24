using System.Windows.Media;

namespace Fako;

internal static class ColorUtil
{
    public static readonly Color DefaultBackground = Color.FromArgb(0xA0, 0x20, 0x24, 0x28);

    /// <summary>"#AARRGGBB" / "#RRGGBB" / 色名を Color に。読めなければ fallback。</summary>
    public static Color Parse(string? s, Color fallback, byte minAlpha = 0)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(s) && ColorConverter.ConvertFromString(s) is Color c)
            {
                if (c.A < minAlpha) c.A = minAlpha;
                return c;
            }
        }
        catch (FormatException) { }
        return fallback;
    }

    public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}
