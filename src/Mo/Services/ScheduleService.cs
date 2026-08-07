namespace Mo.Services;

public sealed class ScheduleService : IScheduleService
{
    private readonly IProfileService _profileService;
    private readonly IDisplayService _displayService;
    private Timer? _checkTimer;

    public ScheduleService(IProfileService profileService, IDisplayService displayService)
    {
        _profileService = profileService;
        _displayService = displayService;
    }

    public void Start()
    {
        _checkTimer = new Timer(CheckSchedules, null, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));
    }

    public void Stop()
    {
        _checkTimer?.Dispose();
        _checkTimer = null;
    }

    public void Reconfigure()
    {
        // Timer already checks every minute, no special reconfiguration needed
    }

    private void CheckSchedules(object? state)
    {
        try
        {
            var now = TimeOnly.FromDateTime(DateTime.Now);
            var today = DateTime.Now.DayOfWeek;

            foreach (var profile in _profileService.Profiles)
            {
                if (profile.Schedule is not { Enabled: true, Time: not null }) continue;
                if (!profile.Schedule.Days.Contains(today)) continue;

                var schedTime = profile.Schedule.Time.Value;
                // Check if within the current minute window
                var diff = Math.Abs((now.ToTimeSpan() - schedTime.ToTimeSpan()).TotalMinutes);
                if (diff < 0.5) // within 30 seconds
                {
                    var target = profile;
                    App.MainWindow?.DispatcherQueue.TryEnqueue(async () =>
                    {
                        // A schedule fires whether or not anyone is at the machine, so an
                        // unanswered countdown would revert every scheduled switch and make
                        // the feature useless. Confirm only when the hardware no longer
                        // matches what the profile expects — that is the case that can
                        // strand a user who *is* sitting there.
                        bool? confirm = _displayService.CheckCompatibility(target).IsFullMatch ? false : null;
                        await _profileService.ApplyProfileAsync(target.Id, trigger: ApplyTrigger.Schedule, confirm: confirm);
                    });
                }
            }
        }
        catch
        {
        }
    }

    public void Dispose() => Stop();
}
