using System.Collections.Generic;
using Mo.Models;

namespace Mo.Services;

public interface IDisplayService
{
    List<MonitorInfo> GetCurrentConfiguration();
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
