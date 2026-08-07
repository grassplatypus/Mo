using System.Collections.Generic;
using Mo.Models;

namespace Mo.Services;

public interface IDisplayService
{
    List<MonitorInfo> GetCurrentConfiguration();

    /// <summary>
    /// Enumerates every monitor the GPU currently has a physical connection to,
    /// including ones detected but inactive (cable plugged in but Windows has the
    /// path turned off). Each item carries a `IsEnabled` flag set to false for the
    /// inactive ones so callers can render them differently.
    /// </summary>
    List<MonitorInfo> GetAllConnectedMonitors();
    DisplayApplyResult ApplyProfile(DisplayProfile profile);
    ProfileCompatibility CheckCompatibility(DisplayProfile profile);

    /// <summary>
    /// Evaluates several profiles against one hardware read. Prefer this over calling
    /// <see cref="CheckCompatibility"/> in a loop — each call costs two full CCD
    /// round trips, and the hardware cannot change between iterations.
    /// </summary>
    IReadOnlyList<ProfileCompatibility> CheckCompatibilityAll(IReadOnlyList<DisplayProfile> profiles);

    /// <summary>Returns true if the monitor reports advanced-color (HDR) support.</summary>
    HdrState GetHdrState(MonitorInfo monitor);

    /// <summary>Toggles HDR on/off via Windows CCD. Returns true on success.</summary>
    bool SetHdrEnabled(MonitorInfo monitor, bool enabled);
}

public sealed record HdrState(bool Supported, bool Enabled, bool ForceDisabled);

public enum DisplayApplyResult
{
    Success,
    PartialMatch,
    Failed,
    ValidationError,

    /// <summary>
    /// The change was applied but then rolled back, because the user declined the
    /// confirmation prompt or let it time out. Never produced by IDisplayService
    /// itself — only by IProfileService, which owns the confirmation step.
    /// </summary>
    Reverted,
}

public sealed record ProfileCompatibility(
    bool IsFullMatch,
    List<string> MissingMonitors,
    List<string> ExtraMonitors,
    List<string> Warnings);
