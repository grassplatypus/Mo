using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mo.Core.DisplayConfiguration;
using Mo.Models;

namespace Mo.Controls;

// Read-only plan view of a profile's monitor arrangement: the list leads with the
// geometry rather than describing it as "2 monitors". Separate from
// MonitorLayoutCanvas, whose drag/snap/hit-testing would be dead weight in a list item.
public sealed partial class MonitorLayoutThumbnail : Grid
{
    private readonly Canvas _canvas = new();

    public MonitorLayoutThumbnail()
    {
        Children.Add(_canvas);
        SizeChanged += OnSizeChanged;
        ActualThemeChanged += (_, _) => Render();
    }

    private double _renderedWidth;
    private double _renderedHeight;

    /// <summary>
    /// Re-draws only when the size changed enough to look different.
    /// </summary>
    /// <remarks>
    /// A window-edge drag resizes every card on every frame; rebuilding all panels and
    /// labels each time is a lot of allocation for an identical picture.
    /// </remarks>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - _renderedWidth) < 1 &&
            Math.Abs(e.NewSize.Height - _renderedHeight) < 1)
            return;

        Render();
    }

    public static readonly DependencyProperty MonitorsProperty = DependencyProperty.Register(
        nameof(Monitors), typeof(IReadOnlyList<MonitorInfo>), typeof(MonitorLayoutThumbnail),
        new PropertyMetadata(null, (d, _) => ((MonitorLayoutThumbnail)d).Render()));

    public IReadOnlyList<MonitorInfo>? Monitors
    {
        get => (IReadOnlyList<MonitorInfo>?)GetValue(MonitorsProperty);
        set => SetValue(MonitorsProperty, value);
    }

    /// <summary>Draws the number, primary marker and resolution inside each panel.</summary>
    public static readonly DependencyProperty ShowDetailProperty = DependencyProperty.Register(
        nameof(ShowDetail), typeof(bool), typeof(MonitorLayoutThumbnail),
        new PropertyMetadata(true, (d, _) => ((MonitorLayoutThumbnail)d).Render()));

    public bool ShowDetail
    {
        get => (bool)GetValue(ShowDetailProperty);
        set => SetValue(ShowDetailProperty, value);
    }

    // Below these, the label is dropped rather than shrunk to illegibility.
    private const double MinWidthForNumber = 26;
    private const double MinHeightForNumber = 22;
    private const double MinWidthForResolution = 68;
    private const double MinHeightForResolution = 40;

    private void Render()
    {
        _canvas.Children.Clear();

        var monitors = Monitors;
        if (monitors == null || monitors.Count == 0) return;

        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        _renderedWidth = w;
        _renderedHeight = h;

        // Disabled panels are drawn ghosted, not omitted.
        var drawable = monitors.Where(m => m.Width > 0 && m.Height > 0).ToList();
        if (drawable.Count == 0) return;

        var rects = drawable
            .Select(m => new DisplayTopology.MonitorRect(m.PositionX, m.PositionY, m.Width, m.Height))
            .ToList();

        var bounds = DisplayTopology.ComputeBoundingBox(rects);
        // Padding scales with the control so one class serves both sizes.
        double padding = Math.Max(2, Math.Min(w, h) * 0.06);
        double scale = DisplayTopology.ComputeScaleFactor(bounds, w, h, padding);
        if (scale <= 0 || double.IsInfinity(scale) || double.IsNaN(scale)) return;

        for (int i = 0; i < drawable.Count; i++)
            DrawPanel(drawable[i], i + 1, bounds, scale, w, h);
    }

    private void DrawPanel(MonitorInfo monitor, int number, DisplayTopology.Bounds bounds, double scale, double w, double h)
    {
        var (x, y) = DisplayTopology.TransformToCanvas(monitor.PositionX, monitor.PositionY, bounds, scale, w, h);

        // 1px inset separates abutting panels; Max() protects tiny thumbnails.
        double pw = Math.Max(3, monitor.Width * scale - 1);
        double ph = Math.Max(3, monitor.Height * scale - 1);

        bool off = !monitor.IsEnabled;

        var fill = (Brush)Application.Current.Resources[
            monitor.IsPrimary ? "AccentFillColorDefaultBrush" : "AccentFillColorSecondaryBrush"];
        var stroke = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];

        var panel = new Rectangle
        {
            Width = pw,
            Height = ph,
            RadiusX = 2,
            RadiusY = 2,
            Fill = off ? null : fill,
            Stroke = stroke,
            StrokeThickness = 1,
            // Dashed and unfilled reads as "present but off" without a legend.
            StrokeDashArray = off ? [3, 3] : null,
        };
        Canvas.SetLeft(panel, x);
        Canvas.SetTop(panel, y);
        _canvas.Children.Add(panel);

        if (!ShowDetail) return;

        var onAccent = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
        var normal = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        var labelBrush = off ? normal : onAccent;

        if (pw >= MinWidthForNumber && ph >= MinHeightForNumber)
        {
            var label = new TextBlock
            {
                Text = number.ToString(),
                FontSize = Math.Clamp(ph * 0.32, 10, 20),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = labelBrush,
                Opacity = off ? 0.5 : 0.9,
            };
            Place(label, x + 4, y + 2);

            if (monitor.IsPrimary)
            {
                var star = new TextBlock
                {
                    // FavoriteStarFill — Windows anchors the origin and taskbar here.
                    Text = "",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = Math.Clamp(ph * 0.2, 8, 12),
                    Foreground = labelBrush,
                    Opacity = 0.9,
                };
                Place(star, x + pw - 16, y + 3);
            }
        }

        if (pw >= MinWidthForResolution && ph >= MinHeightForResolution)
        {
            var res = new TextBlock
            {
                Text = $"{monitor.Width}×{monitor.Height}",
                FontSize = 10,
                Foreground = labelBrush,
                Opacity = off ? 0.45 : 0.8,
            };
            Place(res, x + 4, y + ph - 16);
        }
    }

    private void Place(UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        _canvas.Children.Add(element);
    }
}
