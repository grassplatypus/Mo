namespace Mo.Services;

public interface IWallpaperService
{
    string? GetCurrentWallpaper();
    void SetWallpaper(string path);
}
