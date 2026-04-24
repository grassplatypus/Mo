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
    Task<DisplayApplyResult> ApplyProfileAsync(string profileId, bool applyColor = true);
    event EventHandler<DisplayProfile>? ProfileApplied;
}
