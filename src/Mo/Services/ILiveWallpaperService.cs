using Mo.Models;

namespace Mo.Services;

public interface ILiveWallpaperService
{
    LiveWallpaperProvider DetectProvider();
    LiveWallpaperConfig? CaptureCurrentConfig();
    void ApplyConfig(LiveWallpaperConfig config);
    bool IsProviderRunning(LiveWallpaperProvider provider);
}
