using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Fako;

public partial class FenceWindow
{
    private readonly DesktopBackground _desktopBackground = new();
    private bool _backgroundQueued;
    private bool _backgroundClosed;

    private void InitializeDesktopBackground()
    {
        Loaded += (_, _) => QueueDesktopBackground();
        LocationChanged += (_, _) => QueueDesktopBackground();
        Root.SizeChanged += (_, _) => QueueDesktopBackground();
        DpiChanged += (_, _) => QueueDesktopBackground();
        IsVisibleChanged += (_, _) => { if (IsVisible) QueueDesktopBackground(); };
        SystemEvents.DisplaySettingsChanged += OnBackgroundDisplayChanged;
        Closed += (_, _) =>
        {
            _backgroundClosed = true;
            SystemEvents.DisplaySettingsChanged -= OnBackgroundDisplayChanged;
        };
    }

    private void OnBackgroundDisplayChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(QueueDesktopBackground));

    private void QueueDesktopBackground()
    {
        if (_backgroundClosed || _backgroundQueued || string.IsNullOrWhiteSpace(Host.Style.BackgroundImagePath))
            return;
        _backgroundQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _backgroundQueued = false;
            if (!_backgroundClosed) UpdateDesktopBackground();
        }));
    }

    internal Rect? DesktopBackgroundRegion()
    {
        if (PresentationSource.FromVisual(Root) is null)
            return null;
        var inner = DesktopBackground.InnerLocalBounds(Root.ActualWidth, Root.ActualHeight, Root.BorderThickness);
        if (inner.IsEmpty)
            return null;
        var first = Root.PointToScreen(inner.TopLeft);
        var last = Root.PointToScreen(inner.BottomRight);
        return new Rect(first, last);
    }

    private void UpdateDesktopBackground()
    {
        var inner = DesktopBackground.InnerLocalBounds(Root.ActualWidth, Root.ActualHeight, Root.BorderThickness);
        Root.Background = _desktopBackground.CreateBrush(Host.Style,
            DesktopBackgroundRegion() ?? Rect.Empty,
            inner.IsEmpty ? 0 : inner.Width,
            inner.IsEmpty ? 0 : inner.Height);
    }
}
