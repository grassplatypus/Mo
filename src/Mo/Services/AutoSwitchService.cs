using Microsoft.Win32;
using Mo.Models;

namespace Mo.Services;

/// <summary>
/// Applies a matching profile when the set of connected monitors changes.
/// </summary>
/// <remarks>
/// Driven by <c>SystemEvents.DisplaySettingsChanged</c> rather than a poll. The
/// previous version called QueryDisplayConfig every two seconds for the entire life of
/// the process — a constant background cost for an event that fires a few times a day
/// — and, because the callback could outlast its own two-second period, two runs could
/// overlap and both decide to apply.
///
/// A short debounce still matters: a single dock or undock raises the event several
/// times as Windows settles the topology, and acting on the first one would read a
/// half-built configuration.
/// </remarks>
public sealed class AutoSwitchService : IAutoSwitchService
{
    private readonly IDisplayService _displayService;
    private readonly IProfileService _profileService;
    private readonly ISettingsService _settingsService;
    private Timer? _debounceTimer;
    private string? _lastConfigHash;
    private int _running;
    private bool _started;

    public event EventHandler<string>? ProfileAutoApplied;

    public AutoSwitchService(IDisplayService displayService, IProfileService profileService, ISettingsService settingsService)
    {
        _displayService = displayService;
        _profileService = profileService;
        _settingsService = settingsService;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        _lastConfigHash = GetConfigHash();
        _debounceTimer = new Timer(CheckForChanges, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _debounceTimer?.Dispose();
        _debounceTimer = null;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Restart the one-shot timer on every event, so the burst that accompanies a
        // dock/undock collapses into a single evaluation once things stop moving.
        try { _debounceTimer?.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void CheckForChanges(object? state)
    {
        if (!_settingsService.Settings.AutoSwitchEnabled) return;

        // One evaluation at a time. Applying a profile itself changes the display
        // configuration, which raises the event again — without this guard that can
        // re-enter while the first apply is still in flight.
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

        // Marshalled to the UI thread: IProfileService.Profiles is an
        // ObservableCollection mutated there, and enumerating it from the timer thread
        // throws "collection was modified" — swallowed by the catch below, so an
        // auto-switch would silently not happen.
        var queue = App.MainWindow?.DispatcherQueue;
        if (queue == null || !queue.TryEnqueue(EvaluateOnUiThread))
            Interlocked.Exchange(ref _running, 0);
    }

    private void EvaluateOnUiThread()
    {
        try
        {
            var currentHash = GetConfigHash();
            if (currentHash == _lastConfigHash) return;
            _lastConfigHash = currentHash;

            // Display config changed - find matching auto-switch profile
            var current = _displayService.GetCurrentConfiguration();
            var currentIdentities = current
                .Select(m => (m.DevicePath, m.EdidManufacturerId, m.EdidProductCodeId))
                .ToList();

            foreach (var profile in _profileService.Profiles.ToList())
            {
                if (!profile.AutoSwitch) continue;

                var profileIdentities = profile.Monitors
                    .Select(m => (m.DevicePath, m.EdidManufacturerId, m.EdidProductCodeId))
                    .ToList();

                if (profileIdentities.Count == currentIdentities.Count &&
                    profileIdentities.All(pi => currentIdentities.Any(ci =>
                        (!string.IsNullOrEmpty(pi.DevicePath) && pi.DevicePath == ci.DevicePath) ||
                        (pi.EdidManufacturerId != 0 &&
                         pi.EdidManufacturerId == ci.EdidManufacturerId &&
                         pi.EdidProductCodeId == ci.EdidProductCodeId))))
                {
                    // Already on the UI thread here.
                    var target = profile;
                    _ = ApplyMatchAsync(target);
                    break;
                }
            }
        }
        catch
        {
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);

            // The apply above is queued to the UI thread, so the configuration it
            // produces lands after this method returns. Re-baseline once it settles;
            // otherwise the next event sees a hash that differs from the stale one and
            // applies the same profile all over again.
            try { _debounceTimer?.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task ApplyMatchAsync(DisplayProfile target)
    {
        try
        {
            // Reached only when every monitor the profile names is physically present,
            // so the layout is the one the user authored for exactly this hardware — a
            // countdown on every dock/undock would be noise. CheckCompatibility still
            // gets the final say, because it also weighs mode support, not just identity.
            bool? confirm = _displayService.CheckCompatibility(target).IsFullMatch ? false : null;
            await _profileService.ApplyProfileAsync(target.Id, trigger: ApplyTrigger.AutoSwitch, confirm: confirm);
            ProfileAutoApplied?.Invoke(this, target.Id);
        }
        catch (Exception ex) { Helpers.BootLog.WriteError("autoswitch.apply", ex); }
    }

    private string GetConfigHash()
    {
        try
        {
            var monitors = _displayService.GetCurrentConfiguration();
            return string.Join("|", monitors.Select(m =>
                $"{m.DevicePath}:{m.Width}x{m.Height}:{m.PositionX},{m.PositionY}"));
        }
        catch
        {
            return "";
        }
    }

    public void Dispose() => Stop();
}
