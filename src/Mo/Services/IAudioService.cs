namespace Mo.Services;

public interface IAudioService
{
    (string? id, string? name) GetDefaultAudioDevice();
    void SetDefaultAudioDevice(string deviceId);
    Task<List<(string id, string name)>> GetAudioDevicesAsync();
}
