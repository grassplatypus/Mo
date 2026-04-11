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
                    App.MainWindow?.DispatcherQueue.TryEnqueue(async () =>
                    {
                        await _profileService.ApplyProfileAsync(profile.Id);
                        ProfileAutoApplied?.Invoke(this, profile.Id);
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
