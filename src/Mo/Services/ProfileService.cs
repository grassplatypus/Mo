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
                    loaded.Add(profile);
                }
            }
            catch
            {
                // Skip corrupt files
            }
        }

        // The user's order decides what Ctrl+Alt+1..9 apply and how next/previous
        // cycles, so it has to be stable and theirs. CreatedAt breaks ties for profiles
        // saved before SortOrder existed (they all deserialize to 0), which at least
        // gives them oldest-first instead of GUID order.
        foreach (var profile in loaded.OrderBy(p => p.SortOrder).ThenBy(p => p.CreatedAt))
            Profiles.Add(profile);

        NormalizeSortOrder();
    }

    /// <summary>
    /// Rewrites SortOrder to match the current list positions, persisting only the
    /// profiles whose value actually changed.
    /// </summary>
    /// <remarks>
    /// Deliberately does not touch ModifiedAt: reordering is not an edit to a profile,
    /// and bumping it would make every card read "updated just now" after a drag.
    /// </remarks>
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

    /// <summary>
    /// Refuses to write a profile whose serialized form has lost data.
    /// </summary>
    /// <remarks>
    /// MoJsonContext is source-generated, so the JSON contract is decided at compile
    /// time from what the generator can see on <see cref="DisplayProfile"/>. A member
    /// that becomes invisible to it — the way a CommunityToolkit
    /// <c>[ObservableProperty]</c> field does, since that property is emitted by a
    /// second generator the first one never sees — silently disappears from the
    /// contract. Every save would then overwrite a good file with a blank one, and
    /// nothing would report it. Checking the round-trip at the moment of writing
    /// turns that class of mistake into a loud failure instead of data loss.
    /// </remarks>
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

    /// <summary>
    /// Clears descriptions this app generated itself in earlier versions.
    /// </summary>
    /// <remarks>
    /// Done in memory on every load rather than as a one-shot rewrite of the files:
    /// re-saving every profile at startup would churn ModifiedAt and make each one
    /// read "updated just now" the first time the user opens the new build.
    /// </remarks>
    private static void MigrateGeneratedDescription(DisplayProfile profile)
    {
        if (Mo.Core.Formatting.LegacyDescription.IsGenerated(profile.Description))
            profile.Description = string.Empty;
    }

    public Task SaveProfileAsync(DisplayProfile profile) => SaveProfileAsync(profile, touchModified: true);

    /// <param name="touchModified">
    /// False for bookkeeping writes that are not edits the user made — reordering, for
    /// one. Bumping ModifiedAt there would relabel every card "updated just now".
    /// </param>
    public async Task SaveProfileAsync(DisplayProfile profile, bool touchModified)
    {
        if (touchModified) profile.ModifiedAt = DateTime.UtcNow;

        // A profile the user just created has no place in the order yet; put it last
        // rather than letting it default to 0 and jump to the front.
        if (profile.SortOrder == 0 && !Profiles.Contains(profile) && Profiles.Count > 0)
            profile.SortOrder = Profiles.Max(p => p.SortOrder) + 1;

        var filePath = Path.Combine(_profilesDir, $"{profile.Id}.json");
        var json = JsonSerializer.Serialize(profile, MoJsonContext.Default.DisplayProfile);

        EnsureRoundTrips(profile, json);

        // Temp file + atomic replace: a crash or power loss mid-write must not be able
        // to leave a half-written profile behind.
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

    public async Task<DisplayProfile> CaptureCurrentAsync(string name)
    {
        var monitors = _displayService.GetCurrentConfiguration();
        var profile = new DisplayProfile
        {
            Name = name,
            // Description is the user's own note and stays empty until they write one.
            // The monitor count and last-modified time are derived at render time —
            // baking them in here produced untranslatable English in the saved JSON
            // that also went stale the moment the profile was edited.
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

        // Capture monitor color settings (brightness, contrast, RGB gain)
        try
        {
            var colorService = App.Services.GetRequiredService<IMonitorColorService>();
            var colorSettings = colorService.CaptureAllMonitors();
            for (int i = 0; i < Math.Min(profile.Monitors.Count, colorSettings.Count); i++)
            {
                if (colorSettings[i].HasValues)
                    profile.Monitors[i].ColorSettings = colorSettings[i];
            }
        }
        catch { }

        await Task.CompletedTask;
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

        // Snapshot BEFORE anything touches the hardware. The guard is wired here, at
        // the single choke point every caller (UI, tray, hotkey, auto-switch, startup)
        // already goes through, so no future call site can forget to opt in.
        var guard = TryGetGuard(out var guardService) ? guardService : null;
        var snapshot = guard?.Capture();

        var result = _displayService.ApplyProfile(profile);

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
                    var colorService = App.Services.GetRequiredService<IMonitorColorService>();
                    var entries = profile.Monitors
                        .Select((m, i) => (index: i, settings: m.ColorSettings))
                        .Where(e => e.settings is { HasValues: true })
                        .Select(e => (e.index, e.settings!))
                        .ToList();
                    if (entries.Count > 0)
                        colorService.ApplyAll(entries);
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

            // Confirm last, after color/audio/wallpaper have landed — a profile that
            // drives DDC/CI brightness to zero is just as unusable as a bad topology,
            // so the user has to be judging the finished result.
            if (guard != null && snapshot != null && ShouldGuard(confirm))
            {
                if (!await guard.ConfirmOrRevertAsync(snapshot, trigger))
                {
                    // Reverted. Deliberately do NOT record LastAppliedProfileId or raise
                    // ProfileApplied: a rejected profile must not come back on next boot.
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

    // Every trigger is guarded by default — a hotkey, an auto-switch on hot-plug and a
    // schedule can all strand the user just as thoroughly as a click on Apply. Only an
    // explicit `confirm: false` from the caller, or the user's own ConfirmApply
    // setting, turns it off.
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
