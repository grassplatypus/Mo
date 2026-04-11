using System.Text.Json;

namespace Mo.Services;

public sealed class UpdateService : IUpdateService
{
    private const string CurrentVersion = "0.9.0";
    private const string GitHubApiUrl = "https://api.github.com/repos/Mo-app/Mo/releases/latest";

    public async Task<(bool available, string? version, string? url)> CheckForUpdateAsync()
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "Mo-Monitor-Profile-Manager");
            var json = await http.GetStringAsync(GitHubApiUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
            var htmlUrl = root.GetProperty("html_url").GetString();

            if (!string.IsNullOrEmpty(tagName) && string.Compare(tagName, CurrentVersion, StringComparison.Ordinal) > 0)
            {
                return (true, tagName, htmlUrl);
            }
        }
        catch
        {
        }
        return (false, null, null);
    }
}
