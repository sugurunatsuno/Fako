using System.Windows;
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
}
