using System.Threading.Tasks;

namespace Mo.Services;

public interface IStartupService
{
    Task<bool> IsRegisteredForStartupAsync();
    Task RegisterForStartupAsync();
    Task UnregisterFromStartupAsync();
}
