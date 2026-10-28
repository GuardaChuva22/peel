using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace FileAction.App.Native;

/// <summary>
/// A native companion HWND supplies live compositor blur beneath the layered WPF
/// wheel. HostBackdropBrush samples other windows inside DWM: the app never reads,
/// captures or stores desktop pixels. No redraw timer is required.
/// </summary>
internal sealed class GlassBackdrop : IDisposable
{
    private const int Unsupported = unchecked((int)0x80004001);
    private static readonly WindowProcedure Procedure = WindowProc;
    private static readonly string ClassName = "Peel.HostBackdrop." + Environment.ProcessId;
    private static ushort _windowClass;
    [ThreadStatic] private static IntPtr _queueController;
    private IntPtr _handle;
    private Compositor? _compositor;
    private DesktopWindowTarget? _target;
    private ContainerVisual? _root;
    private CompositionBrush? _backdrop;
    private SpriteVisual? _sprite;
    private CompositionEllipseGeometry? _ellipse;
    private CompositionGeometricClip? _clip;
    private readonly List<(SpriteVisual Sprite, CompositionRoundedRectangleGeometry Shape)> _panes = [];
    private (int Left, int Top, int Width, int Height) _paneBounds;
    private bool _disposed;

    /// <summary>One rounded blur area, in physical pixels relative to the surface.</summary>
    public readonly record struct Pane(float X, float Y, float Width, float Height, float Radius, float Opacity);

    public IntPtr Handle => _handle;
    public int BackdropResult { get; private set; } = Unsupported;
    public int RegionResult { get; private set; }
    public bool Supported => !_disposed && _handle != IntPtr.Zero && _target is not null && BackdropResult >= 0;

    /// <param name="owner">Optional owner. Do not use the overlay as owner: owned HWNDs must stay above their owner.</param>
    public GlassBackdrop(IntPtr owner = default)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || SystemParameters.HighContrast) return;
        try
        {
            EnsureDispatcherQueue();
            EnsureWindowClass();
            _handle = CreateWindowEx(0x00200000 | 0x80 | 0x08000000 | 0x20, ClassName, "Peel live glass",
                unchecked((int)0x80000000), -32000, -32000, 1, 1, owner, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (_handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var enabled = 1;
            BackdropResult = DwmSetWindowAttribute(_handle, 17, ref enabled, sizeof(int));
            Marshal.ThrowExceptionForHR(BackdropResult);
            _compositor = new Compositor();
            var interop = _compositor.As<ICompositorDesktopInterop>();
            interop.CreateDesktopWindowTarget(_handle, true, out var target);
            try { _target = MarshalInterface<DesktopWindowTarget>.FromAbi(target); }
            finally { Marshal.Release(target); }
            _root = _compositor.CreateContainerVisual();
            _backdrop = _compositor.CreateHostBackdropBrush();
            _sprite = _compositor.CreateSpriteVisual();
            _sprite.Brush = _backdrop;
            _ellipse = _compositor.CreateEllipseGeometry();
            _clip = _compositor.CreateGeometricClip(_ellipse);
            _sprite.Clip = _clip;
            _root.Children.InsertAtTop(_sprite);
            _target.Root = _root;
        }
        catch (Exception ex) when (ex is COMException or System.ComponentModel.Win32Exception or NotSupportedException or TypeLoadException)
        {
            BackdropResult = ex.HResult < 0 ? ex.HResult : Unsupported;
            ReleaseResources();
        }
    }

    /// <summary>
    /// Physical pixels. Inserts this surface immediately below the overlay HWND without activation.
    /// With <paramref name="reveal"/>, the first appearance fades and grows in step with the WPF wheel
    /// so the blur never shows as a bare disc before the glass above it.
    /// </summary>
    public void ShowBehind(IntPtr overlay, int left, int top, int size, bool circular = true, TimeSpan? reveal = null)
    {
        if (!Supported || size <= 0) return;
        _sprite!.IsVisible = true;
        _sprite.Size = new Vector2(size);
        _sprite.CenterPoint = new Vector3(size / 2f, size / 2f, 0);
        _ellipse!.Center = new Vector2(size / 2f);
        _ellipse.Radius = new Vector2(size / 2f);
        _sprite.Clip = circular ? _clip : null;
        var revealing = reveal is { } duration && duration > TimeSpan.Zero && !IsWindowVisible(_handle);
        if (revealing)
        {
            _sprite.Opacity = 0;
            _sprite.Scale = new Vector3(0.94f, 0.94f, 1);
        }
        var region = circular ? CreateEllipticRgn(0, 0, size + 1, size + 1) : IntPtr.Zero;
        RegionResult = SetWindowRgn(_handle, region, true);
        if (RegionResult == 0 && region != IntPtr.Zero) DeleteObject(region);
        SetWindowPos(_handle, overlay, left, top, size, size, 0x0010 | 0x0040);
        if (revealing)
            Reveal(reveal!.Value);
    }

    private void Reveal(TimeSpan duration)
    {
        // Matches the wheel's CubicEase EaseOut.
        var ease = _compositor!.CreateCubicBezierEasingFunction(new Vector2(0.215f, 0.61f), new Vector2(0.355f, 1f));
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = duration;
        var grow = _compositor.CreateVector3KeyFrameAnimation();
        grow.InsertKeyFrame(0, new Vector3(0.94f, 0.94f, 1));
        grow.InsertKeyFrame(1, Vector3.One, ease);
        grow.Duration = duration;
        _sprite!.Opacity = 1;
        _sprite.Scale = Vector3.One;
        _sprite.StartAnimation("Opacity", fade);
        _sprite.StartAnimation("Scale", grow);
    }

    /// <summary>
    /// Physical pixels. A rectangular surface below <paramref name="overlay"/> that blurs only
    /// inside the given rounded panes (one per notification card); the space between stays clear.
    /// Cheap enough to call every frame while the cards animate.
    /// </summary>
    public void ShowPanes(IntPtr overlay, int left, int top, int width, int height, IReadOnlyList<Pane> panes)
    {
        if (!Supported || width <= 0 || height <= 0) return;
        _sprite!.IsVisible = false;
        while (_panes.Count < panes.Count)
        {
            var shape = _compositor!.CreateRoundedRectangleGeometry();
            var sprite = _compositor.CreateSpriteVisual();
            sprite.Brush = _backdrop;
            sprite.Clip = _compositor.CreateGeometricClip(shape);
            _root!.Children.InsertAtTop(sprite);
            _panes.Add((sprite, shape));
        }
        for (var i = 0; i < _panes.Count; i++)
        {
            var (sprite, shape) = _panes[i];
            if (i >= panes.Count)
            {
                sprite.IsVisible = false;
                continue;
            }
            var pane = panes[i];
            sprite.IsVisible = pane.Opacity > 0.01f && pane.Width > 0 && pane.Height > 0;
            sprite.Offset = new Vector3(pane.X, pane.Y, 0);
            sprite.Size = new Vector2(pane.Width, pane.Height);
            sprite.Opacity = pane.Opacity;
            shape.Size = sprite.Size;
            shape.CornerRadius = new Vector2(pane.Radius);
        }
        var bounds = (left, top, width, height);
        if (bounds == _paneBounds && IsWindowVisible(_handle)) return;
        _paneBounds = bounds;
        RegionResult = SetWindowRgn(_handle, IntPtr.Zero, true);
        SetWindowPos(_handle, overlay, left, top, width, height, 0x0010 | 0x0040);
    }

    public void Hide()
    {
        if (_handle != IntPtr.Zero) ShowWindow(_handle, 0);
    }

    private static void EnsureDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null || _queueController != IntPtr.Zero) return;
        var options = new QueueOptions { Size = Marshal.SizeOf<QueueOptions>(), ThreadType = 2, Apartment = 2 };
        // One queue per UI thread, retained for that thread's lifetime. This is
        // compositor infrastructure, not a worker or polling service.
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out _queueController));
    }

    private static void EnsureWindowClass()
    {
        if (_windowClass != 0) return;
        var cls = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Procedure,
            Instance = GetModuleHandle(null), ClassName = ClassName,
        };
        _windowClass = RegisterClassEx(ref cls);
        if (_windowClass == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam) => message switch
    {
        0x0084 => new IntPtr(-1), // HTTRANSPARENT
        0x0021 => new IntPtr(3),  // MA_NOACTIVATE
        _ => DefWindowProc(hwnd, message, wParam, lParam),
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseResources();
    }

    private void ReleaseResources()
    {
        if (_handle != IntPtr.Zero) ShowWindow(_handle, 0);
        if (_target is not null) _target.Root = null;
        foreach (var (sprite, shape) in _panes) { sprite.Clip?.Dispose(); sprite.Dispose(); shape.Dispose(); }
        _panes.Clear();
        _clip?.Dispose(); _clip = null;
        _ellipse?.Dispose(); _ellipse = null;
        _sprite?.Dispose(); _sprite = null;
        _backdrop?.Dispose(); _backdrop = null;
        _root?.Dispose(); _root = null;
        _target?.Dispose(); _target = null;
        _compositor?.Dispose(); _compositor = null;
        if (_handle != IntPtr.Zero) { DestroyWindow(_handle); _handle = IntPtr.Zero; }
    }

    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IntPtr target);
        void EnsureOnThread(int threadId);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public WindowProcedure Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
        public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct QueueOptions { public int Size, ThreadType, Apartment; }
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(QueueOptions options, out IntPtr controller);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(int extended, string cls, string name, int style, int x, int y, int width, int height, IntPtr owner, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
}
