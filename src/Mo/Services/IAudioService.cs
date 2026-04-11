namespace Mo.Services;

public interface IAudioService
{
    (string? id, string? name) GetDefaultAudioDevice();
    void SetDefaultAudioDevice(string deviceId);
    List<(string id, string name)> GetAudioDevices();
}
