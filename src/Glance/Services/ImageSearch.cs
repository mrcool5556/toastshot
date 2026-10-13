using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace Glance.Services;

/// <summary>
/// "Search Google" for a capture: a small local page that uploads the image to Google Lens from the user's own
/// browser (so results open signed in, with their language and consent), then Lens shows similar images.
/// </summary>
public static class ImageSearch
{
    public const string LensUploadUrl = "https://lens.google.com/v3/upload";

    /// <summary>Writes the upload page to Glance's temp folder and opens it in the default browser.</summary>
    public static void SearchGoogle(BitmapSource image)
    {
        var page = TempFiles.NewPath(".html");
        File.WriteAllText(page, BuildPage(ImageExport.EncodePng(image)));
        Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
    }

    /// <summary>A page that posts <paramref name="png"/> to Google Lens as soon as it opens.</summary>
    public static string BuildPage(byte[] png) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>Searching Google…</title>
        <style>body{margin:0;height:100vh;display:grid;place-items:center;background:#141416;color:#a1a1aa;font:14px "Segoe UI",system-ui,sans-serif}</style>
        </head>
        <body>
        <p>Sending the capture to Google Lens…</p>
        <form id="upload" method="post" enctype="multipart/form-data" action="{{LensUploadUrl}}">
        <input type="file" name="encoded_image" id="image" hidden>
        </form>
        <script>
        const bytes = Uint8Array.from(atob("{{Convert.ToBase64String(png)}}"), c => c.charCodeAt(0));
        const files = new DataTransfer();
        files.items.add(new File([bytes], "glance.png", { type: "image/png" }));
        document.getElementById("image").files = files.files;
        document.getElementById("upload").submit();
        </script>
        </body>
        </html>
        """;
}
