using System.Text.Json;
using Mo.Helpers;
using Mo.Models;
using Mo.Services;

namespace Mo.Tests;

public class ExportImportServiceTests
{
    // Minimal stand-in: import only needs somewhere to put the profile.
    private sealed class FakeProfileService : IProfileService
    {
        public System.Collections.ObjectModel.ObservableCollection<DisplayProfile> Profiles { get; } = [];
        public bool ThrowOnSave { get; set; }

        public event EventHandler<DisplayProfile>? ProfileApplied;

        public Task LoadAllAsync() => Task.CompletedTask;

        public Task SaveProfileAsync(DisplayProfile profile)
        {
            if (ThrowOnSave) throw new IOException("disk full");
            Profiles.Add(profile);
            ProfileApplied?.Invoke(this, profile);
            return Task.CompletedTask;
        }

        public Task PersistOrderAsync() => Task.CompletedTask;
        public Task DeleteProfileAsync(string profileId) => Task.CompletedTask;
        public Task<DisplayProfile> CaptureCurrentAsync(string name) => Task.FromResult(new DisplayProfile { Name = name });
        public Task<DisplayApplyResult> ApplyProfileAsync(string profileId, bool applyColor = true,
            ApplyTrigger trigger = ApplyTrigger.User, bool? confirm = null)
            => Task.FromResult(DisplayApplyResult.Success);
    }

    private static (ExportImportService svc, FakeProfileService store) Build()
    {
        var store = new FakeProfileService();
        return (new ExportImportService(store), store);
    }

    private static string ExportedJson(Action<DisplayProfile>? tweak = null)
    {
        var profile = new DisplayProfile
        {
            Id = "original-id",
            Name = "Work",
            SortOrder = 7,
            AutoSwitch = true,
            Hotkey = new HotkeyBinding { Key = Windows.System.VirtualKey.F5, Ctrl = true },
            Monitors = [new MonitorInfo { FriendlyName = "LG", Width = 3440, Height = 1440 }],
        };
        tweak?.Invoke(profile);
        return JsonSerializer.Serialize(profile, MoJsonContext.Default.DisplayProfile);
    }

    [Fact]
    public async Task ImportsAValidExport()
    {
        var (svc, store) = Build();

        var result = await svc.ImportAsync(ExportedJson());

        Assert.True(result.Succeeded);
        Assert.Equal("Work", result.Profile!.Name);
        Assert.Single(store.Profiles);
    }

    // Identity and ordering belong to this machine, not the exporter's.
    [Fact]
    public async Task ImportTakesANewIdentityAndOrder()
    {
        var (svc, _) = Build();

        var result = await svc.ImportAsync(ExportedJson());

        Assert.NotEqual("original-id", result.Profile!.Id);
        Assert.Equal(0, result.Profile.SortOrder);
    }

    // Two profiles claiming the same shortcut or monitor set would quietly compete.
    [Fact]
    public async Task ImportDoesNotInheritShortcutOrAutoSwitch()
    {
        var (svc, _) = Build();

        var result = await svc.ImportAsync(ExportedJson());

        Assert.Null(result.Profile!.Hotkey);
        Assert.False(result.Profile.AutoSwitch);
    }

    [Fact]
    public async Task MalformedJsonReportsNotValidJson()
    {
        var (svc, store) = Build();

        var result = await svc.ImportAsync("{ this is not json");

        Assert.False(result.Succeeded);
        Assert.Equal(ExportImportService.ImportError.NotValidJson, result.Error);
        Assert.Empty(store.Profiles);
    }

    // Valid JSON that is not a profile would otherwise be saved as a nameless entry.
    [Fact]
    public async Task ValidJsonThatIsNotAProfileIsRejected()
    {
        var (svc, store) = Build();

        var result = await svc.ImportAsync("""{ "unrelated": 1 }""");

        Assert.False(result.Succeeded);
        Assert.Equal(ExportImportService.ImportError.NotAProfile, result.Error);
        Assert.Empty(store.Profiles);
    }

    [Fact]
    public async Task SaveFailureIsReportedRatherThanThrown()
    {
        var (svc, store) = Build();
        store.ThrowOnSave = true;

        var result = await svc.ImportAsync(ExportedJson());

        Assert.False(result.Succeeded);
        Assert.Equal(ExportImportService.ImportError.SaveFailed, result.Error);
    }

    [Fact]
    public async Task ExportRoundTripsThroughImport()
    {
        var (svc, _) = Build();
        var original = new DisplayProfile
        {
            Name = "Gaming",
            Monitors = [new MonitorInfo { FriendlyName = "AOC", Width = 2560, Height = 1440 }],
        };

        var result = await svc.ImportAsync(svc.Serialize(original));

        Assert.True(result.Succeeded);
        Assert.Equal("Gaming", result.Profile!.Name);
        Assert.Equal("AOC", Assert.Single(result.Profile.Monitors).FriendlyName);
    }
}
