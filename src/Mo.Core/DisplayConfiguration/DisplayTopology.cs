namespace Mo.Core.DisplayConfiguration;

public static class DisplayTopology
{
    public sealed record Bounds(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    public sealed record MonitorRect(int X, int Y, int Width, int Height);

    public static Bounds ComputeBoundingBox(IReadOnlyList<MonitorRect> monitors)
    {
        if (monitors.Count == 0)
            return new Bounds(0, 0, 0, 0);

        int left = int.MaxValue, top = int.MaxValue;
        int right = int.MinValue, bottom = int.MinValue;

        foreach (var m in monitors)
        {
            left = Math.Min(left, m.X);
            top = Math.Min(top, m.Y);
            right = Math.Max(right, m.X + m.Width);
            bottom = Math.Max(bottom, m.Y + m.Height);
        }

        return new Bounds(left, top, right, bottom);
    }

    public static double ComputeScaleFactor(Bounds bounds, double canvasWidth, double canvasHeight, double padding = 20)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return 1.0;

        double availableWidth = canvasWidth - padding * 2;
        double availableHeight = canvasHeight - padding * 2;

        double scaleX = availableWidth / bounds.Width;
        double scaleY = availableHeight / bounds.Height;

        return Math.Min(scaleX, scaleY);
    }

    public static (double X, double Y) TransformToCanvas(
        int monitorX, int monitorY, Bounds bounds, double scale, double canvasWidth, double canvasHeight)
    {
        double totalScaledWidth = bounds.Width * scale;
        double totalScaledHeight = bounds.Height * scale;
        double offsetX = (canvasWidth - totalScaledWidth) / 2;
        double offsetY = (canvasHeight - totalScaledHeight) / 2;

        double x = (monitorX - bounds.Left) * scale + offsetX;
        double y = (monitorY - bounds.Top) * scale + offsetY;

        return (x, y);
    }
}
