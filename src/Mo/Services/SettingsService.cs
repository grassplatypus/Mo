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

    public event EventHandler<string>? SettingChanged;

    public async Task LoadAsync()
    {
        if (_loaded) return;

        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = await File.ReadAllTextAsync(_settingsFilePath);
                Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonHelper.Options) ?? new();
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
        var json = JsonSerializer.Serialize(Settings, JsonHelper.Options);
        await File.WriteAllTextAsync(_settingsFilePath, json);
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
