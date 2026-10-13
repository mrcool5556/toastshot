using Glance.Services;

namespace Glance.Tests;

public sealed class OverlayTests
{
    [Fact]
    public void Google_Search_Page_Posts_The_Image_To_Lens()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var page = ImageSearch.BuildPage(png);
        Assert.Contains("action=\"https://lens.google.com/v3/upload\"", page);
        Assert.Contains("method=\"post\"", page);
        Assert.Contains("enctype=\"multipart/form-data\"", page);
        Assert.Contains("name=\"encoded_image\"", page);
        Assert.Contains(Convert.ToBase64String(png), page);
        Assert.Contains(".submit()", page);
    }
}
