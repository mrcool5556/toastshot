using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace Toastshot.Services;

public sealed record OcrLanguageOption(string Tag, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>Text recognition with the OCR engine built into Windows (offline, no model needed).</summary>
public static partial class OcrService
{
    public static IReadOnlyList<OcrLanguageOption> InstalledLanguages() =>
        OcrEngine.AvailableRecognizerLanguages
            .Select(l => new OcrLanguageOption(l.LanguageTag, l.DisplayName))
            .ToList();

    public static async Task<string> RecognizeAsync(BitmapSource image, string? languageTag)
    {
        var engine = CreateEngine(languageTag);
        var prepared = Prepare(image);
        var width = prepared.PixelWidth;
        var height = prepared.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        prepared.CopyPixels(pixels, stride, 0);

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            CryptographicBuffer.CreateFromByteArray(pixels), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        return CleanUp(string.Join("\n", result.Lines.Select(l => l.Text)));
    }

    private static OcrEngine CreateEngine(string? languageTag)
    {
        if (!string.IsNullOrWhiteSpace(languageTag))
        {
            var language = new Language(languageTag);
            if (!OcrEngine.IsLanguageSupported(language))
            {
                throw new InvalidOperationException(
                    $"Windows OCR for \"{language.DisplayName}\" isn't installed. Add it in Settings > Time & language > Language & region " +
                    "(add the language, then install its optional \"Optical character recognition\" feature).");
            }

            return OcrEngine.TryCreateFromLanguage(language)
                   ?? throw new InvalidOperationException($"Windows OCR couldn't start for \"{language.DisplayName}\".");
        }

        return OcrEngine.TryCreateFromUserProfileLanguages()
               ?? throw new InvalidOperationException(
                   "No Windows OCR language is installed. Add one in Settings > Time & language > Language & region.");
    }

    /// <summary>
    /// Converts to BGRA and resizes: small screenshots are enlarged (Windows OCR misses small UI text),
    /// and anything over the engine limit is shrunk.
    /// </summary>
    private static BitmapSource Prepare(BitmapSource image)
    {
        var max = (double)OcrEngine.MaxImageDimension;
        var longest = Math.Max(image.PixelWidth, image.PixelHeight);
        double scale = 1;
        if (longest > max)
        {
            scale = max / longest;
        }
        else if (longest * 2 <= max && (long)image.PixelWidth * image.PixelHeight <= 2_500_000)
        {
            scale = 2;
        }

        BitmapSource source = image;
        if (scale != 1)
        {
            source = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        }

        if (source.Format != PixelFormats.Bgra32)
        {
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        }

        return source;
    }

    /// <summary>
    /// Windows OCR separates every Chinese/Japanese character with a space; remove those
    /// (Korean keeps its spaces because it uses them between words).
    /// </summary>
    public static string CleanUp(string text) => CjkGap().Replace(text, "").Trim();

    [GeneratedRegex(@"(?<=[　-ヿ㐀-䶿一-鿿豈-﫿＀-￯]) +(?=[　-ヿ㐀-䶿一-鿿豈-﫿＀-￯])")]
    private static partial Regex CjkGap();
}
