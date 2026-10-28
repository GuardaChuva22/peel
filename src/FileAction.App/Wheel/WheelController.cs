using System.IO;
using System.Windows.Threading;
using FileAction.App.Actions;
using FileAction.App.Input;
using FileAction.App.Jobs;
using FileAction.App.Native;
using FileAction.App.Shell;
using FileAction.Core;
using FileAction.Core.Operations;
using static FileAction.Core.Loc;

namespace FileAction.App.Wheel;

/// <summary>Decides when a wheel opens (drag gesture, hotkey, context menu) and with which operations.</summary>
internal sealed class WheelController
{
    private readonly AppSettings _settings;
    private readonly ToolLocator _tools;
    private readonly JobRunner _jobs;
    private readonly GlobalInput _input;
    private IReadOnlyList<Operation> _coreOperations = BuildOperations();
    private IReadOnlyList<Operation> _actions;
    private readonly List<string> _pendingPaths = [];
    private readonly DispatcherTimer _argsTimer;
    private WheelWindow? _wheel;
    public Action<IReadOnlyList<string>>? WorkspaceRequested { get; set; }

    public WheelController(AppSettings settings, ToolLocator tools, JobRunner jobs, GlobalInput input)
    {
        _settings = settings;
        _tools = tools;
        _jobs = jobs;
        _input = input;
        _actions = FileActions.Build(settings, settings.Save);

        // Explorer starts one process per selected file; gather paths that arrive close together.
        _argsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _argsTimer.Tick += (_, _) =>
        {
            _argsTimer.Stop();
            var paths = _pendingPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _pendingPaths.Clear();
            OpenFor(paths, ExplorerSelection.CursorOrCenter());
        };

        input.IsWheelKey = key => !_settings.Paused && _settings.WheelFor(key) is not null;
        input.ModifierTapped += OnModifierTapped;
        input.LeftButtonUp += () => _wheel?.OnDragEnded();
        input.MouseDown += point =>
        {
            if (_wheel is { IsFinished: false, Mode: WheelMode.Click } wheel && !wheel.Contains(point))
                wheel.Finish();
        };
        input.EscapePressed += () =>
        {
            if (_wheel is { IsFinished: false, Mode: WheelMode.Drag } wheel)
                wheel.Finish();
        };
    }

    /// <summary>Rebuilds the wheels after the UI language changed (labels are fixed when an operation is created).</summary>
    public void Relocalize()
    {
        _wheel?.Finish();
        _coreOperations = BuildOperations();
        _actions = FileActions.Build(_settings, _settings.Save);
    }

    /// <summary>The cross-platform catalog plus tools that need Windows itself (OCR).</summary>
    public static IReadOnlyList<Operation> BuildOperations() => [.. Catalog.Build(), new Ocr.OcrOperation()];

    private IReadOnlyList<Operation> Operations(WheelKind wheel, IReadOnlyList<FileItem> items) =>
        WheelLayout.Arrange(Catalog.For(wheel == WheelKind.Actions ? _actions : _coreOperations, wheel, items, _tools),
            _settings.HiddenOperations, _settings.FavoriteOperations);

    /// <summary>Every operation the wheels can show (for the wheel editor).</summary>
    public IReadOnlyList<Operation> AllOperations => [.. _coreOperations, .. _actions];

    private Operation? LastFor(IReadOnlyList<FileItem> items)
    {
        var id = _settings.LastOperationId;
        var op = id is null ? null : AllOperations.FirstOrDefault(o => o.Id == id);
        return op is not null && op.AppliesTo(items) && op.IsAvailable(_tools, items) ? op : null;
    }

    private void OnModifierTapped(ModifierKey key)
    {
        if (_settings.Paused || _settings.WheelFor(key) is not { } wheel)
            return;
        if (_wheel is { IsFinished: false, IsProbing: true } waiting)
            waiting.Finish(); // nothing has been shown yet: start over with this key
        else if (_wheel is { IsFinished: false } open)
        {
            // Same key again: toggle "repeat last"; another key: switch wheel.
            if (wheel == open.CurrentWheel)
                open.ToggleRepeat();
            else
                open.SwitchTo(wheel);
            return;
        }
        // A held, moving left button is not necessarily a file drag (text selection, a rubber
        // band, a game). The shell's drag image proves one; otherwise the wheel waits unseen
        // for the drag to reach it.
        if (_input.IsDragging)
            Open(WheelMode.Drag, wheel, null, _input.Cursor, probe: !ShellDragImageVisible());
    }

    private static bool ShellDragImageVisible()
    {
        var image = FindWindow("SysDragImage", null);
        return image != IntPtr.Zero && IsWindowVisible(image);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    public void OnHotkey()
    {
        var paths = ExplorerSelection.Get(Win32.GetForegroundWindow());
        if (paths.Count == 0)
        {
            if (WorkspaceRequested is not null) { WorkspaceRequested([]); return; }
            Notifications.Show(T("Nic není vybráno", "Nothing selected"),
                T("Vyberte soubory v Průzkumníku, zkopírujte je (Ctrl+C) nebo pořiďte snímek obrazovky (Win+Shift+S) a stiskněte zkratku znovu.",
                  "Select files in File Explorer, copy them (Ctrl+C) or take a screenshot (Win+Shift+S), then press the shortcut again."));
            return;
        }
        OpenFor(paths, ExplorerSelection.CursorOrCenter());
    }

    /// <summary>Opens the wheel for whatever is on the clipboard (tray menu).</summary>
    public void OpenForClipboard()
    {
        var paths = ExplorerSelection.FromClipboard();
        if (paths.Count == 0)
        {
            Notifications.Show(T("Schránka je prázdná", "The clipboard is empty"),
                T("Zkopírujte soubory nebo obrázek, třeba snímek obrazovky (Win+Shift+S).", "Copy files or an image, e.g. a screenshot (Win+Shift+S)."));
            return;
        }
        OpenFor(paths, ExplorerSelection.CursorOrCenter());
    }

    /// <summary>Paths from the context menu (possibly one call per file).</summary>
    public void QueuePaths(IEnumerable<string> paths)
    {
        _pendingPaths.AddRange(paths);
        _argsTimer.Stop();
        _argsTimer.Start();
    }

    public void OpenFor(IReadOnlyList<string> paths, Win32.POINT at)
    {
        if (WorkspaceRequested is not null) { WorkspaceRequested(paths); return; }
        var items = paths
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .Select(p => new FileItem(p))
            .ToList();
        if (items.Count == 0)
            return;
        Open(WheelMode.Click, WheelKind.Convert, items, at);
    }

    private void Open(WheelMode mode, WheelKind wheel, IReadOnlyList<FileItem>? items, Win32.POINT at, bool probe = false)
    {
        _wheel?.Finish();
        UI.Theme.Refresh();
        var window = new WheelWindow(mode, wheel, items, _settings, Operations, _jobs.Start)
        {
            LastOperation = LastFor,
            RunRepeat = targets =>
            {
                if (LastFor(targets) is { } last)
                    _jobs.Start(last, targets, _settings.LastParameters);
            },
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_wheel, window))
                _wheel = null;
        };
        _wheel = window;
        window.ShowAt(at, probe);
    }
}
