using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fako.Models;
using WinForms = System.Windows.Forms;

namespace Fako;

/// <summary>仮想デスクトップ上に固定した画像をフェンス内に描画する。</summary>
internal sealed class DesktopBackground
{
    private string? _path;
    private BitmapSource? _image;

    internal static double NormalizeOpacity(double opacity) =>
        double.IsFinite(opacity) ? Math.Clamp(opacity, 0, 1) : 1;

    // Screen.Bounds と PointToScreen は物理ピクセルに統一する。
    internal static Rect DesktopBounds()
    {
        var bounds = Rect.Empty;
        foreach (var screen in WinForms.Screen.AllScreens)
        {
            var b = screen.Bounds;
            bounds.Union(new Rect(b.X, b.Y, b.Width, b.Height));
        }
        return bounds.IsEmpty ? new Rect(0, 0, 1, 1) : bounds;
    }

    internal static Rect ImageBounds(Rect desktop, double width, double height, string? mode)
    {
        if (mode == "Fill" || width <= 0 || height <= 0) return desktop;
        double sx = desktop.Width / width, sy = desktop.Height / height;
        double scale = mode == "Uniform" ? Math.Min(sx, sy) : Math.Max(sx, sy);
        double w = width * scale, h = height * scale;
        return new Rect(desktop.X + (desktop.Width - w) / 2,
            desktop.Y + (desktop.Height - h) / 2, w, h);
    }

    internal static Rect LocalImageBounds(Rect image, Rect region, double width, double height) =>
        new((image.X - region.X) * width / region.Width,
            (image.Y - region.Y) * height / region.Height,
            image.Width * width / region.Width, image.Height * height / region.Height);

    private static BitmapSource Decode(string path)
    {
        using var stream = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    internal static string Import(string source)
    {
        _ = Decode(source); // 不正な画像は設定しない
        var directory = Path.Combine(ConfigStore.Directory, "backgrounds");
        System.IO.Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, Guid.NewGuid().ToString("N") + Path.GetExtension(source));
        File.Copy(source, target);
        return target;
    }

    private BitmapSource? GetImage(string? path)
    {
        if (string.Equals(path, _path, StringComparison.Ordinal)) return _image;
        _path = path;
        _image = null;
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { _image = Decode(path); }
        catch (Exception ex) { Log.Error("背景画像の読み込みに失敗", ex); }
        return _image;
    }

    internal Brush CreateBrush(FenceStyle style, Rect region, double width, double height)
    {
        var color = ColorUtil.Parse(style.Background, ColorUtil.DefaultBackground, 0x10);
        var tint = new SolidColorBrush(color);
        var image = GetImage(style.BackgroundImagePath);
        if (image is null || region.IsEmpty || region.Width <= 0 || region.Height <= 0
            || width <= 0 || height <= 0 || NormalizeOpacity(style.BackgroundImageOpacity) == 0)
            return tint;

        var desktop = DesktopBounds();
        var placement = ImageBounds(desktop, image.PixelWidth, image.PixelHeight, style.BackgroundImageStretch);
        var local = LocalImageBounds(placement, region, width, height);
        var area = new Rect(0, 0, width, height);
        var geometry = new RectangleGeometry(area);
        var layers = new DrawingGroup { ClipGeometry = geometry };
        // 透明な描画でもブラシの座標系をフェンス全体に固定する。
        layers.Children.Add(new GeometryDrawing(Brushes.Transparent, null, geometry));
        var imageLayer = new DrawingGroup { Opacity = NormalizeOpacity(style.BackgroundImageOpacity) };
        imageLayer.Children.Add(new ImageDrawing(image, local));
        layers.Children.Add(imageLayer);
        layers.Children.Add(new GeometryDrawing(tint, null, geometry));
        var brush = new DrawingBrush(layers)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = area,
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewport = new Rect(0, 0, 1, 1),
            Stretch = Stretch.Fill,
        };
        brush.Freeze();
        // Border 自体の背景ブラシなので角丸の形状に沿って描画される。
        return brush;
    }
}
