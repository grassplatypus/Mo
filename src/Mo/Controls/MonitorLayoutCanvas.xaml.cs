using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mo.Core.DisplayConfiguration;
using Mo.Models;

namespace Mo.Controls;

public sealed partial class MonitorLayoutCanvas : UserControl
{
    private List<MonitorInfo> _monitors = [];
    private readonly List<MonitorTile> _tiles = [];
    private MonitorTile? _selectedTile;
    private MonitorTile? _draggingTile;
    private Windows.Foundation.Point _dragStart;
    private double _tileStartLeft;
    private double _tileStartTop;

    public MonitorLayoutCanvas()
    {
        InitializeComponent();
    }

    public event EventHandler<MonitorInfo?>? MonitorSelected;
    public bool IsEditable { get; set; }

    public void SetMonitors(List<MonitorInfo> monitors)
    {
        _monitors = monitors;
        RebuildLayout();
    }

    public List<MonitorInfo> GetMonitors() => _monitors;

    private void RebuildLayout()
    {
        LayoutCanvas.Children.Clear();
        _tiles.Clear();
        _selectedTile = null;

        if (_monitors.Count == 0)
        {
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;

        var rects = _monitors.Select(m =>
            new DisplayTopology.MonitorRect(m.PositionX, m.PositionY, m.Width, m.Height)).ToList();

        var bounds = DisplayTopology.ComputeBoundingBox(rects);
        double canvasWidth = LayoutCanvas.ActualWidth > 0 ? LayoutCanvas.ActualWidth : ActualWidth;
        double canvasHeight = LayoutCanvas.ActualHeight > 0 ? LayoutCanvas.ActualHeight : ActualHeight;

        if (canvasWidth <= 0 || canvasHeight <= 0)
            return;

        double scale = DisplayTopology.ComputeScaleFactor(bounds, canvasWidth, canvasHeight, 20);

        for (int i = 0; i < _monitors.Count; i++)
        {
            var monitor = _monitors[i];
            var tile = new MonitorTile
            {
                Monitor = monitor,
                MonitorIndex = i,
                Width = monitor.Width * scale,
                Height = monitor.Height * scale,
            };

            var (x, y) = DisplayTopology.TransformToCanvas(
                monitor.PositionX, monitor.PositionY,
                bounds, scale, canvasWidth, canvasHeight);

            Canvas.SetLeft(tile, x);
            Canvas.SetTop(tile, y);

            tile.PointerPressed += Tile_PointerPressed;
            tile.PointerMoved += Tile_PointerMoved;
            tile.PointerReleased += Tile_PointerReleased;

            LayoutCanvas.Children.Add(tile);
            _tiles.Add(tile);
        }
    }

    private void Tile_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not MonitorTile tile) return;

        // Select
        if (_selectedTile != null)
            _selectedTile.IsSelected = false;

        tile.IsSelected = true;
        _selectedTile = tile;
        MonitorSelected?.Invoke(this, tile.Monitor);

        // Start drag if editable
        if (IsEditable)
        {
            _draggingTile = tile;
            _dragStart = e.GetCurrentPoint(LayoutCanvas).Position;
            _tileStartLeft = Canvas.GetLeft(tile);
            _tileStartTop = Canvas.GetTop(tile);
            tile.CapturePointer(e.Pointer);
        }
    }

    private void Tile_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingTile == null || !ReferenceEquals(sender, _draggingTile)) return;

        var current = e.GetCurrentPoint(LayoutCanvas).Position;
        double dx = current.X - _dragStart.X;
        double dy = current.Y - _dragStart.Y;

        Canvas.SetLeft(_draggingTile, _tileStartLeft + dx);
        Canvas.SetTop(_draggingTile, _tileStartTop + dy);
    }

    private void Tile_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingTile == null) return;

        _draggingTile.ReleasePointerCapture(e.Pointer);

        // Update monitor position from canvas coordinates
        if (IsEditable && _draggingTile.Monitor != null)
        {
            // Reverse transform: canvas coords → desktop coords
            // For simplicity in v1, we'll recalculate relative positions
            // based on the visual layout after drag
        }

        _draggingTile = null;
    }

    private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_monitors.Count > 0)
        {
            RebuildLayout();
        }
    }
}
