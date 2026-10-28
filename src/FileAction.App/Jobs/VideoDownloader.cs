using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileAction.App.Shell;
using FileAction.Core;
using static FileAction.Core.Loc;

namespace FileAction.App.Jobs;

internal enum VideoFormat { Mp4, Mp3 }

/// <summary>The card shown for a copied video link: MP4 or MP3, then the download's progress.</summary>
internal static class VideoOffers
{
    public static void Show(VideoLink link, ToolLocator tools)
    {
        var card = Toasts.Link(link.Site, T("Zkopírovaný odkaz · načítám podrobnosti…", "Copied link · getting details…"));
        if (card is null)
            return;
        if (VideoDownloader.Locate() is not { } ytDlp)
        {
            card.SetDetails(link.Site, T("Pro stahování chybí yt-dlp. Nainstalujte ho: winget install yt-dlp.yt-dlp", "Downloading needs yt-dlp. Install it with: winget install yt-dlp.yt-dlp"));
            return;
        }
        VideoInfo? info = null;
        var details = new CancellationTokenSource();
        card.Dismissed += _ => details.Cancel();
        Task<bool>? refreshing = null;

        // A site that rejects yt-dlp usually changed; one refresh of Peel's own copy is shared by
        // the details lookup and the download, and each retries once afterwards.
        async Task<bool> Refresh()
        {
            refreshing ??= ToolSetup.RefreshYtDlpAsync();
            var ok = await refreshing;
            if (VideoDownloader.Locate() is { } updated)
                ytDlp = updated;
            return ok;
        }

        async void Start(VideoFormat format)
        {
            var label = format == VideoFormat.Mp4 ? "MP4" : "MP3";
            var title = info?.Title ?? link.Site;
            using var cancellation = new CancellationTokenSource();
            card.BeginJob(title, T($"Stahuji {label} · {link.Site}", $"Downloading {label} · {link.Site}"), () => cancellation.Cancel());
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var file = await VideoDownloader.DownloadAsync(ytDlp, tools.Ffmpeg, link, format, new Progress<double>(card.Report), cancellation.Token);
                    card.Complete([file], null);
                    card.SetDetails(title, T($"{label} uloženo do Stažených souborů", $"{label} saved to Downloads"));
                    return;
                }
                catch (OperationCanceledException)
                {
                    card.Cancelled();
                    return;
                }
                catch (SiteChangedException) when (attempt == 0)
                {
                    card.SetDetails(title, T("Aktualizuji stahovač a zkouším to znovu…", "Updating the downloader and trying again…"));
                    await Refresh();
                    card.SetDetails(title, T($"Stahuji {label} · {link.Site}", $"Downloading {label} · {link.Site}"));
                }
                catch (Exception ex) when (ex is SiteChangedException or ConversionException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    card.Fail(ex.Message);
                    return;
                }
            }
        }

        card.Offer([("MP4", "", true, () => Start(VideoFormat.Mp4)), ("MP3", "", false, () => Start(VideoFormat.Mp3))]);
        _ = LoadDetails();

        async Task LoadDetails()
        {
            try
            {
                info = await VideoDownloader.ProbeAsync(ytDlp, link, details.Token);
                if (info is null && !card.Leaving)
                {
                    if (!card.IsWorking) card.SetDetails(link.Site, T("Aktualizuji stahovač…", "Updating the downloader…"));
                    if (await Refresh())
                        info = await VideoDownloader.ProbeAsync(ytDlp, link, details.Token);
                }
                if (card.Leaving)
                    return;
                if (info is null)
                {
                    // MP4 / MP3 stay available: the download reports its own, specific error.
                    if (!card.IsWorking) card.SetDetails(link.Site, T("Podrobnosti nejsou k dispozici · MP4 nebo MP3 můžete zkusit i tak", "Details unavailable · you can still try MP4 or MP3"));
                    return;
                }
                var parts = new List<string> { link.Site };
                if (!string.IsNullOrWhiteSpace(info.Uploader)) parts.Add(info.Uploader!);
                if (info.Duration is double seconds && seconds > 0) parts.Add(TimeSpan.FromSeconds(Math.Round(seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss"));
                if (!card.IsWorking) card.SetDetails(info.Title, string.Join(" · ", parts));
                if (info.Thumbnail is { } thumbnail && await VideoDownloader.ThumbnailAsync(thumbnail, details.Token) is { } image)
                    card.SetThumbnail(image);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}

internal sealed record VideoInfo(string Title, string? Uploader, double? Duration, string? Thumbnail);

/// <summary>The site rejected yt-dlp in a way a newer yt-dlp usually fixes.</summary>
internal sealed class SiteChangedException(string message) : Exception(message);

/// <summary>
/// Downloads a video page with yt-dlp into Downloads. MP4 prefers H.264 + AAC up to 1080p so
/// the file opens in any editor; MP3 is 320 kbps. Partial files stay in Peel's own temporary
/// folder, so a cancelled download never leaves fragments in Downloads.
/// </summary>
internal static class VideoDownloader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>yt-dlp next to Peel (tools\yt-dlp.exe), then anywhere on PATH.</summary>
    public static string? Locate()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp.exe");
        if (File.Exists(bundled))
            return bundled;
        var path = string.Join(Path.PathSeparator, Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "yt-dlp.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    /// <summary>Title, author, length and thumbnail, without downloading the video.</summary>
    public static async Task<VideoInfo?> ProbeAsync(string ytDlp, VideoLink link, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var output = new StringBuilder();
        var (exit, _) = await RunAsync(ytDlp, ["--dump-single-json", "--skip-download", "--no-playlist", "--no-warnings", "--encoding", "utf-8", link.Url],
            line => output.AppendLine(line), timeout.Token);
        if (exit != 0)
            return null;
        try
        {
            using var json = JsonDocument.Parse(output.ToString());
            var root = json.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            double? Number(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
            var title = Text("title") is { Length: > 0 } t ? t : Text("fulltitle") ?? link.Site;
            return new VideoInfo(title, Text("uploader") ?? Text("channel"), Number("duration"), Text("thumbnail"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<ImageSource?> ThumbnailAsync(string url, CancellationToken cancellation)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(url, cancellation);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 132;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException or IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Returns the finished file in Downloads.</summary>
    public static async Task<string> DownloadAsync(string ytDlp, string? ffmpeg, VideoLink link, VideoFormat format, IProgress<double> progress, CancellationToken cancellation)
    {
        var temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Peel", "downloads", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var args = new List<string>
        {
            "--no-playlist", "--newline", "--no-warnings", "--encoding", "utf-8", "--windows-filenames", "--no-mtime",
            "--progress", "--progress-template", "download:PEEL|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s",
            "--print", "before_dl:PEELFMT %(format_id)s",
            "--print", "after_move:PEELFILE %(filepath)s",
            "-P", KnownFolders.Downloads, "-P", "temp:" + temp,
            "-o", "%(title).150B.%(ext)s",
        };
        if (ffmpeg is not null)
            args.AddRange(["--ffmpeg-location", ffmpeg]);
        if (format == VideoFormat.Mp4)
            args.AddRange(["-f", "bv*+ba/b", "-S", "res:1080,vcodec:h264,acodec:aac", "--merge-output-format", "mp4"]);
        else
            args.AddRange(["-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "320K", "--embed-metadata"]);
        args.Add(link.Url);

        string? file = null;
        var parts = 1;
        var part = 0;
        var last = 0.0;
        try
        {
            var (exit, errors) = await RunAsync(ytDlp, args, line =>
            {
                if (line.StartsWith("PEELFMT ", StringComparison.Ordinal))
                    parts = Math.Max(1, line[8..].Count(c => c == '+') + 1);
                else if (line.StartsWith("PEELFILE ", StringComparison.Ordinal))
                    file = line[9..].Trim();
                else if (line.StartsWith("PEEL|", StringComparison.Ordinal) && Fraction(line) is { } value)
                {
                    // Video and audio arrive one after another; each restarts at 0%.
                    if (value < last - 0.5 && part < parts - 1)
                        part++;
                    last = value;
                    progress.Report(Math.Min(0.96, (part + value) / parts * 0.96));
                }
                else if (line.StartsWith("[Merger]", StringComparison.Ordinal) || line.StartsWith("[ExtractAudio]", StringComparison.Ordinal))
                    progress.Report(0.97);
            }, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (exit != 0)
                throw SiteChanged(errors) ? new SiteChangedException(Friendly(errors, link)) : new ConversionException(Friendly(errors, link));
            if (file is null || !File.Exists(file))
                throw new ConversionException(T("Stahování skončilo, ale soubor se nenašel.", "The download finished, but the file wasn't found."));
            progress.Report(1);
            return file;
        }
        finally
        {
            // Only Peel's own temporary folder for this download is removed.
            try { Directory.Delete(temp, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static double? Fraction(string line)
    {
        var fields = line.Split('|');
        if (fields.Length < 4 || !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var done))
            return null;
        var total = double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var exact) ? exact
            : double.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var estimate) ? estimate : 0;
        return total > 0 ? Math.Clamp(done / total, 0, 1) : null;
    }

    /// <summary>yt-dlp's last error, in words a person can act on.</summary>
    private static string Friendly(string errors, VideoLink link)
    {
        var line = errors.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal)) ?? errors.Trim();
        var lower = line.ToLowerInvariant();
        if (lower.Contains("ip address is blocked") || lower.Contains("not available in your country") || lower.Contains("geo"))
            return $"{link.Site} doesn't make this post available in your region.";
        if (lower.Contains("login") || lower.Contains("cookies") || lower.Contains("rate-limit") || lower.Contains("sign in"))
            return $"{link.Site} asked for a login for this post (private, age-restricted or rate-limited). Try again later.";
        if (lower.Contains("unsupported url"))
            return "This link isn't a video Peel can download.";
        if (lower.Contains("unavailable") || lower.Contains("removed") || lower.Contains("not found") || lower.Contains("404"))
            return "This video is unavailable or was removed.";
        if (lower.Contains("no video could be found") || lower.Contains("no video formats"))
            return "There's no video in this post.";
        if (SiteChanged(errors))
            return $"{link.Site} changed how its pages work and the downloader doesn't support it yet. Updates usually arrive within a day or two; Peel checks every day, so try again later.";
        // "ERROR: [youtube] abc123: message" → "message", without yt-dlp's "please report this issue" advice.
        var message = System.Text.RegularExpressions.Regex.Replace(line, @"^ERROR:\s*(\[[^\]]+\]\s*)?([\w-]+:\s*)?", "");
        var advice = message.IndexOf("; please report this issue", StringComparison.OrdinalIgnoreCase);
        if (advice > 0)
            message = message[..advice] + ".";
        return message.Length == 0 ? "The download failed." : message.Length > 180 ? message[..180] + "…" : message;
    }

    /// <summary>The signs of a site that changed under yt-dlp, which a newer yt-dlp usually handles.</summary>
    private static bool SiteChanged(string errors)
    {
        var lower = errors.ToLowerInvariant();
        return lower.Contains("unexpected response") || lower.Contains("unable to extract") || lower.Contains("please report this issue")
            || lower.Contains("extractor") || lower.Contains("unable to download webpage") || lower.Contains("requested format is not available");
    }

    private static async Task<(int Exit, string Errors)> RunAsync(string exe, IEnumerable<string> args, Action<string> onLine, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUTF8"] = "1";
        using var process = new Process { StartInfo = info };
        var errors = new StringBuilder();
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (dispatcher is null) onLine(e.Data);
            else dispatcher.BeginInvoke(onLine, e.Data);
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (errors) errors.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // yt-dlp starts FFmpeg for merging; cancelling ends the whole tree.
        using var registration = cancellation.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        await process.WaitForExitAsync(CancellationToken.None);
        // Let queued output lines reach their handler before the caller reads the results.
        if (dispatcher is not null)
            await dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        cancellation.ThrowIfCancellationRequested();
        lock (errors)
            return (process.ExitCode, errors.ToString());
    }
}
