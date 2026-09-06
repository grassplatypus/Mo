using System.Collections.Generic;
using Mo.Models;

namespace Mo.Services;

public interface IDisplayService
{
    List<MonitorInfo> GetCurrentConfiguration();

    /// <summary>Every monitor with a physical connection, including detected-but-inactive
    /// ones. Inactive items carry <c>IsEnabled = false</c> so callers can show
    /// them differently.</summary>
    List<MonitorInfo> GetAllConnectedMonitors();
    /// <summary>Applies a profile. The trigger decides how intrusive the apply may be:
    /// the cursor-plane reset blanks every panel, so it is reserved for
    /// <see cref="ApplyTrigger.User"/> and never fires during a revert.</summary>
    DisplayApplyResult ApplyProfile(DisplayProfile profile, ApplyTrigger trigger = ApplyTrigger.User);
    ProfileCompatibility CheckCompatibility(DisplayProfile profile);

    /// <summary>Evaluates several profiles against one hardware read. Prefer it over
    /// <see cref="CheckCompatibility"/> in a loop — each call costs two CCD round
    /// trips, and the hardware cannot change between iterations.</summary>
    IReadOnlyList<ProfileCompatibility> CheckCompatibilityAll(IReadOnlyList<DisplayProfile> profiles);

    /// <summary>Modes the panel actually offers, newest-largest first, de-duplicated.
    /// Empty when the monitor is not currently attached, since Windows only enumerates
    /// modes for a display it is driving.</summary>
    IReadOnlyList<DisplayMode> GetAvailableModes(MonitorInfo monitor);

    /// <summary>Scaling percentages the monitor accepts, and the one in effect. Empty
    /// when the display does not report a range.</summary>
    DpiScaleState GetDpiScale(MonitorInfo monitor);

    /// <summary>Sets the monitor's scaling. Returns false when the percentage is not one
    /// this display offers.</summary>
    bool SetDpiScale(MonitorInfo monitor, int percent);

    /// <summary>Returns true if the monitor reports advanced-color (HDR) support.</summary>
    HdrState GetHdrState(MonitorInfo monitor);

    /// <summary>Toggles HDR on/off via Windows CCD. Returns true on success.</summary>
    bool SetHdrEnabled(MonitorInfo monitor, bool enabled);
}

public sealed record HdrState(bool Supported, bool Enabled, bool ForceDisabled);

/// <summary>A mode the panel supports, in panel-native dimensions. Width/Height are the
/// pre-rotation source mode, so a caller showing desktop extents runs them through
/// <see cref="Mo.Core.DisplayConfiguration.RotationGeometry.ToDesktop"/>.</summary>
public sealed record DisplayMode(int Width, int Height, int RefreshHz);

/// <summary>What a monitor's scaling is and what it could be, in percent.</summary>
public sealed record DpiScaleState(int Current, IReadOnlyList<int> Available);

public enum DisplayApplyResult
{
    Success,
    PartialMatch,
    Failed,
    ValidationError,

    /// <summary>Applied, then rolled back on decline or timeout. Never produced by
    /// IDisplayService — only by IProfileService, which owns the confirmation.</summary>
    Reverted,
}

public sealed record ProfileCompatibility(
    bool IsFullMatch,
    List<string> MissingMonitors,
    List<string> ExtraMonitors,
    List<string> Warnings);
