using Windows.ApplicationModel;

namespace Mo.Services;

public sealed class StartupService : IStartupService
{
    private const string TaskId = "MoStartupTask";

    public async Task<bool> IsRegisteredForStartupAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            return task.State == StartupTaskState.Enabled;
        }
        catch
        {
            return false;
        }
    }

    public async Task RegisterForStartupAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (task.State == StartupTaskState.Disabled)
            {
                await task.RequestEnableAsync();
            }
        }
        catch
        {
            // Startup task not available (unpackaged mode)
        }
    }

    public async Task UnregisterFromStartupAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            task.Disable();
        }
        catch
        {
            // Startup task not available
        }
        await Task.CompletedTask;
    }
}
