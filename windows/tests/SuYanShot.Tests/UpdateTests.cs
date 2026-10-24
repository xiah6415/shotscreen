using System.Text;
using SuYanShot.Update;
using Xunit;

namespace SuYanShot.Tests;

public sealed class UpdateTests
{
    [Theory]
    [InlineData("0.3.2", "0.3.1", true)]
    [InlineData("v1.10", "1.9", true)]
    [InlineData("1.2", "1.2.0", false)]
    [InlineData("1.2.0-beta", "1.2", false)]
    [InlineData("0.3.1", "0.3.1", false)]
    [InlineData("0.2.9", "0.3", false)]
    public void ComparesVersionsLikeMacOS(string candidate, string installed, bool newer) =>
        Assert.Equal(newer, AppVersion.IsNewer(candidate, installed));

    [Fact]
    public void PicksTheWindowsAssetsAndNeverTheMacZip()
    {
        var json = """
            {"tag_name":"v0.4.0","body":"- 新功能\n","html_url":"https://github.com/xiah6415/shotscreen/releases/tag/v0.4.0",
             "assets":[{"name":"Glean-0.4.0.zip","browser_download_url":"https://x/mac.zip"},
                       {"name":"Glean-0.4.0-win-x64.exe.sha256","browser_download_url":"https://x/win.sha256"},
                       {"name":"Glean-0.4.0-win-x64.exe","browser_download_url":"https://x/win.exe"}]}
            """;
        var release = UpdateChecker.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal("0.4.0", release.Version);
        Assert.Equal("- 新功能", release.Notes);
        Assert.Equal("https://x/win.exe", release.ExeUrl);
        Assert.Equal("https://x/win.sha256", release.ChecksumUrl);
    }

    [Fact]
    public void AMacOnlyReleaseOffersNothingToWindows()
    {
        var json = """{"tag_name":"v0.3.1","html_url":"https://x","assets":[{"name":"Glean-0.3.1.zip","browser_download_url":"https://x/mac.zip"}]}""";
        var release = UpdateChecker.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Null(release.ExeUrl);
        Assert.Null(release.ChecksumUrl);
    }

    [Fact]
    public void RunningVersionComesFromTheVersionFile()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "VERSION");
        Assert.Equal(File.ReadAllText(file).Trim(), AppVersion.Current);
    }
}
