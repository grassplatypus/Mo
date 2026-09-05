using System.Threading.Tasks;

namespace Mo.Services;

public interface IStartupService
{
    Task<bool> IsRegisteredForStartupAsync();
    Task RegisterForStartupAsync();
    Task UnregisterFromStartupAsync();

    /// <summary>Brings an existing logon entry up to date with the current exe path and
    /// startup argument. Cheap, and safe to call on every launch.</summary>
    void RepairRegistryEntry();
}
