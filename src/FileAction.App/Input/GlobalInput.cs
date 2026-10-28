using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using FileAction.App.Native;

namespace FileAction.App.Input;

public enum ModifierKey
{
    None,
    Shift,
    Ctrl,
    Alt,
}

/// <summary>
/// Low-level mouse and keyboard hooks on a dedicated thread. Detects "a modifier was tapped while the
/// left mouse button is dragging something" – the gesture that opens a wheel – and forwards events to
/// the UI dispatcher.
/// </summary>
internal sealed class GlobalInput : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Win32.HookProc _keyboardProc;
    private readonly Win32.HookProc _mouseProc;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private readonly object _lifecycle = new();
    private System.Threading.Timer? _watchdog;
    private volatile bool _disposed;
    private bool _subscribed;
    private int _recoverQueued;
    private long _lastCallbackAt = Environment.TickCount64;
    private long _lastPumpAt = Environment.TickCount64;
    private uint _lastNativeInputAt = unchecked((uint)Environment.TickCount);
    private int _dragThresholdX;
    private int _dragThresholdY;
    private const uint RecoverMessage = 0x8001;
    private const uint ProbeMessage = 0x8002;

    private bool _leftDown;
    private Win32.POINT _downAt;
    private volatile bool _dragging;
    private ModifierKey _pendingTap;
    private int _heldModifierKeys;
    private int _cursorX;
    private int _cursorY;

    public GlobalInput(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

    /// <summary>A modifier was pressed and released on its own while dragging.</summary>
    public event Action<ModifierKey>? ModifierTapped;

    /// <summary>The left button was released (any drag ended).</summary>
    public event Action? LeftButtonUp;

    /// <summary>Any mouse button went down, with the physical screen position.</summary>
    public event Action<Win32.POINT>? MouseDown;

    public event Action? EscapePressed;

    /// <summary>Called on the hook thread to decide whether Alt taps should be neutralised.</summary>
    public Func<ModifierKey, bool>? IsWheelKey { get; set; }

    public bool IsDragging => _dragging;

    public Win32.POINT Cursor => new() { X = Volatile.Read(ref _cursorX), Y = Volatile.Read(ref _cursorY) };

    public void Start()
    {
        TaskCompletionSource<bool> ready;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is { IsAlive: true })
                return;
            ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(() => Run(ready))
            {
                IsBackground = true,
                Name = "FileAction input hooks",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }
        // The completion object belongs to the thread until it exits; a slow start cannot
        // signal a disposed wait handle. Keep startup bounded even if Windows is unhealthy.
        if (!ready.Task.Wait(750))
        {
            Dispose();
            throw new TimeoutException("The input hook thread did not start in time.");
        }
        ready.Task.GetAwaiter().GetResult();
        lock (_lifecycle)
        {
            if (_disposed || _subscribed)
                return;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            _subscribed = true;
            _watchdog = new System.Threading.Timer(CheckHealth, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_lifecycle)
        {
            if (_disposed)
                return;
            _disposed = true;
            _watchdog?.Dispose();
            _watchdog = null;
            if (_subscribed)
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                _subscribed = false;
            }
            thread = _thread;
            _dragging = false;
            if (_threadId != 0)
                Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        if (thread is not null && thread != Thread.CurrentThread)
            thread.Join(250);
    }

    /// <summary>Reinstall both hooks after resume/unlock or a detected silent hook removal.</summary>
    public void Recover()
    {
        lock (_lifecycle)
        {
            if (_disposed || _thread is null)
                return;
            if (!_thread.IsAlive)
            {
                // Restart on the pool so recovery never waits on the UI dispatcher.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Start(); }
                    catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Input recovery: {0}", ex.Message); }
                });
                return;
            }
            if (Interlocked.Exchange(ref _recoverQueued, 1) == 0 &&
                !Win32.PostThreadMessage(_threadId, RecoverMessage, IntPtr.Zero, IntPtr.Zero))
                Interlocked.Exchange(ref _recoverQueued, 0);
        }
    }

    private void Run(TaskCompletionSource<bool> ready)
    {
        try
        {
            _threadId = Win32.GetCurrentThreadId();
            // Explicitly create the queue before publishing readiness or accepting posts.
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            InstallHooks();
            if (_disposed)
            {
                ready.TrySetException(new ObjectDisposedException(nameof(GlobalInput)));
                return;
            }
            ready.TrySetResult(true);
            while (Win32.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                Volatile.Write(ref _lastPumpAt, Environment.TickCount64);
                if (message.message == RecoverMessage)
                {
                    Interlocked.Exchange(ref _recoverQueued, 0);
                    if (_disposed)
                        break;
                    RemoveHooks();
                    ResetDrag();
                    try { InstallHooks(); }
                    catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Input hooks: {0}", ex.Message); }
                    if (_disposed)
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
            System.Diagnostics.Trace.TraceWarning("Input thread: {0}", ex.Message);
        }
        finally
        {
            RemoveHooks();
            ResetDrag();
            Interlocked.Exchange(ref _recoverQueued, 0);
        }
    }

    private void InstallHooks()
    {
        _dragThresholdX = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CXDRAG) * 2);
        _dragThresholdY = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CYDRAG) * 2);
        var module = Win32.GetModuleHandle(null);
        _keyboardHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, module, 0);
        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            RemoveHooks();
            throw new InvalidOperationException("Could not install the global keyboard and mouse hooks.");
        }
        Volatile.Write(ref _lastCallbackAt, Environment.TickCount64);
        Volatile.Write(ref _lastPumpAt, Environment.TickCount64);
        Volatile.Write(ref _lastNativeInputAt, unchecked((uint)Environment.TickCount));
    }

    private void RemoveHooks()
    {
        if (_keyboardHook != IntPtr.Zero)
            Win32.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero)
            Win32.UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
    }

    private void ResetDrag()
    {
        _leftDown = false;
        _dragging = false;
        _pendingTap = ModifierKey.None;
        _heldModifierKeys = 0;
    }

    private void RecordInput(uint nativeTime)
    {
        Volatile.Write(ref _lastNativeInputAt, nativeTime);
        Volatile.Write(ref _lastCallbackAt, Environment.TickCount64);
    }

    private void CheckHealth(object? state)
    {
        lock (_lifecycle)
        {
            if (_disposed)
                return;
            if (_thread is not { IsAlive: true } || _keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
            {
                Recover();
                return;
            }
            var lastInput = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            // Compare native timestamps with wraparound-safe subtraction. Idle time alone
            // must never trigger hook churn; Windows must have observed newer real input.
            if (GetLastInputInfo(ref lastInput) &&
                unchecked((int)(lastInput.dwTime - Volatile.Read(ref _lastNativeInputAt))) > 0 &&
                Environment.TickCount64 - Volatile.Read(ref _lastCallbackAt) > 10_000)
                Recover();
            else if (Environment.TickCount64 - Volatile.Read(ref _lastPumpAt) > 90_000)
                Recover();
            Win32.PostThreadMessage(_threadId, ProbeMessage, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            Recover();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon or SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect)
            Recover();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Win32.MSG message, IntPtr hwnd, uint min, uint max, uint remove);

    private static ModifierKey ToModifier(uint vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => ModifierKey.Shift,
        0x11 or 0xA2 or 0xA3 => ModifierKey.Ctrl,
        0x12 or 0xA4 or 0xA5 => ModifierKey.Alt,
        _ => ModifierKey.None,
    };

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
                RecordInput(data.time);
                var message = (int)wParam;
                var injected = (data.flags & Win32.LLKHF_INJECTED) != 0;
                // Ignore synthetic keys (ours and other tools'); a tap must come from the keyboard.
                if (!injected)
                    OnKey(data.vkCode, message is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN);
            }
            catch
            {
                // Never let an exception escape into the system hook chain.
            }
        }
        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void OnKey(uint vk, bool down)
    {
        var modifier = ToModifier(vk);
        var modifierBit = vk switch
        {
            0x10 => 1, 0xA0 => 2, 0xA1 => 4,
            0x11 => 8, 0xA2 => 16, 0xA3 => 32,
            0x12 => 64, 0xA4 => 128, 0xA5 => 256,
            _ => 0,
        };
        if (down)
        {
            if (modifier == ModifierKey.None)
            {
                _pendingTap = ModifierKey.None;
                if (vk == 0x1B)
                    _dispatcher.BeginInvoke(() => EscapePressed?.Invoke());
                return;
            }
            if ((_heldModifierKeys & modifierBit) != 0)
                return; // auto-repeat
            var anotherModifierHeld = _heldModifierKeys != 0;
            _heldModifierKeys |= modifierBit;
            _pendingTap = _dragging && !anotherModifierHeld ? modifier : ModifierKey.None;
            if (modifier == ModifierKey.Alt && _pendingTap == modifier && IsWheelKey?.Invoke(modifier) == true)
            {
                // A bare Alt press/release would activate the menu bar of the window under the drag.
                Win32.keybd_event(Win32.VK_DUMMY, 0, 0, UIntPtr.Zero);
                Win32.keybd_event(Win32.VK_DUMMY, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            return;
        }

        _heldModifierKeys &= ~modifierBit;
        if (modifier != ModifierKey.None && modifier == _pendingTap && _heldModifierKeys == 0)
        {
            _pendingTap = ModifierKey.None;
            if (_dragging)
                _dispatcher.BeginInvoke(() => ModifierTapped?.Invoke(modifier));
        }
        else if (modifier == ModifierKey.None)
        {
            _pendingTap = ModifierKey.None;
        }
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                RecordInput(data.time);
                Volatile.Write(ref _cursorX, data.pt.X);
                Volatile.Write(ref _cursorY, data.pt.Y);
                switch ((int)wParam)
                {
                    case Win32.WM_LBUTTONDOWN:
                        _leftDown = true;
                        _dragging = false;
                        _downAt = data.pt;
                        Raise(data.pt);
                        break;
                    case Win32.WM_RBUTTONDOWN:
                    case Win32.WM_MBUTTONDOWN:
                        Raise(data.pt);
                        break;
                    case Win32.WM_MOUSEMOVE when _leftDown && !_dragging:
                        var dx = Math.Abs(data.pt.X - _downAt.X);
                        var dy = Math.Abs(data.pt.Y - _downAt.Y);
                        if (dx > _dragThresholdX || dy > _dragThresholdY)
                            _dragging = true;
                        break;
                    case Win32.WM_LBUTTONUP:
                        _leftDown = false;
                        _dragging = false;
                        _dispatcher.BeginInvoke(() => LeftButtonUp?.Invoke());
                        break;
                }
            }
            catch
            {
                // Never let an exception escape into the system hook chain.
            }
        }
        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void Raise(Win32.POINT pt) => _dispatcher.BeginInvoke(() => MouseDown?.Invoke(pt));
}
