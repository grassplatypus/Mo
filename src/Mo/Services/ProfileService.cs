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

        foreach (var file in Directory.GetFiles(_profilesDir, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var profile = JsonSerializer.Deserialize<DisplayProfile>(json, JsonHelper.Options);
                if (profile != null)
                    Profiles.Add(profile);
            }
            catch
            {
                // Skip corrupt files
            }
        }
    }

    public async Task SaveProfileAsync(DisplayProfile profile)
    {
        profile.ModifiedAt = DateTime.UtcNow;
        var filePath = Path.Combine(_profilesDir, $"{profile.Id}.json");
        var json = JsonSerializer.Serialize(profile, JsonHelper.Options);
        await File.WriteAllTextAsync(filePath, json);

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
            Description = $"{monitors.Count} monitor(s) — {DateTime.Now:g}",
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

        // Capture live wallpaper
        try
        {
            var liveWpService = App.Services.GetRequiredService<ILiveWallpaperService>();
            profile.LiveWallpaper = liveWpService.CaptureCurrentConfig();
        }
        catch { }

        await Task.CompletedTask;
        return profile;
    }

    public async Task<DisplayApplyResult> ApplyProfileAsync(string profileId)
    {
        var profile = Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile == null)
            return DisplayApplyResult.Failed;

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

            ProfileApplied?.Invoke(this, profile);
        }

        await Task.CompletedTask;
        return result;
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
