using Mo.Helpers;
using Mo.Services;

namespace Mo.Tests;

/// <summary>The EDID size lookup joins two spellings of the same device id, one from CCD
/// and one from WMI. A join that stops matching returns null rather than failing, so it
/// has to be checked against the machine's real monitors.</summary>
public class MonitorPhysicalSizeTests
{
    [Fact]
    public void EveryActiveMonitorResolvesToAPlausibleSize()
    {
        var sizes = MonitorPhysicalSize.ReadAll();
        if (sizes.Count == 0) return; // No panel reports its size; nothing to assert.

        var active = new DisplayService().GetCurrentConfiguration().Where(m => m.IsEnabled).ToList();
        Assert.NotEmpty(active);

        int matched = 0;
        foreach (var monitor in active)
        {
            var inches = MonitorPhysicalSize.For(monitor.DevicePath, sizes);
            if (inches is null) continue;

            matched++;
            Assert.InRange(inches.Value, 5, 120);
        }

        Assert.True(matched > 0, "No active monitor matched a WMI size entry: the id join is broken.");
    }
}
