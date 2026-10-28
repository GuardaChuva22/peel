using System.Windows;
using System.Windows.Media;

namespace FileAction.App.UI;

/// <summary>
/// Peel's shared mint-and-white palette. Floating surfaces use translucent whites;
/// explicit dark overrides remain available for diagnostics.
/// </summary>
internal static class Theme
{
    private static readonly Palette DarkPalette = new(
        Backdrop: Color.FromArgb(0xF7, 0x11, 0x15, 0x12),
        BackdropEdge: Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
        Slice: Color.FromArgb(0xFF, 0x20, 0x26, 0x21),
        SliceEdge: Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
        Hub: Color.FromArgb(0xFF, 0x17, 0x1B, 0x19),
        Panel: Color.FromArgb(0xFF, 0x17, 0x1B, 0x19),
        PanelEdge: Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF),
        Text: Color.FromRgb(0xF2, 0xF4, 0xEA),
        Muted: Color.FromRgb(0xA3, 0xAF, 0xA2),
        Track: Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF),
        Page: Color.FromRgb(0x17, 0x1B, 0x19),
        Card: Color.FromRgb(0x20, 0x26, 0x21),
        Hover: Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF),
        Shadow: 0.32);

    private static readonly Palette LightPalette = new(
        Backdrop: Color.FromArgb(0xE8, 0xF4, 0xFC, 0xF6),
        BackdropEdge: Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF),
        Slice: Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF),
        SliceEdge: Color.FromArgb(0x96, 0xFF, 0xFF, 0xFF),
        Hub: Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
        Panel: Color.FromArgb(0xF9, 0xFF, 0xFF, 0xFF),
        PanelEdge: Color.FromRgb(0xDE, 0xE7, 0xE1),
        Text: Color.FromRgb(0x16, 0x25, 0x1C),
        Muted: Color.FromRgb(0x68, 0x77, 0x6E),
        Track: Color.FromRgb(0xE8, 0xEE, 0xEA),
        Page: Color.FromRgb(0xF5, 0xF7, 0xF6),
        Card: Color.FromRgb(0xFF, 0xFF, 0xFF),
        Hover: Color.FromRgb(0xED, 0xFC, 0xF0),
        Shadow: 0.13);

    private sealed record Palette(
        Color Backdrop,
        Color BackdropEdge,
        Color Slice,
        Color SliceEdge,
        Color Hub,
        Color Panel,
        Color PanelEdge,
        Color Text,
        Color Muted,
        Color Track,
        Color Page,
        Color Card,
        Color Hover,
        double Shadow);

    private static Palette _palette = LightPalette;

    public static readonly Color AccentColor = Color.FromRgb(0x37, 0xF0, 0x62);
    public static readonly Color AccentColor2 = Color.FromRgb(0x6D, 0xF6, 0x8D);

    /// <summary>A restrained mint highlight for selected actions and progress.</summary>
    public static readonly LinearGradientBrush AccentGradient = Freeze(new LinearGradientBrush(AccentColor, AccentColor2, new Point(0, 0), new Point(1, 1)));

    public static readonly SolidColorBrush Accent = Freeze(new SolidColorBrush(AccentColor));
    public static readonly SolidColorBrush TextOnAccent = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x3D, 0x20)));
    public static readonly SolidColorBrush Error = Freeze(new SolidColorBrush(Color.FromRgb(0xBE, 0x3B, 0x46)));
    public static readonly SolidColorBrush Success = Freeze(new SolidColorBrush(Color.FromRgb(0x15, 0x81, 0x3A)));

    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");

    public static bool IsDark { get; private set; }

    public static Brush Backdrop => Solid(_palette.Backdrop);
    public static Brush BackdropEdge => Solid(_palette.BackdropEdge);
    public static Brush Slice => Solid(_palette.Slice);
    public static Brush SliceEdge => Solid(_palette.SliceEdge);
    public static Brush Hub => Solid(_palette.Hub);
    public static Brush Panel => Solid(_palette.Panel);
    public static Brush PanelBorder => Solid(_palette.PanelEdge);
    public static Brush Text => Solid(_palette.Text);
    public static Brush Muted => Solid(_palette.Muted);
    public static Brush Track => Solid(_palette.Track);
    public static Brush Page => Solid(_palette.Page);
    public static Brush Card => Solid(_palette.Card);
    public static Brush Hover => Solid(_palette.Hover);
    public static Brush AccentSoft => Solid(Color.FromArgb(IsDark ? (byte)0x38 : (byte)0x26, AccentColor.R, AccentColor.G, AccentColor.B));

    /// <summary>Soft drop shadow under floating surfaces.</summary>
    public static System.Windows.Media.Effects.DropShadowEffect Shadow(double blur = 24) =>
        new() { BlurRadius = blur, ShadowDepth = 5, Direction = 270, Opacity = _palette.Shadow, Color = Color.FromRgb(0x22, 0x51, 0x31) };

    /// <summary>Coloured glow for the highlighted slice.</summary>
    public static System.Windows.Media.Effects.DropShadowEffect Glow() =>
        new() { BlurRadius = 20, ShadowDepth = 0, Opacity = 0.28, Color = AccentColor };

    /// <summary>Forces dark (true) or light (false) instead of following Windows; used by the self-test.</summary>
    public static bool? Override { get; set; }

    /// <summary>Applies Peel's light identity unless explicitly overridden.</summary>
    public static void Refresh() => Use(Override ?? false);

    private static void Use(bool dark)
    {
        IsDark = dark;
        _palette = dark ? DarkPalette : LightPalette;
    }

    private static SolidColorBrush Solid(Color color) => Freeze(new SolidColorBrush(color));

    private static T Freeze<T>(T brush)
        where T : Freezable
    {
        brush.Freeze();
        return brush;
    }
}
