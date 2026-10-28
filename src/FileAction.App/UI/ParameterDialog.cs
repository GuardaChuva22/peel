using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileAction.Core;
using static FileAction.Core.Loc;

namespace FileAction.App.UI;

/// <summary>Asks for the values an operation needs (e.g. trim start and end).</summary>
internal sealed class ParameterDialog : Window
{
    private readonly Dictionary<string, TextBox> _boxes = new();

    public ParameterDialog(Operation op, IReadOnlyList<FileItem> items)
    {
        Title = op.Label;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        MinWidth = 420;
        Theme.Refresh();
        Background = Theme.Page;
        FontFamily = Theme.UiFont;
        Foreground = Theme.Text;

        var stack = new StackPanel { Margin = new Thickness(28, 24, 28, 26) };
        stack.Children.Add(new TextBlock { Text = op.Label, FontFamily = Theme.DisplayFont, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        stack.Children.Add(new TextBlock
        {
            Text = items.Count == 1 ? items[0].Name : Items(items.Count),
            Foreground = Theme.Muted,
            Margin = new Thickness(0, 0, 0, 18),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 420,
        });

        foreach (var p in op.Parameters)
        {
            stack.Children.Add(new TextBlock { Text = p.Label, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) });
            var box = Controls.Input(p.Label, p.DefaultValue);
            box.MinWidth = 330;
            _boxes[p.Key] = box;
            stack.Children.Add(box);
            if (p.Hint is not null)
                stack.Children.Add(new TextBlock { Text = p.Hint, Foreground = Theme.Muted, FontSize = 12, MaxWidth = 420, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
        }

        var ok = Controls.Button(T("Spustit", "Run"), () => DialogResult = true, primary: true);
        ok.Margin = new Thickness(0, 0, 8, 0);
        var cancel = Controls.Button(T("Zrušit", "Cancel"), () => DialogResult = false);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                DialogResult = true;
            else if (e.Key == Key.Escape)
                DialogResult = false;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        stack.Children.Add(buttons);
        Content = stack;

        Loaded += (_, _) =>
        {
            ForegroundHelper.Force(this);
            if (_boxes.Values.FirstOrDefault() is { } first)
            {
                first.Focus();
                first.SelectAll();
                Keyboard.Focus(first);
            }
        };
    }

    public IReadOnlyDictionary<string, string> Values => _boxes.ToDictionary(kv => kv.Key, kv => kv.Value.Text);
}
