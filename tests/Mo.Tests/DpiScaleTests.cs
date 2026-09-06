using Mo.Core.DisplayConfiguration;
using Mo.Services;

namespace Mo.Tests;

/// <summary>`GET_DPI_SCALE` is undocumented, so its struct layout and its relative-step
/// arithmetic are only ever proved by reading a real display back.</summary>
public class DpiScaleTests
{
    [Fact]
    public void EveryActiveMonitorReportsAScaleItAlsoOffers()
    {
        var service = new DisplayService();
        var active = service.GetCurrentConfiguration().Where(m => m.IsEnabled).ToList();

        Assert.NotEmpty(active);

        foreach (var monitor in active)
        {
            var state = service.GetDpiScale(monitor);

            Assert.Contains(state.Current, DpiScaling.Steps);
            Assert.NotEmpty(state.Available);
            Assert.Contains(state.Current, state.Available);
        }
    }

    /// <summary>The capture path has to agree with a direct read, or profiles record a
    /// scale the apply will then try to "correct" on every run.</summary>
    [Fact]
    public void CapturedScaleMatchesADirectRead()
    {
        var service = new DisplayService();

        foreach (var monitor in service.GetCurrentConfiguration().Where(m => m.IsEnabled))
            Assert.Equal(service.GetDpiScale(monitor).Current, monitor.DpiScale);
    }

    /// <summary>Setting a monitor to the scale it already has must not touch the driver.
    /// The apply calls this for every monitor on every run.</summary>
    [Fact]
    public void SettingTheCurrentScaleIsANoOpThatSucceeds()
    {
        var service = new DisplayService();
        var monitor = service.GetCurrentConfiguration().First(m => m.IsEnabled);
        var before = service.GetDpiScale(monitor);

        Assert.True(service.SetDpiScale(monitor, before.Current));
        Assert.Equal(before.Current, service.GetDpiScale(monitor).Current);
    }

    [Fact]
    public void AScaleTheDisplayDoesNotOfferIsRefused()
    {
        var service = new DisplayService();
        var monitor = service.GetCurrentConfiguration().First(m => m.IsEnabled);

        Assert.False(service.SetDpiScale(monitor, 137));
    }
}
