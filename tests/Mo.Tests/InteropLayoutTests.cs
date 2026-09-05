using System.Runtime.InteropServices;
using Mo.Interop.DisplayConfig;

namespace Mo.Tests;

/// <summary>Pins the sizes Windows validates. A P/Invoke struct that is short by a field
/// still compiles and still runs; the API just rejects the call, or reads past the
/// buffer. Both happened in this repo. See .claude/rules/30-display-apis.md.</summary>
public class InteropLayoutTests
{
    /// <summary>DEVMODEW is 220 bytes. Mo's copy stopped at dmDisplayFrequency and was
    /// 188, so any caller passing Marshal.SizeOf as dmSize let the OS read 32 bytes past
    /// the allocation.</summary>
    [Fact]
    public void DevMode_IsTheSizeWindowsExpects()
    {
        Assert.Equal(220, Marshal.SizeOf<NativeDisplayApi.DEVMODE>());
    }

    /// <summary>DISPLAY_DEVICEW is 840 bytes, the value that goes in cb.</summary>
    [Fact]
    public void DisplayDevice_IsTheSizeWindowsExpects()
    {
        Assert.Equal(840, Marshal.SizeOf<NativeDisplayApi.DISPLAY_DEVICE>());
    }

    /// <summary>The CCD mode union is a fixed 64 bytes with the target and source modes
    /// overlaid at offset 16. Getting the overlay wrong silently mixes the two.</summary>
    [Fact]
    public void DisplayConfigModeInfo_IsTheSizeWindowsExpects()
    {
        Assert.Equal(64, Marshal.SizeOf<DISPLAYCONFIG_MODE_INFO>());
    }

    [Fact]
    public void DisplayConfigPathInfo_IsTheSizeWindowsExpects()
    {
        Assert.Equal(72, Marshal.SizeOf<DISPLAYCONFIG_PATH_INFO>());
    }

    /// <summary>dmFields bits are positional; a typo here writes the wrong field.</summary>
    [Fact]
    public void DevModeFieldBits_MatchWingdi()
    {
        Assert.Equal(0x00000020u, NativeDisplayApi.DM_POSITION);
        Assert.Equal(0x00000080u, NativeDisplayApi.DM_DISPLAYORIENTATION);
        Assert.Equal(0x00040000u, NativeDisplayApi.DM_BITSPERPEL);
        Assert.Equal(0x00080000u, NativeDisplayApi.DM_PELSWIDTH);
        Assert.Equal(0x00100000u, NativeDisplayApi.DM_PELSHEIGHT);
        Assert.Equal(0x00400000u, NativeDisplayApi.DM_DISPLAYFREQUENCY);
    }

    /// <summary>The exact dmFields NVIDIA's own rotation path writes, read out of
    /// nvxdapix.dll. Asserting the number keeps the decode honest: it already caught a
    /// write-up that dropped DM_BITSPERPEL from the set.</summary>
    [Fact]
    public void NvidiaRotationFieldSet_DecodesTo0x005C00A0()
    {
        uint fields = NativeDisplayApi.DM_POSITION | NativeDisplayApi.DM_DISPLAYORIENTATION
            | NativeDisplayApi.DM_BITSPERPEL | NativeDisplayApi.DM_PELSWIDTH
            | NativeDisplayApi.DM_PELSHEIGHT | NativeDisplayApi.DM_DISPLAYFREQUENCY;

        Assert.Equal(0x005C00A0u, fields);
    }

    /// <summary>Enumerating modes is what feeds the editor's resolution and refresh
    /// pickers. A short DEVMODE or a stale dmSize returns a plausible-looking but empty
    /// list, so assert the loop actually yields modes for a display that exists.</summary>
    [Fact]
    public void EnumDisplaySettings_ReturnsModesForTheFirstDisplay()
    {
        var device = new NativeDisplayApi.DISPLAY_DEVICE
        {
            cb = (uint)Marshal.SizeOf<NativeDisplayApi.DISPLAY_DEVICE>(),
        };
        Assert.True(NativeDisplayApi.EnumDisplayDevices(null, 0, ref device, 0));

        var dm = new NativeDisplayApi.DEVMODE
        {
            dmSize = (ushort)Marshal.SizeOf<NativeDisplayApi.DEVMODE>(),
        };

        int count = 0;
        for (int i = 0; NativeDisplayApi.EnumDisplaySettings(device.DeviceName, i, ref dm); i++)
        {
            if (dm.dmBitsPerPel >= 32 && dm.dmPelsWidth > 0 && dm.dmDisplayFrequency > 1)
                count++;
            dm.dmSize = (ushort)Marshal.SizeOf<NativeDisplayApi.DEVMODE>();
        }

        Assert.True(count > 0, $"No 32-bit modes enumerated for {device.DeviceName}.");
    }
}
