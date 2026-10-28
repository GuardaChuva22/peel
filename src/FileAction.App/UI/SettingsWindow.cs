using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using FileAction.App.Input;
using FileAction.App.Shell;
using FileAction.Core;
using static FileAction.App.UI.Controls;
using static FileAction.Core.Loc;

namespace FileAction.App.UI;

/// <summary>Legacy utility settings, using the same light Peel controls as the workspace.</summary>
internal sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    /// <summary>Set by the app: the update checker and the operations the wheel editor lists.</summary>
    public static Shell.Updater? Updater { get; set; }

    public static Func<IReadOnlyList<Operation>>? Operations { get; set; }
    private readonly AppSettings _settings;
    private readonly ToolLocator _tools;
    private readonly Action _changed;
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    internal SettingsWindow(AppSettings settings, ToolLocator tools, Action changed)
    {
        _settings = settings;
        _tools = tools;
        _changed = changed;
        Theme.Refresh();
        Title = T("Peel · nastavení", "Peel · settings");
        Width = 680;
        Height = 820;
        MinWidth = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/Peel.ico"));
        Background = Theme.Page;
        Foreground = Theme.Text;
        FontFamily = Theme.UiFont;
        Content = _scroll;
        Build();
        Action refresh = () => Dispatcher.BeginInvoke(Rebuild);
        if (Updater is not null)
            Updater.Changed += refresh;
        Closed += (_, _) =>
        {
            _open = null;
            if (Updater is not null)
                Updater.Changed -= refresh;
        };
    }

    public static void ShowSingle(AppSettings settings, ToolLocator tools, Action changed)
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }
        _open = new SettingsWindow(settings, tools, changed);
        _open.Show();
        _open.Activate();
    }

    /// <summary>Redraws the open settings window after the app changed a setting itself.</summary>
    public static void RefreshIfOpen() => _open?.Rebuild();

    private void Save()
    {
        _settings.Save();
        _changed();
    }

    /// <summary>Rebuilds every card (used when one change affects others), keeping the scroll position.</summary>
    private void Rebuild()
    {
        var offset = _scroll.VerticalOffset;
        Build();
        _scroll.UpdateLayout();
        _scroll.ScrollToVerticalOffset(offset);
    }

    private void Build()
    {
        var root = new StackPanel { Margin = new Thickness(32, 28, 32, 32), MaxWidth = 720 };
        root.Children.Add(Header());

        root.Children.Add(SectionHeader(T("Jazyk", "Language")));
        root.Children.Add(Card(T("Jazyk aplikace", "App language"), null,
            Segmented<Core.Language?>(
                [(T("Podle Windows", "Windows"), null), ("Čeština", Core.Language.Czech), ("English", Core.Language.English)],
                _settings.Language,
                value =>
                {
                    _settings.Language = value;
                    Save();
                    // Rebuild the window in the new language.
                    Title = T("Peel · nastavení", "Peel · settings");
                    Rebuild();
                }),
            glyph: "\uE774"));

        root.Children.Add(SectionHeader(T("Gesto", "Gesture")));
        root.Children.Add(Card(
            T("Chyťte soubor a během tažení ťukněte na klávesu", "Grab a file and tap a key while dragging"),
            T("Otevře se kolo; soubor pusťte na výseč. Do středu = zrušit.", "A wheel opens; drop the file on a slice. Drop in the middle to cancel."),
            null, glyph: Glyphs.Extract));
        foreach (var (wheel, description) in new[]
                 {
                     (WheelKind.Convert, T("Převod na jiný formát", "Convert to another format")),
                     (WheelKind.Tools, T("Komprimovat, zmenšit, oříznout, spojit…", "Compress, shrink, trim, merge…")),
                     (WheelKind.Actions, T("Kopírovat, přesunout, cesta, terminál, sdílet…", "Copy, move, path, terminal, share…")),
                 })
        {
            root.Children.Add(Card(AppSettings.WheelName(wheel), description, KeyChooser(wheel)));
        }
        root.Children.Add(Card(T("Upravit kolo", "Customize wheels"),
            T("Oblíbené výseče jako první, nepoužívané skrýt. Ťuknutí na stejnou klávesu znovu (nebo R) zopakuje poslední akci.",
              "Favourite slices first, hide the ones you never use. Tap the same key again (or press R) to repeat the last action."),
            Button(T("Upravit…", "Customize…"), () =>
            {
                if (Operations is not null)
                    WheelEditorWindow.ShowSingle(_settings, Operations(), _changed);
            }, glyph: "\uE70F"),
            glyph: "\uE8EE"));
        root.Children.Add(Card(T("Pozastavit gesta", "Pause gestures"), T("Kolo se při tažení neotevře.", "The wheel won’t open while dragging."),
            Toggle(_settings.Paused, v =>
            {
                _settings.Paused = v;
                Save();
            })));

        root.Children.Add(SectionHeader(T("Klávesnice", "Keyboard")));
        root.Children.Add(Card(T("Zkratka pro výběr v Průzkumníku", "Shortcut for the File Explorer selection"),
            T("Šipky vyberou výseč, Enter potvrdí, Tab přepne kolo, Esc zavře.", "Arrows pick a slice, Enter runs it, Tab switches wheels, Esc closes."),
            null,
            below: Wrap(Segmented(
                Enum.GetValues<HotkeyPreset>().Where(p => p != HotkeyPreset.None).Append(HotkeyPreset.None)
                    .Select(p => (AppSettings.HotkeyName(p), p)).ToList(),
                _settings.Hotkey,
                value =>
                {
                    _settings.Hotkey = value;
                    Save();
                })),
            glyph: "\uE765"));

        root.Children.Add(SectionHeader(T("Výsledky", "Results")));
        var folderLine = Text(_settings.OutputDirectory ?? "", 12, brush: Theme.Muted, wrap: false);
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        folderRow.Children.Add(Button(T("Vybrat složku…", "Choose folder…"), PickFolder, glyph: Glyphs.Folder));
        folderLine.Margin = new Thickness(12, 0, 0, 0);
        folderLine.VerticalAlignment = VerticalAlignment.Center;
        folderRow.Children.Add(folderLine);
        var toFolder = !string.IsNullOrWhiteSpace(_settings.OutputDirectory);
        var below = new StackPanel();
        below.Children.Add(Wrap(Segmented(
            [(T("Vedle originálu", "Next to the original"), false), (T("Do složky", "To a folder"), true)],
            toFolder,
            value =>
            {
                if (value)
                {
                    PickFolder();
                }
                else
                {
                    _settings.OutputDirectory = null;
                    Save();
                    Rebuild();
                }
            })));
        if (toFolder)
            below.Children.Add(folderRow);
        root.Children.Add(Card(T("Kam ukládat", "Save results"), T("Originál zůstává vždy beze změny.", "The original file is never changed."), null, below, Glyphs.Folder));
        root.Children.Add(Card(T("Po dokončení ukázat ve složce", "Show in folder when done"), null,
            Toggle(_settings.RevealResult, v =>
            {
                _settings.RevealResult = v;
                Save();
            })));

        root.Children.Add(SectionHeader(T("Kvalita", "Quality")));
        root.Children.Add(Card(T("JPG, WebP a AVIF", "JPG, WebP and AVIF"), T("Kvalita při převodu obrázků", "Quality when converting images"),
            Slider(50, 100, _settings.Quality.JpegQuality, v => _settings.Quality.JpegQuality = v, Save), glyph: Glyphs.Photo));
        root.Children.Add(Card(T("Komprimovat", "Compress"), T("Kvalita nástroje Komprimovat", "Quality of the Compress tool"),
            Slider(30, 95, _settings.Quality.CompressQuality, v => _settings.Quality.CompressQuality = v, Save), glyph: Glyphs.Shrink));
        root.Children.Add(Card(T("PDF na obrázky", "PDF to images"), T("Rozlišení v DPI", "Resolution in DPI"),
            Slider(72, 600, _settings.Quality.PdfDpi, v => _settings.Quality.PdfDpi = v, Save), glyph: Glyphs.Page));

        root.Children.Add(SectionHeader(T("Systém", "System")));
        root.Children.Add(Card(T("Spouštět po přihlášení", "Start when I sign in"), null,
            Toggle(ShellIntegration.IsAutostartEnabled, v => Apply(() => ShellIntegration.SetAutostart(v))), glyph: "\uE7E8"));
        root.Children.Add(Card(T("Kontextová nabídka Průzkumníka", "File Explorer context menu"), T("Položka „Peel…“ po kliknutí pravým tlačítkem", "A “Peel…” item on right-click"),
            Toggle(ShellIntegration.IsContextMenuEnabled, v => Apply(() => ShellIntegration.SetContextMenu(v))), glyph: "\uE8A7"));

        root.Children.Add(SectionHeader(T("Místní verze", "Local build")));
        root.Children.Add(Card("Peel 1.1",
            T("Zpracování v oddělených procesech, fronta úloh a obnova gest po probuzení.",
              "Isolated conversions, a two-job queue and gesture recovery after sleep. Updates are managed locally."),
            null, glyph: "\uE777"));

        root.Children.Add(SectionHeader(T("Pomocné programy", "Helper programs")));
        root.Children.Add(Card(T("Co zvládá FileAction sám", "Built in"),
            T("Obrázky, PDF, tabulky, titulky a archivy. Audio a video potřebují FFmpeg, dokumenty Pandoc a soubory Office LibreOffice.",
              "Images, PDFs, spreadsheets, subtitles and archives. Audio and video need FFmpeg, documents need Pandoc and Office files need LibreOffice."),
            Button(T("Zkontrolovat znovu", "Check again"), () =>
            {
                _tools.Refresh();
                Rebuild();
            }, glyph: "\uE72C"),
            glyph: "\uE90F"));
        foreach (var tool in Enum.GetValues<ExternalTool>())
            root.Children.Add(ToolCard(tool));

        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        var footer = Text($"Peel {version?.ToString(3)} · " + T("vše běží lokálně, soubory se nikam neodesílají.", "everything runs locally, files never leave your PC."), 12, brush: Theme.Muted);
        footer.Margin = new Thickness(2, 24, 0, 0);
        root.Children.Add(footer);

        _scroll.Content = root;
    }

    private static FrameworkElement Wrap(FrameworkElement element)
    {
        element.HorizontalAlignment = HorizontalAlignment.Left;
        element.Margin = new Thickness(0, 12, 0, 0);
        return element;
    }

    private UIElement Header()
    {
        var panel = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var image = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/Peel.png")),
            Width = 56,
            Height = 56,
            Margin = new Thickness(0, 0, 18, 0),
        };
        panel.Children.Add(image);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = Text("Peel", 28, FontWeights.SemiBold);
        title.FontFamily = Theme.DisplayFont;
        text.Children.Add(title);
        text.Children.Add(Text(T("Chyťte soubor, ťukněte na klávesu, pusťte ho na formát.", "Grab a file, tap a key, drop it on a format."), 13.5, brush: Theme.Muted));
        panel.Children.Add(text);
        return panel;
    }

    private Border KeyChooser(WheelKind wheel) => Segmented(
        [("Shift", ModifierKey.Shift), ("Ctrl", ModifierKey.Ctrl), ("Alt", ModifierKey.Alt), (T("Vyp.", "Off"), ModifierKey.None)],
        _settings.KeyFor(wheel),
        key =>
        {
            var old = _settings.KeyFor(wheel);
            // Keep keys unique: the wheel that had this key gets the old one.
            foreach (var other in new[] { WheelKind.Convert, WheelKind.Tools, WheelKind.Actions })
            {
                if (other != wheel && key != ModifierKey.None && _settings.KeyFor(other) == key)
                    SetKey(other, old);
            }
            SetKey(wheel, key);
            Save();
            Rebuild();
        });

    private void SetKey(WheelKind wheel, ModifierKey key)
    {
        switch (wheel)
        {
            case WheelKind.Convert:
                _settings.ConvertKey = key;
                break;
            case WheelKind.Tools:
                _settings.ToolsKey = key;
                break;
            default:
                _settings.ActionsKey = key;
                break;
        }
    }

    private void PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = T("Výstupní složka", "Output folder") };
        if (dialog.ShowDialog(this) == true)
        {
            _settings.OutputDirectory = dialog.FolderName;
            Save();
        }
        Rebuild();
    }

    private void Apply(Action action)
    {
        try
        {
            action();
            Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Peel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private Border UpdateCard()
    {
        var updater = Updater;
        var current = Shell.Updater.Current;
        string status;
        UIElement? action = null;
        if (updater is null)
        {
            status = "";
        }
        else if (updater.Installing)
        {
            status = T("Stahuji a instaluji…", "Downloading and installing…");
        }
        else if (updater.Available is { } update)
        {
            status = T($"K dispozici je verze {update.Version}.", $"Version {update.Version} is available.");
            action = Button(Shell.Updater.IsInstalled ? T("Aktualizovat", "Update") : T("Stáhnout", "Download"),
                () => _ = updater.InstallAsync(update), primary: true, glyph: "\uE896");
        }
        else
        {
            status = updater.LastError is not null ? T("Kontrola se nepovedla: ", "Check failed: ") + updater.LastError
                : updater.LastChecked is { } at ? T($"Máte nejnovější verzi (kontrola {at:HH:mm}).", $"You're up to date (checked {at:HH:mm}).")
                : T("Zatím nezkontrolováno.", "Not checked yet.");
            action = Button(T("Zkontrolovat", "Check now"), async () =>
            {
                await updater.CheckAsync();
                Rebuild();
            }, glyph: "\uE72C");
        }
        return Card($"FileAction {current}", status, action, glyph: "\uE777");
    }

    private Border ToolCard(ExternalTool tool)
    {
        var path = _tools.Find(tool);
        var description = path ?? tool switch
        {
            ExternalTool.Ffmpeg => T("Audio a video", "Audio and video"),
            ExternalTool.Pandoc => T("Dokumenty (DOCX, Markdown, EPUB…)", "Documents (DOCX, Markdown, EPUB…)"),
            _ => T("DOC, PPTX, ODS a převod do PDF", "DOC, PPTX, ODS and conversion to PDF"),
        };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(Badge(path is null ? T("chybí", "missing") : T("nalezeno", "found"), path is not null));
        if (path is null)
        {
            var id = tool switch
            {
                ExternalTool.Ffmpeg => "Gyan.FFmpeg",
                ExternalTool.Pandoc => "JohnMacFarlane.Pandoc",
                _ => "TheDocumentFoundation.LibreOffice",
            };
            var install = Button(T("Nainstalovat", "Install"), () =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("cmd.exe",
                        $"/c winget install -e --id {id} --accept-source-agreements --accept-package-agreements & echo. & echo {T("Hotovo. Ve FileAction kliknete na Zkontrolovat znovu.", "Done. Click Check again in FileAction.")} & pause")
                    { UseShellExecute = true });
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Peel", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }, primary: true, glyph: "\uE896");
            install.Margin = new Thickness(10, 0, 0, 0);
            right.Children.Add(install);
        }
        return Card(ToolLocator.DisplayName(tool), description, right);
    }
}
