using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FileAction.App.Shell;

/// <summary>A copied link to a video page Peel can download.</summary>
internal sealed record VideoLink(string Url, string Site);

internal static partial class VideoLinks
{
    private static readonly (Regex Pattern, string Site)[] Sites =
    [
        (YouTubeShorts(), "YouTube Shorts"),
        (YouTube(), "YouTube"),
        (Instagram(), "Instagram"),
        (TikTok(), "TikTok"),
        (X(), "X"),
    ];

    /// <summary>The link when the whole copied text is one supported video address.</summary>
    public static VideoLink? Match(string? text)
    {
        var url = text?.Trim();
        if (string.IsNullOrEmpty(url) || url.Length > 2048 || url.Any(char.IsWhiteSpace))
            return null;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        foreach (var (pattern, site) in Sites)
        {
            if (pattern.IsMatch(url))
                return new VideoLink(url, site);
        }
        return null;
    }

    private const string Host = @"^https?://(?:[a-z0-9-]+\.)*";

    [GeneratedRegex(Host + @"youtube\.com/shorts/[\w-]{6,}", RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeShorts();

    [GeneratedRegex(Host + @"(?:youtube\.com/(?:watch\?(?:[^#]*&)?v=|live/|embed/)[\w-]{6,}|youtu\.be/[\w-]{6,})", RegexOptions.IgnoreCase)]
    private static partial Regex YouTube();

    [GeneratedRegex(Host + @"instagram\.com/(?:[\w.]+/)?(?:reels?|p|tv)/[\w-]+", RegexOptions.IgnoreCase)]
    private static partial Regex Instagram();

    [GeneratedRegex(Host + @"(?:tiktok\.com/(?:@[\w.-]+/video/\d+|t/\w+)|(?:vm|vt)\.tiktok\.com/\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex TikTok();

    [GeneratedRegex(Host + @"(?:x|twitter)\.com/\w+/status/\d+", RegexOptions.IgnoreCase)]
    private static partial Regex X();
}

/// <summary>
/// Notices the moment a video link is copied. Windows announces every clipboard change
/// (no polling); only text that is a single supported link is acted on, and only locally.
/// </summary>
internal sealed class LinkWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private readonly HwndSource _source;
    private string? _last;
    private long _lastAt;

    public LinkWatcher()
    {
        // A message-only window: never shown, only receives the clipboard notices.
        _source = new HwndSource(new HwndSourceParameters("Peel clipboard") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _source.AddHook(Hook);
        AddClipboardFormatListener(_source.Handle);
    }

    public bool Enabled { get; set; } = true;

    public event Action<VideoLink>? Copied;

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_CLIPBOARDUPDATE)
        {
            // Read after the copying app has finished writing and closed the clipboard.
            _source.Dispatcher.BeginInvoke(DispatcherPriority.Background, Check);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void Check()
    {
        if (!Enabled || CopiedByPeel())
            return;
        string? text = null;
        for (var attempt = 0; attempt < 3 && text is null; attempt++)
        {
            try
            {
                if (!Clipboard.ContainsText())
                    return;
                text = Clipboard.GetText();
            }
            catch (COMException)
            {
                // Another app still holds the clipboard; try again in a moment.
                Thread.Sleep(40);
            }
        }
        if (VideoLinks.Match(text) is not { } link)
            return;
        // Copying the same link again soon after is not a new request.
        if (link.Url == _last && Environment.TickCount64 - _lastAt < 180_000)
            return;
        _last = link.Url;
        _lastAt = Environment.TickCount64;
        Copied?.Invoke(link);
    }

    private static bool CopiedByPeel()
    {
        var owner = GetClipboardOwner();
        return owner != IntPtr.Zero && GetWindowThreadProcessId(owner, out var process) != 0 && process == Environment.ProcessId;
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(Hook);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
}
