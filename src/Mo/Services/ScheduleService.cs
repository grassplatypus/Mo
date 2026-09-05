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

    // Occurrences already acted on. A 60s timer checking a ±30s window can put two
    // consecutive ticks inside the same window — as can a clock or DST change — so
    // recording what ran is what makes the trigger idempotent.
    private readonly Dictionary<string, DateTime> _lastFired = new();

    private void CheckSchedules(object? state)
    {
        // Marshalled to the UI thread as a whole: Profiles is an ObservableCollection
        // mutated there, so enumerating from the timer thread throws "collection was
        // modified" — swallowed by the outer catch, skipping that minute's schedules.
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

            // A schedule fires with nobody at the machine, so an unanswered countdown
            // would revert every scheduled switch. Confirm only on a partial match —
            // the case that can strand a user who *is* sitting there.
            bool? confirm = _displayService.CheckCompatibility(profile).IsFullMatch ? false : null;
            await _profileService.ApplyProfileAsync(profile.Id, trigger: ApplyTrigger.Schedule, confirm: confirm);
        }
    }

    public void Dispose() => Stop();
}
