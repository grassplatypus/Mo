using System.Text.Json;
using Mo.Helpers;
using Mo.Models;

namespace Mo.Services;

public sealed class SettingsService : ISettingsService
{
    private readonly string _settingsFilePath;
    private bool _loaded;

    public SettingsService()
    {
        _settingsFilePath = GetSettingsFilePath();
        var dir = Path.GetDirectoryName(_settingsFilePath);
        if (dir != null)
            Directory.CreateDirectory(dir);
    }

    public AppSettings Settings { get; private set; } = new();

    public void Load()
    {
        if (_loaded) return;

        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                Settings = JsonSerializer.Deserialize(json, MoJsonContext.Default.AppSettings) ?? new();
            }
        }
        catch
        {
            Settings = new AppSettings();
        }

        _loaded = true;
    }

    // ConfigureAwait(false) on every await: this service is called from the UI
    // thread during startup, and a continuation posted back to a blocked
    // dispatcher deadlocks the whole app before any window exists.
    public async Task LoadAsync()
    {
        if (_loaded) return;

        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = await File.ReadAllTextAsync(_settingsFilePath).ConfigureAwait(false);
                Settings = JsonSerializer.Deserialize(json, MoJsonContext.Default.AppSettings) ?? new();
            }
        }
        catch
        {
            Settings = new AppSettings();
        }

        _loaded = true;
    }

    public async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(Settings, MoJsonContext.Default.AppSettings);
        // Write to a temp file then atomically replace, so a crash or power loss
        // mid-write can never leave a truncated settings.json behind — the exact
        // corruption Program.QuarantineCorruptUserData exists to clean up after.
        var tmp = _settingsFilePath + ".tmp";
        await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
        try { File.Move(tmp, _settingsFilePath, overwrite: true); }
        catch { File.Copy(tmp, _settingsFilePath, overwrite: true); try { File.Delete(tmp); } catch { } }
    }

    private static string GetSettingsFilePath()
    {
        try
        {
            var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            return Path.Combine(localFolder, "settings.json");
        }
        catch
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Mo", "settings.json");
        }
    }
}
