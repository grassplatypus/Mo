using System.Reflection;
using System.Text.Json;

namespace Mo.Services;

public sealed class UpdateService : IUpdateService
{
    // Will be updated after GitHub repo is created
    private const string GitHubOwner = "grassplatypus";
    private const string GitHubRepo = "Mo";
    private static readonly string GitHubApiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";

    public static string CurrentVersion
    {
        get
        {
            // MSIX package version is most reliable
            try
            {
                var pkgVer = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{pkgVer.Major}.{pkgVer.Minor}.{pkgVer.Build}";
            }
            catch { }

            // Fallback to assembly info (strip git hash suffix)
            var infoVer = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(infoVer))
            {
                var plusIdx = infoVer.IndexOf('+');
                return plusIdx > 0 ? infoVer[..plusIdx] : infoVer;
            }

            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    public async Task<(bool available, string? version, string? url)> CheckForUpdateAsync()
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "Mo-Monitor-Profile-Manager");
            http.Timeout = TimeSpan.FromSeconds(10);

            var json = await http.GetStringAsync(GitHubApiUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
            var htmlUrl = root.GetProperty("html_url").GetString();

            if (!string.IsNullOrEmpty(tagName) && CompareSemVer(tagName, CurrentVersion) > 0)
                return (true, tagName, htmlUrl);
        }
        catch { }

        return (false, null, null);
    }

    /// <summary>
    /// Proper semver comparison: 1.10.0 > 1.9.0
    /// Returns positive if a > b, negative if a < b, 0 if equal.
    /// </summary>
    private static int CompareSemVer(string a, string b)
    {
        var pa = a.Split('.', '-')[..3];
        var pb = b.Split('.', '-')[..3];

        for (int i = 0; i < 3; i++)
        {
            int va = i < pa.Length && int.TryParse(pa[i], out var x) ? x : 0;
            int vb = i < pb.Length && int.TryParse(pb[i], out var y) ? y : 0;
            if (va != vb) return va - vb;
        }
        return 0;
    }
}
