using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace FileAction.App.UI;

/// <summary>
/// Quiet, rounded native controls shared by Peel's utility dialogs.
/// </summary>
internal static class Controls
{
    public static TextBlock Text(string text, double size = 14, FontWeight? weight = null, Brush? brush = null, bool wrap = true) => new()
    {
        Text = text,
        FontFamily = Theme.UiFont,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = brush ?? Theme.Text,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    public static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        FontFamily = Theme.DisplayFont,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Foreground = Theme.Text,
        Margin = new Thickness(2, 26, 0, 8),
    };

    /// <summary>A rounded card with a title/description on the left and a control on the right.</summary>
    public static Border Card(string title, string? description, UIElement? control, UIElement? below = null, string? glyph = null)
    {
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Text(title, 14, FontWeights.Normal));
        if (!string.IsNullOrEmpty(description))
        {
            var d = Text(description, 12, brush: Theme.Muted);
            d.Margin = new Thickness(0, 2, 0, 0);
            left.Children.Add(d);
        }

        var row = new DockPanel { LastChildFill = true };
        if (glyph is not null)
        {
            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = Theme.Icons,
                FontSize = 18,
                Foreground = Theme.Text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0),
            };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
        }
        if (control is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            fe.Margin = new Thickness(16, 0, 0, 0);
            DockPanel.SetDock(fe, Dock.Right);
            row.Children.Add(fe);
        }
        row.Children.Add(left);

        var content = new StackPanel();
        content.Children.Add(row);
        if (below is not null)
            content.Children.Add(below);

        return new Border
        {
            Background = Theme.Card,
            BorderBrush = Theme.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18, 16, 18, 16),
            Margin = new Thickness(0, 0, 0, 8),
            Child = content,
        };
    }

    /// <summary>A row of mutually exclusive pills; the active one is filled with the accent gradient.</summary>
    public static Border Segmented<T>(IReadOnlyList<(string Label, T Value)> options, T selected, Action<T> changed)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        var pills = new List<(System.Windows.Controls.Button Pill, TextBlock Text, T Value)>();
        void Select(T value)
        {
            foreach (var (pill, text, v) in pills)
            {
                var on = EqualityComparer<T>.Default.Equals(v, value);
                pill.Background = on ? Theme.AccentGradient : Brushes.Transparent;
                text.Foreground = on ? Theme.TextOnAccent : Theme.Text;
                text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
        foreach (var (label, value) in options)
        {
            var text = new TextBlock { Text = label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            var pill = new System.Windows.Controls.Button
            {
                Padding = new Thickness(12, 7, 12, 7),
                MinHeight = 32,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Content = text,
                Template = ButtonTemplate(9),
            };
            AutomationProperties.SetName(pill, label);
            var v = value;
            pill.Click += (_, _) =>
            {
                Select(v);
                changed(v);
            };
            pills.Add((pill, text, value));
            panel.Children.Add(pill);
        }
        Select(selected);
        return new Border
        {
            Background = Theme.Track,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(3),
            Child = panel,
        };
    }

    /// <summary>A native keyboard-accessible switch with a quiet mint state.</summary>
    public static FrameworkElement Toggle(bool value, Action<bool> changed)
    {
        var knob = new Ellipse { Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Left };
        var track = new ToggleButton
        {
            Width = 44,
            Height = 26,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Content = knob,
            IsChecked = value,
            Template = ButtonTemplate(13),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(track, "Toggle setting");
        var translate = new TranslateTransform();
        knob.RenderTransform = translate;
        var on = value;
        void Paint(bool animate)
        {
            track.Background = on ? Theme.AccentGradient : Brushes.Transparent;
            track.BorderBrush = on ? Brushes.Transparent : Theme.Muted;
            knob.Fill = on ? Theme.TextOnAccent : Theme.Muted;
            var x = on ? 25.0 : 5.0;
            if (animate && SystemParameters.ClientAreaAnimation)
                translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase() });
            else
                translate.X = x;
        }
        track.Click += (_, _) =>
        {
            on = track.IsChecked == true;
            Paint(true);
            changed(on);
        };
        Paint(false);
        return track;
    }

    /// <summary>A slim slider with an accent-filled track and a round thumb.</summary>
    public static FrameworkElement Slider(int min, int max, int value, Action<int> changed, Action committed)
    {
        const double width = 200;
        var label = new TextBlock { Width = 40, FontSize = 13, Foreground = Theme.Text, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.AccentGradient, HorizontalAlignment = HorizontalAlignment.Left };
        var thumb = new Ellipse
        {
            Width = 18,
            Height = 18,
            Fill = Brushes.White,
            Stroke = Theme.PanelBorder,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            Effect = Theme.Shadow(6),
        };
        var surface = new Grid { Width = width, Height = 24, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        surface.Focusable = true;
        AutomationProperties.SetName(surface, "Adjust value");
        surface.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.Track });
        surface.Children.Add(fill);
        surface.Children.Add(thumb);

        var current = value;
        void Show()
        {
            var t = (current - min) / (double)(max - min);
            fill.Width = Math.Max(0, t * width);
            thumb.Margin = new Thickness(t * (width - 18), 0, 0, 0);
            label.Text = current.ToString();
        }
        void SetFrom(Point p)
        {
            var t = Math.Clamp((p.X - 9) / (width - 18), 0, 1);
            var v = (int)Math.Round(min + t * (max - min));
            if (v == current)
                return;
            current = v;
            Show();
            changed(v);
        }
        surface.MouseLeftButtonDown += (_, e) =>
        {
            surface.Focus();
            surface.CaptureMouse();
            SetFrom(e.GetPosition(surface));
        };
        surface.MouseMove += (_, e) =>
        {
            if (surface.IsMouseCaptured)
                SetFrom(e.GetPosition(surface));
        };
        surface.MouseLeftButtonUp += (_, _) =>
        {
            surface.ReleaseMouseCapture();
            committed();
        };
        surface.KeyDown += (_, e) =>
        {
            var next = e.Key switch { Key.Left or Key.Down => current - 1, Key.Right or Key.Up => current + 1, Key.Home => min, Key.End => max, _ => current };
            if (next == current) return;
            current = Math.Clamp(next, min, max);
            Show(); changed(current); committed(); e.Handled = true;
        };
        surface.GotKeyboardFocus += (_, _) => thumb.Stroke = Theme.Success;
        surface.LostKeyboardFocus += (_, _) => thumb.Stroke = Theme.PanelBorder;
        Show();

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(surface);
        row.Children.Add(label);
        return row;
    }

    /// <summary>A rounded button; <paramref name="primary"/> ones use the accent gradient.</summary>
    public static Border Button(string text, Action click, bool primary = false, string? glyph = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var foreground = primary ? Theme.TextOnAccent : Theme.Text;
        if (glyph is not null)
        {
            content.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = Theme.Icons,
                FontSize = 12,
                Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 7, 0),
            });
        }
        content.Children.Add(new TextBlock { Text = text, FontSize = 13, Foreground = foreground, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center });
        var normal = primary ? Theme.AccentGradient : Theme.Card;
        var native = new System.Windows.Controls.Button
        {
            Background = normal,
            BorderBrush = primary ? Brushes.Transparent : Theme.PanelBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 8, 14, 8),
            MinHeight = 36,
            Cursor = Cursors.Hand,
            Content = content,
            Template = ButtonTemplate(10),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(native, text);
        native.Click += (_, _) => click();
        return new Border { Child = native, HorizontalAlignment = HorizontalAlignment.Left };
    }

    private static ControlTemplate ButtonTemplate(double radius)
    {
        var template = new ControlTemplate(typeof(ButtonBase));
        var border = new FrameworkElementFactory(typeof(Border), "Surface");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        foreach (var (property, source) in new[] { (Border.BackgroundProperty, "Background"), (Border.BorderBrushProperty, "BorderBrush"), (Border.BorderThicknessProperty, "BorderThickness"), (Border.PaddingProperty, "Padding") })
            border.SetBinding(property, new Binding(source) { RelativeSource = RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.HorizontalAlignmentProperty, new Binding("HorizontalContentAlignment") { RelativeSource = RelativeSource.TemplatedParent });
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Success, "Surface"));
        var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Success, "Surface"));
        focused.Setters.Add(new Setter(Border.EffectProperty, Theme.Glow(), "Surface"));
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.78, "Surface"));
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4, "Surface"));
        template.Triggers.Add(hover); template.Triggers.Add(focused); template.Triggers.Add(pressed); template.Triggers.Add(disabled);
        return template;
    }

    public static TextBox Input(string label, string value)
    {
        var box = new TextBox { Text = value, MinHeight = 40, Padding = new Thickness(12, 9, 12, 9), FontSize = 14,
            Background = Theme.Card, Foreground = Theme.Text, CaretBrush = Theme.Success, BorderBrush = Theme.PanelBorder,
            BorderThickness = new Thickness(1), SelectionBrush = Theme.AccentSoft, VerticalContentAlignment = VerticalAlignment.Center };
        var template = new ControlTemplate(typeof(TextBox));
        var border = new FrameworkElementFactory(typeof(Border), "Surface");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        foreach (var (property, source) in new[] { (Border.BackgroundProperty, "Background"), (Border.BorderBrushProperty, "BorderBrush"), (Border.BorderThicknessProperty, "BorderThickness"), (Border.PaddingProperty, "Padding") })
            border.SetBinding(property, new Binding(source) { RelativeSource = RelativeSource.TemplatedParent });
        border.AppendChild(new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost"));
        template.VisualTree = border;
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Success, "Surface"));
        template.Triggers.Add(focus);
        box.Template = template;
        AutomationProperties.SetName(box, label);
        return box;
    }

    /// <summary>A small coloured status label, e.g. "found" / "missing".</summary>
    public static Border Badge(string text, bool good) => new()
    {
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(8, 2, 8, 3),
        Background = new SolidColorBrush(Color.FromArgb(0x26, good ? (byte)0x3C : (byte)0xF0, good ? (byte)0xC3 : (byte)0x5A, good ? (byte)0x6E : (byte)0x5A)),
        Child = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = good ? Theme.Success : Theme.Error },
    };
}
