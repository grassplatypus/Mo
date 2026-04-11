namespace Mo.Services;

public interface IScheduleService : IDisposable
{
    void Start();
    void Stop();
    void Reconfigure();
}
