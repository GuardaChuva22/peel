using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FileAction.Core;
using static FileAction.App.UI.Controls;
using static FileAction.Core.Loc;

namespace FileAction.App.UI;

/// <summary>
/// Customise the wheels: hide slices you never use and star favourites, which then always come first
/// (so they stay in the same place and your hand learns where to drop).
/// </summary>
internal sealed class WheelEditorWindow : Window
{
    private static WheelEditorWindow? _open;
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<Operation> _operations;
    private readonly Action _changed;
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    /// <summary>File types shown as groups, with sample names used to find which slices apply.</summary>
    private static (string Czech, string English, string Glyph, string[] Samples)[] Groups =>
    [
        // Two formats per group, so "convert to X" shows up even when one sample already is X.
        ("Obrázky", "Images", Glyphs.Photo, ["a.png", "a.jpg", "a.gif"]),
        ("Video", "Video", Glyphs.Video, ["a.mp4", "a.mov"]),
        ("Audio", "Audio", Glyphs.Audio, ["a.mp3", "a.wav"]),
        ("PDF", "PDF", Glyphs.Page, ["a.pdf"]),
        ("Dokumenty", "Documents", Glyphs.Document, ["a.docx", "a.md"]),
        ("Prezentace", "Slides", Glyphs.Page, ["a.pptx", "a.odp"]),
        ("Tabulky", "Spreadsheets", Glyphs.Table, ["a.xlsx", "a.csv"]),
        ("Titulky", "Subtitles", Glyphs.Subtitles, ["a.srt", "a.vtt"]),
        ("Archivy", "Archives", Glyphs.Pack, ["a.zip", "a.tar"]),
    ];

    internal WheelEditorWindow(AppSettings settings, IReadOnlyList<Operation> operations, Action changed)
    {
        _settings = settings;
        _operations = operations;
        _changed = changed;
        Theme.Refresh();
        Title = T("Peel · úprava kola", "Peel · customize wheels");
        Width = 640;
        Height = 820;
        MinWidth = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/Peel.ico"));
        Background = Theme.Page;
        Foreground = Theme.Text;
        FontFamily = Theme.UiFont;
        Content = _scroll;
        Build();
        Closed += (_, _) => _open = null;
    }

    public static void ShowSingle(AppSettings settings, IReadOnlyList<Operation> operations, Action changed)
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }
        _open = new WheelEditorWindow(settings, operations, changed);
        _open.Show();
        _open.Activate();
    }

    private void Save()
    {
        _settings.Save();
        _changed();
    }

    private void Build()
    {
        var offset = _scroll.VerticalOffset;
        var root = new StackPanel { Margin = new Thickness(32, 28, 32, 32) };
        var title = Text(T("Úprava kola", "Customize wheels"), 26, FontWeights.SemiBold);
        title.FontFamily = Theme.DisplayFont;
        root.Children.Add(title);
        var intro = Text(T("Hvězdičkou označte oblíbené výseče, budou vždy první a na stejném místě. Přepínačem výseč skryjete.",
                           "Star your favourite slices: they always come first, in the same place. Use the switch to hide a slice."),
            13.5, brush: Theme.Muted);
        intro.Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(intro);
        var reset = Button(T("Obnovit výchozí", "Reset to default"), () =>
        {
            _settings.HiddenOperations.Clear();
            _settings.FavoriteOperations.Clear();
            Save();
            Build();
        }, glyph: "");
        reset.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(reset);

        // Slices that work for anything (archive, actions) are listed once, separately.
        var folder = new FileItem(Path.GetTempPath());
        var shown = new HashSet<string>();
        foreach (var (czech, english, glyph, samples) in Groups)
        {
            var items = samples.Select(s => new FileItem(s)).ToList();
            var ops = _operations
                .Where(o => o.Wheel != WheelKind.Actions && !o.AppliesTo([folder]) && Applies(o, items) && shown.Add(o.Id))
                .ToList();
            AddGroup(root, T(czech, english), glyph, ops);
        }
        AddGroup(root, T("Všechny soubory a složky", "Any file or folder"), Glyphs.Folder,
            _operations.Where(o => shown.Add(o.Id)).ToList());

        _scroll.Content = root;
        _scroll.UpdateLayout();
        _scroll.ScrollToVerticalOffset(offset);
    }

    private static bool Applies(Operation op, IReadOnlyList<FileItem> samples) =>
        samples.Any(s => op.AppliesTo([s]) || op.AppliesTo([s, s with { Path = "b" + Path.GetExtension(s.Path) }]));

    private void AddGroup(Panel root, string name, string glyph, IReadOnlyList<Operation> ops)
    {
        if (ops.Count == 0)
            return;
        var header = SectionHeader(name);
        root.Children.Add(header);
        var list = new StackPanel();
        foreach (var wheel in new[] { WheelKind.Convert, WheelKind.Tools, WheelKind.Actions })
        {
            var group = WheelLayout.Arrange(ops.Where(o => o.Wheel == wheel), [], _settings.FavoriteOperations);
            foreach (var op in group)
                list.Children.Add(Row(op, list.Children.Count > 0));
        }
        root.Children.Add(new Border
        {
            Background = Theme.Card,
            BorderBrush = Theme.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(8, 6, 8, 6),
            Child = list,
        });
    }

    private UIElement Row(Operation op, bool separator)
    {
        var hidden = _settings.HiddenOperations.Contains(op.Id);
        var favorite = _settings.FavoriteOperations.Contains(op.Id);

        var star = new TextBlock
        {
            Text = favorite ? "" : "",
            FontFamily = Theme.Icons,
            FontSize = 16,
            Foreground = favorite ? Theme.Success : Theme.Muted,
            Padding = new Thickness(8),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = T("Oblíbená – vždy první", "Favourite – always first"),
            Focusable = true,
        };
        void ToggleFavorite()
        {
            if (!_settings.FavoriteOperations.Remove(op.Id))
            {
                _settings.FavoriteOperations.Add(op.Id);
                _settings.HiddenOperations.Remove(op.Id);
            }
            Save();
            Build();
        }
        System.Windows.Automation.AutomationProperties.SetName(star, "Favourite " + op.Label);
        star.MouseLeftButtonUp += (_, _) => ToggleFavorite();
        star.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { ToggleFavorite(); e.Handled = true; } };
        star.GotKeyboardFocus += (_, _) => star.Background = Theme.AccentSoft;
        star.LostKeyboardFocus += (_, _) => star.Background = System.Windows.Media.Brushes.Transparent;

        var toggle = Toggle(!hidden, visible =>
        {
            if (visible)
                _settings.HiddenOperations.Remove(op.Id);
            else if (!_settings.HiddenOperations.Contains(op.Id))
                _settings.HiddenOperations.Add(op.Id);
            Save();
        });
        System.Windows.Automation.AutomationProperties.SetName(toggle, "Show " + op.Label);
        toggle.Margin = new Thickness(8, 0, 8, 0);
        toggle.VerticalAlignment = VerticalAlignment.Center;

        var isFormat = op.Wheel == WheelKind.Convert && op.Label.Length <= 6 && op.Label.ToUpperInvariant() == op.Label;
        var icon = new TextBlock
        {
            Text = isFormat ? "" : op.Glyph,
            FontFamily = Theme.Icons,
            FontSize = 16,
            Foreground = Theme.Text,
            Width = 28,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(Text(op.Label, 14, isFormat ? FontWeights.SemiBold : FontWeights.Normal, wrap: false));
        var detail = AppSettings.WheelName(op.Wheel) + (string.IsNullOrEmpty(op.Subtitle) ? "" : " · " + op.Subtitle);
        label.Children.Add(Text(detail, 11.5, brush: Theme.Muted, wrap: false));

        var row = new DockPanel { Margin = new Thickness(10, 6, 2, 6) };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(toggle, Dock.Right);
        DockPanel.SetDock(star, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(toggle);
        row.Children.Add(star);
        row.Children.Add(label);
        return new Border
        {
            BorderBrush = Theme.PanelBorder,
            BorderThickness = new Thickness(0, separator ? 1 : 0, 0, 0),
            Child = row,
        };
    }
}
