using System.Windows;
using System.Windows.Media;

namespace FileAction.App.UI;

/// <summary>
/// Peel's glass material, shared by the wheel and the notification cards.
/// One glass object lit from above: the compositor blurs what is behind it, a single
/// milky body tints that blur strongly enough for text to hold its contrast on any
/// wallpaper, and a soft sheen over the top supplies the gloss instead of bevels.
/// </summary>
internal sealed record GlassStyle(
    Brush Body, Brush BodyOpaque, Brush Pane, Brush PaneEdge, Brush Hub, Brush HubOpaque,
    Brush Sheen, Brush HubSheen, Brush Rim, Brush Ink, Brush Muted, Brush Track, Brush TrackEdge,
    Brush SelectedEdge, Brush Danger, Color Shadow, double ShadowOpacity, bool IsDark);

internal static class Glass
{
    public static readonly GlassStyle Light = new(
        Body: Vertical(("#8CF7FFFA", 0), ("#A3E9F3EE", 1)),
        BodyOpaque: Vertical(("#F2F7FCF9", 0), ("#F5EBF3EE", 1)),
        Pane: Solid(0x24, 0xFF, 0xFF, 0xFF),
        PaneEdge: Solid(0x8C, 0xFF, 0xFF, 0xFF),
        Hub: Vertical(("#BFFFFFFF", 0), ("#ADF0F7F3", 1)),
        HubOpaque: Vertical(("#FAFFFFFF", 0), ("#F7F1F7F3", 1)),
        // The sheen keeps a faint lower edge: a soft curved horizon, not a hard split.
        Sheen: Vertical(("#B8FFFFFF", 0), ("#38FFFFFF", .75), ("#24FFFFFF", 1)),
        HubSheen: Vertical(("#C7FFFFFF", 0), ("#2EFFFFFF", .8), ("#14FFFFFF", 1)),
        Rim: Vertical(("#F5FFFFFF", 0), ("#70FFFFFF", .55), ("#555C7A68", 1)),
        Ink: Solid(0xFF, 0x0F, 0x20, 0x17),
        Muted: Solid(0xFF, 0x3A, 0x4D, 0x42),
        Track: Solid(0x6B, 0xFF, 0xFF, 0xFF),
        TrackEdge: Solid(0xB8, 0xFF, 0xFF, 0xFF),
        SelectedEdge: Solid(0xD9, 0xFF, 0xFF, 0xFF),
        Danger: Solid(0xFF, 0xB4, 0x2B, 0x3A),
        Shadow: Color.FromRgb(0x1E, 0x3A, 0x2A), ShadowOpacity: 0.26, IsDark: false);

    public static readonly GlassStyle Dark = new(
        Body: Vertical(("#8C111C16", 0), ("#A60A1410", 1)),
        BodyOpaque: Vertical(("#F2141F19", 0), ("#F50D1612", 1)),
        Pane: Solid(0x10, 0xFF, 0xFF, 0xFF),
        PaneEdge: Solid(0x2B, 0xFF, 0xFF, 0xFF),
        Hub: Vertical(("#AD1C2A23", 0), ("#BD121D18", 1)),
        HubOpaque: Vertical(("#F51C2A23", 0), ("#F7121D18", 1)),
        Sheen: Vertical(("#4DFFFFFF", 0), ("#14FFFFFF", .75), ("#0DFFFFFF", 1)),
        HubSheen: Vertical(("#5CFFFFFF", 0), ("#14FFFFFF", .8), ("#08FFFFFF", 1)),
        Rim: Vertical(("#B8FFFFFF", 0), ("#38FFFFFF", .5), ("#1FFFFFFF", 1)),
        Ink: Solid(0xFF, 0xF3, 0xFA, 0xF5),
        Muted: Solid(0xFF, 0xB4, 0xC4, 0xBA),
        Track: Solid(0x14, 0xFF, 0xFF, 0xFF),
        TrackEdge: Solid(0x33, 0xFF, 0xFF, 0xFF),
        SelectedEdge: Solid(0xA6, 0xC6, 0xFF, 0xD4),
        Danger: Solid(0xFF, 0xFF, 0x86, 0x8F),
        Shadow: Colors.Black, ShadowOpacity: 0.42, IsDark: true);

    // The selection is almost opaque so a green wallpaper behind the glass can never
    // be mistaken for it; dark ink on bright green reads in both themes.
    public static readonly Color Green = Color.FromRgb(0x37, 0xF0, 0x62);
    public static readonly Brush SelectedFill = Vertical(("#F59BFCB3", 0), ("#F55CF07E", .45), ("#F526D157", 1));
    public static readonly Brush SelectedInk = Solid(0xFF, 0x0A, 0x2C, 0x15);
    public static readonly Brush SelectedMuted = Solid(0xFF, 0x1C, 0x52, 0x2E);

    /// <summary>The glass follows the Windows app theme; the self-test can force either.</summary>
    public static GlassStyle Current => UseDark() ? Dark : Light;

    private static bool UseDark()
    {
        if (Theme.Override is { } forced)
            return forced;
        try
        {
            return Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or System.IO.IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static SolidColorBrush Solid(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    /// <summary>Top-to-bottom, matching the single light source above Peel's glass.</summary>
    public static LinearGradientBrush Vertical(params (string Color, double Position)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        foreach (var stop in stops)
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(stop.Color), stop.Position));
        brush.Freeze();
        return brush;
    }
}
