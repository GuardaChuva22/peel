using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using FileAction.App.Native;
using FileAction.App.UI;
using FileAction.Core;
using static FileAction.Core.Loc;

namespace FileAction.App.Wheel;

internal enum WheelMode
{
    /// <summary>Opened mid-drag: the user drops the file on a slice. Never takes focus.</summary>
    Drag,

    /// <summary>Opened from the context menu or hotkey: click a slice or use the keyboard.</summary>
    Click,
}

/// <summary>The radial menu. One instance per appearance.</summary>
internal sealed class WheelWindow : Window
{
    public delegate IReadOnlyList<Operation> OperationSource(WheelKind wheel, IReadOnlyList<FileItem> items);

    private const double OuterRadius = 180;
    private const double InnerRadius = 78;
    private const double Rim = 9;
    private const double Gap = 6;
    private const double Corner = 11;
    private const double PopOut = 6;

    /// <summary>Room around the disc for its shadow.</summary>
    private const double Pad = 26;
    private const double ShadowSpread = 19;
    private const double ShadowDrop = 6;
    private const double Size = 2 * (OuterRadius + Rim + Pad);
    private static readonly WheelKind[] WheelOrder = [WheelKind.Convert, WheelKind.Tools, WheelKind.Actions];

    // Peel's shared glass material (UI/Glass.cs).
    private static readonly Color Green = Glass.Green;
    private static readonly Brush SelectedFill = Glass.SelectedFill;
    private static readonly Brush SelectedInk = Glass.SelectedInk;
    private static readonly Brush SelectedMuted = Glass.SelectedMuted;

    private readonly GlassStyle _glass;
    private Brush WheelText => SystemParameters.HighContrast ? SystemColors.WindowTextBrush : _glass.Ink;
    private Brush WheelMuted => SystemParameters.HighContrast ? SystemColors.WindowTextBrush : _glass.Muted;
    private static Brush EdgeBrush(Brush glass) => SystemParameters.HighContrast ? SystemColors.WindowTextBrush : glass;
    private static Brush WheelSurface(Brush glass) => SystemParameters.HighContrast ? SystemColors.WindowBrush : glass;
    private static bool Animate => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(160);

    private readonly AppSettings _settings;
    private readonly OperationSource _source;
    private readonly Action<Operation, IReadOnlyList<FileItem>> _execute;
    private readonly Canvas _canvas = new() { Width = Size, Height = Size };
    private readonly List<Path> _slices = [];
    private readonly List<StackPanel> _labels = [];
    private readonly List<Canvas> _groups = [];
    private readonly Image _icon = new() { Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBlock _iconGlyph = new() { FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _title = HubText(15, FontWeights.SemiBold);
    private readonly TextBlock _detail = HubText(11.5, FontWeights.Normal);
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly Border _tabTrack = new() { CornerRadius = new CornerRadius(11), Padding = new Thickness(2), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _hubContent = new() { Width = InnerRadius * 1.75 };

    private IReadOnlyList<FileItem> _items;
    private IReadOnlyList<Operation> _ops = [];
    private IReadOnlyList<Operation> _allOps = [];
    private const int PageSize = 8;
    private int _page;
    private int PageCount => Math.Max(1, (_allOps.Count + PageSize - 1) / PageSize);
    private readonly StackPanel _pager = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) };
    private Border? _previousPage;
    private Border? _nextPage;
    private DispatcherTimer? _pageHoverTimer;
    private int _hoverPage;
    private bool _hoverUsed;
    private WheelKind _wheel;
    private int _highlight = -1;
    private Win32.POINT _anchor;
    private bool _finished;
    private bool _repeat;
    private DispatcherTimer? _closeTimer;
    private DispatcherTimer? _probeTimer;
    private bool _probing;
    private GlassBackdrop? _nativeGlass;
    internal bool NativeBlurActive { get; private set; }
    // Without the compositor blur the desktop would show sharply through the glass
    // and fight the labels, so the fallback body is nearly opaque.
    private Brush BodyMaterial => NativeBlurActive ? _glass.Body : _glass.BodyOpaque;
    private Brush HubMaterial => NativeBlurActive ? _glass.Hub : _glass.HubOpaque;

    public WheelWindow(
        WheelMode mode,
        WheelKind wheel,
        IReadOnlyList<FileItem>? items,
        AppSettings settings,
        OperationSource source,
        Action<Operation, IReadOnlyList<FileItem>> execute)
    {
        Mode = mode;
        _wheel = wheel;
        _items = items ?? [];
        _settings = settings;
        _source = source;
        _execute = execute;
        _glass = Glass.Current;

        Title = "Peel";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = mode == WheelMode.Click;
        AllowDrop = true;
        Width = Size;
        Height = Size;
        Left = -32000;
        Top = -32000;
        Opacity = 0;
        FontFamily = Theme.UiFont;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Focusable = true;
        Content = _canvas;
        _canvas.RenderTransformOrigin = new Point(0.5, 0.5);
        _canvas.RenderTransform = new ScaleTransform(0.9, 0.9);
        _tabTrack.Child = _tabs;
        _iconGlyph.FontFamily = Theme.Icons;
        _hubContent.Children.Add(_icon);
        _hubContent.Children.Add(_iconGlyph);
        _hubContent.Children.Add(_title);
        _hubContent.Children.Add(_detail);
        _hubContent.Children.Add(_tabTrack);
        _hubContent.Children.Add(_pager);

        SourceInitialized += (_, _) =>
        {
            var flags = Win32.WS_EX_TOOLWINDOW | (mode == WheelMode.Drag ? Win32.WS_EX_NOACTIVATE : 0);
            Win32.AddExStyle(Handle, flags);
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(PositionAtAnchor);
        Closed += (_, _) => { _nativeGlass?.Dispose();_nativeGlass=null;NativeBlurActive=false; };

        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, e) =>
        {
            UpdatePageHover(e.GetPosition(_canvas));
            SetHighlight(-1);
            e.Handled = true;
        };
        Drop += OnDrop;
        MouseMove += (sender, e) =>
        {
            if (Mode == WheelMode.Click)
                SetHighlight(HitTest(e.GetPosition(_canvas), out bool _));
        };
        MouseLeftButtonUp += OnClick;
        MouseWheel += (_, e) =>
        {
            if (Mode == WheelMode.Click && PageCount > 1)
            {
                ChangePage(e.Delta > 0 ? -1 : 1);
                e.Handled = true;
            }
        };
        KeyDown += OnKeyDown;
        Deactivated += (_, _) =>
        {
            if (Mode == WheelMode.Click)
                Finish();
        };

        LoadOperations();
        Build();
    }

    public WheelMode Mode { get; }

    public WheelKind CurrentWheel => _wheel;

    /// <summary>The last operation if it fits these items (for the "repeat last" view).</summary>
    public Func<IReadOnlyList<FileItem>, Operation?>? LastOperation { get; init; }

    /// <summary>Runs the last operation again with the values it used last time.</summary>
    public Action<IReadOnlyList<FileItem>>? RunRepeat { get; init; }

    public bool IsFinished => _finished;

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <param name="probe">
    /// The mouse is held and moving, but nothing proves a file is being dragged (text selection,
    /// a rubber band, a game). The wheel waits invisibly and appears only when a drag Peel can read
    /// reaches it; otherwise it closes without ever being seen.
    /// </param>
    public void ShowAt(Win32.POINT screenPoint, bool probe = false)
    {
        _anchor = screenPoint;
        Show();
        PositionAtAnchor();
        if (probe && Mode == WheelMode.Drag)
        {
            _probing = true;
            // Barely above zero alpha: invisible, yet still a hit-testable drop target.
            Opacity = 0.012;
            _probeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Normal, (_, _) => { if (_probing) Finish(); }, Dispatcher);
            _probeTimer.Start();
            return;
        }
        Appear();
    }

    /// <summary>True while the wheel waits invisibly for a real drag.</summary>
    public bool IsProbing => _probing;

    private void Appear()
    {
        _probing = false;
        _probeTimer?.Stop();
        EnableNativeGlass();
        Build();
        var scale = (ScaleTransform)_canvas.RenderTransform;
        if (Animate)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = OpenDuration;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });
        }
        else
        {
            Opacity = 1;
            scale.ScaleX = scale.ScaleY = 1;
        }
        AnimateSlicesIn();

        if (Mode == WheelMode.Click)
        {
            ForegroundHelper.Force(this);
            Keyboard.Focus(this);
            if (_ops.Count > 0)
                SetHighlight(0);
        }
    }

    private void PositionAtAnchor()
    {
        if (_finished || !Win32.GetWindowRect(Handle, out var rect))
            return;
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        var work = Win32.WorkAreaAt(_anchor);
        var x = Math.Clamp(_anchor.X - w / 2, work.Left, Math.Max(work.Left, work.Right - w));
        var y = Math.Clamp(_anchor.Y - h / 2, work.Top, Math.Max(work.Top, work.Bottom - h));
        Win32.SetWindowPos(Handle, new IntPtr(-1), x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        PositionNativeGlass();
    }

    private void EnableNativeGlass()
    {
        if(SystemParameters.HighContrast)return;
        try
        {
            // No owner: an owned native window would be forced above the WPF controls.
            _nativeGlass ??= new GlassBackdrop();
            NativeBlurActive=_nativeGlass.Supported;
            if(NativeBlurActive)PositionNativeGlass();
            else{_nativeGlass.Dispose();_nativeGlass=null;}
        }
        catch(Exception ex) when(ex is System.Runtime.InteropServices.COMException or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        { _nativeGlass?.Dispose();_nativeGlass=null;NativeBlurActive=false;App.Log(ex); }
    }

    private void PositionNativeGlass()
    {
        if(_nativeGlass is null||!NativeBlurActive||_finished||!Win32.GetWindowRect(Handle,out var bounds))return;
        var scale=(bounds.Right-bounds.Left)/Size;
        _nativeGlass.ShowBehind(Handle,bounds.Left+(int)Math.Round(Pad*scale),bounds.Top+(int)Math.Round(Pad*scale),(int)Math.Round(2*(OuterRadius+Rim)*scale),
            reveal:Animate?OpenDuration:null);
    }

    /// <summary>True when a physical screen point lies on the wheel.</summary>
    public bool Contains(Win32.POINT p)
    {
        if (!Win32.GetWindowRect(Handle, out var rect))
            return false;
        var scale = (rect.Right - rect.Left) / Size;
        var cx = (rect.Left + rect.Right) / 2.0;
        var cy = (rect.Top + rect.Bottom) / 2.0;
        var radius = (OuterRadius + Rim) * scale;
        return Math.Pow(p.X - cx, 2) + Math.Pow(p.Y - cy, 2) <= radius * radius;
    }

    public void SwitchTo(WheelKind wheel)
    {
        if (_finished)
            return;
        _wheel = wheel;
        _repeat = false;
        LoadOperations();
        Build();
        _canvas.UpdateLayout();
        AnimateSlicesIn();
        if (Mode == WheelMode.Click && _ops.Count > 0)
            SetHighlight(0);
    }

    /// <summary>Tapping the same key again shows a single slice that repeats the last action.</summary>
    public void ToggleRepeat()
    {
        if (_finished)
            return;
        if (_repeat)
        {
            SwitchTo(_wheel);
            return;
        }
        _repeat = true;
        LoadOperations();
        Build();
        AnimateSlicesIn();
        if (Mode == WheelMode.Click && _ops.Count > 0)
            SetHighlight(0);
    }

    /// <summary>The drag ended somewhere: close unless the drop landed on us.</summary>
    public void OnDragEnded()
    {
        if (Mode != WheelMode.Drag || _finished)
            return;
        if (_probing)
        {
            // It was never a drag of anything: leave at once so no click lands on us.
            Finish();
            return;
        }
        _closeTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Normal, (_, _) => Finish(), Dispatcher);
        _closeTimer.Start();
    }

    public void Finish()
    {
        if (_finished)
            return;
        _finished = true;
        _nativeGlass?.Dispose();_nativeGlass=null;NativeBlurActive=false;
        _closeTimer?.Stop();
        _probeTimer?.Stop();
        _pageHoverTimer?.Stop();
        Close();
    }

    /// <summary>Renders the wheel to a PNG without showing it (used by --self-test).</summary>
    public void RenderPreview(string file, int highlight, bool transparencyComparisons = false)
    {
        _canvas.RenderTransform = Transform.Identity;
        foreach (var group in _groups)
        {
            // Offscreen rendering doesn't advance animations; show the final state.
            group.BeginAnimation(OpacityProperty, null);
            var scale = (ScaleTransform)((TransformGroup)group.RenderTransform).Children[0];
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
        if (highlight >= 0 && highlight < _ops.Count)
            SetHighlight(highlight);
        Content = null;
        var root = new Grid { Background = SelfTest.Desktop(), Width = Size, Height = Size };
        root.Children.Add(_canvas);
        _canvas.HorizontalAlignment = HorizontalAlignment.Center;
        _canvas.VerticalAlignment = VerticalAlignment.Center;
        SelfTest.SaveElement(root, file);
        if (transparencyComparisons || System.IO.Path.GetFileNameWithoutExtension(file).Contains("wheel-image-convert", StringComparison.OrdinalIgnoreCase))
        {
            root.Children.Remove(_canvas);
            var transparent = new Grid { Width = Size, Height = Size, Background = Brushes.Transparent };
            transparent.Children.Add(_canvas);
            SelfTest.SaveElement(transparent, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file) ?? ".",
                System.IO.Path.GetFileNameWithoutExtension(file) + "-alpha.png"));
            transparent.Children.Remove(_canvas);
            RenderTransparencyComparison(file, "dark", dark: true);
            RenderTransparencyComparison(file, "light", dark: false);
        }
    }

    private void RenderTransparencyComparison(string file, string name, bool dark)
    {
        var backdrop = TransparencyPattern(dark);
        var comparison = new Grid { Width = Size * 2, Height = Size };
        comparison.ColumnDefinitions.Add(new ColumnDefinition());
        comparison.ColumnDefinitions.Add(new ColumnDefinition());
        var before = new Grid { Width = Size, Height = Size, Background = backdrop };
        var after = new Grid { Width = Size, Height = Size, Background = backdrop };
        Grid.SetColumn(after, 1);
        comparison.Children.Add(before);
        comparison.Children.Add(after);
        after.Children.Add(_canvas);
        var directory = System.IO.Path.GetDirectoryName(file) ?? ".";
        var stem = System.IO.Path.GetFileNameWithoutExtension(file);
        SelfTest.SaveElement(comparison, System.IO.Path.Combine(directory, stem + "-transparency-" + name + ".png"));
        after.Children.Remove(_canvas);
    }

    private static DrawingBrush TransparencyPattern(bool dark)
    {
        var drawing = new DrawingGroup();
        var background = dark ? Color.FromRgb(0x08, 0x12, 0x0C) : Color.FromRgb(0xE4, 0xEE, 0xE8);
        var line = dark ? Color.FromRgb(0x34, 0x60, 0x45) : Color.FromRgb(0x89, 0xA3, 0x92);
        var stripe = dark ? Color.FromRgb(0x18, 0x32, 0x23) : Color.FromRgb(0xC6, 0xD8, 0xCD);
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(background), null, new RectangleGeometry(new Rect(0, 0, Size, Size))));
        // Repeated desktop-like lines cross text, slices and the hub, exposing any
        // accidental opaque layer immediately in both high- and low-luminance scenes.
        for (var i = 0; i < 9; i++)
        {
            drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(stripe), null,
                new RectangleGeometry(new Rect(0, i * 48 + 12, Size, 16))));
            drawing.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(line), 2),
                new LineGeometry(new Point(i * 48 + 12, 0), new Point(i * 48 + 12, Size))));
        }
        var brush = new DrawingBrush(drawing) { Stretch = Stretch.None, Viewbox = new Rect(0, 0, Size, Size), ViewboxUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }

    // ------------------------------------------------------------------ data

    private void LoadOperations()
    {
        _allOps = _items.Count == 0 ? []
            : _repeat ? (LastOperation?.Invoke(_items) is { } last ? [last] : [])
            : _source(_wheel, _items);
        _page = 0;
        _ops = _allOps.Take(PageSize).ToArray();
        _highlight = -1;
        _pageHoverTimer?.Stop();
        _hoverPage = 0;
        _hoverUsed = false;
    }

    private void ChangePage(int delta)
    {
        if (_finished || PageCount <= 1) return;
        _page = (_page + delta + PageCount) % PageCount;
        _ops = _allOps.Skip(_page * PageSize).Take(PageSize).ToArray();
        _highlight = -1;
        Build();
        _canvas.UpdateLayout();
        AnimateSlicesIn();
        if (Mode == WheelMode.Click && _ops.Count > 0) SetHighlight(0);
    }

    private int PageArrowAt(Point position)
    {
        if (PageCount <= 1) return 0;
        foreach (var (button, direction) in new[] { (_previousPage, -1), (_nextPage, 1) })
        {
            if (button is null || button.ActualWidth <= 0) continue;
            var origin = button.TranslatePoint(new Point(), _canvas);
            if (new Rect(origin, new System.Windows.Size(button.ActualWidth, button.ActualHeight)).Contains(position)) return direction;
        }
        return 0;
    }

    private void UpdatePageHover(Point position)
    {
        if (Mode != WheelMode.Drag) return;
        var direction = PageArrowAt(position);
        if (direction != _hoverPage)
        {
            _pageHoverTimer?.Stop();
            _hoverPage = direction;
            _hoverUsed = false;
        }
        if (direction == 0 || _hoverUsed) return;
        _pageHoverTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        if (_pageHoverTimer.IsEnabled) return;
        // Require leaving the arrow before it can turn another page.
        _pageHoverTimer.Tick -= OnPageHover;
        _pageHoverTimer.Tick += OnPageHover;
        _pageHoverTimer.Start();
    }

    private void OnPageHover(object? sender, EventArgs e)
    {
        _pageHoverTimer?.Stop();
        if (_finished || _hoverPage == 0 || _hoverUsed) return;
        _hoverUsed = true;
        ChangePage(_hoverPage);
    }


    // ------------------------------------------------------------------ drag & drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_items.Count == 0)
        {
            var preview = DropContent.Read(e.Data, materialize: false);
            if (preview is null)
            {
                e.Effects = DragDropEffects.None;
                return;
            }
            _items = preview.Items;
            LoadOperations();
            if (_probing)
                Appear(); // a real drag of something Peel can use: now the wheel shows
            else
            {
                Build();
                AnimateSlicesIn();
            }
        }
        _closeTimer?.Stop();
        var position = e.GetPosition(_canvas);
        UpdatePageHover(position);
        var index = HitTest(position, out _);
        SetHighlight(index);
        e.Effects = index >= 0 ? SafeEffect(e.AllowedEffects) : DragDropEffects.None;
    }

    /// <summary>
    /// Copy keeps the original untouched. Never report Move: the drag source would delete the file.
    /// </summary>
    private static DragDropEffects SafeEffect(DragDropEffects allowed) =>
        (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
        : (allowed & DragDropEffects.Link) != 0 ? DragDropEffects.Link
        : DragDropEffects.None;

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var index = HitTest(e.GetPosition(_canvas), out _);
        var op = index >= 0 && index < _ops.Count ? _ops[index] : null;
        e.Effects = op is null ? DragDropEffects.None : SafeEffect(e.AllowedEffects);
        // Virtual files and images can only be read while the drop is in progress.
        var content = op is null ? null : DropContent.Read(e.Data, materialize: true);
        Finish();
        if (op is not null && content is { Items.Count: > 0 })
        {
            // Return to the drag source right away; do the work afterwards.
            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    if (content.Prepare is not null)
                        await content.Prepare();
                }
                catch (Exception ex)
                {
                    Shell.Notifications.Show(T("Stažení se nepovedlo", "Download failed"), ex.Message, error: true);
                    return;
                }
                Dispatch(op, content.Items);
            });
        }
    }

    // ------------------------------------------------------------------ mouse & keyboard (click mode)

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (Mode != WheelMode.Click || e.Handled)
            return;
        var index = HitTest(e.GetPosition(_canvas), out var inHub);
        if (index >= 0)
            Run(index);
        else if (inHub)
            Finish();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        switch (key)
        {
            case Key.Escape:
                Finish();
                break;
            case Key.Left or Key.Up:
                Move(-1);
                break;
            case Key.Right or Key.Down:
                Move(1);
                break;
            case Key.Enter or Key.Space:
                if (_highlight >= 0)
                    Run(_highlight);
                break;
            case Key.Tab:
                var next = (Array.IndexOf(WheelOrder, _wheel) + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 2 : 1)) % WheelOrder.Length;
                SwitchTo(WheelOrder[next]);
                break;
            case Key.PageUp:
                ChangePage(-1);
                break;
            case Key.PageDown:
                ChangePage(1);
                break;
            case Key.LeftShift or Key.RightShift:
                SwitchToKey(Input.ModifierKey.Shift);
                break;
            case Key.LeftCtrl or Key.RightCtrl:
                SwitchToKey(Input.ModifierKey.Ctrl);
                break;
            case Key.LeftAlt or Key.RightAlt:
                SwitchToKey(Input.ModifierKey.Alt);
                break;
            case Key.R:
                ToggleRepeat();
                break;
            case >= Key.D1 and <= Key.D9:
                if (key - Key.D1 < _ops.Count)
                    Run(key - Key.D1);
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    private void SwitchToKey(Input.ModifierKey key)
    {
        if (_settings.WheelFor(key) is { } wheel && wheel != _wheel)
            SwitchTo(wheel);
    }

    private void Move(int delta)
    {
        if (_ops.Count == 0)
            return;
        SetHighlight(_highlight < 0 ? 0 : ((_highlight + delta) % _ops.Count + _ops.Count) % _ops.Count);
    }

    private void Run(int index)
    {
        var op = _ops[index];
        var items = _items;
        Finish();
        Dispatch(op, items);
    }

    private void Dispatch(Operation op, IReadOnlyList<FileItem> items)
    {
        if (_repeat && RunRepeat is not null)
            RunRepeat(items);
        else
            _execute(op, items);
    }


    // ------------------------------------------------------------------ geometry

    private static Point Polar(double radius, double degrees)
    {
        var rad = degrees * Math.PI / 180;
        return new Point(Size / 2 + radius * Math.Sin(rad), Size / 2 - radius * Math.Cos(rad));
    }

    private static double Deg(double radians) => radians * 180 / Math.PI;

    private int HitTest(Point p, out bool inHub)
    {
        var dx = p.X - Size / 2;
        var dy = p.Y - Size / 2;
        var r = Math.Sqrt(dx * dx + dy * dy);
        inHub = r < InnerRadius;
        if (inHub || r > OuterRadius + Rim || _ops.Count == 0)
            return -1;
        var angle = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;
        var step = 360.0 / _ops.Count;
        return (int)(((angle + step / 2) % 360) / step) % _ops.Count;
    }

    /// <summary>
    /// A ring segment between angles <paramref name="a0"/> and <paramref name="a1"/> with a constant-width gap
    /// to its neighbours and rounded corners.
    /// </summary>
    private static Geometry Sector(double a0, double a1)
    {
        // Angle of a point on an edge that is shifted sideways by half the gap, at radius r.
        double Edge(double r, double a, int side) => a + side * Deg(Math.Asin(Gap / 2 / r));

        var outerStart = Edge(OuterRadius, a0, 1);
        var outerEnd = Edge(OuterRadius, a1, -1);
        var innerStart = Edge(InnerRadius, a0, 1);
        var innerEnd = Edge(InnerRadius, a1, -1);
        var cOut = Math.Min(Corner, Rad(outerEnd - outerStart) * OuterRadius * 0.45);
        var cIn = Math.Min(Corner * 0.7, Rad(innerEnd - innerStart) * InnerRadius * 0.45);
        var dOut = Deg(cOut / OuterRadius);
        var dIn = Deg(cIn / InnerRadius);

        var start = Polar(OuterRadius, outerStart + dOut);
        var figure = new PathFigure { StartPoint = start, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment(Polar(OuterRadius, outerEnd - dOut), new System.Windows.Size(OuterRadius, OuterRadius), 0,
            outerEnd - outerStart - 2 * dOut > 180, SweepDirection.Clockwise, true));
        figure.Segments.Add(new QuadraticBezierSegment(Polar(OuterRadius, outerEnd), Polar(OuterRadius - cOut, Edge(OuterRadius - cOut, a1, -1)), true));
        figure.Segments.Add(new LineSegment(Polar(InnerRadius + cIn, Edge(InnerRadius + cIn, a1, -1)), true));
        figure.Segments.Add(new QuadraticBezierSegment(Polar(InnerRadius, innerEnd), Polar(InnerRadius, innerEnd - dIn), true));
        figure.Segments.Add(new ArcSegment(Polar(InnerRadius, innerStart + dIn), new System.Windows.Size(InnerRadius, InnerRadius), 0,
            innerEnd - innerStart - 2 * dIn > 180, SweepDirection.Counterclockwise, true));
        figure.Segments.Add(new QuadraticBezierSegment(Polar(InnerRadius, innerStart), Polar(InnerRadius + cIn, Edge(InnerRadius + cIn, a0, 1)), true));
        figure.Segments.Add(new LineSegment(Polar(OuterRadius - cOut, Edge(OuterRadius - cOut, a0, 1)), true));
        figure.Segments.Add(new QuadraticBezierSegment(Polar(OuterRadius, outerStart), start, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;

        static double Rad(double degrees) => degrees * Math.PI / 180;
    }

    private static Geometry Ring()
    {
        var center = new Point(Size / 2, Size / 2);
        var ring = new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(center, OuterRadius, OuterRadius),
            new EllipseGeometry(center, InnerRadius, InnerRadius));
        ring.Freeze();
        return ring;
    }

    // ------------------------------------------------------------------ visuals

    private static TextBlock HubText(double size, FontWeight weight) => new()
    {
        FontSize = size,
        FontWeight = weight,
        TextAlignment = TextAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        HorizontalAlignment = HorizontalAlignment.Center,
        MaxWidth = InnerRadius * 1.65,
    };

    /// <summary>
    /// A soft shadow that exists only outside the disc. A DropShadowEffect on the
    /// translucent body would also darken the glass from inside.
    /// </summary>
    private UIElement OuterShadow(double disc)
    {
        var reach = disc + ShadowSpread;
        var edge = disc / reach;
        var color = _glass.Shadow;
        Color At(double opacity) => Color.FromArgb((byte)Math.Round(255 * _glass.ShadowOpacity * opacity), color.R, color.G, color.B);
        var fill = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(At(1), 0), new GradientStop(At(1), edge * 0.97),
                new GradientStop(At(0.42), edge + (1 - edge) * 0.3), new GradientStop(At(0), 1),
            },
        };
        fill.Freeze();
        var shadow = new Ellipse
        {
            Width = 2 * reach, Height = 2 * reach, Fill = fill, IsHitTestVisible = false,
            Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, 2 * reach, 2 * reach)),
                new EllipseGeometry(new Point(reach, reach - ShadowDrop), disc, disc)),
        };
        Canvas.SetLeft(shadow, Size / 2 - reach);
        Canvas.SetTop(shadow, Size / 2 - reach + ShadowDrop);
        return shadow;
    }

    /// <summary>Gloss over the top of a circle: a wide ellipse whose lower edge curves across its middle.</summary>
    private static Path Sheen(Point center, double radius, Brush fill, double depth) => new()
    {
        Data = new CombinedGeometry(GeometryCombineMode.Intersect,
            new EllipseGeometry(center, radius - 1, radius - 1),
            new EllipseGeometry(new Point(center.X, center.Y - radius * 0.95), radius * 1.4, radius * (0.95 + depth))),
        Fill = fill,
        IsHitTestVisible = false,
    };

    private void Build()
    {
        _canvas.Children.Clear();
        _slices.Clear();
        _labels.Clear();
        _groups.Clear();

        var disc = OuterRadius + Rim;
        var center = new Point(Size / 2, Size / 2);
        var highContrast = SystemParameters.HighContrast;
        if (!highContrast)
            _canvas.Children.Add(OuterShadow(disc));

        // The body is the input surface as well as the glass tint.
        var body = new Ellipse { Width = 2 * disc, Height = 2 * disc, Fill = WheelSurface(BodyMaterial) };
        Canvas.SetLeft(body, Pad);
        Canvas.SetTop(body, Pad);
        _canvas.Children.Add(body);
        if (!highContrast)
            _canvas.Children.Add(Sheen(center, disc, _glass.Sheen, 0.12));

        var n = _ops.Count;
        var step = n == 0 ? 360 : 360.0 / n;
        // Labels sit a little outside the middle of the ring, where slices are wider,
        // and shrink to the slice's width instead of spilling into the neighbours.
        var labelRadius = InnerRadius + (OuterRadius - InnerRadius) * 0.54;
        var labelWidth = n <= 2 ? 120 : Math.Clamp(2 * labelRadius * Math.Sin(step / 2 * Math.PI / 180) * 0.84, 48, 110);
        for (var i = 0; i < n; i++)
        {
            var a0 = i * step - step / 2;
            var path = new Path
            {
                Data = n == 1 ? Ring() : Sector(a0, a0 + step),
                Fill = WheelSurface(_glass.Pane),
                Stroke = EdgeBrush(_glass.PaneEdge),
                StrokeThickness = 1,
            };
            var label = SliceLabel(_ops[i]);
            var fit = new Viewbox
            {
                Child = label,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                MaxWidth = labelWidth,
                MaxHeight = (OuterRadius - InnerRadius) * 0.8,
                IsHitTestVisible = false,
            };
            var mid = n == 1 ? new Point(Size / 2, Size / 2 - labelRadius) : Polar(labelRadius, i * step);
            fit.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(fit, mid.X - fit.DesiredSize.Width / 2);
            Canvas.SetTop(fit, mid.Y - fit.DesiredSize.Height / 2);

            // Slice and label move together when the slice pops out.
            var group = new Canvas { Width = Size, Height = Size };
            var transforms = new TransformGroup();
            transforms.Children.Add(new ScaleTransform(1, 1, Size / 2, Size / 2));
            transforms.Children.Add(new TranslateTransform());
            group.RenderTransform = transforms;
            group.Children.Add(path);
            group.Children.Add(fit);
            _slices.Add(path);
            _labels.Add(label);
            _groups.Add(group);
            _canvas.Children.Add(group);
        }

        var hubSize = 2 * (InnerRadius - 6);
        // The hub is a raised bubble on the same glass: brighter body, its own gloss.
        var hub = new Ellipse
        {
            Width = hubSize,
            Height = hubSize,
            Fill = WheelSurface(HubMaterial),
            Stroke = EdgeBrush(_glass.Rim),
            StrokeThickness = 1.2,
        };
        Canvas.SetLeft(hub, Size / 2 - hubSize / 2);
        Canvas.SetTop(hub, Size / 2 - hubSize / 2);
        _canvas.Children.Add(hub);
        if (!highContrast)
            _canvas.Children.Add(Sheen(center, hubSize / 2, _glass.HubSheen, 0.02));

        BuildHub();
        _hubContent.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_hubContent, Size / 2 - _hubContent.Width / 2);
        Canvas.SetTop(_hubContent, Size / 2 - _hubContent.DesiredSize.Height / 2);
        _canvas.Children.Add(_hubContent);

        // One rim, bright where the light hits the top and fading underneath.
        var rim = new Ellipse
        {
            Width = 2 * disc, Height = 2 * disc,
            Stroke = EdgeBrush(_glass.Rim), StrokeThickness = 1.25, IsHitTestVisible = false,
        };
        Canvas.SetLeft(rim, Pad);
        Canvas.SetTop(rim, Pad);
        _canvas.Children.Add(rim);
    }

    /// <summary>Slices grow out of the centre one after another.</summary>
    private void AnimateSlicesIn()
    {
        if (!Animate)
        {
            foreach (var group in _groups)
            {
                group.BeginAnimation(OpacityProperty, null);
                group.Opacity = 1;
                var scale = (ScaleTransform)((TransformGroup)group.RenderTransform).Children[0];
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = scale.ScaleY = 1;
            }
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        for (var i = 0; i < _groups.Count; i++)
        {
            var delay = TimeSpan.FromMilliseconds(8 * i);
            var duration = TimeSpan.FromMilliseconds(170);
            var scale = (ScaleTransform)((TransformGroup)_groups[i].RenderTransform).Children[0];
            _groups[i].BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { BeginTime = delay, EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, duration) { BeginTime = delay, EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, duration) { BeginTime = delay, EasingFunction = ease });
        }
    }

    private StackPanel SliceLabel(Operation op)
    {
        var panel = new StackPanel { MaxWidth = 96, IsHitTestVisible = false };
        var isFormat = op.Wheel == WheelKind.Convert && op.Label.Length <= 6 && op.Label.ToUpperInvariant() == op.Label;
        if (!isFormat)
        {
            panel.Children.Add(new TextBlock
            {
                Text = op.Glyph,
                FontFamily = Theme.Icons,
                FontSize = 20,
                Foreground = WheelText,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }
        panel.Children.Add(new TextBlock
        {
            Text = op.Label,
            FontFamily = Theme.DisplayFont,
            FontSize = isFormat ? 19 : 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = WheelText,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        if (!string.IsNullOrEmpty(op.Subtitle))
        {
            panel.Children.Add(new TextBlock
            {
                Text = op.Subtitle,
                Tag = "subtitle",
                FontSize = 10.5,
                Foreground = WheelMuted,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }
        return panel;
    }

    private void BuildHub()
    {
        _title.Foreground = WheelText;
        _detail.Foreground = WheelMuted;
        _tabTrack.Background = WheelSurface(_glass.Track);
        _tabTrack.BorderBrush = EdgeBrush(_glass.TrackEdge);

        if (_repeat)
        {
            _icon.Visibility = Visibility.Collapsed;
            _iconGlyph.Text = "\uE8EE"; // RepeatAll
            _iconGlyph.Foreground = WheelText;
            _iconGlyph.Visibility = Visibility.Visible;
        }
        else if (_items.Count > 0)
        {
            _icon.Source = Shell.FileIcons.Get(_items[0].Path);
            _icon.Visibility = _icon.Source is null ? Visibility.Collapsed : Visibility.Visible;
            _iconGlyph.Visibility = Visibility.Collapsed;
        }
        else
        {
            _icon.Visibility = Visibility.Collapsed;
            _iconGlyph.Text = Glyphs.Extract;
            _iconGlyph.Foreground = WheelText;
            _iconGlyph.Visibility = Visibility.Visible;
        }

        _tabs.Children.Clear();
        foreach (var wheel in WheelOrder)
        {
            var key = _settings.KeyFor(wheel);
            if (key == Input.ModifierKey.None && wheel != _wheel)
                continue;
            var active = wheel == _wheel;
            var tab = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 1, 7, 2),
                Background = active ? SystemParameters.HighContrast ? SystemColors.HighlightBrush : SelectedFill : Brushes.Transparent,
                BorderBrush = active ? SystemParameters.HighContrast ? SystemColors.HighlightTextBrush : _glass.SelectedEdge : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Cursor = Mode == WheelMode.Click ? Cursors.Hand : null,
                ToolTip = AppSettings.WheelName(wheel),
                Child = new TextBlock
                {
                    Text = key == Input.ModifierKey.None ? AppSettings.WheelName(wheel) : AppSettings.KeyName(key),
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = active && SystemParameters.HighContrast ? SystemColors.HighlightTextBrush : active ? SelectedInk : WheelMuted,
                },
            };
            if (Mode == WheelMode.Click)
            {
                var target = wheel;
                tab.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    SwitchTo(target);
                };
            }
            _tabs.Children.Add(tab);
        }
        UpdateHubText();
        BuildPager();
    }

    private void BuildPager()
    {
        _pager.Children.Clear();
        _pager.Visibility = PageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        _previousPage = _nextPage = null;
        if (PageCount <= 1) return;
        Border Arrow(string text, int direction)
        {
            var arrow = new Border
            {
                Width = 28, Height = 24, CornerRadius = new CornerRadius(6),
                Background = WheelSurface(_glass.Track), BorderBrush = EdgeBrush(_glass.TrackEdge),
                BorderThickness = new Thickness(1), Cursor = Mode == WheelMode.Click ? Cursors.Hand : Cursors.Arrow,
                ToolTip = Mode == WheelMode.Drag ? "Hold over this arrow to change page" : "Change page (Page Up / Page Down)",
                Child = new TextBlock { Text = text, FontSize = 19, Foreground = WheelText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            if (Mode == WheelMode.Click)
                arrow.MouseLeftButtonUp += (_, e) => { e.Handled = true; ChangePage(direction); };
            return arrow;
        }
        _previousPage = Arrow("‹", -1);
        _nextPage = Arrow("›", 1);
        _pager.Children.Add(_previousPage);
        _pager.Children.Add(new TextBlock { Text = $"{_page + 1} / {PageCount}", FontSize = 10.5, Foreground = WheelMuted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 7, 0) });
        _pager.Children.Add(_nextPage);
    }

    private void SetHighlight(int index)
    {
        if (index == _highlight)
            return;
        if (_highlight >= 0 && _highlight < _slices.Count)
            Paint(_highlight, false);
        _highlight = index;
        if (_highlight >= 0 && _highlight < _slices.Count)
            Paint(_highlight, true);
        UpdateHubText();
    }

    private void Paint(int index, bool on)
    {
        var slice = _slices[index];
        var highContrast = SystemParameters.HighContrast;
        slice.Fill = on ? highContrast ? SystemColors.HighlightBrush : SelectedFill : WheelSurface(_glass.Pane);
        slice.Stroke = highContrast ? on ? SystemColors.HighlightTextBrush : SystemColors.WindowTextBrush : on ? _glass.SelectedEdge : _glass.PaneEdge;
        slice.StrokeThickness = on ? 1.25 : 1;
        slice.Effect = on && !highContrast
            ? new DropShadowEffect { BlurRadius = 26, ShadowDepth = 0, Opacity = 0.5, Color = Green }
            : null;
        foreach (var child in _labels[index].Children.OfType<TextBlock>())
        {
            var subtitle = Equals(child.Tag, "subtitle");
            child.Foreground = highContrast ? on ? SystemColors.HighlightTextBrush : WheelText
                : on ? subtitle ? SelectedMuted : SelectedInk
                : subtitle ? WheelMuted : WheelText;
            child.Opacity = 1;
        }

        // Pop the slice outwards along its middle angle.
        var n = _ops.Count;
        var angle = n <= 1 ? 0 : index * 360.0 / n * Math.PI / 180;
        var distance = on && n > 1 ? PopOut : 0;
        var move = (TranslateTransform)((TransformGroup)_groups[index].RenderTransform).Children[1];
        var duration = TimeSpan.FromMilliseconds(130);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (IsVisible && Animate)
        {
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(distance * Math.Sin(angle), duration) { EasingFunction = ease });
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-distance * Math.Cos(angle), duration) { EasingFunction = ease });
        }
        else
        {
            move.BeginAnimation(TranslateTransform.XProperty, null);
            move.BeginAnimation(TranslateTransform.YProperty, null);
            move.X = distance * Math.Sin(angle);
            move.Y = -distance * Math.Cos(angle);
        }
    }

    private void UpdateHubText()
    {
        if (_repeat)
        {
            var last = _ops.Count > 0 ? _ops[0] : null;
            _title.Text = _highlight >= 0 && last is not null ? last.Label : T("Zopakovat", "Repeat");
            _detail.Text = last is null
                ? T("Poslední akce se sem nehodí", "Last action doesn't fit")
                : _highlight >= 0 ? T("poslední akce", "last action") : last.Label;
            return;
        }
        if (_highlight >= 0 && _highlight < _ops.Count)
        {
            var op = _ops[_highlight];
            _title.Text = op.Label;
            _detail.Text = op.Subtitle ?? DescribeItems();
            return;
        }
        _title.Text = AppSettings.WheelName(_wheel);
        if (_items.Count == 0)
            _detail.Text = T("Přetáhněte sem soubor", "Drag a file here");
        else if (_ops.Count == 0)
            _detail.Text = T("Pro tento výběr nic není", "Nothing for this selection");
        else
            _detail.Text = PageCount > 1 && Mode == WheelMode.Drag ? "Hover ‹ / › for more" : DescribeItems();
    }

    private string DescribeItems() => _items.Count == 1 ? _items[0].Name : Items(_items.Count);
}
