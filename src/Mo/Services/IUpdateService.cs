namespace Mo.Services;

public interface IUpdateService
{
    Task<(bool available, string? version, string? url)> CheckForUpdateAsync();
}
