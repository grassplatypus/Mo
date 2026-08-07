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

    // Occurrence (profile id → local date + time) already acted on. The ±30s window is
    // checked by a timer with a 60s period, so ordinary drift can put two consecutive
    // ticks inside the same window and fire a schedule twice; a clock or DST change can
    // do the same. Recording what already ran makes the trigger idempotent.
    private readonly Dictionary<string, DateTime> _lastFired = new();

    private void CheckSchedules(object? state)
    {
        // Marshalled to the UI thread as a whole. IProfileService.Profiles is an
        // ObservableCollection mutated on the UI thread, and enumerating it from the
        // timer thread throws "collection was modified" — which the outer catch then
        // swallowed, silently skipping that minute's schedules.
        App.MainWindow?.DispatcherQueue.TryEnqueue(async () =>
        {
            try { await EvaluateSchedulesAsync(); }
            catch (Exception ex) { Helpers.BootLog.WriteError("schedule.evaluate", ex); }
        });
    }

    private async Task EvaluateSchedulesAsync()
    {
        var nowLocal = DateTime.Now;
        var now = TimeOnly.FromDateTime(nowLocal);
        var today = nowLocal.DayOfWeek;

        foreach (var profile in _profileService.Profiles.ToList())
        {
            if (profile.Schedule is not { Enabled: true, Time: not null }) continue;
            if (!profile.Schedule.Days.Contains(today)) continue;

            var schedTime = profile.Schedule.Time.Value;
            // Check if within the current minute window
            var diff = Math.Abs((now.ToTimeSpan() - schedTime.ToTimeSpan()).TotalMinutes);
            if (diff >= 0.5) continue; // within 30 seconds

            var occurrence = nowLocal.Date + schedTime.ToTimeSpan();
            if (_lastFired.TryGetValue(profile.Id, out var previous) && previous == occurrence)
                continue;
            _lastFired[profile.Id] = occurrence;

            // A schedule fires whether or not anyone is at the machine, so an
            // unanswered countdown would revert every scheduled switch and make
            // the feature useless. Confirm only when the hardware no longer
            // matches what the profile expects — that is the case that can
            // strand a user who *is* sitting there.
            bool? confirm = _displayService.CheckCompatibility(profile).IsFullMatch ? false : null;
            await _profileService.ApplyProfileAsync(profile.Id, trigger: ApplyTrigger.Schedule, confirm: confirm);
        }
    }

    public void Dispose() => Stop();
}
