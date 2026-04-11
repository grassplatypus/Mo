namespace Mo.Services;

public interface IAutoSwitchService : IDisposable
{
    void Start();
    void Stop();
    event EventHandler<string>? ProfileAutoApplied; // profileId
}
