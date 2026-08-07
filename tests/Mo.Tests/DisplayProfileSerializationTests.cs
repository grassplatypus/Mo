using System.Text.Json;
using Mo.Helpers;
using Mo.Models;

namespace Mo.Tests;

/// <summary>
/// Guards the JSON contract for saved profiles.
/// </summary>
/// <remarks>
/// MoJsonContext is a System.Text.Json source generator, so the contract is fixed at
/// compile time from what that generator can see on <see cref="DisplayProfile"/>. A
/// member it cannot see silently vanishes from the contract and every save overwrites
/// a good file with a blank one.
///
/// That is not hypothetical: converting DisplayProfile to use CommunityToolkit's
/// <c>[ObservableProperty]</c> did exactly this, because the MVVM generator emits its
/// properties from the same original compilation that STJ already read — STJ saw only
/// the private fields. Profiles loaded empty. ProfileService.EnsureRoundTrips catches
/// it at runtime; these tests catch it at build time.
/// </remarks>
public class DisplayProfileSerializationTests
{
    private static DisplayProfile RoundTrip(DisplayProfile profile)
    {
        var json = JsonSerializer.Serialize(profile, MoJsonContext.Default.DisplayProfile);
        var back = JsonSerializer.Deserialize(json, MoJsonContext.Default.DisplayProfile);
        Assert.NotNull(back);
        return back!;
    }

    private static DisplayProfile Populated() => new()
    {
        Id = "abc123",
        Name = "Work",
        Description = "left monitor rotated",
        SortOrder = 3,
        AutoSwitch = true,
        UnmatchedAction = UnmatchedMonitorAction.Disable,
        AudioDeviceId = "audio-id",
        AudioDeviceName = "Speakers",
        WallpaperPath = @"C:\wall.jpg",
        NightLightEnabled = true,
        Hotkey = new HotkeyBinding { Key = Windows.System.VirtualKey.F5, Ctrl = true, Alt = true },
        Monitors =
        [
            new MonitorInfo
            {
                DevicePath = @"\\?\DISPLAY#GSM5B09#5&1234",
                FriendlyName = "LG HDR WQHD",
                EdidManufacturerId = 0x1E6D,
                EdidProductCodeId = 0x5B09,
                PositionX = -3440, PositionY = 120,
                Width = 3440, Height = 1440,
                Rotation = DisplayRotation.Rotate270,
                RefreshRateNumerator = 60000, RefreshRateDenominator = 1001,
                DpiScale = 125,
                IsPrimary = false,
                IsEnabled = true,
                HdrEnabled = true,
                ColorSettings = new MonitorColorSettings { Brightness = 42, Contrast = 55, RedGain = 51 },
            },
        ],
    };

    [Fact]
    public void EveryPersistedMemberSurvivesARoundTrip()
    {
        var original = Populated();
        var back = RoundTrip(original);

        Assert.Equal(original.Id, back.Id);
        Assert.Equal(original.Name, back.Name);
        Assert.Equal(original.Description, back.Description);
        Assert.Equal(original.SortOrder, back.SortOrder);
        Assert.Equal(original.AutoSwitch, back.AutoSwitch);
        Assert.Equal(original.UnmatchedAction, back.UnmatchedAction);
        Assert.Equal(original.AudioDeviceId, back.AudioDeviceId);
        Assert.Equal(original.AudioDeviceName, back.AudioDeviceName);
        Assert.Equal(original.WallpaperPath, back.WallpaperPath);
        Assert.Equal(original.NightLightEnabled, back.NightLightEnabled);
        Assert.Equal(original.CreatedAt, back.CreatedAt);
        Assert.Equal(original.ModifiedAt, back.ModifiedAt);
    }

    [Fact]
    public void HotkeySurvivesARoundTrip()
    {
        var back = RoundTrip(Populated());

        Assert.NotNull(back.Hotkey);
        Assert.Equal(Windows.System.VirtualKey.F5, back.Hotkey!.Key);
        Assert.True(back.Hotkey.Ctrl);
        Assert.True(back.Hotkey.Alt);
        Assert.False(back.Hotkey.Shift);
    }

    [Fact]
    public void MonitorDetailSurvivesARoundTrip()
    {
        var expected = Populated().Monitors[0];
        var actual = Assert.Single(RoundTrip(Populated()).Monitors);

        Assert.Equal(expected.DevicePath, actual.DevicePath);
        Assert.Equal(expected.FriendlyName, actual.FriendlyName);
        Assert.Equal(expected.EdidManufacturerId, actual.EdidManufacturerId);
        Assert.Equal(expected.EdidProductCodeId, actual.EdidProductCodeId);
        Assert.Equal(expected.PositionX, actual.PositionX);
        Assert.Equal(expected.PositionY, actual.PositionY);
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Rotation, actual.Rotation);
        Assert.Equal(expected.RefreshRateNumerator, actual.RefreshRateNumerator);
        Assert.Equal(expected.RefreshRateDenominator, actual.RefreshRateDenominator);
        Assert.Equal(expected.DpiScale, actual.DpiScale);
        Assert.Equal(expected.IsPrimary, actual.IsPrimary);
        Assert.Equal(expected.IsEnabled, actual.IsEnabled);
        Assert.Equal(expected.HdrEnabled, actual.HdrEnabled);
    }

    [Fact]
    public void ColorSettingsSurviveARoundTrip()
    {
        var color = Assert.Single(RoundTrip(Populated()).Monitors).ColorSettings;

        Assert.NotNull(color);
        Assert.Equal(42, color!.Brightness);
        Assert.Equal(55, color.Contrast);
        Assert.Equal(51, color.RedGain);
        Assert.Null(color.GreenGain);
    }

    // Runtime-only state describes the machine right now, not the profile.
    [Fact]
    public void RuntimeOnlyStateIsNotPersisted()
    {
        var profile = Populated();
        profile.IsActive = true;
        profile.IsAvailable = false;
        profile.Monitors[0].GdiDeviceName = @"\\.\DISPLAY2";

        var json = JsonSerializer.Serialize(profile, MoJsonContext.Default.DisplayProfile);

        Assert.DoesNotContain("isActive", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isAvailable", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gdiDeviceName", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("monitorCount", json, StringComparison.OrdinalIgnoreCase);
    }

    // A hand-edited or third-party file can parse cleanly and still hold nulls, which
    // the corrupt-file quarantine in Program.cs does not catch.
    [Fact]
    public void NullsInJsonDoNotProduceNullProperties()
    {
        const string json = """
            { "id": "x", "name": null, "description": null, "monitors": null }
            """;

        var profile = JsonSerializer.Deserialize(json, MoJsonContext.Default.DisplayProfile);

        Assert.NotNull(profile);
        Assert.Equal(string.Empty, profile!.Name);
        Assert.Equal(string.Empty, profile.Description);
        Assert.NotNull(profile.Monitors);
        Assert.Equal(0, profile.MonitorCount);
    }

    [Fact]
    public void AppSettingsRoundTripsEveryMember()
    {
        var settings = new AppSettings
        {
            LaunchAtStartup = true,
            MinimizeToTrayOnClose = false,
            StartMinimized = true,
            Theme = "Dark",
            HotkeysEnabled = false,
            LastAppliedProfileId = "profile-1",
            AutoSwitchEnabled = false,
            CheckForUpdates = false,
            RotationMethod = RotationMethod.AmdDriver,
            ConfirmApply = false,
            ApplyConfirmSeconds = 30,
            RestoreOnStartup = false,
            RestoreColorOnStartup = false,
            Language = "ko-KR",
            WindowPlacement = new WindowPlacement { X = 10, Y = 20, Width = 900, Height = 700, IsMaximized = true },
        };

        var json = JsonSerializer.Serialize(settings, MoJsonContext.Default.AppSettings);
        var back = JsonSerializer.Deserialize(json, MoJsonContext.Default.AppSettings);

        Assert.NotNull(back);
        Assert.True(back!.LaunchAtStartup);
        Assert.False(back.MinimizeToTrayOnClose);
        Assert.True(back.StartMinimized);
        Assert.Equal("Dark", back.Theme);
        Assert.False(back.HotkeysEnabled);
        Assert.Equal("profile-1", back.LastAppliedProfileId);
        Assert.Equal(RotationMethod.AmdDriver, back.RotationMethod);
        Assert.False(back.ConfirmApply);
        Assert.Equal(30, back.ApplyConfirmSeconds);
        Assert.Equal("ko-KR", back.Language);
        Assert.NotNull(back.WindowPlacement);
        Assert.Equal(900, back.WindowPlacement!.Width);
        Assert.True(back.WindowPlacement.IsMaximized);
    }
}
