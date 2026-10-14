using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using Toastshot.Services;

namespace Toastshot.Tests;

public sealed class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+S", ModifierKeys.Control | ModifierKeys.Alt, Key.S, "Ctrl+Alt+S")]
    [InlineData("shift + ctrl + 4", ModifierKeys.Control | ModifierKeys.Shift, Key.D4, "Ctrl+Shift+4")]
    [InlineData("F9", ModifierKeys.None, Key.F9, "F9")]
    [InlineData("PrintScreen", ModifierKeys.None, Key.PrintScreen, "PrintScreen")]
    [InlineData("Win+Shift+G", ModifierKeys.Windows | ModifierKeys.Shift, Key.G, "Shift+Win+G")]
    public void Parses_And_Formats(string text, ModifierKeys modifiers, Key key, string formatted)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(modifiers, gesture.Modifiers);
        Assert.Equal(key, gesture.Key);
        Assert.Equal(formatted, gesture.ToString());
        Assert.True(HotkeyGesture.TryParse(gesture.ToString(), out var again));
        Assert.Equal(gesture, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("S")]          // no modifier
    [InlineData("Ctrl+Alt")]   // no key
    [InlineData("Ctrl+S+T")]   // two keys
    [InlineData("Ctrl+Banana")]
    public void Rejects_Invalid(string text) => Assert.False(HotkeyGesture.TryParse(text, out _));

    [Fact]
    public void Native_Modifiers_Include_NoRepeat()
    {
        HotkeyGesture.TryParse("Ctrl+Alt+S", out var g);
        Assert.Equal(HotkeyGesture.MOD_CONTROL | HotkeyGesture.MOD_ALT | HotkeyGesture.MOD_NOREPEAT, g.NativeModifiers);
        Assert.Equal(0x53, g.VirtualKey);
    }
}

public sealed class SettingsTests
{
    [Fact]
    public void Round_Trips_And_Clamps()
    {
        var path = TestUtil.TempFile(".json");
        var settings = new AppSettings { TranslateModel = " qwen2.5:7b ", GifFramesPerSecond = 99, OllamaUrl = "http://box:11434/", LastSaveFolder = @"D:\x" };
        settings.Save(path);
        var loaded = AppSettings.Load(path);
        Assert.Equal("qwen2.5:7b", loaded.TranslateModel);
        Assert.Equal(30, loaded.GifFramesPerSecond);
        Assert.Equal("http://box:11434", loaded.OllamaUrl);
        Assert.Equal(@"D:\x", loaded.LastSaveFolder);
        File.Delete(path);
    }

    [Fact]
    public void Broken_File_Gives_Defaults()
    {
        var path = TestUtil.TempFile(".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        var loaded = AppSettings.Load(path);
        Assert.Equal(LocalAi.RecommendedModel, loaded.TranslateModel);
        Assert.Equal("PrintScreen", loaded.CaptureHotkey);
        File.Delete(path);
    }

    [Theory]
    [InlineData("Ctrl+Alt+S", "PrintScreen")] // the old default moves to the new one
    [InlineData("Ctrl+Alt+F", "Ctrl+Alt+F")]  // a key the user chose stays
    public void Old_Settings_Files_Get_Print_Screen_Only_If_They_Had_The_Old_Default(string saved, string expected)
    {
        var path = TestUtil.TempFile(".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"CaptureHotkey\": \"" + saved + "\", \"GifHotkey\": \"Ctrl+Alt+G\" }");
        var loaded = AppSettings.Load(path);
        Assert.Equal(expected, loaded.CaptureHotkey);
        Assert.Equal("Shift+PrintScreen", loaded.GifHotkey); // the old GIF default moves too

        // Once saved in the current format, a later Ctrl+Alt+S is the user's own choice and stays.
        loaded.CaptureHotkey = "Ctrl+Alt+S";
        loaded.Save(path);
        Assert.Equal("Ctrl+Alt+S", AppSettings.Load(path).CaptureHotkey);
        File.Delete(path);
    }
}

public sealed class TextTests
{
    [Theory]
    [InlineData("こ ん に ち は 世 界", "こんにちは世界")]
    [InlineData("你 好 ， 世 界", "你好，世界")]
    [InlineData("안녕 하세요", "안녕 하세요")]                // Korean keeps word spaces
    [InlineData("Hello world 123", "Hello world 123")]
    [InlineData("価 格 100 円", "価格 100 円")]
    public void Ocr_CleanUp_Removes_Spaces_Between_CJK(string input, string expected) =>
        Assert.Equal(expected, OcrService.CleanUp(input));

    [Fact]
    public void Think_Filter_Drops_Reasoning_Split_Across_Chunks()
    {
        var filter = new ThinkTagFilter();
        var chunks = new[] { "<thi", "nk>let me think", " more</th", "ink>Hel", "lo <", "b>world" };
        var output = string.Concat(chunks.Select(filter.Push));
        Assert.Equal("Hello <b>world", output);
    }

    [Fact]
    public void Think_Filter_Passes_Plain_Text_Through() =>
        Assert.Equal("a < b", new ThinkTagFilter().Push("a < b"));

    [Fact]
    public void Translation_Prompt_Names_Target_And_Keeps_Text()
    {
        var (system, user) = AiTasks.TranslationPrompt("Bonjour\nle monde", "German");
        Assert.Contains("German", system);
        Assert.Equal("Translate this text into German:\n\nBonjour\nle monde", user);
    }
}

public sealed class OllamaClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request, LastBody);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Streams_Chat_And_Sends_Expected_Request()
    {
        var handler = new FakeHandler((req, _) =>
        {
            Assert.Equal("http://ollama:11434/api/chat", req.RequestUri!.ToString());
            return Json("""
                {"message":{"role":"assistant","content":"Hel"},"done":false}
                {"message":{"role":"assistant","content":"lo"},"done":false}
                {"message":{"role":"assistant","content":""},"done":true}
                """);
        });
        var client = new OllamaClient(new HttpClient(handler), "http://ollama:11434/");

        var chunks = new List<string>();
        await foreach (var c in client.ChatAsync("qwen2.5:3b", "sys", "Bonjour", [new byte[] { 1, 2, 3 }], CancellationToken.None))
        {
            chunks.Add(c);
        }

        Assert.Equal(["Hel", "lo"], chunks);
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("qwen2.5:3b", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.False(messages[0].TryGetProperty("images", out _));
        Assert.Equal("Bonjour", messages[1].GetProperty("content").GetString());
        Assert.Equal("AQID", messages[1].GetProperty("images")[0].GetString());
    }

    [Fact]
    public async Task Missing_Model_Gives_Pull_Hint()
    {
        var client = new OllamaClient(new HttpClient(new FakeHandler((_, _) =>
            Json("""{"error":"model 'nope' not found"}""", HttpStatusCode.NotFound))), "http://x");
        var ex = await Assert.ThrowsAsync<OllamaException>(async () =>
        {
            await foreach (var _ in client.ChatAsync("nope", "s", "u", null, CancellationToken.None))
            {
            }
        });
        Assert.Contains("ollama pull nope", ex.Message);
    }

    [Fact]
    public async Task Stream_Error_Is_Surfaced()
    {
        var client = new OllamaClient(new HttpClient(new FakeHandler((_, _) => Json("""{"error":"out of memory"}"""))), "http://x");
        var ex = await Assert.ThrowsAsync<OllamaException>(() => AiTasks.CollectAsync(
            client.ChatAsync("m", "s", "u", null, CancellationToken.None), null, CancellationToken.None));
        Assert.Equal("out of memory", ex.Message);
    }

    [Fact]
    public async Task Unreachable_Server_Explains_Itself()
    {
        // Port 9 (discard) on loopback is closed on a normal machine.
        var client = new OllamaClient(new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, "http://127.0.0.1:9");
        var ex = await Assert.ThrowsAsync<OllamaException>(() => client.ListModelsAsync(CancellationToken.None));
        Assert.Contains("Can't reach Ollama", ex.Message);
    }

    [Fact]
    public async Task Lists_Models_With_Vision_Flag()
    {
        var client = new OllamaClient(new HttpClient(new FakeHandler((_, _) => Json("""
            {"models":[{"name":"qwen2.5:3b","capabilities":["completion"]},{"name":"llava:7b","capabilities":["completion","vision"]}]}
            """))), "http://x");
        var models = await client.ListModelsAsync(CancellationToken.None);
        Assert.Equal(["llava:7b", "qwen2.5:3b"], models.Select(m => m.Name));
        Assert.True(models[0].HasVision);
        Assert.False(models[1].HasVision);
    }

    [Theory]
    [InlineData("2m", "\"2m\"")]
    [InlineData(null, null)]
    public async Task Chat_Sends_Keep_Alive_So_Memory_Is_Freed_After_Use(string? keepAlive, string? expected)
    {
        var handler = new FakeHandler((_, _) => Json("""{"message":{"role":"assistant","content":""},"done":true}"""));
        var client = new OllamaClient(new HttpClient(handler), "http://x", keepAlive);
        await AiTasks.CollectAsync(client.ChatAsync("m", "s", "u", null, CancellationToken.None), null, CancellationToken.None);
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(expected, body.RootElement.TryGetProperty("keep_alive", out var value) ? value.GetRawText() : null);
    }

    [Fact]
    public async Task Always_Ready_Sends_Minus_One()
    {
        var handler = new FakeHandler((_, _) => Json("""{"message":{"role":"assistant","content":""},"done":true}"""));
        var client = new OllamaClient(new HttpClient(handler), "http://x", LocalAi.KeepAliveValue("-1"));
        await AiTasks.CollectAsync(client.ChatAsync("m", "s", "u", null, CancellationToken.None), null, CancellationToken.None);
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(-1, body.RootElement.GetProperty("keep_alive").GetInt32());
    }

    [Fact]
    public async Task Lists_Loaded_Models_With_Graphics_Memory()
    {
        var client = new OllamaClient(new HttpClient(new FakeHandler((req, _) =>
        {
            Assert.EndsWith("/api/ps", req.RequestUri!.ToString());
            return Json("""{"models":[{"name":"gemma3:4b","size":5100000000,"size_vram":4200000000,"expires_at":"2026-10-11T10:02:00Z"}]}""");
        })), "http://x");
        var loaded = Assert.Single(await client.ListLoadedAsync(CancellationToken.None));
        Assert.Equal(("gemma3:4b", 5_100_000_000L, 4_200_000_000L), (loaded.Name, loaded.SizeBytes, loaded.VramBytes));
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 10, 2, 0, TimeSpan.Zero), loaded.UnloadsAt);
    }

    [Fact]
    public async Task Unload_Asks_For_Keep_Alive_Zero()
    {
        var handler = new FakeHandler((req, _) =>
        {
            Assert.EndsWith("/api/generate", req.RequestUri!.ToString());
            return Json("""{"done":true}""");
        });
        await new OllamaClient(new HttpClient(handler), "http://x").UnloadAsync("gemma3:4b", CancellationToken.None);
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("gemma3:4b", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("keep_alive").GetInt32());
    }

    [Fact]
    public async Task Pull_Reports_Progress_Until_Success()
    {
        var client = new OllamaClient(new HttpClient(new FakeHandler((_, _) => Json("""
            {"status":"pulling manifest"}
            {"status":"pulling abc","total":1000,"completed":250}
            {"status":"pulling abc","total":1000,"completed":1000}
            {"status":"verifying sha256 digest"}
            {"status":"success"}
            """))), "http://x");
        var steps = new List<PullProgress>();
        await client.PullAsync("gemma3:4b", new SyncProgress<PullProgress>(steps.Add), CancellationToken.None);
        Assert.Equal(["pulling manifest", "pulling abc", "pulling abc", "verifying sha256 digest", "success"], steps.Select(s => s.Status));
        Assert.Equal(0.25, steps[1].Fraction);
        Assert.Null(steps[0].Fraction);
    }

    [Fact]
    public async Task Pull_Error_Is_Surfaced()
    {
        var client = new OllamaClient(new HttpClient(new FakeHandler((_, _) => Json("""{"error":"pull model manifest: file does not exist"}"""))), "http://x");
        var ex = await Assert.ThrowsAsync<OllamaException>(() => client.PullAsync("nope", null, CancellationToken.None));
        Assert.Contains("does not exist", ex.Message);
    }

    [Theory]
    [InlineData(12.0, Fit.Comfortable)]
    [InlineData(6.0, Fit.Tight)]
    [InlineData(4.0, Fit.TooSmall)]
    public void Graphics_Memory_Decides_The_Fit(double cardGb, Fit expected) =>
        Assert.Equal(expected, LocalAi.FitFor(new GraphicsCard("GPU", (long)(cardGb * 1073741824)), LocalAi.RecommendedVramGb));

    [Fact]
    public void No_Card_Means_Unknown_Fit() => Assert.Equal(Fit.Unknown, LocalAi.FitFor(null, 4));

    [Fact]
    public void A_File_Not_Signed_By_Ollama_Is_Refused() =>
        Assert.False(LocalAi.IsSignedByOllama(typeof(LocalAi).Assembly.Location));

    /// <summary>Progress&lt;T&gt; posts to a sync context; this reports immediately so tests can read the list.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

