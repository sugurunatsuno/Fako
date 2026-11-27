using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fako.Models;
using Xunit;

namespace Fako.Tests;

public class DesktopBackgroundTests
{
    [Fact]
    public void CoverUsesDesktopNotFence()
    {
        var desktop = new Rect(-1920, 0, 3840, 1080);
        var image = DesktopBackground.ImageBounds(desktop, 1920, 1080, "UniformToFill");
        Assert.Equal(new Rect(-1920, -540, 3840, 2160), image);
        var a = DesktopBackground.LocalImageBounds(image, new Rect(-100, 100, 200, 100), 200, 100);
        var b = DesktopBackground.LocalImageBounds(image, new Rect(100, 100, 200, 100), 200, 100);
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.X - 200, b.X);
    }

    [Fact]
    public void ResizeAndDpiKeepPhysicalScale()
    {
        var image = new Rect(0, 0, 1920, 1080);
        var a = DesktopBackground.LocalImageBounds(image, new Rect(300, 200, 300, 150), 200, 100);
        var b = DesktopBackground.LocalImageBounds(image, new Rect(300, 200, 600, 300), 400, 200);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ContainAndFillUseDesktopBounds()
    {
        var desktop = new Rect(-100, 0, 200, 100);
        Assert.Equal(new Rect(-50, 0, 100, 100), DesktopBackground.ImageBounds(desktop, 100, 100, "Uniform"));
        Assert.Equal(desktop, DesktopBackground.ImageBounds(desktop, 100, 100, "Fill"));
    }

    [Fact]
    public void OpacityIsNormalized()
    {
        Assert.Equal(0d, DesktopBackground.NormalizeOpacity(-1));
        Assert.Equal(1d, DesktopBackground.NormalizeOpacity(double.NaN));
        Assert.Equal(1d, DesktopBackground.NormalizeOpacity(2));
    }

    [Fact]
    public void InnerLocalBoundsSubtractsBorderThickness()
    {
        Assert.Equal(new Rect(4, 4, 192, 92),
            DesktopBackground.InnerLocalBounds(200, 100, new Thickness(4)));
        Assert.Equal(Rect.Empty,
            DesktopBackground.InnerLocalBounds(8, 8, new Thickness(4)));
    }

    [Fact]
    public void ImportCopiesValidImageAndRejectsInvalidFile()
    {
        var tempDir = TestTemp.CreateDirectory();
        try
        {
            var srcPng = Path.Combine(tempDir, "quad.png");
            SaveQuadrantPng(srcPng, 200, 100);
            var targetDir = Path.Combine(tempDir, "backgrounds");

            var imported = DesktopBackground.Import(srcPng, targetDir);
            Assert.True(File.Exists(imported));
            Assert.Equal(targetDir, Path.GetDirectoryName(imported));
            Assert.Equal(".png", Path.GetExtension(imported));

            var broken = Path.Combine(tempDir, "broken.png");
            File.WriteAllText(broken, "not a png");
            Assert.ThrowsAny<Exception>(() => DesktopBackground.Import(broken, targetDir));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void RenderedBorder_StaysFixedOnMoveReturnAndDoesNotRedecode()
    {
        var tempDir = TestTemp.CreateDirectory();
        try
        {
            var png = Path.Combine(tempDir, "quad.png");
            SaveQuadrantPng(png, 200, 100);

            RunOnSta(() =>
            {
                var bg = new DesktopBackground();
                var desktop = new Rect(0, 0, 200, 100);
                var style = new FenceStyle
                {
                    Background = "#10000000",
                    BackgroundImagePath = png,
                    BackgroundImageOpacity = 1.0,
                    BackgroundImageStretch = "Fill",
                    BorderThickness = 0,
                    CornerRadius = 0,
                };

                var first = RenderFencePixels(bg, style, desktop, new Rect(0, 0, 100, 50));
                var right = RenderFencePixels(bg, style, desktop, new Rect(100, 0, 100, 50));
                var bottom = RenderFencePixels(bg, style, desktop, new Rect(0, 50, 100, 50));
                var bottomRight = RenderFencePixels(bg, style, desktop, new Rect(100, 50, 100, 50));
                var returned = RenderFencePixels(bg, style, desktop, new Rect(0, 0, 100, 50));

                AssertRed(PixelAt(first, 100, 50, 25));
                AssertGreen(PixelAt(right, 100, 50, 25));
                AssertBlue(PixelAt(bottom, 100, 50, 25));
                AssertYellow(PixelAt(bottomRight, 100, 50, 25));
                Assert.Equal(first, returned);
                Assert.Equal(1, bg.DecodeCount);
            });
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void RenderedBorder_AlignsAcrossBorderThicknessWithoutShift()
    {
        var tempDir = TestTemp.CreateDirectory();
        try
        {
            var png = Path.Combine(tempDir, "quad.png");
            SaveQuadrantPng(png, 200, 100);

            RunOnSta(() =>
            {
                var bg = new DesktopBackground();
                var desktop = new Rect(0, 0, 200, 100);
                var style = new FenceStyle
                {
                    Background = "#10000000",
                    BackgroundImagePath = png,
                    BackgroundImageOpacity = 1.0,
                    BackgroundImageStretch = "Fill",
                    BorderThickness = 10,
                    CornerRadius = 0,
                };

                // 外側 X=[86..146]、枠 10px のため内側 X=[96..136]。
                // X=100 の赤/緑境界は内側 +4px = 外側 x=14 に一致する必要がある。
                var pixels = RenderFencePixels(bg, style, desktop, new Rect(86, 10, 60, 40));
                AssertRed(PixelAt(pixels, 60, 12, 20));
                AssertGreen(PixelAt(pixels, 60, 16, 20));
            });
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void RenderedBorder_ClipsToCornerRadiusAndRespectsOpacity()
    {
        var tempDir = TestTemp.CreateDirectory();
        try
        {
            var png = Path.Combine(tempDir, "solid-red.png");
            SaveSolidPng(png, 100, 100, Colors.Red);

            RunOnSta(() =>
            {
                var bg = new DesktopBackground();
                var desktop = new Rect(0, 0, 100, 100);
                var style = new FenceStyle
                {
                    Background = "#10000000",
                    BackgroundImagePath = png,
                    BackgroundImageOpacity = 1.0,
                    BackgroundImageStretch = "Fill",
                    BorderThickness = 0,
                    CornerRadius = 20,
                };

                var opaqueImg = RenderFencePixels(bg, style, desktop, new Rect(0, 0, 100, 100));
                Assert.Equal(0, PixelAt(opaqueImg, 100, 0, 0).A); // 角丸の外側には描画しない
                var centerOpaque = PixelAt(opaqueImg, 100, 50, 50);
                Assert.Equal(255, centerOpaque.A);
                AssertRed(centerOpaque);

                // 画像の濃さだけ 50% に下げると、透過率が独立して下がる
                style.BackgroundImageOpacity = 0.5;
                var halfImg = RenderFencePixels(bg, style, desktop, new Rect(0, 0, 100, 100));
                var centerHalf = PixelAt(halfImg, 100, 50, 50);
                Assert.InRange(centerHalf.A, 120, 165);
                Assert.True(centerHalf.R < centerOpaque.R);

                // 画像の濃さ 0% なら背景色のみ（青）にフォールバック
                style.Background = "#FF0000FF";
                style.BackgroundImageOpacity = 0.0;
                var tintOnly = RenderFencePixels(bg, style, desktop, new Rect(0, 0, 100, 100));
                AssertBlue(PixelAt(tintOnly, 100, 50, 50));
            });
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void RenderedBorder_UniformModeDoesNotDrawOutsideImageBounds()
    {
        var tempDir = TestTemp.CreateDirectory();
        try
        {
            var png = Path.Combine(tempDir, "square-red.png");
            SaveSolidPng(png, 100, 100, Colors.Red);

            RunOnSta(() =>
            {
                var bg = new DesktopBackground();
                var desktop = new Rect(0, 0, 200, 100); // Uniform 配置では画像領域が X=[50..150]
                var style = new FenceStyle
                {
                    Background = "#20000000",
                    BackgroundImagePath = png,
                    BackgroundImageOpacity = 1.0,
                    BackgroundImageStretch = "Uniform",
                    BorderThickness = 0,
                    CornerRadius = 0,
                };

                var outside = RenderFencePixels(bg, style, desktop, new Rect(0, 0, 40, 40));
                var outsidePx = PixelAt(outside, 40, 20, 20);
                Assert.Equal(0, outsidePx.R);
                Assert.Equal(0x20, outsidePx.A);

                var inside = RenderFencePixels(bg, style, desktop, new Rect(60, 20, 40, 40));
                AssertRed(PixelAt(inside, 40, 20, 20));
            });
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CreateBrush_MissingOrCorruptedImageFallsBackToSolidTint()
    {
        var tempDir = TestTemp.CreateDirectory();
        try
        {
            var broken = Path.Combine(tempDir, "broken.png");
            File.WriteAllText(broken, "corrupted");

            RunOnSta(() =>
            {
                var bg = new DesktopBackground();
                var desktop = new Rect(0, 0, 200, 100);
                var region = new Rect(10, 10, 80, 60);

                var missingBrush = bg.CreateBrush(new FenceStyle
                {
                    Background = "#00112233",
                    BackgroundImagePath = Path.Combine(tempDir, "missing.png"),
                }, desktop, region, 80, 60);
                var missingSolid = Assert.IsType<SolidColorBrush>(missingBrush);
                Assert.Equal(Color.FromArgb(0x10, 0x11, 0x22, 0x33), missingSolid.Color);

                var brokenBrush = bg.CreateBrush(new FenceStyle
                {
                    Background = "#80445566",
                    BackgroundImagePath = broken,
                }, desktop, region, 80, 60);
                var brokenSolid = Assert.IsType<SolidColorBrush>(brokenBrush);
                Assert.Equal(Color.FromArgb(0x80, 0x44, 0x55, 0x66), brokenSolid.Color);
            });
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static byte[] RenderFencePixels(DesktopBackground bg, FenceStyle style, Rect desktop, Rect outerScreenRect)
    {
        int w = (int)outerScreenRect.Width;
        int h = (int)outerScreenRect.Height;
        var bt = new Thickness(style.BorderThickness);
        var inner = DesktopBackground.InnerLocalBounds(w, h, bt);
        var innerScreenRect = inner.IsEmpty
            ? Rect.Empty
            : new Rect(outerScreenRect.X + inner.X, outerScreenRect.Y + inner.Y, inner.Width, inner.Height);

        var border = new Border
        {
            Width = w,
            Height = h,
            BorderThickness = bt,
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(style.CornerRadius),
            Background = bg.CreateBrush(style, desktop, innerScreenRect,
                inner.IsEmpty ? 0 : inner.Width, inner.IsEmpty ? 0 : inner.Height),
        };
        border.Measure(new Size(w, h));
        border.Arrange(new Rect(0, 0, w, h));
        border.UpdateLayout();

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(border);
        var pixels = new byte[w * h * 4];
        rtb.CopyPixels(pixels, w * 4, 0);
        return pixels;
    }

    private static Color PixelAt(byte[] pbgra, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        return Color.FromArgb(pbgra[i + 3], pbgra[i + 2], pbgra[i + 1], pbgra[i]);
    }

    private static void AssertRed(Color c) { Assert.True(c.R > 200 && c.G < 30 && c.B < 30, $"Expected Red, got {c}"); }
    private static void AssertGreen(Color c) { Assert.True(c.G > 200 && c.R < 30 && c.B < 30, $"Expected Green, got {c}"); }
    private static void AssertBlue(Color c) { Assert.True(c.B > 200 && c.R < 30 && c.G < 30, $"Expected Blue, got {c}"); }
    private static void AssertYellow(Color c) { Assert.True(c.R > 200 && c.G > 200 && c.B < 30, $"Expected Yellow, got {c}"); }

    private static void SaveQuadrantPng(string path, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        int halfW = width / 2, halfH = height / 2;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Color c = (x < halfW, y < halfH) switch
                {
                    (true, true) => Colors.Red,
                    (false, true) => Colors.Lime,
                    (true, false) => Colors.Blue,
                    (false, false) => Colors.Yellow,
                };
                int i = (y * width + x) * 4;
                pixels[i] = c.B;
                pixels[i + 1] = c.G;
                pixels[i + 2] = c.R;
                pixels[i + 3] = 255;
            }
        }
        SaveBgraPng(path, width, height, pixels);
    }

    private static void SaveSolidPng(string path, int width, int height, Color c)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = c.B;
            pixels[i + 1] = c.G;
            pixels[i + 2] = c.R;
            pixels[i + 3] = c.A;
        }
        SaveBgraPng(path, width, height, pixels);
    }

    private static void SaveBgraPng(string path, int width, int height, byte[] bgra)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
