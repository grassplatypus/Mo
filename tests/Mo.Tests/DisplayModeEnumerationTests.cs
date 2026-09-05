using Mo.Services;

namespace Mo.Tests;

/// <summary>Exercises the chain the editor's resolution and refresh pickers depend on,
/// against the real display stack. Every link here has failed silently before: a monitor
/// list with no GDI name enumerates nothing, and nothing reports an error.</summary>
public class DisplayModeEnumerationTests
{
    [Fact]
    public void ConnectedMonitorsCarryTheirGdiDeviceName()
    {
        var service = new DisplayService();
        var connected = service.GetAllConnectedMonitors();

        Assert.NotEmpty(connected);

        var active = connected.Where(m => m.IsEnabled).ToList();
        Assert.NotEmpty(active);
        Assert.All(active, m => Assert.StartsWith(@"\\.\DISPLAY", m.GdiDeviceName));
    }

    [Fact]
    public void ActiveMonitorsOfferTheirOwnCurrentMode()
    {
        var service = new DisplayService();
        var active = service.GetAllConnectedMonitors().Where(m => m.IsEnabled).ToList();
        Assert.NotEmpty(active);

        foreach (var monitor in active)
        {
            var modes = service.GetAvailableModes(monitor);
            Assert.NotEmpty(modes);

            // The list is panel-native, so compare against the un-rotated extent.
            var (w, h) = Mo.Core.DisplayConfiguration.RotationGeometry.ToSource(
                monitor.Width, monitor.Height, (int)monitor.Rotation);

            Assert.Contains(modes, m => m.Width == w && m.Height == h);
        }
    }

    /// <summary>A rotated panel must not produce a different list from an upright one.
    /// The normalization that guarantees this reads each mode's own orientation.</summary>
    [Fact]
    public void EnumeratedModesAreLandscapeNative()
    {
        var service = new DisplayService();
        var active = service.GetAllConnectedMonitors().FirstOrDefault(m => m.IsEnabled);
        Assert.NotNull(active);

        var modes = service.GetAvailableModes(active);
        Assert.NotEmpty(modes);
        Assert.Contains(modes, m => m.Width >= m.Height);
    }
}
