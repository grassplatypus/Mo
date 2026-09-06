namespace Mo.Tests;

/// <summary>Assets have to be copied next to the exe, not merely packaged. `Content`
/// alone satisfies an MSIX and leaves the unpackaged publish that actually ships without
/// the file, and every consumer of a missing one fails quietly.</summary>
public class BundledAssetTests
{
    /// <summary>The window icon, the title bar's and the About page's. A miss here shows
    /// up as `window.icon.missing` and `appicon.failed — E_NETWORK_ERROR` in boot.log,
    /// which is how it was found.</summary>
    [Theory]
    [InlineData("AppIcon.ico")]
    [InlineData("TrayIcon.ico")]
    public void IconsAreCopiedBesideTheExecutable(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);

        Assert.True(File.Exists(path),
            $"{fileName} is missing from the build output. Assets need CopyToOutputDirectory, not just Content.");
    }

    /// <summary>Everything under Assets travels, so a new one cannot be forgotten.</summary>
    [Fact]
    public void EveryProjectAssetIsCopied()
    {
        var source = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Mo", "Assets"));

        if (!Directory.Exists(source)) return; // Running from somewhere the repo is not.

        var output = Path.Combine(AppContext.BaseDirectory, "Assets");
        var missing = Directory.GetFiles(source)
            .Select(Path.GetFileName)
            .Where(name => !File.Exists(Path.Combine(output, name!)))
            .ToList();

        Assert.True(missing.Count == 0, $"Not copied to the output: {string.Join(", ", missing)}");
    }
}
