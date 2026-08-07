using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Mo.Models;

namespace Mo.Services;

public interface IProfileService
{
    ObservableCollection<DisplayProfile> Profiles { get; }
    Task LoadAllAsync();
    Task SaveProfileAsync(DisplayProfile profile);
    Task DeleteProfileAsync(string profileId);
    Task<DisplayProfile> CaptureCurrentAsync(string name);
    /// <summary>
    /// Applies a profile and, unless the user has turned the safety net off, asks them
    /// to confirm the result — rolling the displays back if they decline or do not
    /// answer. <paramref name="trigger"/> tells the guard whether the window has to be
    /// surfaced first (hotkey / auto-switch / startup applies run with it hidden).
    /// </summary>
    /// <param name="confirm">
    /// null (default) honours the user's ConfirmApply setting. false forces the prompt
    /// off for a change already known to be safe — currently only the startup restore
    /// of a fully-compatible profile the user confirmed the first time round.
    /// </param>
    Task<DisplayApplyResult> ApplyProfileAsync(
        string profileId,
        bool applyColor = true,
        ApplyTrigger trigger = ApplyTrigger.User,
        bool? confirm = null);
    event EventHandler<DisplayProfile>? ProfileApplied;
}
