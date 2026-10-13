using System.Runtime.CompilerServices;

namespace Glance.Services;

/// <summary>The two model jobs Glance runs: translating text and reading text from an image.</summary>
public static class AiTasks
{
    /// <summary>
    /// The instruction is repeated in the user turn: small models (1-4B) otherwise tend to echo the
    /// text back untranslated.
    /// </summary>
    public static (string System, string User) TranslationPrompt(string text, string targetLanguage) => (
        $"You are a professional translator. You translate text into {targetLanguage}. " +
        "Reply with only the translation: no notes, quotes, explanations or romanization. " +
        "Keep the original line breaks. Keep names, code, numbers and URLs as they are.",
        $"Translate this text into {targetLanguage}:\n\n{text}");

    public const string ReadTextSystem =
        "You transcribe text from images. Output only the text that appears in the image, exactly as written, " +
        "in reading order, keeping line breaks. Do not translate, describe, summarise or add anything. " +
        "If there is no text, output nothing.";

    public static IAsyncEnumerable<string> TranslateAsync(OllamaClient client, string model, string text, string targetLanguage, CancellationToken ct)
    {
        var (system, user) = TranslationPrompt(text, targetLanguage);
        return client.ChatAsync(model, system, user, null, ct);
    }

    public static IAsyncEnumerable<string> ReadTextAsync(OllamaClient client, string model, byte[] png, CancellationToken ct) =>
        client.ChatAsync(model, ReadTextSystem, "Transcribe all text in this image.", [png], ct);

    /// <summary>Collects a stream while reporting the text so far.</summary>
    public static async Task<string> CollectAsync(IAsyncEnumerable<string> chunks, Action<string>? onProgress, CancellationToken ct)
    {
        var text = new System.Text.StringBuilder();
        await foreach (var chunk in chunks.WithCancellation(ct).ConfigureAwait(true))
        {
            text.Append(chunk);
            onProgress?.Invoke(text.ToString().TrimStart());
        }

        return text.ToString().Trim();
    }

    /// <summary>Common target languages for the picker (any text is accepted).</summary>
    public static IReadOnlyList<string> CommonLanguages { get; } =
    [
        "English", "Spanish", "French", "German", "Italian", "Portuguese", "Dutch", "Polish", "Russian",
        "Ukrainian", "Turkish", "Arabic", "Hindi", "Japanese", "Korean", "Chinese (Simplified)",
        "Chinese (Traditional)", "Vietnamese", "Thai", "Indonesian"
    ];
}
