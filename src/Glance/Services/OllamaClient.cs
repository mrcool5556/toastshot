using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glance.Services;

public sealed class OllamaException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record OllamaModel(string Name, IReadOnlyList<string> Capabilities, long SizeBytes = 0)
{
    public bool HasVision => Capabilities.Contains("vision", StringComparer.OrdinalIgnoreCase);
}

/// <summary>A model Ollama has in memory: its total size and the part on the graphics card.</summary>
public sealed record LoadedModel(string Name, long SizeBytes, long VramBytes, DateTimeOffset? UnloadsAt);

/// <summary>A step of a model download: what is happening and how far along it is.</summary>
public readonly record struct PullProgress(string Status, long Completed, long Total)
{
    public double? Fraction => Total > 0 ? (double)Completed / Total : null;
}

/// <summary>Minimal client for a local Ollama server: models, streaming chat, memory, downloads.</summary>
/// <param name="keepAlive">How long Ollama keeps the model in memory after a request: "2m", "30s", or -1 for always; null leaves Ollama's default.</param>
public sealed class OllamaClient(HttpClient http, string baseUrl, object? keepAlive = null)
{
    private readonly string _baseUrl = baseUrl.TrimEnd('/');

    public string BaseUrl => _baseUrl;

    public async Task<IReadOnlyList<OllamaModel>> ListModelsAsync(CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync($"{_baseUrl}/api/tags", ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, null, ct).ConfigureAwait(false);
            var tags = await response.Content.ReadFromJsonAsync<TagsResponse>(cancellationToken: ct).ConfigureAwait(false);
            return tags?.Models?.Select(m => new OllamaModel(m.Name ?? "", m.Capabilities ?? [], m.Size))
                       .Where(m => m.Name.Length > 0)
                       .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                       .ToList()
                   ?? [];
        }
        catch (Exception ex) when (IsConnectionFailure(ex, ct))
        {
            throw Unreachable(ex);
        }
    }

    /// <summary>Streams the assistant's reply. Text inside &lt;think&gt; tags is dropped.</summary>
    public async IAsyncEnumerable<string> ChatAsync(
        string model,
        string system,
        string user,
        IReadOnlyList<byte[]>? images,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new OllamaException("No model is set. Choose one in Settings.");
        }

        var request = new ChatRequest
        {
            Model = model,
            Stream = true,
            Messages =
            [
                new ChatMessage { Role = "system", Content = system },
                new ChatMessage
                {
                    Role = "user",
                    Content = user,
                    Images = images?.Select(Convert.ToBase64String).ToArray()
                }
            ],
            Options = new ChatOptions { Temperature = 0.2 },
            KeepAlive = keepAlive
        };

        HttpResponseMessage response;
        try
        {
            var message = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
            {
                Content = JsonContent.Create(request, options: JsonOptions)
            };
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsConnectionFailure(ex, ct))
        {
            throw Unreachable(ex);
        }

        using (response)
        {
            await EnsureSuccessAsync(response, model, ct).ConfigureAwait(false);
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var filter = new ThinkTagFilter();
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var chunk = JsonSerializer.Deserialize<ChatChunk>(line, JsonOptions);
                if (chunk?.Error is { Length: > 0 } error)
                {
                    throw new OllamaException(error);
                }

                var visible = filter.Push(chunk?.Message?.Content ?? "");
                if (visible.Length > 0)
                {
                    yield return visible;
                }

                if (chunk?.Done == true)
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>Models in memory now (/api/ps).</summary>
    public async Task<IReadOnlyList<LoadedModel>> ListLoadedAsync(CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync($"{_baseUrl}/api/ps", ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, null, ct).ConfigureAwait(false);
            var ps = await response.Content.ReadFromJsonAsync<PsResponse>(JsonOptions, ct).ConfigureAwait(false);
            return ps?.Models?.Select(m => new LoadedModel(m.Name ?? "", m.Size, m.SizeVram, m.ExpiresAt)).ToList() ?? [];
        }
        catch (Exception ex) when (IsConnectionFailure(ex, ct))
        {
            throw Unreachable(ex);
        }
    }

    /// <summary>Frees the model's memory now (a request with keep_alive 0).</summary>
    public async Task UnloadAsync(string model, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsync($"{_baseUrl}/api/generate",
                JsonContent.Create(new { model, keep_alive = 0 }), ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, model, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsConnectionFailure(ex, ct))
        {
            throw Unreachable(ex);
        }
    }

    /// <summary>Downloads a model, reporting progress (/api/pull, streamed).</summary>
    public async Task PullAsync(string model, IProgress<PullProgress>? progress, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            var message = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/pull") { Content = JsonContent.Create(new { model, stream = true }) };
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsConnectionFailure(ex, ct))
        {
            throw Unreachable(ex);
        }

        using (response)
        {
            await EnsureSuccessAsync(response, null, ct).ConfigureAwait(false);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var step = JsonSerializer.Deserialize<PullChunk>(line, JsonOptions);
                if (step?.Error is { Length: > 0 } error)
                {
                    throw new OllamaException(error);
                }

                progress?.Report(new PullProgress(step?.Status ?? "", step?.Completed ?? 0, step?.Total ?? 0));
                if (step?.Status == "success")
                {
                    return;
                }
            }
        }
    }

    private OllamaException Unreachable(Exception ex) =>
        new($"Can't reach Ollama at {_baseUrl}. Is it running? (Start it with \"ollama serve\" or the Ollama app.)", ex);

    private static bool IsConnectionFailure(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or SocketException ||
        (ex is TaskCanceledException && !ct.IsCancellationRequested); // HttpClient timeout

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string? model, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? detail = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            detail = JsonSerializer.Deserialize<ChatChunk>(body, JsonOptions)?.Error ?? body;
        }
        catch (JsonException)
        {
        }

        if ((int)response.StatusCode == 404 && model is not null)
        {
            throw new OllamaException($"Model \"{model}\" isn't installed in Ollama. Install it with: ollama pull {model}");
        }

        throw new OllamaException($"Ollama returned {(int)response.StatusCode}: {detail ?? response.ReasonPhrase}");
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal sealed class ChatRequest
    {
        public string Model { get; set; } = "";
        public bool Stream { get; set; }
        public ChatMessage[] Messages { get; set; } = [];
        public ChatOptions? Options { get; set; }
        public object? KeepAlive { get; set; }
    }

    internal sealed class ChatMessage
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
        public string[]? Images { get; set; }
    }

    internal sealed class ChatOptions
    {
        public double Temperature { get; set; }
    }

    private sealed class ChatChunk
    {
        public ChatMessage? Message { get; set; }
        public bool Done { get; set; }
        public string? Error { get; set; }
    }

    private sealed class TagsResponse
    {
        public List<TagModel>? Models { get; set; }
    }

    private sealed class PsResponse
    {
        public List<PsModel>? Models { get; set; }
    }

    private sealed class PsModel
    {
        public string? Name { get; set; }
        public long Size { get; set; }
        public long SizeVram { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
    }

    private sealed class PullChunk
    {
        public string? Status { get; set; }
        public long Completed { get; set; }
        public long Total { get; set; }
        public string? Error { get; set; }
    }

    private sealed class TagModel
    {
        public string? Name { get; set; }
        public long Size { get; set; }
        public List<string>? Capabilities { get; set; }
    }
}

/// <summary>Removes &lt;think&gt;...&lt;/think&gt; sections from streamed text, even when tags are split across chunks.</summary>
public sealed class ThinkTagFilter
{
    private const string Open = "<think>";
    private const string Close = "</think>";
    private string _carry = "";
    private bool _inside;

    public string Push(string chunk)
    {
        var text = _carry + chunk;
        _carry = "";
        var output = new System.Text.StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var tag = _inside ? Close : Open;
            var at = text.IndexOf(tag, i, StringComparison.Ordinal);
            if (at >= 0)
            {
                if (!_inside)
                {
                    output.Append(text, i, at - i);
                }

                _inside = !_inside;
                i = at + tag.Length;
                continue;
            }

            // Hold back a possible partial tag at the end until the next chunk arrives.
            var keep = PartialTagSuffix(text, i, tag);
            if (!_inside)
            {
                output.Append(text, i, text.Length - i - keep);
            }

            _carry = text[^keep..];
            break;
        }

        return output.ToString();
    }

    private static int PartialTagSuffix(string text, int start, string tag)
    {
        for (var len = Math.Min(tag.Length - 1, text.Length - start); len > 0; len--)
        {
            if (string.CompareOrdinal(text, text.Length - len, tag, 0, len) == 0)
            {
                return len;
            }
        }

        return 0;
    }
}
