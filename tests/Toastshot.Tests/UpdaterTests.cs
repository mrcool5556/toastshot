using Toastshot.Services;

namespace Toastshot.Tests;

public sealed class UpdaterTests
{
    private const string Releases = """
        [
          {"tag_name":"v0.4.0-beta","draft":false,"prerelease":true,"body":"beta","published_at":"2026-11-01T00:00:00Z","html_url":"https://x/b","assets":[]},
          {"tag_name":"v0.3.0","draft":false,"prerelease":false,"body":"- GIF size menu\n- MP4 export","published_at":"2026-10-20T10:00:00Z","html_url":"https://x/3",
           "assets":[{"name":"Toastshot.zip","browser_download_url":"https://x/3/Toastshot.zip","digest":"sha256:ABC123"}]},
          {"tag_name":"v0.2.1","draft":false,"prerelease":false,"body":"- Clipboard fix","published_at":"2026-10-12T10:00:00Z","html_url":"https://x/21",
           "assets":[{"name":"Toastshot.zip","browser_download_url":"https://x/21/Toastshot.zip"}]},
          {"tag_name":"v0.5.0","draft":true,"prerelease":false,"body":"draft","published_at":null,"html_url":"https://x/d","assets":[]},
          {"tag_name":"v0.2.0","draft":false,"prerelease":false,"body":"old","published_at":"2026-10-01T10:00:00Z","html_url":"https://x/2","assets":[]},
          {"tag_name":"not-a-version","draft":false,"prerelease":false,"body":"","published_at":"2026-10-01T10:00:00Z","html_url":"https://x/n","assets":[]}
        ]
        """;

    [Fact]
    public void Lists_Every_Newer_Release_Newest_First()
    {
        var newer = Updater.Parse(Releases, new Version(0, 2, 0));

        // 0.2.0 is current; drafts, prereleases and odd tags are skipped; both releases since are listed.
        Assert.Equal(["v0.3.0", "v0.2.1"], newer.Select(r => r.Tag));
        Assert.Equal(new Version(0, 3, 0), newer[0].Version);
        Assert.Equal("- GIF size menu\n- MP4 export", newer[0].Notes);
        Assert.Equal("https://x/3/Toastshot.zip", newer[0].ZipUrl);
        Assert.Equal("ABC123", newer[0].ZipSha256);
        Assert.Null(newer[1].ZipSha256); // older releases may not publish a digest
    }

    [Fact]
    public void Up_To_Date_Means_Nothing_Newer() =>
        Assert.Empty(Updater.Parse(Releases, new Version(0, 3, 0)));

    [Fact]
    public void Current_Version_Has_Three_Parts() =>
        Assert.Equal(-1, Updater.Current.Revision);
}
