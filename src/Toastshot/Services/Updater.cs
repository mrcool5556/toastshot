using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Toastshot.Services;

/// <summary>Where Toastshot lives online. Empty values turn the matching feature off (nothing is contacted).</summary>
public static class Project
{
    /// <summary>GitHub "owner/repository" that releases come from.</summary>
    public const string Repository = "mrcool5556/toastshot";

    /// <summary>Where the Support button goes (GitHub Sponsors, Ko-fi…).</summary>
    public const string DonationUrl = "https://ko-fi.com/toters";

    public static string? RepositoryUrl => Repository.Length > 0 ? $"https://github.com/{Repository}" : null;
}

/// <summary>One published version and what changed in it (the release notes, Markdown).</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Notes, DateTimeOffset Published, string PageUrl, string? PackageUrl, string? PackageSha256);

/// <summary>The releases newer than this copy, newest first.</summary>
public sealed record UpdateCheck(Version Current, IReadOnlyList<ReleaseInfo> Newer)
{
    public ReleaseInfo? Latest => Newer.Count > 0 ? Newer[0] : null;
}

/// <summary>
/// Updates from GitHub releases: checks for versions newer than this one (with every release's notes, so someone who
/// skipped a few sees all of them), and installs one by running the release's own installer.
/// </summary>
public static class Updater
{
    /// <summary>The release asset that holds the installer package (see installer\build.ps1).</summary>
    public const string PackageName = "ToastshotSetup.exe";

    public static Version Current { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    public static bool IsConfigured => Project.Repository.Length > 0;

    /// <summary>True when running from the installed location (%LOCALAPPDATA%\Programs\Toastshot), where updates install in place.</summary>
    public static bool IsInstalledCopy =>
        Environment.ProcessPath is { } exe && Path.GetDirectoryName(exe) is { } dir && string.Equals(
            Path.GetFullPath(dir).TrimEnd('\\'),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Toastshot"),
            StringComparison.OrdinalIgnoreCase);

    public static async Task<UpdateCheck> CheckAsync(HttpClient http, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Project.Repository}/releases?per_page=30");
        request.Headers.UserAgent.ParseAdd($"Toastshot/{Current}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return new UpdateCheck(Current, Parse(json, Current));
    }

    /// <summary>Published, non-prerelease releases tagged v1.2.3 that are newer than <paramref name="current"/>, newest first.</summary>
    internal static IReadOnlyList<ReleaseInfo> Parse(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var releases = new List<ReleaseInfo>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.GetProperty("draft").GetBoolean() || r.GetProperty("prerelease").GetBoolean())
            {
                continue;
            }

            var tag = r.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || Normalize(version) <= current)
            {
                continue;
            }

            string? zipUrl = null, sha = null;
            foreach (var asset in r.TryGetProperty("assets", out var assets) ? assets.EnumerateArray() : default)
            {
                if (string.Equals(asset.GetProperty("name").GetString(), PackageName, StringComparison.OrdinalIgnoreCase))
                {
                    zipUrl = asset.GetProperty("browser_download_url").GetString();
                    sha = asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.Ordinal)
                        ? d["sha256:".Length..]
                        : null;
                }
            }

            releases.Add(new ReleaseInfo(
                Normalize(version),
                tag,
                r.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                r.TryGetProperty("published_at", out var published) && published.TryGetDateTimeOffset(out var at) ? at : default,
                r.GetProperty("html_url").GetString() ?? "",
                zipUrl,
                sha));
        }

        return releases.OrderByDescending(r => r.Version).ToList();
    }

    /// <summary>
    /// Downloads the release package, checks it against the checksum GitHub publishes, unpacks it and runs its
    /// installer, which closes Toastshot, replaces it and starts the new version. Progress is the download fraction.
    /// </summary>
    public static async Task InstallAsync(HttpClient http, ReleaseInfo release, IProgress<double?> progress, CancellationToken ct)
    {
        if (release.PackageUrl is null)
        {
            throw new InvalidOperationException($"Release {release.Tag} has no {PackageName} to install.");
        }

        var setup = TempFiles.NewPath(".exe");
        using (var response = await http.GetAsync(release.PackageUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(setup);
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

        if (release.PackageSha256 is { } expected)
        {
            string actual;
            await using (var check = File.OpenRead(setup))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(check, ct));
            }

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                TempFiles.TryDelete(setup);
                throw new InvalidOperationException("The download doesn't match the release's checksum, so it wasn't installed.");
            }
        }

        // Setup closes this copy of Toastshot, installs over it and starts the new version, so it runs on its own.
        progress.Report(null);
        Process.Start(new ProcessStartInfo(setup, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = false });
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
}
