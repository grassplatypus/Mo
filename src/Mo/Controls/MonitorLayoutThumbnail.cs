using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mo.Core.DisplayConfiguration;
using Mo.Models;

namespace Mo.Controls;

/// <summary>
/// Read-only miniature of a profile's monitor arrangement.
///
/// Mo's whole subject is where the monitors sit relative to each other, yet the
/// profile list described that with a number ("2 monitors"). A thumbnail lets the
/// user tell two profiles apart at a glance without opening either. Intentionally
/// separate from <see cref="MonitorLayoutCanvas"/>: that one is an editor with drag,
/// snapping and hit-testing, all of which would be dead weight — and a scrolling
/// cost — inside a list item.
/// </summary>
/// <remarks>
/// Derives from <see cref="Grid"/> and builds its own Canvas rather than using a
/// templated <c>Control</c>: the project has no Themes/Generic.xaml, and adding that
/// plumbing for one non-interactive shape would cost more than it explains.
/// </remarks>
public sealed partial class MonitorLayoutThumbnail : Grid
{
    private readonly Canvas _canvas = new();

    public MonitorLayoutThumbnail()
    {
        Children.Add(_canvas);
        SizeChanged += (_, _) => Render();
    }

    public static readonly DependencyProperty MonitorsProperty = DependencyProperty.Register(
        nameof(Monitors), typeof(IReadOnlyList<MonitorInfo>), typeof(MonitorLayoutThumbnail),
        new PropertyMetadata(null, (d, _) => ((MonitorLayoutThumbnail)d).Render()));

    public IReadOnlyList<MonitorInfo>? Monitors
    {
        get => (IReadOnlyList<MonitorInfo>?)GetValue(MonitorsProperty);
        set => SetValue(MonitorsProperty, value);
    }

    /// <summary>Highlights the monitor marked primary. Off for dense lists.</summary>
    public static readonly DependencyProperty ShowPrimaryProperty = DependencyProperty.Register(
        nameof(ShowPrimary), typeof(bool), typeof(MonitorLayoutThumbnail),
        new PropertyMetadata(true, (d, _) => ((MonitorLayoutThumbnail)d).Render()));

    public bool ShowPrimary
    {
        get => (bool)GetValue(ShowPrimaryProperty);
        set => SetValue(ShowPrimaryProperty, value);
    }

    private void Render()
    {
        _canvas.Children.Clear();

        var monitors = Monitors;
        if (monitors == null || monitors.Count == 0) return;

        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Disabled monitors are not part of the desktop, so they must not stretch the
        // bounding box — otherwise a profile that turns one off renders shrunken and
        // off-centre compared with the layout the user will actually get.
        var active = monitors.Where(m => m.IsEnabled && m.Width > 0 && m.Height > 0).ToList();
        if (active.Count == 0) return;

        var rects = active.Select(m => new DisplayTopology.MonitorRect(
            m.PositionX, m.PositionY, m.Width, m.Height)).ToList();

        var bounds = DisplayTopology.ComputeBoundingBox(rects);
        // Padding scales with the control so the same class works at card size and at
        // the larger size the editor header uses.
        double padding = Math.Max(2, Math.Min(w, h) * 0.06);
        double scale = DisplayTopology.ComputeScaleFactor(bounds, w, h, padding);
        if (scale <= 0 || double.IsInfinity(scale)) return;

        var fill = (Brush)Application.Current.Resources["AccentFillColorSecondaryBrush"];
        var primaryFill = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var stroke = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];

        for (int i = 0; i < active.Count; i++)
        {
            var m = active[i];
            var (x, y) = DisplayTopology.TransformToCanvas(
                m.PositionX, m.PositionY, bounds, scale, w, h);

            bool isPrimary = ShowPrimary && m.IsPrimary;
            var rect = new Rectangle
            {
                // A 1px gap keeps abutting monitors readable as separate tiles; the
                // Max() stops that gap from consuming a very small thumbnail entirely.
                Width = Math.Max(2, m.Width * scale - 1),
                Height = Math.Max(2, m.Height * scale - 1),
                RadiusX = 2,
                RadiusY = 2,
                Fill = isPrimary ? primaryFill : fill,
                Stroke = stroke,
                StrokeThickness = 1,
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            _canvas.Children.Add(rect);
        }
    }
}
