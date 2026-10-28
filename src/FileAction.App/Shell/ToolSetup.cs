using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FileAction.App.Jobs;
using FileAction.Core;
using static FileAction.Core.Loc;

namespace FileAction.App.Shell;

/// <summary>
/// Fetches the helper engines Peel does not ship: FFmpeg (with ffprobe), Pandoc and yt-dlp, each from
/// its official GitHub release, verified against the SHA-256 that GitHub publishes for the file, and
/// unpacked into tools\ next to Peel.exe. Runs on start when something is missing; one card shows it.
/// yt-dlp, whose sites change often, is asked to update itself once a day.
/// </summary>
internal static partial class ToolSetup
{
    private static readonly HttpClient Http = CreateHttp();
    private static bool _running;

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "tools");

    private sealed record Tool(string Name, string Repository, string Release, Func<string, bool> PicksAsset, string[] Files);

    private static readonly Tool Ffmpeg = new("FFmpeg", "BtbN/FFmpeg-Builds", "tags/latest", name => StableFfmpeg().IsMatch(name), ["ffmpeg.exe", "ffprobe.exe"]);
    private static readonly Tool Pandoc = new("Pandoc", "jgm/pandoc", "latest", name => PandocWindows().IsMatch(name), ["pandoc.exe"]);
    private static readonly Tool YtDlp = new("yt-dlp", "yt-dlp/yt-dlp", "latest", name => name == "yt-dlp.exe", ["yt-dlp.exe"]);

    public static async Task EnsureAsync(ToolLocator tools)
    {
        if (_running)
            return;
        _running = true;
        try
        {
            var missing = new List<Tool>();
            if (tools.Ffmpeg is not { } ffmpeg || !File.Exists(Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe")))
                missing.Add(Ffmpeg);
            if (tools.Pandoc is null)
                missing.Add(Pandoc);
            if (VideoDownloader.Locate() is null)
                missing.Add(YtDlp);
            if (missing.Count == 0)
            {
                await UpdateYtDlpAsync();
                return;
            }
            await InstallAsync(missing, tools);
        }
        finally
        {
            _running = false;
        }
    }

    private static async Task InstallAsync(IReadOnlyList<Tool> missing, ToolLocator tools)
    {
        using var cancellation = new CancellationTokenSource();
        var names = string.Join(", ", missing.Select(tool => tool.Name));
        var card = Toasts.Job(T("Připravuji Peel", "Setting up Peel"), T($"Stahuji {names}", $"Downloading {names}"), null, () => cancellation.Cancel());
        var current = "";
        try
        {
            Directory.CreateDirectory(Folder);
            // Resolve everything first so the bar can count real bytes across all downloads.
            var assets = new List<(Tool Tool, string Url, long Size, string Sha256)>();
            foreach (var tool in missing)
            {
                current = tool.Name;
                assets.Add(await ResolveAsync(tool, cancellation.Token));
            }
            var total = Math.Max(1, assets.Sum(asset => asset.Size));
            long done = 0;
            for (var i = 0; i < assets.Count; i++)
            {
                var (tool, url, size, sha) = assets[i];
                current = tool.Name;
                card.SetDetails(T("Připravuji Peel", "Setting up Peel"), T($"Stahuji {tool.Name} · {i + 1} z {assets.Count}", $"Downloading {tool.Name} · {i + 1} of {assets.Count}"));
                var before = done;
                await InstallOneAsync(tool, url, sha, bytes => card.Report((before + bytes) / (double)total * 0.98), cancellation.Token);
                done += size;
            }
            tools.Refresh();
            card.Complete([], null);
            card.SetDetails(T("Peel je připravený", "Peel is ready"), T($"Nainstalováno: {names}", $"Installed {names}"));
        }
        catch (OperationCanceledException)
        {
            card.Cancelled();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            App.Log(ex);
            card.Fail(T($"{current} se nepodařilo stáhnout: {ex.Message} Peel to zkusí znovu při příštím spuštění.",
                $"Couldn't download {current}: {ex.Message} Peel will try again next time it starts."));
        }
    }

    /// <summary>The release file and the SHA-256 GitHub computed for it when it was uploaded.</summary>
    private static async Task<(Tool, string, long, string)> ResolveAsync(Tool tool, CancellationToken cancellation)
    {
        using var json = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{tool.Repository}/releases/{tool.Release}", cancellation));
        var asset = json.RootElement.GetProperty("assets").EnumerateArray()
            .Where(item => tool.PicksAsset(item.GetProperty("name").GetString() ?? ""))
            .OrderByDescending(item => NameVersion(item.GetProperty("name").GetString() ?? ""))
            .FirstOrDefault();
        if (asset.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(T("Vydání neobsahuje soubor pro Windows.", "The release has no Windows file."));
        var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(T("Vydání nemá kontrolní součet; soubor se nestáhne.", "The release publishes no checksum, so it isn't downloaded."));
        return (tool, asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64(), digest[7..].ToLowerInvariant());
    }

    private static async Task InstallOneAsync(Tool tool, string url, string expectedSha256, Action<long> progress, CancellationToken cancellation)
    {
        var download = Path.Combine(Folder, $".{tool.Name}-{Guid.NewGuid():N}.part");
        try
        {
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellation);
                await using var output = File.Create(download);
                var buffer = new byte[1 << 16];
                long copied = 0;
                var lastReport = 0L;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    copied += read;
                    if (Environment.TickCount64 - lastReport > 150)
                    {
                        lastReport = Environment.TickCount64;
                        progress(copied);
                    }
                }
                progress(copied);
            }

            await using (var check = File.OpenRead(download))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(check, cancellation)).ToLowerInvariant();
                if (actual != expectedSha256)
                    throw new InvalidDataException(T("Stažený soubor nesouhlasí s kontrolním součtem.", "The downloaded file doesn't match its published checksum."));
            }

            if (!url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                File.Move(download, Path.Combine(Folder, tool.Files[0]), overwrite: true);
                return;
            }
            using var archive = ZipFile.OpenRead(download);
            foreach (var file in tool.Files)
            {
                var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.Name, file, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException(T($"{file} v archivu chybí.", $"{file} is missing from the archive."));
                var target = Path.Combine(Folder, file);
                var temp = target + ".new";
                entry.ExtractToFile(temp, overwrite: true);
                File.Move(temp, target, overwrite: true);
            }
        }
        finally
        {
            try { File.Delete(download); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>yt-dlp in tools\ is the standalone build, which can verify and replace itself.</summary>
    private static async Task UpdateYtDlpAsync()
    {
        var exe = Path.Combine(Folder, "yt-dlp.exe");
        var stamp = Path.Combine(AppSettings.Folder, "yt-dlp-checked.txt");
        try
        {
            if (!File.Exists(exe) || (File.Exists(stamp) && DateTime.Now - File.GetLastWriteTime(stamp) < TimeSpan.FromDays(1)))
                return;
            Directory.CreateDirectory(AppSettings.Folder);
            await File.WriteAllTextAsync(stamp, DateTime.Now.ToString("s"));
            using var process = Process.Start(new ProcessStartInfo(exe, "-U") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (process is null)
                return;
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            App.Log(ex);
        }
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Peel/{Updater.Current}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>"ffmpeg-n10.0-…" sorts above "ffmpeg-n9.0-…", which plain text order would not.</summary>
    private static Version NameVersion(string name) =>
        VersionInName().Match(name) is { Success: true } match && Version.TryParse(match.Value, out var version) ? version : new Version(0, 0);

    [GeneratedRegex(@"\d+(?:\.\d+)+")]
    private static partial Regex VersionInName();

    // The newest stable FFmpeg line (e.g. ffmpeg-n9.0-latest-win64-gpl-9.0.zip), not nightly master or shared builds.
    [GeneratedRegex(@"^ffmpeg-n\d+\.\d+-latest-win64-gpl-\d+\.\d+\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex StableFfmpeg();

    [GeneratedRegex(@"^pandoc-[\d.]+-windows-x86_64\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex PandocWindows();
}
