using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Mo.Services;

// MSIX packaged builds register via Windows' StartupTask (surfaces in Settings →
// Startup Apps). Unpackaged builds fall back to HKCU\...\Run so dev/test runs still
// auto-launch.
public sealed class StartupService : IStartupService
{
    private const string TaskId = "MoStartupTask";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Mo";

    public async Task<bool> IsRegisteredForStartupAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            return task.State == StartupTaskState.Enabled;
        }
        catch
        {
            return IsRegisteredInRegistry();
        }
    }

    public async Task RegisterForStartupAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (task.State == StartupTaskState.Disabled)
                await task.RequestEnableAsync();
            return;
        }
        catch
        {
            // StartupTask unavailable (unpackaged) — fall through to Registry path.
        }

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.SetValue(RunValueName, $"\"{exePath}\"");
        }
        catch { }
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
            // Fall through to Registry cleanup.
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(RunValueName) != null)
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch { }

        await Task.CompletedTask;
    }

    private static bool IsRegisteredInRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) != null;
        }
        catch { return false; }
    }
}
