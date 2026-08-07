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

    public string Serialize(DisplayProfile profile) =>
        JsonSerializer.Serialize(profile, MoJsonContext.Default.DisplayProfile);

    public enum ImportError
    {
        None,
        NotValidJson,
        NotAProfile,
        SaveFailed,
    }

    public sealed record ImportResult(DisplayProfile? Profile, ImportError Error)
    {
        public bool Succeeded => Profile != null && Error == ImportError.None;
    }

    /// <summary>
    /// Adds a profile from exported JSON, reporting why it failed rather than
    /// returning a bare null.
    /// </summary>
    public async Task<ImportResult> ImportAsync(string json)
    {
        DisplayProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize(json, MoJsonContext.Default.DisplayProfile);
        }
        catch (JsonException)
        {
            return new ImportResult(null, ImportError.NotValidJson);
        }

        // Valid JSON that is not a profile deserializes to an object with nothing in
        // it, which would otherwise be saved as a nameless empty entry.
        if (profile == null || (string.IsNullOrWhiteSpace(profile.Name) && profile.Monitors.Count == 0))
            return new ImportResult(null, ImportError.NotAProfile);

        // Identity and ordering belong to this machine, not the exporter's.
        profile.Id = Guid.NewGuid().ToString("N");
        profile.CreatedAt = DateTime.UtcNow;
        profile.SortOrder = 0;   // SaveProfileAsync places it last.

        // A shortcut belongs to one profile, and auto-switch to one monitor set;
        // inheriting either would have two profiles quietly competing.
        profile.Hotkey = null;
        profile.AutoSwitch = false;

        if (string.IsNullOrWhiteSpace(profile.Name))
            profile.Name = ResourceHelper.GetString("ImportedProfileFallbackName");

        try
        {
            await _profileService.SaveProfileAsync(profile);
        }
        catch (Exception ex)
        {
            BootLog.WriteError("profile.import", ex);
            return new ImportResult(null, ImportError.SaveFailed);
        }

        return new ImportResult(profile, ImportError.None);
    }
}
