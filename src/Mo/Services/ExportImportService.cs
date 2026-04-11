using System.Text.Json;
using Mo.Helpers;
using Mo.Models;

namespace Mo.Services;

public sealed class ExportImportService
{
    private readonly IProfileService _profileService;

    public ExportImportService(IProfileService profileService)
    {
        _profileService = profileService;
    }

    public async Task ExportProfileAsync(DisplayProfile profile, string filePath)
    {
        var json = JsonSerializer.Serialize(profile, JsonHelper.Options);
        await File.WriteAllTextAsync(filePath, json);
    }

    public async Task<DisplayProfile?> ImportProfileAsync(string filePath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var profile = JsonSerializer.Deserialize<DisplayProfile>(json, JsonHelper.Options);
            if (profile != null)
            {
                profile.Id = Guid.NewGuid().ToString("N"); // New ID to avoid conflicts
                profile.CreatedAt = DateTime.UtcNow;
                profile.ModifiedAt = DateTime.UtcNow;
                await _profileService.SaveProfileAsync(profile);
            }
            return profile;
        }
        catch
        {
            return null;
        }
    }
}
