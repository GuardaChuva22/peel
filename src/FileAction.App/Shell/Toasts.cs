using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using FileAction.App.Native;
using FileAction.App.UI;
using static FileAction.Core.Loc;
using Path = System.Windows.Shapes.Path;

namespace FileAction.App.Shell;

/// <summary>
/// Peel's own notifications: glass cards stacked in the corner where Windows shows its
/// toasts. They never take focus, pause while hovered, can be swiped away and stay out
/// of the way of full-screen apps and presentations. One card follows a job from
/// progress to its result instead of a new notification for every step.
/// </summary>
internal static class Toasts
{
    private static ToastHost? _host;

    public static bool Animate => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    /// <summary>A short message. Safe to call from any thread.</summary>
    public static void Info(string title, string text, bool error = false)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Info(title, text, error));
            return;
        }
        if (Quiet())
            return;
        var host = Host();
        host.Add(new ToastCard(title, text, null, null, host.BlurActive, error ? ToastCard.Kind.Error : ToastCard.Kind.Info));
    }

    /// <summary>A card that follows one job. Call from the UI thread.</summary>
    public static ToastCard Job(string title, string detail, string? iconPath, Action cancel)
    {
        var host = Quiet() ? null : Host();
        var card = new ToastCard(title, detail, iconPath, cancel, host?.BlurActive ?? false, ToastCard.Kind.Job);
        // Suppressed cards still accept updates; they are just never shown.
        host?.Add(card);
        return card;
    }

    /// <summary>A card that offers choices (and may then follow the chosen job). Null while quiet.</summary>
    public static ToastCard? Link(string title, string detail)
    {
        if (Quiet())
            return null;
        var host = Host();
        var card = new ToastCard(title, detail, null, null, host.BlurActive, ToastCard.Kind.Link);
        host.Add(card);
        return card;
    }

    private static ToastHost Host()
    {
        _host ??= new ToastHost();
        _host.EnsureShown();
        return _host;
    }

    /// <summary>Full-screen games, video and presentations are never interrupted.</summary>
    private static bool Quiet()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}

/// <summary>Transparent, click-through window in the corner that holds the cards.</summary>
internal sealed class ToastHost : Window
{
    /// <summary>Room around the cards for their shadows.</summary>
    private const double Side = 28;
    /// <summary>Distance between a card and the screen edge, like Windows' own toasts.</summary>
    private const double Edge = 12;
    private const double Gap = 10;
    private const int MaxCards = 4;

    private readonly Canvas _canvas = new();
    private readonly List<ToastCard> _cards = [];
    private GlassBackdrop? _glass;
    private long _liveUntil;
    private bool _rendering;

    public ToastHost()
    {
        Title = "Peel · notifications";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        FontFamily = Theme.UiFont;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Width = ToastCard.CardWidth + 2 * Side;
        Content = _canvas;
        SourceInitialized += (_, _) => Win32.AddExStyle(Handle, Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
        Closed += (_, _) => { _glass?.Dispose(); _glass = null; };
    }

    public bool BlurActive { get; private set; }

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    public void EnsureShown()
    {
        if (IsVisible)
            return;
        var work = SystemParameters.WorkArea;
        Height = Math.Min(660, work.Height - 40);
        Left = work.Right - Edge - ToastCard.CardWidth - Side;
        Top = work.Bottom - Edge + Side - Height;
        Show();
        if (_glass is null && !SystemParameters.HighContrast)
        {
            try
            {
                _glass = new GlassBackdrop();
                if (!_glass.Supported) { _glass.Dispose(); _glass = null; }
            }
            catch (Exception ex) when (ex is COMException or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                _glass?.Dispose(); _glass = null; App.Log(ex);
            }
        }
        BlurActive = _glass is not null;
    }

    public void Add(ToastCard card)
    {
        var live = _cards.Where(c => !c.Leaving).ToList();
        if (live.Count >= MaxCards)
            (live.FirstOrDefault(c => !c.IsWorking) ?? live[0]).Dismiss();
        _cards.Add(card);
        Canvas.SetLeft(card, Side);
        _canvas.Children.Add(card);
        card.LeaveStarted += _ => Restack(animated: true);
        card.Dismissed += Remove;
        card.Moved += () => Kick(80);
        card.SizeChanged += (_, _) => Restack(animated: true);
        card.Measure(new Size(ToastCard.CardWidth, double.PositiveInfinity));
        Restack(animated: true, entering: card);
        card.Enter();
        Kick(700);
    }

    /// <summary>Newest at the bottom; older cards slide up to make room.</summary>
    private void Restack(bool animated, ToastCard? entering = null)
    {
        var y = Height - Side;
        for (var i = _cards.Count - 1; i >= 0; i--)
        {
            var card = _cards[i];
            if (card.Leaving)
                continue;
            var height = card.ActualHeight > 0 ? card.ActualHeight : card.DesiredSize.Height;
            y -= height;
            card.MoveTo(y, animated && card != entering && Toasts.Animate);
            y -= Gap;
        }
        Kick(450);
    }

    private void Remove(ToastCard card)
    {
        _cards.Remove(card);
        _canvas.Children.Remove(card);
        if (_cards.Count > 0)
        {
            Kick(120);
            return;
        }
        Hide();
        _glass?.Hide();
    }

    /// <summary>Keeps the blur under the cards in step while they move, then goes idle.</summary>
    private void Kick(int milliseconds)
    {
        if (_glass is null)
            return;
        _liveUntil = Math.Max(_liveUntil, Environment.TickCount64 + milliseconds);
        if (_rendering)
            return;
        _rendering = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        UpdatePanes();
        if (Environment.TickCount64 <= _liveUntil)
            return;
        CompositionTarget.Rendering -= OnRendering;
        _rendering = false;
    }

    private void UpdatePanes()
    {
        if (_glass is null || !IsVisible || ActualWidth <= 0 || !Win32.GetWindowRect(Handle, out var bounds))
            return;
        var scale = (bounds.Right - bounds.Left) / ActualWidth;
        var panes = new List<GlassBackdrop.Pane>(_cards.Count);
        foreach (var card in _cards)
        {
            if (card.ActualWidth <= 0 || card.Opacity <= 0.01)
                continue;
            var rect = card.TransformToAncestor(_canvas).TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
            panes.Add(new GlassBackdrop.Pane((float)(rect.X * scale), (float)(rect.Y * scale), (float)(rect.Width * scale), (float)(rect.Height * scale),
                (float)(ToastCard.Radius * scale * rect.Width / card.ActualWidth), (float)card.Opacity));
        }
        _glass.ShowPanes(Handle, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, panes);
    }
}

/// <summary>One glass notification card.</summary>
internal sealed class ToastCard : Grid
{
    public enum Kind { Info, Error, Job, Link }

    public const double CardWidth = 360;
    public const double Radius = 18;
    private static readonly Brush Amber = Glass.Solid(0xFF, 0xF5, 0xB8, 0x3D);
    private static readonly Lazy<ImageSource?> PeelIcon = new(() =>
    {
        try
        {
            var image = new BitmapImage(new Uri("pack://application:,,,/Assets/Peel.png"));
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    });

    private readonly GlassStyle _glass = Glass.Current;
    private readonly Grid _shadow = new() { Margin = new Thickness(-22, -14, -22, -30), IsHitTestVisible = false };
    private readonly Path _sheen = new() { IsHitTestVisible = false };
    private readonly TextBlock _title;
    private readonly TextBlock _detail;
    private readonly TextBlock _percent;
    private readonly Grid _progress = new() { Height = 6, Margin = new Thickness(0, 11, 0, 1) };
    private readonly Border _fill;
    private readonly TranslateTransform _sweep = new();
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Grid _badge = new() { Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -5, -5) };
    private readonly Border _close;
    private Action? _cancel;
    private readonly Image _iconImage = new() { Width = 28, Height = 28 };
    private readonly TranslateTransform _slide = new();
    private readonly ScaleTransform _pop = new(1, 1);
    private readonly TranslateTransform _stack = new();
    private DispatcherTimer? _timer;
    private RotateTransform? _spin;
    private Action? _primary;
    private double _value;
    private bool _placed;
    private Point _pressed;
    private bool _dragging;

    public event Action<ToastCard>? LeaveStarted;
    public event Action<ToastCard>? Dismissed;
    public event Action? Moved;

    public bool Leaving { get; private set; }
    public bool IsWorking { get; private set; }

    public ToastCard(string title, string detail, string? iconPath, Action? cancel, bool blur, Kind kind)
    {
        _cancel = cancel;
        Width = CardWidth;
        Opacity = Toasts.Animate ? 0 : 1;
        RenderTransformOrigin = new Point(1, 1);
        RenderTransform = new TransformGroup { Children = { _pop, _slide, _stack } };
        Cursor = Cursors.Arrow;

        // Shadow only outside the card, so it never darkens the glass from inside.
        var shadowColor = _glass.Shadow;
        _shadow.Children.Add(new Border
        {
            Margin = new Thickness(22, 20, 22, 24),
            CornerRadius = new CornerRadius(Radius),
            Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * Math.Min(1, _glass.ShadowOpacity * 1.15)), shadowColor.R, shadowColor.G, shadowColor.B)),
            Effect = new BlurEffect { Radius = 26, RenderingBias = RenderingBias.Performance },
        });
        Children.Add(_shadow);
        Children.Add(new Border { CornerRadius = new CornerRadius(Radius), Background = blur ? _glass.Body : _glass.BodyOpaque });
        _sheen.Fill = _glass.Sheen;
        Children.Add(_sheen);
        Children.Add(new Border { CornerRadius = new CornerRadius(Radius), BorderThickness = new Thickness(1), BorderBrush = _glass.Rim, IsHitTestVisible = false });
        SizeChanged += (_, _) => Reshape();

        _title = new TextBlock { Text = title, Foreground = _glass.Ink, FontFamily = Theme.DisplayFont, FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        _detail = new TextBlock { Text = detail, Foreground = _glass.Muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, MaxHeight = 34 };
        _percent = new TextBlock { Foreground = _glass.Muted, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };

        _progress.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = _glass.Track, BorderBrush = _glass.TrackEdge, BorderThickness = new Thickness(_glass.IsDark ? 0 : 0.5) });
        var shine = new Rectangle
        {
            Width = 70, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = new TranslateTransform(-70, 0),
            Fill = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5), GradientStops = { new GradientStop(Color.FromArgb(0, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0x8C, 255, 255, 255), 0.5), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) } },
        };
        _fill = new Border
        {
            CornerRadius = new CornerRadius(3), Background = Glass.SelectedFill, HorizontalAlignment = HorizontalAlignment.Left,
            Width = 64, ClipToBounds = true, RenderTransform = _sweep, Child = shine,
            Effect = new DropShadowEffect { Color = Glass.Green, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.55 },
        };
        _progress.Children.Add(_fill);
        _progress.Visibility = kind == Kind.Job ? Visibility.Visible : Visibility.Collapsed;
        _buttons.Visibility = Visibility.Collapsed;

        _close = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, -4, -4, 0), Opacity = 0, ToolTip = T("Zavřít", "Dismiss"),
            Child = new TextBlock { Text = "", FontFamily = Theme.Icons, FontSize = 9, Foreground = _glass.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        _close.MouseEnter += (_, _) => _close.Background = _glass.Track;
        _close.MouseLeave += (_, _) => _close.Background = Brushes.Transparent;
        _close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _close.MouseLeftButtonUp += (_, e) => { e.Handled = true; Dismiss(); };

        var heading = new DockPanel();
        DockPanel.SetDock(_close, Dock.Right);
        DockPanel.SetDock(_percent, Dock.Right);
        heading.Children.Add(_close);
        heading.Children.Add(_percent);
        heading.Children.Add(_title);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(heading);
        text.Children.Add(_detail);
        text.Children.Add(_progress);
        text.Children.Add(_buttons);

        var icon = new Grid { Width = 44, Height = 44, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = blur ? _glass.Hub : _glass.HubOpaque, BorderBrush = _glass.Rim, BorderThickness = new Thickness(1) });
        icon.Children.Add(new Border { CornerRadius = new CornerRadius(14, 14, 6, 6), Height = 20, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(1.5, 1.5, 1.5, 0), Background = _glass.HubSheen, IsHitTestVisible = false });
        var source = iconPath is null ? PeelIcon.Value : FileIcons.Get(iconPath) ?? PeelIcon.Value;
        _iconImage.Source = source;
        icon.Children.Add(_iconImage);
        icon.Children.Add(_badge);

        var row = new DockPanel { Margin = new Thickness(14, 14, 14, 14) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);
        Children.Add(row);

        MouseEnter += (_, _) => Fade(_close, 1, 140);
        MouseLeave += (_, _) => Fade(_close, 0, 200);
        MouseLeftButtonDown += OnPress;
        MouseMove += OnDrag;
        MouseLeftButtonUp += OnRelease;

        switch (kind)
        {
            case Kind.Job:
                IsWorking = true;
                ShowSpinner();
                AddButton(T("Zrušit", "Cancel"), "", primary: false, () => _cancel?.Invoke());
                StartSweep();
                break;
            case Kind.Link:
                StartTimer(TimeSpan.FromSeconds(12));
                break;
            case Kind.Error:
                ShowMark(_glass.Danger, "!");
                StartTimer(TimeSpan.FromSeconds(9));
                break;
            default:
                StartTimer(TimeSpan.FromSeconds(5));
                break;
        }
    }

    // ------------------------------------------------------------------ link offers

    /// <summary>The choices on a link card (MP4 / MP3).</summary>
    public void Offer(IReadOnlyList<(string Text, string Glyph, bool Primary, Action Click)> choices)
    {
        _buttons.Children.Clear();
        foreach (var (text, glyph, primary, click) in choices)
            AddButton(text, glyph, primary, click);
        _primary = null;
    }

    public void SetDetails(string title, string detail)
    {
        _title.Text = title;
        _detail.Text = detail;
    }

    /// <summary>Replaces the icon with a picture that fills the tile (a video thumbnail).</summary>
    public void SetThumbnail(ImageSource image)
    {
        _iconImage.Source = image;
        _iconImage.Width = _iconImage.Height = 44;
        _iconImage.Stretch = Stretch.UniformToFill;
        _iconImage.Clip = new RectangleGeometry(new Rect(0, 0, 44, 44), 14, 14);
        if (!Toasts.Animate)
            return;
        _iconImage.Opacity = 0;
        Fade(_iconImage, 1, 260);
    }

    /// <summary>The same card carries on as the job's progress card.</summary>
    public void BeginJob(string title, string detail, Action cancel)
    {
        if (Leaving)
            return;
        _timer?.Stop();
        _cancel = cancel;
        _primary = null;
        _value = 0;
        IsWorking = true;
        SetDetails(title, detail);
        _percent.Visibility = Visibility.Collapsed;
        _progress.BeginAnimation(OpacityProperty, null);
        _progress.Opacity = 1;
        _progress.Visibility = Visibility.Visible;
        _fill.BeginAnimation(WidthProperty, null);
        _fill.Width = 64;
        Offer([(T("Zrušit", "Cancel"), "", false, () => _cancel?.Invoke())]);
        ShowSpinner();
        StartSweep();
    }

    // ------------------------------------------------------------------ job states

    public void Report(double value)
    {
        if (!IsWorking || Leaving || value <= 0)
            return;
        if (_value <= 0)
        {
            _sweep.BeginAnimation(TranslateTransform.XProperty, null);
            _sweep.X = 0;
            _percent.Visibility = Visibility.Visible;
        }
        _value = Math.Clamp(value, 0, 1);
        _percent.Text = $"{Math.Round(_value * 100)}%";
        AnimateFill(_value, 260);
    }

    public void Complete(IReadOnlyList<string> outputs, string? warning, string? note = null)
    {
        IsWorking = false;
        StopMotion();
        if (Leaving)
            return;
        _percent.Text = "100%";
        _percent.Visibility = Visibility.Visible;
        AnimateFill(1, 220);
        if (warning is null)
            ShowCheck();
        else
            ShowMark(Amber, "!");
        var first = outputs.Count > 0 ? outputs[0] : null;
        _detail.Text = warning ?? (outputs.Count == 1 ? System.IO.Path.GetFileName(first) : T($"Uloženo {outputs.Count} výsledků", $"{outputs.Count} results saved"))
            + (warning is null && !string.IsNullOrWhiteSpace(note) ? " · " + note : "");
        var buttons = new List<(string, string, bool, Action)>();
        if (first is not null)
        {
            if (outputs.Count == 1 && File.Exists(first))
                buttons.Add((T("Otevřít", "Open"), "", true, () => { Open(first); Dismiss(); }));
            buttons.Add((T("Zobrazit ve složce", "Show in folder"), "", buttons.Count == 0, () => { Reveal(first); Dismiss(); }));
            _primary = buttons[0].Item4;
        }
        SwapToButtons(buttons);
        StartTimer(TimeSpan.FromSeconds(warning is null ? 7 : 12));
    }

    public void Fail(string message)
    {
        IsWorking = false;
        StopMotion();
        if (Leaving)
            return;
        ShowMark(_glass.Danger, "!");
        _percent.Visibility = Visibility.Collapsed;
        _detail.Text = message;
        _detail.Foreground = _glass.Danger;
        SwapToButtons([(T("Kopírovat chybu", "Copy error"), "", false, () =>
        {
            try { Clipboard.SetText(message); }
            catch (COMException) { }
        })]);
    }

    public void Cancelled()
    {
        IsWorking = false;
        StopMotion();
        Dismiss();
    }

    /// <summary>Ends the looping spinner and shine so a finished card costs nothing.</summary>
    private void StopMotion()
    {
        _spin?.BeginAnimation(RotateTransform.AngleProperty, null);
        _sweep.BeginAnimation(TranslateTransform.XProperty, null);
        _sweep.X = 0;
        ((TranslateTransform)((Rectangle)_fill.Child).RenderTransform).BeginAnimation(TranslateTransform.XProperty, null);
    }

    private void SwapToButtons(IReadOnlyList<(string Text, string Glyph, bool Primary, Action Click)> buttons)
    {
        void Show()
        {
            _progress.Visibility = Visibility.Collapsed;
            _buttons.Children.Clear();
            foreach (var (text, glyph, primary, click) in buttons)
                AddButton(text, glyph, primary, click);
            _buttons.Visibility = _buttons.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!Toasts.Animate)
                return;
            var rise = new TranslateTransform(0, 6);
            _buttons.RenderTransform = rise;
            _buttons.Opacity = 0;
            Fade(_buttons, 1, 220);
            rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, Ms(260)) { EasingFunction = Out });
        }
        if (!Toasts.Animate)
        {
            Show();
            return;
        }
        // Let the bar reach the end, then trade it for the result's actions.
        var fade = new DoubleAnimation(0, Ms(160)) { BeginTime = Ms(260) };
        fade.Completed += (_, _) => { _progress.BeginAnimation(OpacityProperty, null); _progress.Opacity = 1; Show(); };
        _buttons.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Ms(120)));
        _progress.BeginAnimation(OpacityProperty, fade);
    }

    // ------------------------------------------------------------------ pieces

    private void AddButton(string text, string glyph, bool primary, Action click)
    {
        var ink = primary ? Glass.SelectedInk : _glass.Ink;
        var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = glyph, FontFamily = Theme.Icons, FontSize = 11, Foreground = ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0) });
        label.Children.Add(new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ink, VerticalAlignment = VerticalAlignment.Center });
        var press = new ScaleTransform(1, 1);
        var pill = new Border
        {
            Height = 30, CornerRadius = new CornerRadius(15), Padding = new Thickness(13, 0, 15, 0), Margin = new Thickness(0, 0, 8, 0),
            Background = primary ? Glass.SelectedFill : _glass.Track, BorderBrush = primary ? _glass.SelectedEdge : _glass.TrackEdge,
            BorderThickness = new Thickness(1), Child = label, Cursor = Cursors.Hand,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = press,
        };
        var glow = primary ? new DropShadowEffect { Color = Glass.Green, BlurRadius = 14, ShadowDepth = 0, Opacity = 0 } : null;
        pill.Effect = glow;
        pill.MouseEnter += (_, _) =>
        {
            if (glow is not null) glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0.55, Ms(160)));
            else pill.Background = _glass.Hub;
        };
        pill.MouseLeave += (_, _) =>
        {
            if (glow is not null) glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0, Ms(200)));
            else pill.Background = _glass.Track;
            Squeeze(press, 1);
        };
        pill.MouseLeftButtonDown += (_, e) => { e.Handled = true; Squeeze(press, 0.95); };
        pill.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Squeeze(press, 1);
            if (pill.IsMouseOver) click();
        };
        _buttons.Children.Add(pill);
        _buttons.Visibility = Visibility.Visible;
    }

    private void ShowSpinner()
    {
        _badge.Children.Clear();
        _badge.Children.Add(new Ellipse { Fill = _glass.HubOpaque, Stroke = _glass.Rim, StrokeThickness = 1 });
        var turn = _spin = new RotateTransform(0, 10, 10);
        _badge.Children.Add(new Path
        {
            Data = Geometry.Parse("M 10,4.5 A 5.5,5.5 0 1 1 4.5,10"),
            Stroke = new SolidColorBrush(Glass.Green), StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            RenderTransform = turn,
        });
        if (Toasts.Animate)
            turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, Ms(900)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void ShowCheck()
    {
        _badge.Children.Clear();
        _badge.Children.Add(new Ellipse { Fill = Glass.SelectedFill, Stroke = _glass.SelectedEdge, StrokeThickness = 1, Effect = new DropShadowEffect { Color = Glass.Green, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.6 } });
        var check = new Path
        {
            Data = Geometry.Parse("M 5.8,10.4 L 8.8,13.3 L 14.4,7.2"),
            Stroke = Glass.SelectedInk, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            StrokeDashArray = new DoubleCollection { 7, 7 }, StrokeDashOffset = Toasts.Animate ? 7 : 0,
        };
        _badge.Children.Add(check);
        if (!Toasts.Animate)
            return;
        check.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(7, 0, Ms(320)) { BeginTime = Ms(120), EasingFunction = Out });
        Pop(_badge);
    }

    private void ShowMark(Brush fill, string mark)
    {
        _badge.Children.Clear();
        _badge.Children.Add(new Ellipse { Fill = fill, Stroke = _glass.SelectedEdge, StrokeThickness = 1 });
        _badge.Children.Add(new TextBlock { Text = mark, FontWeight = FontWeights.Bold, FontSize = 12, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) });
        if (Toasts.Animate)
            Pop(_badge);
    }

    private static void Pop(FrameworkElement element)
    {
        var scale = new ScaleTransform(0.5, 0.5);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = scale;
        var grow = new DoubleAnimation(0.5, 1, Ms(380)) { EasingFunction = new BackEase { Amplitude = 0.55, EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void StartSweep()
    {
        if (!Toasts.Animate)
            return;
        // Indeterminate: a short glossy segment glides until real progress arrives.
        _sweep.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 210, Ms(950)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        var shine = (TranslateTransform)((Rectangle)_fill.Child).RenderTransform;
        shine.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-70, 300, Ms(1500)) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = Ms(300) });
    }

    private void AnimateFill(double value, int milliseconds)
    {
        var width = Math.Max(6, (CardWidth - 100) * value);
        if (_progress.ActualWidth > 0)
            width = Math.Max(6, _progress.ActualWidth * value);
        if (!Toasts.Animate)
        {
            _fill.Width = width;
            return;
        }
        _fill.BeginAnimation(WidthProperty, new DoubleAnimation(width, Ms(milliseconds)) { EasingFunction = Out }, HandoffBehavior.SnapshotAndReplace);
    }

    private void Reshape()
    {
        var size = new Rect(0, 0, ActualWidth, ActualHeight);
        if (size.Width <= 0 || size.Height <= 0)
            return;
        var card = new RectangleGeometry(size, Radius, Radius);
        // Gloss over the top: a wide ellipse whose lower edge curves gently across the card.
        var curve = new EllipseGeometry(new Point(size.Width / 2, -size.Height * 1.1), size.Width * 0.95, size.Height * 1.1 + Math.Min(46, size.Height * 0.5));
        _sheen.Data = new CombinedGeometry(GeometryCombineMode.Intersect, new RectangleGeometry(new Rect(1, 1, size.Width - 2, size.Height - 2), Radius - 1, Radius - 1), curve);
        var room = new Rect(0, 0, size.Width + 44, size.Height + 44);
        _shadow.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(room), new RectangleGeometry(new Rect(22, 14, size.Width, size.Height), Radius, Radius));
    }

    // ------------------------------------------------------------------ motion

    public void Enter()
    {
        if (!Toasts.Animate)
            return;
        // Slides in from the screen edge and settles with a slight overshoot.
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(56, 0, Ms(460)) { EasingFunction = new BackEase { Amplitude = 0.32, EasingMode = EasingMode.EaseOut } });
        var grow = new DoubleAnimation(0.96, 1, Ms(360)) { EasingFunction = Out };
        _pop.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _pop.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(240)) { EasingFunction = Out });
    }

    public void MoveTo(double y, bool animated)
    {
        if (!_placed || !animated)
        {
            _stack.BeginAnimation(TranslateTransform.YProperty, null);
            _stack.Y = y;
            _placed = true;
            return;
        }
        if (Math.Abs(_stack.Y - y) < 0.5)
            return;
        _stack.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, Ms(340)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }, HandoffBehavior.SnapshotAndReplace);
    }

    public void Dismiss()
    {
        if (Leaving)
            return;
        Leaving = true;
        _timer?.Stop();
        if (!IsWorking)
            StopMotion();
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        LeaveStarted?.Invoke(this);
        if (!Toasts.Animate)
        {
            Dismissed?.Invoke(this);
            return;
        }
        var from = _slide.X;
        var duration = Ms(_dragging ? 180 : 230);
        var exit = new DoubleAnimation(from, from + 90, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        exit.Completed += (_, _) => Dismissed?.Invoke(this);
        _slide.BeginAnimation(TranslateTransform.XProperty, exit);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, duration));
        Moved?.Invoke();
    }

    private void StartTimer(TimeSpan delay)
    {
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = delay };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            // Hovering keeps the card; it leaves a moment after the pointer does.
            if (IsMouseOver || _dragging)
                StartTimer(TimeSpan.FromSeconds(2));
            else
                Dismiss();
        };
        _timer.Start();
    }

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        if (Leaving)
            return;
        _pressed = e.GetPosition(Parent as IInputElement);
        _dragging = false;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnDrag(object sender, MouseEventArgs e)
    {
        if (!IsMouseCaptured || Leaving)
            return;
        var dx = e.GetPosition(Parent as IInputElement).X - _pressed.X;
        if (!_dragging && Math.Abs(dx) < 4)
            return;
        _dragging = true;
        _slide.BeginAnimation(TranslateTransform.XProperty, null);
        BeginAnimation(OpacityProperty, null);
        // Swiping right follows the pointer; the other way resists.
        _slide.X = dx > 0 ? dx : dx * 0.2;
        Opacity = 1 - Math.Clamp(dx / 280, 0, 0.7);
        Moved?.Invoke();
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured)
            return;
        ReleaseMouseCapture();
        e.Handled = true;
        if (Leaving)
            return;
        if (!_dragging)
        {
            if (_primary is { } primary) primary();
            else if (!IsWorking) Dismiss();
            return;
        }
        if (_slide.X > 90)
        {
            Dismiss();
            _dragging = false;
            return;
        }
        _dragging = false;
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, Ms(380)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } });
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, Ms(200)));
        Moved?.Invoke();
    }

    private static void Fade(UIElement element, double to, int milliseconds)
    {
        if (!Toasts.Animate)
        {
            element.Opacity = to;
            return;
        }
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, Ms(milliseconds)), HandoffBehavior.SnapshotAndReplace);
    }

    private static void Squeeze(ScaleTransform scale, double to)
    {
        var animation = new DoubleAnimation(to, Ms(110)) { EasingFunction = Out };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    public static void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
