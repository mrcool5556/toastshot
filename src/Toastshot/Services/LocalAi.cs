using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace Toastshot.Services;

/// <summary>The graphics card with the most dedicated memory.</summary>
public sealed record GraphicsCard(string Name, long MemoryBytes)
{
    public double MemoryGb => MemoryBytes / 1073741824.0;
}

/// <summary>How well a model fits the graphics card.</summary>
public enum Fit
{
    Comfortable,
    Tight,
    TooSmall,
    Unknown
}

/// <summary>
/// The local AI behind Translate and AI vision: Ollama (ollama.com) running one recommended model on this PC.
/// Finds, starts and installs Ollama, and judges whether a model fits the graphics card.
/// </summary>
public static class LocalAi
{
    /// <summary>One small model that both reads images and translates.</summary>
    public const string RecommendedModel = "gemma3:4b";
    public const string RecommendedName = "Gemma 3 4B";
    public const double RecommendedDownloadGb = 3.4;
    public const double RecommendedVramGb = 4;

    public const string InstallerUrl = "https://ollama.com/download/OllamaSetup.exe";

    /// <summary>Free memory choices: Ollama's keep_alive value and how it reads in Settings.</summary>
    public static IReadOnlyList<(string KeepAlive, string Label)> MemoryModes { get; } =
    [
        ("30s", "30 seconds after use"),
        ("2m", "2 minutes after use"),
        ("5m", "5 minutes after use"),
        ("-1", "Never (always ready)")
    ];

    /// <summary>keep_alive as Ollama wants it: a duration string, or the number -1 for "keep loaded".</summary>
    public static object KeepAliveValue(string setting) => setting == "-1" ? -1 : setting;

    public static string DisplayName(string model) =>
        string.Equals(model, RecommendedModel, StringComparison.OrdinalIgnoreCase) ? RecommendedName : model;

    /// <summary>The card with the most dedicated memory, from the display adapters' registry entries.</summary>
    public static GraphicsCard? DetectGraphicsCard()
    {
        const string Adapters = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        GraphicsCard? best = null;
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(Adapters);
            foreach (var name in root?.GetSubKeyNames() ?? [])
            {
                using var key = root!.OpenSubKey(name);
                var description = key?.GetValue("DriverDesc") as string;
                var memory = key?.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] { Length: 8 } b => BitConverter.ToInt64(b),
                    _ => 0
                };
                if (description is null || memory <= 0 || description.Contains("Basic", StringComparison.OrdinalIgnoreCase)
                    || description.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (best is null || memory > best.MemoryBytes)
                {
                    best = new GraphicsCard(description, memory);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // No access to the adapter list: the fit is unknown.
        }

        return best;
    }

    /// <summary>Leaves room for Windows, the browser and games (about 2 GB) on top of the model.</summary>
    public static Fit FitFor(GraphicsCard? card, double modelGb) => card switch
    {
        null => Fit.Unknown,
        _ when card.MemoryGb >= modelGb + 4 => Fit.Comfortable,
        _ when card.MemoryGb >= modelGb + 0.5 => Fit.Tight,
        _ => Fit.TooSmall
    };

    /// <summary>Ollama's executable: the Windows app's default install, or on the PATH.</summary>
    public static string? FindOllama()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
        if (File.Exists(installed))
        {
            return installed;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "ollama.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static bool IsInstalled => FindOllama() is not null;

    /// <summary>Starts Ollama (its tray app if installed, else "ollama serve") and waits until it answers.</summary>
    public static async Task<bool> StartAsync(OllamaClient client, CancellationToken ct)
    {
        if (await IsRunningAsync(client, ct))
        {
            return true;
        }

        var exe = FindOllama();
        if (exe is null)
        {
            return false;
        }

        var app = Path.Combine(Path.GetDirectoryName(exe)!, "ollama app.exe");
        if (File.Exists(app))
        {
            Process.Start(new ProcessStartInfo(app) { UseShellExecute = true });
        }
        else
        {
            Process.Start(new ProcessStartInfo(exe, "serve") { UseShellExecute = false, CreateNoWindow = true });
        }

        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(500, ct);
            if (await IsRunningAsync(client, ct))
            {
                return true;
            }
        }

        return false;
    }

    public static async Task<bool> IsRunningAsync(OllamaClient client, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ListModelsAsync(timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is OllamaException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads Ollama's installer from ollama.com, checks it is signed by Ollama, and runs it for this user
    /// (no admin). Progress is the download fraction, or null while installing.
    /// </summary>
    public static async Task InstallOllamaAsync(HttpClient http, IProgress<double?> progress, CancellationToken ct)
    {
        var installer = TempFiles.NewPath(".exe");
        try
        {
            using (var response = await http.GetAsync(InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var file = File.Create(installer);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress.Report(total > 0 ? (double)done / total.Value : null);
                }
            }

            if (!IsSignedByOllama(installer))
            {
                throw new InvalidOperationException("The download isn't signed by Ollama, so it wasn't run.");
            }

            progress.Report(null);
            using var setup = Process.Start(new ProcessStartInfo(installer, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true })
                              ?? throw new InvalidOperationException("Couldn't start the Ollama installer.");
            await setup.WaitForExitAsync(ct);
            if (setup.ExitCode != 0)
            {
                throw new InvalidOperationException($"The Ollama installer stopped with code {setup.ExitCode}.");
            }
        }
        finally
        {
            TempFiles.TryDelete(installer);
        }
    }

    /// <summary>A valid Authenticode signature (WinVerifyTrust) whose signer is Ollama.</summary>
    public static bool IsSignedByOllama(string path)
    {
        try
        {
            var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return signer.Subject.Contains("Ollama", StringComparison.OrdinalIgnoreCase) && WinTrust.IsTrusted(path);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false; // not signed
        }
    }

    private static class WinTrust
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static bool IsTrusted(string path)
        {
            var file = new FileInfoData { Size = (uint)Marshal.SizeOf<FileInfoData>(), Path = path };
            var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfoData>());
            try
            {
                Marshal.StructureToPtr(file, filePtr, false);
                var data = new TrustData
                {
                    Size = (uint)Marshal.SizeOf<TrustData>(),
                    UiChoice = 2,          // WTD_UI_NONE
                    RevocationChecks = 0,  // WTD_REVOKE_NONE
                    UnionChoice = 1,       // WTD_CHOICE_FILE
                    File = filePtr,
                    StateAction = 0
                };
                var action = GenericVerifyV2;
                return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(filePtr);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfoData
        {
            public uint Size;
            public string Path;
            public IntPtr Handle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData
        {
            public uint Size;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr File;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
            public IntPtr SignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref TrustData data);
    }
}
