using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mo.Core.DisplayConfiguration;
using Mo.Models;

namespace Mo.Controls;

public sealed partial class MonitorLayoutCanvas : UserControl
{
    private const int SnapToleranceDesktopPx = 30;

    private List<MonitorInfo> _monitors = [];
    private readonly List<MonitorTile> _tiles = [];
    private MonitorTile? _selectedTile;
    private MonitorTile? _draggingTile;
    private Windows.Foundation.Point _dragStart;
    private double _tileStartLeft;
    private double _tileStartTop;

    // Cached layout state, refreshed by RebuildLayout, consumed by drag handlers.
    private DisplayTopology.Bounds _bounds = new(0, 0, 0, 0);
    private double _scale = 1.0;
    private double _canvasW;
    private double _canvasH;

    public MonitorLayoutCanvas()
    {
        InitializeComponent();
        // Opt into keyboard events so Arrow/Shift+Arrow can nudge the selected tile.
        IsTabStop = true;
        KeyDown += MonitorLayoutCanvas_KeyDown;
    }

    public event EventHandler<MonitorInfo?>? MonitorSelected;
    public event EventHandler? MonitorPositionChanged;

    public bool IsEditable { get; set; }

    public void SetMonitors(List<MonitorInfo> monitors)
    {
        _monitors = monitors;
        NormalizeAdjacency();
        RebuildLayout();
    }

    // Snap any disconnected monitors (gap from the rest of the layout) to an adjacent
    // position. No-op when everything is already touching. Runs greedily — each monitor
    // is adjusted against the monitors already placed before it.
    private void NormalizeAdjacency()
    {
        if (_monitors.Count <= 1) return;

        var placed = new List<DisplayTopology.MonitorRect>
        {
            new(_monitors[0].PositionX, _monitors[0].PositionY, _monitors[0].Width, _monitors[0].Height),
        };

        for (int i = 1; i < _monitors.Count; i++)
        {
            var m = _monitors[i];
            var rect = new DisplayTopology.MonitorRect(m.PositionX, m.PositionY, m.Width, m.Height);
            var (nx, ny) = SnapCalculator.EnforceAdjacency(rect, placed);
            if (nx != m.PositionX || ny != m.PositionY)
            {
                m.PositionX = nx;
                m.PositionY = ny;
            }
            placed.Add(new DisplayTopology.MonitorRect(nx, ny, m.Width, m.Height));
        }
    }

    public List<MonitorInfo> GetMonitors() => _monitors;

    private void RebuildLayout()
    {
        LayoutCanvas.Children.Clear();
        GuideCanvas.Children.Clear();
        _tiles.Clear();
        _selectedTile = null;

        if (_monitors.Count == 0)
        {
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;

        if (!RecomputeLayoutMetrics()) return;

        for (int i = 0; i < _monitors.Count; i++)
        {
            var monitor = _monitors[i];
            var tile = new MonitorTile
            {
                Monitor = monitor,
                MonitorIndex = i,
                Width = monitor.Width * _scale,
                Height = monitor.Height * _scale,
            };

            var (x, y) = DisplayTopology.TransformToCanvas(
                monitor.PositionX, monitor.PositionY,
                _bounds, _scale, _canvasW, _canvasH);

            Canvas.SetLeft(tile, x);
            Canvas.SetTop(tile, y);

            tile.PointerPressed += Tile_PointerPressed;
            tile.PointerMoved += Tile_PointerMoved;
            tile.PointerReleased += Tile_PointerReleased;

            LayoutCanvas.Children.Add(tile);
            _tiles.Add(tile);
        }
    }

    // Recomputes bounds + scale from the current _monitors snapshot. Returns false when
    // the canvas has no usable size yet (initial measure pass).
    private bool RecomputeLayoutMetrics()
    {
        var rects = _monitors.Select(m =>
            new DisplayTopology.MonitorRect(m.PositionX, m.PositionY, m.Width, m.Height)).ToList();

        _bounds = DisplayTopology.ComputeBoundingBox(rects);
        _canvasW = LayoutCanvas.ActualWidth > 0 ? LayoutCanvas.ActualWidth : ActualWidth;
        _canvasH = LayoutCanvas.ActualHeight > 0 ? LayoutCanvas.ActualHeight : ActualHeight;
        if (_canvasW <= 0 || _canvasH <= 0) return false;

        _scale = DisplayTopology.ComputeScaleFactor(_bounds, _canvasW, _canvasH, 20);
        return true;
    }

    // Reuses existing tile instances and only updates their position + size on the canvas.
    // Cheaper than RebuildLayout for arrow-key nudges that don't change monitor count.
    private void RepositionTiles()
    {
        if (_tiles.Count != _monitors.Count) { RebuildLayout(); return; }
        if (!RecomputeLayoutMetrics()) return;

        for (int i = 0; i < _tiles.Count; i++)
        {
            var monitor = _monitors[i];
            var tile = _tiles[i];
            tile.Width = monitor.Width * _scale;
            tile.Height = monitor.Height * _scale;
            var (x, y) = DisplayTopology.TransformToCanvas(
                monitor.PositionX, monitor.PositionY, _bounds, _scale, _canvasW, _canvasH);
            Canvas.SetLeft(tile, x);
            Canvas.SetTop(tile, y);
        }
    }

    private void Tile_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not MonitorTile tile) return;

        if (_selectedTile != null)
            _selectedTile.IsSelected = false;

        tile.IsSelected = true;
        _selectedTile = tile;
        MonitorSelected?.Invoke(this, tile.Monitor);

        // Take keyboard focus so Arrow-key nudges work without clicking anywhere else.
        Focus(FocusState.Pointer);

        if (IsEditable)
        {
            _draggingTile = tile;
            _dragStart = e.GetCurrentPoint(LayoutCanvas).Position;
            _tileStartLeft = Canvas.GetLeft(tile);
            _tileStartTop = Canvas.GetTop(tile);
            tile.CapturePointer(e.Pointer);
        }
    }

    // Arrow keys nudge the selected monitor by 1 px, Shift+Arrow by 10 px. Snap and
    // adjacency enforcement run on each step, so the tile "sticks" to neighbors just
    // like with drag.
    private void MonitorLayoutCanvas_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsEditable || _selectedTile?.Monitor is not { } m) return;

        int dx = 0, dy = 0;
        switch (e.Key)
        {
            case VirtualKey.Left:  dx = -1; break;
            case VirtualKey.Right: dx =  1; break;
            case VirtualKey.Up:    dy = -1; break;
            case VirtualKey.Down:  dy =  1; break;
            default: return;
        }

        bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        int step = shift ? 10 : 1;

        int requestedX = m.PositionX + dx * step;
        int requestedY = m.PositionY + dy * step;

        var others = OtherRects(m);
        var dragRect = new DisplayTopology.MonitorRect(requestedX, requestedY, m.Width, m.Height);
        var snap = SnapCalculator.ComputeSnap(dragRect, requestedX, requestedY, others, SnapToleranceDesktopPx);
        var resolved = SnapCalculator.ResolveOverlap(
            new DisplayTopology.MonitorRect(snap.X, snap.Y, m.Width, m.Height), others);
        var adjacent = SnapCalculator.EnforceAdjacency(
            new DisplayTopology.MonitorRect(resolved.X, resolved.Y, m.Width, m.Height), others);

        if (m.PositionX == adjacent.X && m.PositionY == adjacent.Y)
        {
            e.Handled = true;
            return;
        }

        m.PositionX = adjacent.X;
        m.PositionY = adjacent.Y;
        MonitorPositionChanged?.Invoke(this, EventArgs.Empty);

        RepositionTiles();
        DrawGuides(snap.Guides);

        e.Handled = true;
    }

    private void Tile_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingTile == null || !ReferenceEquals(sender, _draggingTile)) return;

        var current = e.GetCurrentPoint(LayoutCanvas).Position;
        double dx = current.X - _dragStart.X;
        double dy = current.Y - _dragStart.Y;

        double newLeft = _tileStartLeft + dx;
        double newTop = _tileStartTop + dy;

        // Compute candidate desktop coords and snap preview.
        if (_draggingTile.Monitor is { } m && _scale > 0)
        {
            var (px, py) = DisplayTopology.TransformFromCanvas(newLeft, newTop, _bounds, _scale, _canvasW, _canvasH);
            var dragRect = new DisplayTopology.MonitorRect(px, py, m.Width, m.Height);
            var others = OtherRects(m);
            var snap = SnapCalculator.ComputeSnap(dragRect, px, py, others, SnapToleranceDesktopPx);

            var (snapCanvasX, snapCanvasY) = DisplayTopology.TransformToCanvas(
                snap.X, snap.Y, _bounds, _scale, _canvasW, _canvasH);
            Canvas.SetLeft(_draggingTile, snapCanvasX);
            Canvas.SetTop(_draggingTile, snapCanvasY);

            DrawGuides(snap.Guides);
            return;
        }

        Canvas.SetLeft(_draggingTile, newLeft);
        Canvas.SetTop(_draggingTile, newTop);
    }

    private void Tile_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingTile == null) return;

        _draggingTile.ReleasePointerCapture(e.Pointer);
        GuideCanvas.Children.Clear();

        if (IsEditable && _draggingTile.Monitor is { } m && _scale > 0)
        {
            double canvasX = Canvas.GetLeft(_draggingTile);
            double canvasY = Canvas.GetTop(_draggingTile);
            var (px, py) = DisplayTopology.TransformFromCanvas(canvasX, canvasY, _bounds, _scale, _canvasW, _canvasH);

            var others = OtherRects(m);
            var candidate = new DisplayTopology.MonitorRect(px, py, m.Width, m.Height);
            var (resolvedX, resolvedY) = SnapCalculator.ResolveOverlap(candidate, others);
            // Enforce adjacency: monitors must share at least one pixel of an edge.
            var adjacent = SnapCalculator.EnforceAdjacency(
                new DisplayTopology.MonitorRect(resolvedX, resolvedY, m.Width, m.Height), others);
            var finalX = adjacent.X;
            var finalY = adjacent.Y;

            if (m.PositionX != finalX || m.PositionY != finalY)
            {
                m.PositionX = finalX;
                m.PositionY = finalY;
                MonitorPositionChanged?.Invoke(this, EventArgs.Empty);
            }

            // Redraw so snapped/resolved position is reflected cleanly.
            RebuildLayout();
            // Preserve selection visually after rebuild.
            ReselectByMonitor(m);
        }

        _draggingTile = null;
    }

    private void ReselectByMonitor(MonitorInfo monitor)
    {
        var tile = _tiles.FirstOrDefault(t => ReferenceEquals(t.Monitor, monitor));
        if (tile != null)
        {
            tile.IsSelected = true;
            _selectedTile = tile;
        }
    }

    private List<DisplayTopology.MonitorRect> OtherRects(MonitorInfo self)
    {
        return _monitors
            .Where(o => !ReferenceEquals(o, self))
            .Select(o => new DisplayTopology.MonitorRect(o.PositionX, o.PositionY, o.Width, o.Height))
            .ToList();
    }

    private void DrawGuides(IReadOnlyList<SnapCalculator.AlignmentLine> guides)
    {
        GuideCanvas.Children.Clear();
        if (_scale <= 0) return;

        var brush = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));

        foreach (var g in guides)
        {
            if (g.IsVertical)
            {
                var (cx, cTop) = DisplayTopology.TransformToCanvas(g.DesktopPos, g.StartPerp, _bounds, _scale, _canvasW, _canvasH);
                var (_, cBottom) = DisplayTopology.TransformToCanvas(g.DesktopPos, g.EndPerp, _bounds, _scale, _canvasW, _canvasH);
                var line = new Line { X1 = cx, X2 = cx, Y1 = cTop, Y2 = cBottom, Stroke = brush, StrokeThickness = 1, StrokeDashArray = [4, 3] };
                GuideCanvas.Children.Add(line);
            }
            else
            {
                var (cLeft, cy) = DisplayTopology.TransformToCanvas(g.StartPerp, g.DesktopPos, _bounds, _scale, _canvasW, _canvasH);
                var (cRight, _) = DisplayTopology.TransformToCanvas(g.EndPerp, g.DesktopPos, _bounds, _scale, _canvasW, _canvasH);
                var line = new Line { X1 = cLeft, X2 = cRight, Y1 = cy, Y2 = cy, Stroke = brush, StrokeThickness = 1, StrokeDashArray = [4, 3] };
                GuideCanvas.Children.Add(line);
            }
        }
    }

    private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_monitors.Count > 0)
        {
            RebuildLayout();
        }
    }
}
