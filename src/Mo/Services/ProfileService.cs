using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mo.Helpers;
using Mo.Models;

namespace Mo.Services;

public sealed class ProfileService : IProfileService
{
    private readonly IDisplayService _displayService;
    private readonly string _profilesDir;

    public ProfileService(IDisplayService displayService)
    {
        _displayService = displayService;
        _profilesDir = GetProfilesDirectory();
        Directory.CreateDirectory(_profilesDir);
    }

    public ObservableCollection<DisplayProfile> Profiles { get; } = [];

    public event EventHandler<DisplayProfile>? ProfileApplied;
    public event EventHandler? ApplyWorkFinished;

    public async Task LoadAllAsync()
    {
        Profiles.Clear();

        if (!Directory.Exists(_profilesDir))
            return;

        var loaded = new List<DisplayProfile>();
        foreach (var file in Directory.GetFiles(_profilesDir, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var profile = JsonSerializer.Deserialize(json, MoJsonContext.Default.DisplayProfile);
                if (profile != null)
                {
                    MigrateGeneratedDescription(profile);
                    MigrateUncapturedDpiScale(profile);
                    loaded.Add(profile);
                }
            }
            catch
            {
                // Skip corrupt files
            }
        }

        // CreatedAt breaks ties for profiles saved before SortOrder existed (all 0),
        // giving them oldest-first instead of GUID order.
        foreach (var profile in loaded.OrderBy(p => p.SortOrder).ThenBy(p => p.CreatedAt))
            Profiles.Add(profile);

        NormalizeSortOrder();
    }

    /// <summary>Writes SortOrder to match list positions. Does not touch ModifiedAt —
    /// bumping it would relabel every card "updated just now".</summary>
    public async Task PersistOrderAsync()
    {
        foreach (var profile in NormalizeSortOrder())
            await SaveProfileAsync(profile, touchModified: false);
    }

    /// <summary>Assigns 0..n-1 in list order. Returns the profiles that changed.</summary>
    private List<DisplayProfile> NormalizeSortOrder()
    {
        var changed = new List<DisplayProfile>();
        for (int i = 0; i < Profiles.Count; i++)
        {
            if (Profiles[i].SortOrder == i) continue;
            Profiles[i].SortOrder = i;
            changed.Add(Profiles[i]);
        }
        return changed;
    }

    /// <summary>Refuses to write a profile whose serialized form has lost data — a member
    /// invisible to the source-generated MoJsonContext would silently overwrite a good
    /// file with a blank one. See .claude/rules/10-code-style.md.</summary>
    private static void EnsureRoundTrips(DisplayProfile profile, string json)
    {
        var reloaded = JsonSerializer.Deserialize(json, MoJsonContext.Default.DisplayProfile);

        bool intact = reloaded != null
            && reloaded.Id == profile.Id
            && reloaded.Name == profile.Name
            && reloaded.Monitors.Count == profile.Monitors.Count;

        if (intact) return;

        var message =
            $"Refusing to save profile '{profile.Name}' ({profile.Id}): the serialized form " +
            $"does not round-trip. Expected name='{profile.Name}', monitors={profile.Monitors.Count}; " +
            $"got name='{reloaded?.Name}', monitors={reloaded?.Monitors.Count}. " +
            "MoJsonContext and the DisplayProfile members are out of sync.";

        Helpers.BootLog.Write("profile.save.roundtrip-failed", message);
        throw new InvalidOperationException(message);
    }

    /// <summary>Schema the current build writes. Bump alongside a migration below.</summary>
    private const int CurrentSchemaVersion = 1;

    /// <summary>Clears descriptions earlier versions generated. In memory only, since
    /// rewriting files at startup would churn ModifiedAt for every profile.</summary>
    private static void MigrateGeneratedDescription(DisplayProfile profile)
    {
        if (Mo.Core.Formatting.LegacyDescription.IsGenerated(profile.Description))
            profile.Description = string.Empty;
    }

    /// <summary>Drops the scaling of a profile written before Mo captured it. Those files
    /// all say 100 because that was the field's default, never because anyone chose it,
    /// and applying them would drag every scaled monitor down to 100%.</summary>
    private static void MigrateUncapturedDpiScale(DisplayProfile profile)
    {
        if (profile.SchemaVersion >= 1) return;
        foreach (var monitor in profile.Monitors)
            monitor.DpiScale = MonitorInfo.DpiScaleUnset;
    }

    public Task SaveProfileAsync(DisplayProfile profile) => SaveProfileAsync(profile, touchModified: true);

    /// <param name="touchModified">False for bookkeeping writes such as reordering.</param>
    public async Task SaveProfileAsync(DisplayProfile profile, bool touchModified)
    {
        if (touchModified) profile.ModifiedAt = DateTime.UtcNow;
        profile.SchemaVersion = CurrentSchemaVersion;

        // A profile lists the monitors that will be on. Anything it does not name is
        // switched off, so an entry marked off says nothing the omission does not.
        profile.Monitors.RemoveAll(m => !m.IsEnabled);

        // A new profile goes last rather than defaulting to 0 and jumping to front.
        if (profile.SortOrder == 0 && !Profiles.Contains(profile) && Profiles.Count > 0)
            profile.SortOrder = Profiles.Max(p => p.SortOrder) + 1;

        var filePath = Path.Combine(_profilesDir, $"{profile.Id}.json");
        var json = JsonSerializer.Serialize(profile, MoJsonContext.Default.DisplayProfile);

        EnsureRoundTrips(profile, json);

        // Temp file + atomic replace so an interrupted write cannot truncate.
        var tmp = filePath + ".tmp";
        await File.WriteAllTextAsync(tmp, json).ConfigureAwait(true);
        try { File.Move(tmp, filePath, overwrite: true); }
        catch { File.Copy(tmp, filePath, overwrite: true); try { File.Delete(tmp); } catch { } }

        var existing = Profiles.FirstOrDefault(p => p.Id == profile.Id);
        if (existing != null)
        {
            var index = Profiles.IndexOf(existing);
            Profiles[index] = profile;
        }
        else
        {
            Profiles.Add(profile);
        }
    }

    public async Task DeleteProfileAsync(string profileId)
    {
        var filePath = Path.Combine(_profilesDir, $"{profileId}.json");
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        var existing = Profiles.FirstOrDefault(p => p.Id == profileId);
        if (existing != null)
        {
            Profiles.Remove(existing);
        }

        await Task.CompletedTask;
    }

    /// <summary>Reads the whole machine: display config, audio, wallpaper and a DDC/CI
    /// round trip per monitor. All of it hardware, so all of it off the dispatcher.</summary>
    public Task<DisplayProfile> CaptureCurrentAsync(string name) => Task.Run(() => CaptureCurrent(name));

    private DisplayProfile CaptureCurrent(string name)
    {
        // The monitors that are on, which is exactly what the profile stores. A monitor
        // that is off right now is captured by leaving it out.
        var monitors = _displayService.GetCurrentConfiguration();

        var profile = new DisplayProfile
        {
            Name = name,
            // Description is the user's note only; count and time are derived at
            // render time.
            Monitors = monitors,
        };

        // Capture audio
        try
        {
            var audioService = App.Services.GetRequiredService<IAudioService>();
            var (audioId, audioName) = audioService.GetDefaultAudioDevice();
            profile.AudioDeviceId = audioId;
            profile.AudioDeviceName = audioName;
        }
        catch
        {
        }

        // Capture wallpaper
        try
        {
            var wallpaperService = App.Services.GetRequiredService<IWallpaperService>();
            profile.WallpaperPath = wallpaperService.GetCurrentWallpaper();
        }
        catch { }

        // Capture monitor colour settings (brightness, contrast, RGB gain). Addressed by
        // GDI device name, never by list position: the monitor list includes switched-off
        // displays and DDC/CI only enumerates the live ones, so the indices differ.
        try
        {
            var colorService = App.Services.GetRequiredService<IMonitorColorService>();
            foreach (var monitor in profile.Monitors)
            {
                if (!monitor.IsEnabled || string.IsNullOrEmpty(monitor.GdiDeviceName)) continue;
                var captured = colorService.CaptureByDeviceName(monitor.GdiDeviceName);
                if (captured is { HasValues: true })
                    monitor.ColorSettings = captured;
            }
        }
        catch { }

        return profile;
    }

    public async Task<DisplayApplyResult> ApplyProfileAsync(
        string profileId,
        bool applyColor = true,
        ApplyTrigger trigger = ApplyTrigger.User,
        bool? confirm = null)
    {
        var profile = Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile == null)
            return DisplayApplyResult.Failed;

        // Snapshot before anything touches hardware. Wired at this single choke point
        // so no future call site can forget to opt in.
        var guard = TryGetGuard(out var guardService) ? guardService : null;
        var snapshot = guard?.Capture();

        // Off the dispatcher: ApplyProfile talks to drivers, sleeps for seconds, and
        // broadcasts a window message whose timeout is per window. On the UI thread that
        // freezes Mo outright. See .claude/rules/80-ui-responsiveness.md.
        var result = await Task.Run(() => _displayService.ApplyProfile(profile, trigger));

        if (result is DisplayApplyResult.Success or DisplayApplyResult.PartialMatch)
        {
            // Apply audio
            if (!string.IsNullOrEmpty(profile.AudioDeviceId))
            {
                try
                {
                    var audioService = App.Services.GetRequiredService<IAudioService>();
                    audioService.SetDefaultAudioDevice(profile.AudioDeviceId);
                }
                catch
                {
                }
            }

            // Apply wallpaper
            if (!string.IsNullOrEmpty(profile.WallpaperPath))
            {
                try
                {
                    var wallpaperService = App.Services.GetRequiredService<IWallpaperService>();
                    wallpaperService.SetWallpaper(profile.WallpaperPath);
                }
                catch { }
            }

            // Apply monitor color settings (DDC/CI is not persisted by Windows, so this must
            // run on every apply, including the post-reboot auto-restore).
            if (applyColor)
            {
                try
                {
                    // Resolved by identity against the hardware as it is right now. Not by
                    // list position, which a switched-off monitor shifts, and not by the
                    // stored GDI name, which Windows renumbers across reboots.
                    var colorService = App.Services.GetRequiredService<IMonitorColorService>();
                    var live = _displayService.GetCurrentConfiguration();

                    foreach (var monitor in profile.Monitors)
                    {
                        if (monitor.ColorSettings is not { HasValues: true } settings) continue;
                        var target = live.FirstOrDefault(c => monitor.IsSameMonitorAs(c));
                        if (target == null || string.IsNullOrEmpty(target.GdiDeviceName)) continue;
                        colorService.ApplyToMonitorByDeviceName(target.GdiDeviceName, settings);
                    }
                }
                catch { }
            }

            // Apply live wallpaper
            if (profile.LiveWallpaper is { Provider: not Models.LiveWallpaperProvider.None, Entries.Count: > 0 })
            {
                try
                {
                    var liveWpService = App.Services.GetRequiredService<ILiveWallpaperService>();
                    liveWpService.ApplyConfig(profile.LiveWallpaper);
                }
                catch { }
            }

            // Everything that drives hardware has landed; anything still spinning in the
            // UI should stop before the countdown asks a question.
            ApplyWorkFinished?.Invoke(this, EventArgs.Empty);

            // Confirm after colour/audio/wallpaper land — brightness zero is as
            // unusable as a bad topology.
            if (guard != null && snapshot != null && ShouldGuard(confirm))
            {
                if (!await guard.ConfirmOrRevertAsync(snapshot, trigger))
                {
                    // Not recorded as last-applied: a rejected profile must not return
                    // on next boot.
                    return DisplayApplyResult.Reverted;
                }
            }

            // Remember the last-applied profile so App.InitializeAsync can restore it
            // after reboot (reboot-persistence is unreliable on NVIDIA driver paths).
            try
            {
                var settings = App.Services.GetRequiredService<ISettingsService>();
                if (settings.Settings.LastAppliedProfileId != profileId)
                {
                    settings.Settings.LastAppliedProfileId = profileId;
                    await settings.SaveAsync();
                }
            }
            catch { }

            ProfileApplied?.Invoke(this, profile);
        }

        return result;
    }

    private static bool TryGetGuard(out IApplyGuardService guard)
    {
        try
        {
            guard = App.Services.GetRequiredService<IApplyGuardService>();
            return true;
        }
        catch
        {
            guard = null!;
            return false;
        }
    }

    // Every trigger is guarded by default; only an explicit confirm:false or the
    // user's ConfirmApply setting turns it off.
    private static bool ShouldGuard(bool? confirm)
    {
        if (confirm == false) return false;
        try { return App.Services.GetRequiredService<ISettingsService>().Settings.ConfirmApply; }
        catch { return false; }
    }

    private static string GetProfilesDirectory()
    {
        // For MSIX: use ApplicationData. For unpackaged: use LocalAppData.
        try
        {
            var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            return Path.Combine(localFolder, "profiles");
        }
        catch
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Mo", "profiles");
        }
    }
}
