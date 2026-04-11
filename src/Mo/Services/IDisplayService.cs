using System.Collections.Generic;
using Mo.Models;

namespace Mo.Services;

public interface IDisplayService
{
    List<MonitorInfo> GetCurrentConfiguration();
    DisplayApplyResult ApplyProfile(DisplayProfile profile);
    ProfileCompatibility CheckCompatibility(DisplayProfile profile);
}

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
