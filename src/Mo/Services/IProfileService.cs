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

    /// <summary>Writes the current list order back to disk after a reorder. Does not
    /// touch ModifiedAt — reordering is not an edit to any profile.</summary>
    Task PersistOrderAsync();
    Task DeleteProfileAsync(string profileId);
    Task<DisplayProfile> CaptureCurrentAsync(string name);
    /// <summary>Applies a profile, then asks the user to confirm unless the safety net is
    /// off; <paramref name="trigger"/> says whether to surface the window. confirm: null
    /// honours ConfirmApply, false forces it off for a change already known safe.</summary>
    Task<DisplayApplyResult> ApplyProfileAsync(
        string profileId,
        bool applyColor = true,
        ApplyTrigger trigger = ApplyTrigger.User,
        bool? confirm = null);
    event EventHandler<DisplayProfile>? ProfileApplied;

    /// <summary>Raised once the hardware work is done, before the confirmation countdown
    /// opens. A busy indicator must come down here: leaving it up behind the dialog asks
    /// the user to answer a question while the app still looks busy.</summary>
    event EventHandler? ApplyWorkFinished;
}
