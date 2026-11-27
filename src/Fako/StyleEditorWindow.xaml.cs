using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Fako.Interop;
using Fako.Models;
using WinForms = System.Windows.Forms;

namespace Fako;

/// <summary>
/// フェンスの外観の設定画面。
/// 対象フェンスがあるときは、変更をその場でフェンスにも反映する（キャンセルで元に戻す）。
/// 対象が無いときは「新しいフェンスの既定の外観」を編集する。
/// </summary>
public partial class StyleEditorWindow : Window
{
    private static readonly Dictionary<FenceWindow, StyleEditorWindow> OpenEditors = new();

    private readonly FenceWindow? _fence;
    private readonly FenceStyle _original;
    private FenceStyle _style;
    private bool _loading;
    private bool _committed;

    // 色の RGB 部分（濃さはスライダーで別に持つ）
    private Color _backgroundRgb;
    private Color _titleRgb;
    private Color _titleTextRgb;
    private Color _borderRgb;
    private Color _textRgb;

    /// <summary>フェンスの外観を編集する（同じフェンスの画面が開いていればそれを前面に）。</summary>
    public static void ShowFor(FenceWindow fence)
    {
        if (OpenEditors.TryGetValue(fence, out var existing))
        {
            existing.Activate();
            return;
        }
        var w = new StyleEditorWindow(fence);
        OpenEditors[fence] = w;
        w.Closed += (_, _) => OpenEditors.Remove(fence);
        w.Show();
        w.Activate();
    }

    /// <summary>新しいフェンスの既定の外観を編集する。</summary>
    public static bool EditDefault(Window? owner)
    {
        var w = new StyleEditorWindow(null) { Owner = owner };
        return w.ShowDialog() == true;
    }

    private StyleEditorWindow(FenceWindow? fence)
    {
        _fence = fence;
        _original = (fence?.Host.Style ?? App.Current.Config.DefaultStyle).Clone();
        _style = _original.Clone();
        _loading = true;
        InitializeComponent();

        if (fence != null)
        {
            TargetText.Text = $"フェンス「{fence.Config.Title}」の外観を変更します。";
        }
        else
        {
            Title = "新しいフェンスの既定の外観";
            TargetText.Text = "これから作るフェンスに使われる外観です。";
            MakeDefaultButton.Visibility = Visibility.Collapsed;
            PreviewNote.Text = "既存のフェンスにも使うときは「すべてのフェンスに適用」を押してください。";
        }

        BuildSwatches();
        BuildPreviewItems();
        LoadControls();
        UpdatePreview();
        Closed += OnClosedWindow;
        PreviewRoot.SizeChanged += (_, _) => UpdateBackgroundPreview();
        Loaded += (_, _) => UpdateBackgroundPreview();
    }

    // ------------------------------------------------------------------
    // 画面 ⇔ 設定値
    // ------------------------------------------------------------------

    private static double Pct(byte alpha) => Math.Round(alpha / 2.55);
    private static byte Alpha(Slider s) => (byte)Math.Round(Math.Clamp(s.Value, 0, 100) * 2.55);
    private static Color Rgb(Color c) => Color.FromRgb(c.R, c.G, c.B);

    private void LoadControls()
    {
        _loading = true;
        var s = _style;

        var bg = ColorUtil.Parse(s.Background, ColorUtil.DefaultBackground);
        _backgroundRgb = Rgb(bg);
        BackgroundAlpha.Value = Math.Max(5, Pct(bg.A));
        BackgroundImageAlpha.Value = DesktopBackground.NormalizeOpacity(s.BackgroundImageOpacity) * 100;
        BackgroundImageMode.SelectedIndex = s.BackgroundImageStretch switch { "Uniform" => 1, "Fill" => 2, _ => 0 };
        RefreshBackgroundImageName();

        var title = ColorUtil.Parse(s.TitleBackground, Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        _titleRgb = Rgb(title);
        TitleAlpha.Value = Pct(title.A);
        TitleAuto.IsChecked = string.IsNullOrEmpty(s.TitleBackground);
        _titleTextRgb = Rgb(ColorUtil.Parse(s.TitleForeground, Colors.White));
        TitleSize.Value = s.TitleFontSize;
        TitleBold.IsChecked = s.TitleBold;
        AlignLeft.IsChecked = s.TitleAlignment == TitleAlign.Left;
        AlignCenter.IsChecked = s.TitleAlignment == TitleAlign.Center;
        AlignRight.IsChecked = s.TitleAlignment == TitleAlign.Right;

        var border = ColorUtil.Parse(s.BorderColor, Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        _borderRgb = Rgb(border);
        BorderAlpha.Value = Pct(border.A);
        BorderWidth.Value = s.BorderThickness;
        Corner.Value = s.CornerRadius;

        _textRgb = Rgb(ColorUtil.Parse(s.TextColor, Colors.White));
        TextSize.Value = s.ItemFontSize;
        TextShadow.IsChecked = s.TextShadow;

        UpdateColorButtons();
        _loading = false;
    }

    private void ReadControls()
    {
        var s = _style;
        s.Background = ColorUtil.ToHex(ColorUtil.WithAlpha(_backgroundRgb, Alpha(BackgroundAlpha)));
        s.BackgroundImageOpacity = BackgroundImageAlpha.Value / 100;
        s.BackgroundImageStretch = (BackgroundImageMode.SelectedItem as ComboBoxItem)?.Tag as string ?? "UniformToFill";
        bool auto = TitleAuto.IsChecked == true;
        s.TitleBackground = auto ? null : ColorUtil.ToHex(ColorUtil.WithAlpha(_titleRgb, Alpha(TitleAlpha)));
        s.TitleForeground = ColorUtil.ToHex(_titleTextRgb);
        s.TitleFontSize = TitleSize.Value;
        s.TitleBold = TitleBold.IsChecked == true;
        s.TitleAlignment = AlignLeft.IsChecked == true ? TitleAlign.Left
            : AlignRight.IsChecked == true ? TitleAlign.Right : TitleAlign.Center;
        s.BorderColor = ColorUtil.ToHex(ColorUtil.WithAlpha(_borderRgb, Alpha(BorderAlpha)));
        s.BorderThickness = BorderWidth.Value;
        s.CornerRadius = Corner.Value;
        s.TextColor = ColorUtil.ToHex(_textRgb);
        s.ItemFontSize = TextSize.Value;
        s.TextShadow = TextShadow.IsChecked == true;
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _style is null) return;
        ReadControls();
        if (sender == TitleAuto) UpdateColorButtons();
        UpdatePreview();
    }

    private void UpdateColorButtons()
    {
        SetColorButton(BackgroundColorButton, _backgroundRgb);
        SetColorButton(TitleBackgroundButton, _titleRgb);
        SetColorButton(TitleColorButton, _titleTextRgb);
        SetColorButton(BorderColorButton, _borderRgb);
        SetColorButton(TextColorButton, _textRgb);
        bool auto = TitleAuto.IsChecked == true;
        TitleBackgroundButton.IsEnabled = !auto;
        TitleAlpha.IsEnabled = !auto;
        // Fluent テーマの無効表示は控えめで分かりにくいので、薄くもする
        TitleBackgroundButton.Opacity = auto ? 0.4 : 1;
        TitleAlpha.Opacity = auto ? 0.4 : 1;
    }

    private static void SetColorButton(Button button, Color c)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Rectangle
        {
            Width = 18, Height = 18, RadiusX = 4, RadiusY = 4,
            Fill = new SolidColorBrush(c), Stroke = Brushes.Gray, StrokeThickness = 0.5,
            Margin = new Thickness(0, 0, 8, 0),
        });
        panel.Children.Add(new TextBlock { Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}", VerticalAlignment = VerticalAlignment.Center });
        button.Content = panel;
    }

    // ------------------------------------------------------------------
    // 色の選択
    // ------------------------------------------------------------------

    private void BuildSwatches()
    {
        foreach (var (name, hex) in FenceWindow.ColorPresets)
        {
            var c = Rgb(ColorUtil.Parse(hex, ColorUtil.DefaultBackground));
            var b = new Button { Style = (Style)FindResource("Swatch"), Background = new SolidColorBrush(c), ToolTip = name };
            b.Click += (_, _) =>
            {
                _backgroundRgb = c;
                UpdateColorButtons();
                OnChanged(b, new RoutedEventArgs());
            };
            BackgroundSwatches.Children.Add(b);
        }
    }

    private Color? PickColor(Color initial)
    {
        using var dlg = new WinForms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(initial.R, initial.G, initial.B),
            FullOpen = true,
            AnyColor = true,
        };
        var owner = new Win32Owner(new WindowInteropHelper(this).Handle);
        if (dlg.ShowDialog(owner) != WinForms.DialogResult.OK) return null;
        return Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
    }

    private void Pick(ref Color target)
    {
        if (PickColor(target) is not Color c) return;
        target = c;
        UpdateColorButtons();
        OnChanged(this, new RoutedEventArgs());
    }

    private readonly DesktopBackground _previewBackground = new();

    private void RefreshBackgroundImageName() =>
        BackgroundImageName.Text = _style.BackgroundImagePath ?? "未設定";

    private void OnPickBackgroundImage(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "画像|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|すべてのファイル|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _style.BackgroundImagePath = DesktopBackground.Import(dialog.FileName);
            RefreshBackgroundImageName();
            UpdatePreview();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "画像を読み込めませんでした。\n" + ex.Message, "Fako",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClearBackgroundImage(object sender, RoutedEventArgs e)
    {
        _style.BackgroundImagePath = null;
        RefreshBackgroundImageName();
        UpdatePreview();
    }

    private void OnPickBackground(object sender, RoutedEventArgs e) => Pick(ref _backgroundRgb);
    private void OnPickTitleBackground(object sender, RoutedEventArgs e) => Pick(ref _titleRgb);
    private void OnPickTitleColor(object sender, RoutedEventArgs e) => Pick(ref _titleTextRgb);
    private void OnPickBorder(object sender, RoutedEventArgs e) => Pick(ref _borderRgb);
    private void OnPickTextColor(object sender, RoutedEventArgs e) => Pick(ref _textRgb);

    private sealed class Win32Owner(IntPtr handle) : WinForms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    // ------------------------------------------------------------------
    // プレビュー
    // ------------------------------------------------------------------

    private readonly List<TextBlock> _previewTexts = new();

    private void BuildPreviewItems()
    {
        var samples = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\notepad.exe",
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.System) + @"\cmd.exe",
        };
        foreach (var path in samples)
        {
            var item = ShellIcons.CreateItem(path, 64);
            var panel = new StackPanel { Width = 64, Margin = new Thickness(2) };
            panel.Children.Add(new Image { Source = item.Icon, Width = 32, Height = 32 });
            var text = new TextBlock
            {
                Text = item.Name, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4, 0, 0),
            };
            _previewTexts.Add(text);
            panel.Children.Add(text);
            PreviewItems.Children.Add(panel);
        }
    }

    private void UpdateBackgroundPreview()
    {
        var region = _fence?.DesktopBackgroundRegion();
        var desktop = DesktopBackground.DesktopBounds();
        if (region is null || region.Value.IsEmpty)
            region = new Rect(desktop.X + desktop.Width * 0.15, desktop.Y + desktop.Height * 0.15,
                desktop.Width * 0.35, desktop.Height * 0.25);
        PreviewRoot.Background = _previewBackground.CreateBrush(_style, region.Value,
            PreviewRoot.ActualWidth, PreviewRoot.ActualHeight);
    }

    private void UpdatePreview()
    {
        var s = _style;
        UpdateBackgroundPreview();
        PreviewRoot.BorderBrush = new SolidColorBrush(ColorUtil.Parse(s.BorderColor, Colors.Transparent));
        PreviewRoot.BorderThickness = new Thickness(s.BorderThickness);
        PreviewRoot.CornerRadius = new CornerRadius(s.CornerRadius);
        double inner = Math.Max(0, s.CornerRadius - s.BorderThickness);
        PreviewTitleBar.CornerRadius = new CornerRadius(inner, inner, 0, 0);
        PreviewTitleBar.Height = Math.Round(Math.Max(20, s.TitleFontSize * 2));
        PreviewTitleBar.Background = new SolidColorBrush(
            ColorUtil.Parse(s.TitleBackground, Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));
        PreviewTitle.Foreground = new SolidColorBrush(ColorUtil.Parse(s.TitleForeground, Colors.White));
        PreviewTitle.FontSize = s.TitleFontSize;
        PreviewTitle.FontWeight = s.TitleBold ? FontWeights.SemiBold : FontWeights.Normal;
        PreviewTitle.HorizontalAlignment = s.TitleAlignment switch
        {
            TitleAlign.Left => HorizontalAlignment.Left,
            TitleAlign.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
        var shadow = s.TextShadow ? new DropShadowEffect { ShadowDepth = 1, BlurRadius = 3, Opacity = 0.9, Color = Colors.Black } : null;
        PreviewTitle.Effect = shadow;
        var textBrush = new SolidColorBrush(ColorUtil.Parse(s.TextColor, Colors.White));
        foreach (var t in _previewTexts)
        {
            t.Foreground = textBrush;
            t.FontSize = s.ItemFontSize;
            t.Effect = shadow;
        }

        // 対象フェンスにもその場で反映（保存は OK のとき）
        if (_fence != null)
        {
            _fence.Host.Style = s.Clone();
            _fence.ApplyAppearance();
        }
    }

    // ------------------------------------------------------------------
    // ボタン
    // ------------------------------------------------------------------

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _style = new FenceStyle();
        LoadControls();
        UpdatePreview();
    }

    private void OnMakeDefault(object sender, RoutedEventArgs e)
    {
        App.Current.Config.DefaultStyle = _style.Clone();
        App.Current.SaveSoon();
        MessageBox.Show(this, "この外観を、新しいフェンスの既定にしました。", "Fako", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnApplyAll(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "この外観を、すべてのフェンスに適用しますか？", "Fako",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        App.Current.ApplyStyleToAll(_style);
        if (_fence == null) App.Current.Config.DefaultStyle = _style.Clone();
        Commit();
    }

    private void OnOk(object sender, RoutedEventArgs e) => Commit();

    private void Commit()
    {
        _committed = true;
        if (_fence != null)
        {
            _fence.Host.Style = _style.Clone();
            _fence.ApplyAppearance();
        }
        else
        {
            App.Current.Config.DefaultStyle = _style.Clone();
        }
        App.Current.SaveSoon();
        if (_fence == null) DialogResult = true;
        else Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnClosedWindow(object? sender, EventArgs e)
    {
        if (_committed || _fence == null) return;
        // キャンセル（×ボタン含む）: プレビューしていた外観を元に戻す
        _fence.Host.Style = _original.Clone();
        _fence.ApplyAppearance();
    }
}
