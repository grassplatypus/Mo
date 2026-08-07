using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Mo.Controls;
using Mo.Models;

namespace Mo.Services;

/// <inheritdoc cref="IApplyGuardService"/>
public sealed class ApplyGuardService : IApplyGuardService
{
    private readonly IDisplayService _displayService;

    // ContentDialog is single-instance per XamlRoot in WinUI; a second ShowAsync
    // while one is open throws. Two applies can overlap easily (hotkey pressed while
    // an auto-switch is mid-flight), so serialize the prompt.
    private static int _promptInFlight;

    public ApplyGuardService(IDisplayService displayService) => _displayService = displayService;

    public DisplaySnapshot Capture()
    {
        var monitors = _displayService.GetCurrentConfiguration();

        var color = new Dictionary<string, MonitorColorSettings>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var colorService = App.Services.GetRequiredService<IMonitorColorService>();
            foreach (var m in monitors)
            {
                if (string.IsNullOrEmpty(m.GdiDeviceName)) continue;
                var captured = colorService.CaptureByDeviceName(m.GdiDeviceName);
                if (captured is { HasValues: true }) color[m.GdiDeviceName] = captured;
            }
        }
        catch
        {
            // A monitor without DDC/CI contributes nothing; topology is what matters.
        }

        return new DisplaySnapshot
        {
            Monitors = monitors,
            Color = color,
            Signature = BuildSignature(monitors),
        };
    }

    public async Task<bool> ConfirmOrRevertAsync(DisplaySnapshot snapshot, ApplyTrigger trigger)
    {
        // Nothing observable changed — do not train the user to dismiss the dialog.
        if (BuildSignature(_displayService.GetCurrentConfiguration()) == snapshot.Signature)
            return true;

        if (Interlocked.CompareExchange(ref _promptInFlight, 1, 0) != 0)
            return true; // Another guard already owns the prompt for this change.

        try
        {
            var window = App.MainWindow;
            if (window == null) return true;

            // Callers arrive from hotkey messages, timers and startup continuations.
            var queue = window.DispatcherQueue;
            if (queue is { HasThreadAccess: false })
            {
                var marshalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!queue.TryEnqueue(async () =>
                    {
                        try { marshalled.TrySetResult(await PromptAsync(snapshot, trigger)); }
                        catch (Exception ex) { Helpers.BootLog.WriteError("applyguard.prompt", ex); marshalled.TrySetResult(true); }
                    }))
                {
                    return true; // Dispatcher is shutting down; leave the change in place.
                }
                return await marshalled.Task;
            }

            return await PromptAsync(snapshot, trigger);
        }
        catch (Exception ex)
        {
            // The guard must never be the reason an apply fails. If the prompt itself
            // breaks, keep the configuration the user asked for and log it.
            Helpers.BootLog.WriteError("applyguard.confirm", ex);
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _promptInFlight, 0);
        }
    }

    private async Task<bool> PromptAsync(DisplaySnapshot snapshot, ApplyTrigger trigger)
    {
        var window = App.MainWindow;
        if (window == null) return true;

        // These usually fire with the window hidden; if the change broke the desktop,
        // this dialog is the only thing left to reach.
        if (trigger != ApplyTrigger.User)
            window.ShowAndActivate();

        var xamlRoot = window.Content?.XamlRoot;
        if (xamlRoot == null) return true;

        int seconds = 15;
        try { seconds = App.Services.GetRequiredService<ISettingsService>().Settings.ApplyConfirmSeconds; }
        catch { }

        var dialog = new ApplyConfirmationDialog(seconds) { XamlRoot = xamlRoot };
        bool kept = await dialog.ShowAndWaitAsync();

        if (!kept) Restore(snapshot);
        return kept;
    }

    public bool Restore(DisplaySnapshot snapshot)
    {
        bool ok = false;
        try
        {
            // Reuse the normal apply path for the same fallbacks and cursor fix.
            var revertProfile = new DisplayProfile
            {
                Name = "__revert__",
                Monitors = snapshot.Monitors,
            };
            var result = _displayService.ApplyProfile(revertProfile);
            ok = result is DisplayApplyResult.Success or DisplayApplyResult.PartialMatch;
        }
        catch (Exception ex) { Helpers.BootLog.WriteError("applyguard.restore.display", ex); }

        try
        {
            if (snapshot.Color.Count > 0)
            {
                var colorService = App.Services.GetRequiredService<IMonitorColorService>();
                foreach (var (deviceName, settings) in snapshot.Color)
                    colorService.ApplyToMonitorByDeviceName(deviceName, settings);
            }
        }
        catch (Exception ex) { Helpers.BootLog.WriteError("applyguard.restore.color", ex); }

        return ok;
    }

    /// <summary>
    /// Everything that decides whether the desktop is still usable: which monitors are
    /// on, where, how big, at what orientation and refresh rate. Ordered by DevicePath
    /// so enumeration order can't produce spurious differences.
    /// </summary>
    private static string BuildSignature(List<MonitorInfo> monitors)
    {
        var sb = new StringBuilder();
        foreach (var m in monitors.OrderBy(m => m.DevicePath, StringComparer.Ordinal))
        {
            sb.Append(m.DevicePath).Append('|')
              .Append(m.IsEnabled ? '1' : '0').Append('|')
              .Append(m.PositionX).Append(',').Append(m.PositionY).Append('|')
              .Append(m.Width).Append('x').Append(m.Height).Append('|')
              .Append((int)m.Rotation).Append('|')
              .Append(m.RefreshRateNumerator).Append('/').Append(m.RefreshRateDenominator).Append('|')
              .Append(m.IsPrimary ? '1' : '0').Append(';');
        }
        return sb.ToString();
    }
}
