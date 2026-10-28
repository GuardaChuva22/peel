using System.IO;
using System.Windows;
using System.Windows.Threading;
using FileAction.App.Actions;
using FileAction.App.Input;
using FileAction.App.Jobs;
using FileAction.App.Shell;
using FileAction.App.UI;
using FileAction.App.Wheel;
using FileAction.Core;
using static FileAction.Core.Loc;

namespace FileAction.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if(args.Length>0&&args[0]=="--glass-preview")return GlassPreview.Run(args.Length>1?args[1]:"glass-preview",args.Length>2?args[2]:null,args.Length>3?args[3]:null);
        if(args.Length>0&&args[0]=="--setup-tools")return GlassPreview.RunToolSetup();
        if(args.Length>0&&args[0]=="--link-preview")return GlassPreview.RunLinks(args.Length>1?args[1]:"link-preview",args.Length>2?args[2]:null);
        if(args.Length>0&&args[0]=="--toast-preview")return GlassPreview.RunToasts(args.Length>1?args[1]:"toast-preview",args.Length>2?args[2]:null,args.Length>3?args[3]:null);
        if(args.Length>0&&args[0]=="--verify-peel")return PeelVerification.Run(args.Length>1?args[1]:"peel-verification");
        if (args.Length > 0 && args[0] == "--self-test-workers")
            return SelfTest.RunWorkerTests(args.Length >= 2 ? args[1] : "worker-test");
        if (WorkerHost.TryRun(args, out var workerExitCode)) return workerExitCode;
        if (args.Length >= 1 && args[0] == "--self-test")
            return SelfTest.Run(args.Length >= 2 ? args[1] : "self-test");
        if (args.Contains("--unregister"))
        {
            // Called by the uninstaller.
            ShellIntegration.SetContextMenu(false);
            ShellIntegration.SetAutostart(false);
            return 0;
        }

        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            // Already running: hand over the request (or just open the settings).
            SingleInstance.Forward(args.Length > 0 ? args : ["--home"]);
            return 0;
        }
        var app = new App(instance, args);
        return app.Run();
    }
}

internal sealed class App : Application
{
    private readonly SingleInstance _instance;
    private readonly string[] _args;
    private AppSettings _settings = null!;
    private ToolLocator _tools = null!;
    private GlobalInput? _input;
    private WheelController _controller = null!;
    private TrayIcon? _tray;
    private HotkeyListener? _hotkey;
    private HotkeyPreset? _activeHotkey;
    private LinkWatcher? _links;
    private JobRunner _jobs = null!;
    private readonly Updater _updater = new();

    public App(SingleInstance instance, string[] args)
    {
        _instance = instance;
        _args = args;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandled;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _settings = AppSettings.Load();
        Loc.Current = _settings.Language ?? Loc.Detect();
        Theme.Refresh();
        _tools = new ToolLocator();
        _jobs = new JobRunner(_settings, _tools);

        _input = new GlobalInput(Dispatcher);
        try
        {
            _input.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or AggregateException)
        {
            Log(ex);
            // Keep keyboard/context-menu access available even if Windows temporarily refuses hooks.
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                try { _input.Start(); }
                catch (Exception retry) { Log(retry); }
            });
        }

        _controller = new WheelController(_settings, _tools, _jobs, _input);
        _controller.WorkspaceRequested = paths => OpenHome(paths);
        Ocr.OcrOperation.CopyToClipboard = text => Dispatcher.BeginInvoke(() =>
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }
        });
        try
        {
            _links = new LinkWatcher { Enabled = _settings.DetectVideoLinks };
            _links.Copied += link => VideoOffers.Show(link, _tools);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log(ex);
        }
        _tray = CreateTray();
        SettingsWindow.Updater = null;
        SettingsWindow.Operations = () => _controller.AllOperations;
        _settings.CheckForUpdates = false;
        // Updates come only from Peel's own GitHub releases; helper engines are fetched when missing.
        _updater.StartBackgroundChecks(_settings, OfferUpdate);
        var tools = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        tools.Tick += async (_, _) => { tools.Stop(); await ToolSetup.EnsureAsync(_tools); };
        tools.Start();
        _hotkey = new HotkeyListener(_controller.OnHotkey);
        ApplyHotkey();
        _instance.Listen(Dispatcher, HandleArgs);

        ShellIntegration.Refresh();
        if (!_settings.FirstRunDone)
        {
            _settings.FirstRunDone = true;
            _settings.Save();
            TryIntegrateOnFirstRun();
            OpenHome();
        }
        else if (_args.Contains("--updated"))
        {
            Notifications.Show(T("FileAction je aktualizovaný", "Peel was updated"), T($"Verze {Updater.Current}", $"Version {Updater.Current}"));
        }
        else if (!_args.Contains("--background"))
        {
            Notifications.Show(T("FileAction běží", "Peel is ready"), T("Chyťte soubor, během tažení ťukněte na Shift a pusťte ho na formát.", "Grab a file, tap Shift while dragging and drop it on a format."));
        }
        HandleArgs(_args);
        if (_args.Length == 0 && _settings.FirstRunDone) OpenHome();
    }

    private static void TryIntegrateOnFirstRun()
    {
        try
        {
            ShellIntegration.SetContextMenu(true);
            ShellIntegration.SetAutostart(true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
        }
    }

    private TrayIcon CreateTray()
    {
        var tray = new TrayIcon(() => OpenHome(), OpenSettings, () => _controller.OpenForClipboard(), () => ShelfWindow.Open([]), ShowHelp, SetPaused, _settings.Paused, () => Shutdown(),
            _settings.DetectVideoLinks, SetDetectVideoLinks);
        if (_updater.Available is { } update)
            tray.OfferUpdate(update.Version, () => _ = _updater.InstallAsync(update));
        return tray;
    }

    /// <summary>A new release: a card with Update / What's new, plus an entry in the tray menu.</summary>
    private void OfferUpdate(UpdateInfo update)
    {
        _tray?.OfferUpdate(update.Version, () => _ = _updater.InstallAsync(update));
        var card = Toasts.Link(T($"Peel {update.Version} je k dispozici", $"Peel {update.Version} is available"),
            Updater.IsInstalled ? T("Aktualizace trvá pár vteřin.", "Updating takes a few seconds.") : T("Stáhněte novou verzi z GitHubu.", "Get the new version from GitHub."));
        if (card is null)
            return;
        card.Offer([
            (T("Aktualizovat", "Update"), "", true, () => { card.Dismiss(); _ = _updater.InstallAsync(update); }),
            (T("Co je nového", "What's new"), "", false, () => Updater.Open(update.PageUrl)),
        ]);
    }

    private void HandleArgs(string[] args)
    {
        if (args.Contains("--home")) OpenHome();
        if (args.Contains("--settings"))
            OpenSettings();
        var wheelIndex = Array.IndexOf(args, "--wheel");
        if (wheelIndex >= 0)
            _controller.QueuePaths(args.Skip(wheelIndex + 1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)));
    }

    private void OpenHome(IReadOnlyList<string>? paths = null) => PeelWindow.ShowSingle(_settings, _tools, _jobs, () => _controller.AllOperations, OpenSettings, paths);
    private void OpenSettings() => PeelPreferences.ShowSingle(_settings, _tools, OnSettingsChanged, () => _input?.Recover());

    private void OnSettingsChanged()
    {
        var language = _settings.Language ?? Loc.Detect();
        if (language != Loc.Current)
        {
            Loc.Current = language;
            _controller.Relocalize();
            _tray?.Dispose();
            _tray = CreateTray();
        }
        ApplyHotkey();
        if (_tray is not null)
            _tray.Paused = _settings.Paused;
    }

    /// <summary>
    /// Registers the chosen shortcut. If another app already owns it, switches to the first free preset
    /// and says so once, instead of leaving the user without a shortcut.
    /// </summary>
    private void ApplyHotkey()
    {
        var wanted = _settings.Hotkey;
        if (_hotkey is null || _activeHotkey == wanted)
            return;
        if (_hotkey.Register(wanted))
        {
            _activeHotkey = wanted;
            return;
        }

        var taken = AppSettings.HotkeyName(wanted);
        foreach (var preset in Enum.GetValues<HotkeyPreset>().Where(p => p != HotkeyPreset.None && p != wanted))
        {
            if (!_hotkey.Register(preset))
                continue;
            _activeHotkey = preset;
            _settings.Hotkey = preset;
            _settings.Save();
            SettingsWindow.RefreshIfOpen();
            var now = AppSettings.HotkeyName(preset);
            Notifications.Show(T("Zkratka změněna", "Shortcut changed"),
                T($"{taken} už používá jiná aplikace, FileAction teď používá {now}. Změnit ji můžete v nastavení.",
                  $"{taken} is used by another app, so Peel now uses {now}. You can change it in Settings."));
            return;
        }

        _activeHotkey = HotkeyPreset.None;
        Notifications.Show(T("Zkratka je obsazená", "Shortcut is taken"),
            T("Všechny nabízené zkratky už používají jiné aplikace. Kolo otevřete tažením nebo z nabídky u hodin.",
              "All offered shortcuts are used by other apps. Open the wheel by dragging or from the tray menu."), error: true);
    }

    private void SetPaused(bool paused)
    {
        _settings.Paused = paused;
        _settings.Save();
    }

    private void SetDetectVideoLinks(bool enabled)
    {
        _settings.DetectVideoLinks = enabled;
        _settings.Save();
        if (_links is not null)
            _links.Enabled = enabled;
    }

    private void ShowHelp()
    {
        var convert = AppSettings.KeyName(_settings.ConvertKey);
        var tools = AppSettings.KeyName(_settings.ToolsKey);
        var actions = AppSettings.KeyName(_settings.ActionsKey);
        var hotkey = AppSettings.HotkeyName(_settings.Hotkey);
        var text = T(
            $"""
            1. Chyťte soubor myší (v Průzkumníku, na ploše, obrázek v prohlížeči, přílohu v Outlooku…).
            2. Během tažení ťukněte na klávesu:
                 {convert} – převést na jiný formát
                 {tools} – nástroje (komprimovat, zmenšit, oříznout, spojit…)
                 {actions} – akce (kopírovat, přesunout, cesta, terminál, plovoucí panel, sdílet)
            3. Pusťte soubor na výseč kola. Do středu = zrušit.

            Kolo zůstane otevřené, dokud soubor nepustíte; klávesu můžete pustit a další klávesou přepnout kolo.
            Ťuknutí na stejnou klávesu znovu ukáže „zopakovat poslední akci“.

            Bez myši: vyberte soubory v Průzkumníku (nebo zkopírujte soubor či snímek obrazovky) a stiskněte {hotkey}.
            Šipky vyberou výseč, Enter potvrdí, Tab přepne kolo, Esc zavře.
            Také přes pravé tlačítko → „Peel…“.

            Výsledek se uloží vedle původního souboru, originál zůstane beze změny.
            """,
            $"""
            1. Grab a file with the mouse (in File Explorer, on the desktop, an image in your browser, an Outlook attachment…).
            2. While dragging, tap a key:
                 {convert} – convert to another format
                 {tools} – tools (compress, shrink, trim, merge…)
                 {actions} – actions (copy, move, path, terminal, floating shelf, share)
            3. Drop the file on a slice of the wheel. Drop it in the middle to cancel.

            The wheel stays open until you drop; you can release the key and tap another one to switch wheels.
            Tap the same key again to "repeat the last action".

            Without the mouse: select files in File Explorer (or copy a file or a screenshot) and press {hotkey}.
            This opens Peel's searchable workspace with your selection.
            Ctrl+K searches actions; Ctrl+O adds files; Escape closes to the tray.
            Also via right-click → “Peel…”.

            The result is saved next to the original file; the original stays untouched.
            """);
        MessageBox.Show(text, "Using Peel", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;
        Notifications.Show(T("Peel · chyba", "Peel · error"), e.Exception.Message, error: true);
    }

    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(Path.Combine(AppSettings.Folder, "log.txt"), $"[{DateTime.Now:s}] {ex}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        WorkerHost.CancelAll();
        _hotkey?.Dispose();
        _links?.Dispose();
        _input?.Dispose();
        _tray?.Dispose();
        _settings.Save();
        base.OnExit(e);
    }
}
