namespace Mo.Services;

public sealed class ScheduleService : IScheduleService
{
    private readonly IProfileService _profileService;
    private Timer? _checkTimer;

    public ScheduleService(IProfileService profileService)
    {
        _profileService = profileService;
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
                    App.MainWindow?.DispatcherQueue.TryEnqueue(async () =>
                    {
                        await _profileService.ApplyProfileAsync(profile.Id);
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
