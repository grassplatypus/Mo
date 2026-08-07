using Microsoft.Win32;
using Mo.Models;

namespace Mo.Services;

// Applies a matching profile when the connected monitors change. Event-driven, not
// polled. The debounce matters: one dock/undock raises the event several times as
// Windows settles the topology.
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
        // Restart on every event so a burst collapses into one evaluation.
        try { _debounceTimer?.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void CheckForChanges(object? state)
    {
        if (!_settingsService.Settings.AutoSwitchEnabled) return;

        // Applying a profile re-raises the event; guard against re-entry.
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

        // Profiles is a UI-thread ObservableCollection; enumerating it from the timer
        // thread throws and the catch below would swallow it.
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

            // The apply lands after this returns; re-baseline once it settles or the
            // next event re-applies the same profile.
            try { _debounceTimer?.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task ApplyMatchAsync(DisplayProfile target)
    {
        try
        {
            // Every named monitor is present, so this is the layout the user authored
            // for exactly this hardware; a countdown per dock/undock would be noise.
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
