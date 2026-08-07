namespace Mo.Services;

public sealed class AutoSwitchService : IAutoSwitchService
{
    private readonly IDisplayService _displayService;
    private readonly IProfileService _profileService;
    private readonly ISettingsService _settingsService;
    private Timer? _pollTimer;
    private string? _lastConfigHash;

    public event EventHandler<string>? ProfileAutoApplied;

    public AutoSwitchService(IDisplayService displayService, IProfileService profileService, ISettingsService settingsService)
    {
        _displayService = displayService;
        _profileService = profileService;
        _settingsService = settingsService;
    }

    public void Start()
    {
        _lastConfigHash = GetConfigHash();
        _pollTimer = new Timer(CheckForChanges, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));
    }

    public void Stop()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    private void CheckForChanges(object? state)
    {
        if (!_settingsService.Settings.AutoSwitchEnabled) return;

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

            foreach (var profile in _profileService.Profiles)
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
                    // Match found - apply on UI thread
                    var target = profile;
                    App.MainWindow?.DispatcherQueue.TryEnqueue(async () =>
                    {
                        // This branch is only reached when every monitor the profile names
                        // is physically present, so the layout is the one the user authored
                        // for exactly this hardware — a countdown on every dock/undock would
                        // be noise. CheckCompatibility still gets the final say, because it
                        // also weighs mode support, not just identity.
                        bool? confirm = _displayService.CheckCompatibility(target).IsFullMatch ? false : null;
                        await _profileService.ApplyProfileAsync(target.Id, trigger: ApplyTrigger.AutoSwitch, confirm: confirm);
                        ProfileAutoApplied?.Invoke(this, target.Id);
                    });
                    break;
                }
            }
        }
        catch
        {
        }
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
