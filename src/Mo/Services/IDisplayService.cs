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
    ValidationError
}

public sealed record ProfileCompatibility(
    bool IsFullMatch,
    List<string> MissingMonitors,
    List<string> ExtraMonitors,
    List<string> Warnings);
