using Mo.Helpers;
using Mo.Models;
using Mo.Services;

namespace Mo.Tests;

/// <summary>Every profile capture records `GetAllConnectedMonitors`, and the next apply
/// hands those fields straight back to `SetDisplayConfig`. So it has to agree with the
/// active-paths read on every field the two share.</summary>
public class ConnectedMonitorFidelityTests
{
    [Fact]
    public void ActiveMonitorsMatchTheActivePathsRead()
    {
        var service = new DisplayService();
        var active = service.GetCurrentConfiguration();
        var all = service.GetAllConnectedMonitors();

        Assert.NotEmpty(active);

        foreach (var expected in active)
        {
            var actual = all.FirstOrDefault(m => m.IsSameMonitorAs(expected));
            Assert.NotNull(actual);

            Assert.True(actual.IsEnabled, $"{expected.FriendlyName} is active but listed as off.");
            Assert.Equal(expected.PositionX, actual.PositionX);
            Assert.Equal(expected.PositionY, actual.PositionY);
            Assert.Equal(expected.Width, actual.Width);
            Assert.Equal(expected.Height, actual.Height);
            Assert.Equal(expected.Rotation, actual.Rotation);
            Assert.Equal(expected.GdiDeviceName, actual.GdiDeviceName);
            Assert.Equal(expected.DpiScale, actual.DpiScale);
        }
    }

    /// <summary>Exactly one monitor sits at the origin, and that is the primary. A capture
    /// that marks none would leave the apply with no primary to set.</summary>
    [Fact]
    public void ExactlyOneMonitorIsPrimary()
    {
        var service = new DisplayService();
        var all = service.GetAllConnectedMonitors();

        Assert.Equal(1, all.Count(m => m.IsEnabled && m.IsPrimary));
    }

    /// <summary>A display Windows is not driving has no scale to read, and inventing one
    /// would drag that panel to 100% the moment a profile switched it on.</summary>
    [Fact]
    public void InactiveMonitorsRecordNoScale()
    {
        var service = new DisplayService();

        foreach (var monitor in service.GetAllConnectedMonitors().Where(m => !m.IsEnabled))
            Assert.Equal(MonitorInfo.DpiScaleUnset, monitor.DpiScale);
    }
}
