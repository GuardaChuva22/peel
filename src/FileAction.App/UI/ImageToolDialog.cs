using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using FileAction.Core;
using FileAction.App.Ocr;
using ImageMagick;
using ImageMagick.Drawing;
using Orientation = System.Windows.Controls.Orientation;

namespace FileAction.App.UI;

/// <summary>Local image tool preview. Drag regions in oriented image pixels; originals remain untouched.</summary>
internal sealed class ImageToolDialog : Window
{
    private readonly Operation _operation;
    private readonly IReadOnlyList<FileItem> _items;
    private readonly Dictionary<string, TextBox> _boxes = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Image _result = new() { Stretch = Stretch.Uniform, Height = 126 };
    private readonly Canvas _surface = new() { Background = Brushes.Transparent, ClipToBounds = true };
    private readonly Rectangle _region = new() { Stroke = Theme.Accent, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(40, 55, 240, 98)), IsHitTestVisible = false };
    private readonly TextBlock _status = new() { Text = "Loading preview…", Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Foreground = Theme.Error, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Border _run;
    private readonly DispatcherTimer _debounce;
    private byte[]? _previewPng;
    private int _width;
    private int _height;
    private double _scale = 1;
    private Point? _dragOrigin;
    private int _generation;
    private bool _closed;
    private bool _updatingFields;
    private CancellationTokenSource? _previewCancellation;
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private readonly List<PixelRegion> _detectedRegions = [];
    private readonly Canvas _detectionOverlay = new() { IsHitTestVisible = false };
    private bool _manualSelection;
    private bool _detecting;

    public static bool Supports(Operation operation, IReadOnlyList<FileItem> items) =>
        items.Count > 0 && (operation.Id == "image.collage" || items.Count == 1) &&
        operation.Id is "image.adjust" or "image.crop" or "image.annotate" or "image.background" or "image.redact" or "image.collage";

    public ImageToolDialog(Operation operation, IReadOnlyList<FileItem> items)
    {
        _operation = operation;
        _items = items;
        Theme.Refresh();
        Title = operation.Label;
        Width = 950;
        Height = 710;
        MinWidth = 760;
        MinHeight = 590;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Page;
        Foreground = Theme.Text;
        FontFamily = Theme.UiFont;
        Topmost = true;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); UpdatePreview(); };

        var root = new Grid { Margin = new Thickness(28), Background = Theme.Page };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        heading.Children.Add(new TextBlock { Text = operation.Label, FontSize = 24, FontWeight = FontWeights.SemiBold, FontFamily = Theme.DisplayFont });
        heading.Children.Add(new TextBlock { Text = items.Count == 1 ? items[0].Name : $"{items.Count} images", Foreground = Theme.Muted, Margin = new Thickness(0, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        root.Children.Add(heading);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        var preview = new Grid { Margin = new Thickness(0, 0, 24, 0) };
        preview.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        preview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var host = new Border { Background = Theme.Card, BorderBrush = Theme.PanelBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Padding = new Thickness(16), Effect = Theme.Shadow(16) };
        var viewbox = new Viewbox { Stretch = Stretch.Uniform, Child = _surface };
        host.Child = viewbox;
        preview.Children.Add(host);
        _surface.Children.Add(_image);
        _surface.Children.Add(_detectionOverlay);
        _surface.Children.Add(_region);
        _region.Visibility = Visibility.Collapsed;
        _surface.MouseLeftButtonDown += StartRegion;
        _surface.MouseMove += MoveRegion;
        _surface.MouseLeftButtonUp += EndRegion;
        _surface.MouseRightButtonUp += (_, e) =>
        {
            var point = e.GetPosition(_surface);
            var region = _detectedRegions.LastOrDefault(region => new Rect(region.X * _scale, region.Y * _scale, region.Width * _scale, region.Height * _scale).Contains(point));
            if (region is null) return;
            _detectedRegions.Remove(region);
            DrawRegion();
            SchedulePreview();
            e.Handled = true;
        };
        _status.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(_status, 1);
        preview.Children.Add(_status);
        if (operation.Id is "image.crop" or "image.redact")
        {
            preview.RowDefinitions.Insert(1, new RowDefinition { Height = new GridLength(150) });
            Grid.SetRow(_status, 2);
            var resultPanel = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var label = new TextBlock { Text = "Result preview", Foreground = Theme.Muted, Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(label, Dock.Top);
            resultPanel.Children.Add(label);
            resultPanel.Children.Add(_result);
            Grid.SetRow(resultPanel, 1);
            preview.Children.Add(resultPanel);
        }
        body.Children.Add(preview);

        var fields = new StackPanel();
        if (operation.Id == "image.redact")
        {
            fields.Children.Add(new TextBlock { Text = "Find private areas", FontSize = 15, FontWeight = FontWeights.SemiBold });
            var detectionButtons = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
            var faces = Controls.Button("Find faces", async () => await DetectRegions(true));
            faces.Margin = new Thickness(0, 0, 6, 6);
            var text = Controls.Button("Find text", async () => await DetectRegions(false));
            text.Margin = new Thickness(0, 0, 0, 6);
            detectionButtons.Children.Add(faces);
            detectionButtons.Children.Add(text);
            fields.Children.Add(detectionButtons);
            fields.Children.Add(new TextBlock { Text = "Review suggestions before saving. Right-click a suggestion to remove it, or drag to add a manual area.", Foreground = Theme.Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
            var clear = Controls.Button("Clear suggestions", () => { _detectedRegions.Clear(); DrawRegion(); SchedulePreview(); });
            clear.Margin = new Thickness(0, 8, 0, 4);
            fields.Children.Add(clear);
        }
        foreach (var parameter in operation.Parameters)
        {
            fields.Children.Add(new TextBlock { Text = parameter.Label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 5) });
            var box = Controls.Input(parameter.Label, parameter.DefaultValue);
            System.Windows.Automation.AutomationProperties.SetName(box, parameter.Label);
            _boxes[parameter.Key] = box;
            box.TextChanged += (_, _) =>
            {
                if (!_updatingFields && operation.Id == "image.redact" && parameter.Key is "x" or "y" or "width" or "height") _manualSelection = true;
                SchedulePreview();
            };
            fields.Children.Add(box);
            if (operation.Id == "image.adjust" && parameter.Key is "brightness" or "contrast" or "saturation")
            {
                var slider = new Slider { Minimum = parameter.Key == "saturation" ? 0 : -100, Maximum = parameter.Key == "saturation" ? 200 : 100, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 6, 0, 0) };
                slider.Value = double.TryParse(box.Text, CultureInfo.InvariantCulture, out var initial) ? initial : 0;
                slider.ValueChanged += (_, _) => box.Text = ((int)slider.Value).ToString(CultureInfo.InvariantCulture);
                box.TextChanged += (_, _) => { if (double.TryParse(box.Text, CultureInfo.InvariantCulture, out var number)) slider.Value = Math.Clamp(number, slider.Minimum, slider.Maximum); };
                fields.Children.Add(slider);
            }
            if (!string.IsNullOrEmpty(parameter.Hint))
                fields.Children.Add(new TextBlock { Text = parameter.Hint, Foreground = Theme.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        }
        if (operation.Id is "image.crop" or "image.redact")
        {
            var reset = Controls.Button("Select whole image", () => SetRegion(0, 0, _width, _height));
            reset.Margin = new Thickness(0, 12, 0, 4);
            fields.Children.Add(reset);
        }
        fields.Children.Add(_error);
        var scroll = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1);
        body.Children.Add(scroll);

        var footer = new DockPanel { Margin = new Thickness(0, 20, 0, 0), LastChildFill = false };
        footer.Children.Add(new TextBlock { Text = "Saves a new copy beside the original.", Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        _run = Controls.Button("Save copy", () => { if (ValidateValues()) DialogResult = true; }, primary: true);
        _run.IsEnabled = false;
        _run.Margin = new Thickness(8, 0, 0, 0);
        var cancel = Controls.Button("Cancel", () => DialogResult = false);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
            else if (e.Key == Key.Enter && _run.IsEnabled && ValidateValues()) { DialogResult = true; e.Handled = true; }
        };
        cancel.Focusable = true;
        _run.Focusable = true;
        System.Windows.Automation.AutomationProperties.SetName(cancel, "Cancel");
        System.Windows.Automation.AutomationProperties.SetName(_run, "Save copy");
        cancel.KeyDown += (_, e) => { if (e.Key == Key.Space) { DialogResult = false; e.Handled = true; } };
        _run.KeyDown += (_, e) => { if (e.Key == Key.Space && ValidateValues()) { DialogResult = true; e.Handled = true; } };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_run);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
        Loaded += async (_, _) => { ForegroundHelper.Force(this); await LoadPreview(); };
        Closed += (_, _) => { _closed = true; _generation++; _debounce.Stop(); _previewCancellation?.Cancel(); };
    }

    public IReadOnlyDictionary<string, string> Values
    {
        get
        {
            var values = _boxes.ToDictionary(entry => entry.Key, entry => entry.Value.Text);
            if (_operation.Id == "image.redact")
            {
                var regions = new List<PixelRegion>(_detectedRegions);
                if (_manualSelection) regions.Add(new PixelRegion(Number("x"), Number("y"), Number("width") > 0 ? Number("width") : _width - Number("x"), Number("height") > 0 ? Number("height") : _height - Number("y")));
                values["regions"] = string.Join(';', regions.Distinct().Select(region => $"{region.X},{region.Y},{region.Width},{region.Height}"));
            }
            return values;
        }
    }

    private async Task DetectRegions(bool faces)
    {
        if (_detecting || _previewPng is null || _closed) return;
        _detecting = true;
        _run.IsEnabled = false;
        _status.Text = faces ? "Finding faces on this PC…" : "Finding text on this PC…";
        try
        {
            var regions = await ImagePrivacyDetector.DetectAsync(_items[0].Path, faces, !faces);
            if (_closed) return;
            foreach (var region in regions)
                if (!_detectedRegions.Contains(region)) _detectedRegions.Add(region);
            _status.Text = regions.Count == 0 ? "No suggestions found. Drag to select an area manually." : $"{_detectedRegions.Count} suggestions · review before saving · right-click to remove";
            DrawRegion();
        }
        catch (Exception ex) { if (!_closed) _status.Text = ex.Message; }
        finally { _detecting = false; if (!_closed) SchedulePreview(); }
    }

    private async Task LoadPreview()
    {
        try
        {
            var source = await Task.Run(() =>
            {
                using var image = new MagickImage(_items[0].Path, new MagickReadSettings { FrameIndex = 0, FrameCount = 1 });
                image.AutoOrient();
                var width = (int)image.Width;
                var height = (int)image.Height;
                if (image.Width > 1200 || image.Height > 1200)
                    image.Resize(new MagickGeometry(1200, 1200));
                return (width, height, bytes: image.ToByteArray(MagickFormat.Png));
            });
            if (_closed) return;
            _width = source.width;
            _height = source.height;
            _previewPng = source.bytes;
            var bitmap = Bitmap(source.bytes);
            _scale = (double)bitmap.PixelWidth / _width;
            _surface.Width = bitmap.PixelWidth;
            _surface.Height = bitmap.PixelHeight;
            _image.Width = bitmap.PixelWidth;
            _image.Height = bitmap.PixelHeight;
            _image.Source = bitmap;
            _status.Text = _operation.Id is "image.crop" or "image.redact"
                ? $"Drag to select an area · {_width:N0} × {_height:N0} pixels"
                : $"Preview · {_width:N0} × {_height:N0} pixels";
            SchedulePreview();
        }
        catch (Exception ex)
        {
            if (!_closed) { _error.Text = "Could not load this image: " + ex.Message; _status.Text = "Preview unavailable"; }
        }
    }

    private void SchedulePreview()
    {
        if (_updatingFields || _closed) return;
        _previewCancellation?.Cancel();
        _generation++;
        _debounce.Stop();
        _debounce.Start();
    }

    private bool ValidateValues()
    {
        try
        {
            if (_previewPng is null) return false;
            if (_detecting) return false;
            foreach (var entry in _boxes)
            {
                if (entry.Key is "text" or "color") continue;
                if (!int.TryParse(entry.Value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    throw new ArgumentException("Enter a whole number for " + entry.Key + ".");
                var minimum = entry.Key is "brightness" or "contrast" ? -100 : 0;
                if (number < minimum || (entry.Key is "brightness" or "contrast" && number > 100) || (entry.Key == "saturation" && number > 200))
                    throw new ArgumentException("The value for " + entry.Key + " is outside its allowed range.");
            }
            if (_boxes.ContainsKey("color")) _ = new MagickColor(Text("color", "#ffffff"));
            foreach (var (key, minimum, maximum) in new[] { ("columns", 1, 12), ("cell", 32, 4096), ("gap", 0, 1024), ("padding", 0, 4096), ("size", 6, 1000) })
                if (_boxes.ContainsKey(key) && (Number(key) < minimum || Number(key) > maximum))
                    throw new ArgumentException($"{key}: enter a number between {minimum} and {maximum}.");
            if (_operation.Id == "image.annotate" && string.IsNullOrWhiteSpace(Text("text", "")))
                throw new ArgumentException("Enter the annotation text.");
            if (_operation.Id is "image.crop" or "image.redact")
            {
                if (Number("x") >= _width || Number("y") >= _height) throw new ArgumentException("The selected area must start inside the image.");
                if ((Number("width") > 0 && Number("x") + Number("width") > _width) || (Number("height") > 0 && Number("y") + Number("height") > _height))
                    throw new ArgumentException("The selected area extends beyond the image.");
            }
            if (_operation.Id == "image.redact" && !_manualSelection && _detectedRegions.Count == 0)
                throw new ArgumentException("Select an area, or find faces or text to redact.");
            _error.Text = "";
            return true;
        }
        catch (Exception ex) { _error.Text = ex.Message; return false; }
    }

    private async void UpdatePreview()
    {
        _run.IsEnabled = ValidateValues();
        if (!_run.IsEnabled || _previewPng is null)
        {
            if (_operation.Id == "image.redact" && !_manualSelection && _detectedRegions.Count == 0)
            {
                DrawRegion();
                _result.Source = _image.Source;
            }
            return;
        }
        var generation = ++_generation;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        var cancellation = _previewCancellation.Token;
        var values = Values;
        var bytes = _previewPng;
        DrawRegion();
        try
        {
            var output = await Task.Run(async () =>
            {
                await _previewGate.WaitAsync(cancellation);
                try { cancellation.ThrowIfCancellationRequested(); return RenderResult(bytes, values, cancellation); }
                finally { _previewGate.Release(); }
            }, cancellation);
            if (_closed || generation != _generation) return;
            var bitmap = Bitmap(output);
            if (_operation.Id is "image.crop" or "image.redact") _result.Source = bitmap;
            else
            {
                _surface.Width = bitmap.PixelWidth;
                _surface.Height = bitmap.PixelHeight;
                _image.Width = bitmap.PixelWidth;
                _image.Height = bitmap.PixelHeight;
                _image.Source = bitmap;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && generation == _generation) { _error.Text = "Preview: " + ex.Message; _run.IsEnabled = false; } }
    }

    private byte[] RenderResult(byte[] bytes, IReadOnlyDictionary<string, string> values, CancellationToken cancellation)
    {
        int N(string key, int fallback = 0) => values.TryGetValue(key, out var value) && int.TryParse(value, out var number) ? number : fallback;
        string S(string key, string fallback) => values.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;
        using var image = new MagickImage(bytes);
        var color = new MagickColor(S("color", "#ffffff"));
        var x = Math.Clamp((int)(N("x") * _scale), 0, (int)image.Width - 1);
        var y = Math.Clamp((int)(N("y") * _scale), 0, (int)image.Height - 1);
        var w = Math.Clamp((int)((N("width") > 0 ? N("width") : _width - N("x")) * _scale), 1, (int)image.Width - x);
        var h = Math.Clamp((int)((N("height") > 0 ? N("height") : _height - N("y")) * _scale), 1, (int)image.Height - y);
        switch (_operation.Id)
        {
            case "image.adjust":
                image.BrightnessContrast(new Percentage(N("brightness")), new Percentage(N("contrast")));
                image.Modulate(new Percentage(100), new Percentage(N("saturation", 100)), new Percentage(100));
                break;
            case "image.crop":
                image.Crop(new MagickGeometry(x, y, (uint)w, (uint)h));
                image.ResetPage();
                break;
            case "image.redact":
                foreach (var entry in S("regions", "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var region = entry.Split(',').Select(int.Parse).ToArray();
                    new Drawables().FillColor(color).StrokeColor(MagickColors.Transparent).Rectangle(region[0] * _scale, region[1] * _scale, (region[0] + region[2] - 1) * _scale, (region[1] + region[3] - 1) * _scale).Draw(image);
                }
                break;
            case "image.annotate":
                new Drawables().Font("Arial").FontPointSize(Math.Max(1, N("size", 36) * _scale)).FillColor(color).StrokeColor(MagickColors.Black).StrokeWidth(Math.Max(0.25, _scale)).Text(N("x") * _scale, N("y") * _scale, S("text", "Text")).Draw(image);
                break;
            case "image.background":
                image.BackgroundColor = color;
                image.Alpha(AlphaAction.Remove);
                image.BorderColor = color;
                image.Border((uint)Math.Clamp(N("padding", 40) * _scale, 0, 1200));
                break;
            case "image.collage":
                return RenderCollage(values, cancellation);
        }
        cancellation.ThrowIfCancellationRequested();
        return image.ToByteArray(MagickFormat.Png);
    }

    private byte[] RenderCollage(IReadOnlyDictionary<string, string> values, CancellationToken cancellation)
    {
        int N(string key, int fallback) => values.TryGetValue(key, out var value) && int.TryParse(value, out var number) ? number : fallback;
        var columns = Math.Clamp(N("columns", 2), 1, 12);
        var rows = (_items.Count + columns - 1) / columns;
        var outputCell = N("cell", 640);
        var outputGap = N("gap", 16);
        var outputWidth = (long)columns * outputCell + (columns + 1L) * outputGap;
        var outputHeight = (long)rows * outputCell + (rows + 1L) * outputGap;
        if (outputWidth * outputHeight > 100_000_000) throw new ArgumentException("The collage is too large. Reduce the cell size or number of images.");
        var previewScale = Math.Min(1.0, 1200.0 / Math.Max(outputWidth, outputHeight));
        var cell = Math.Max(1, (int)Math.Round(outputCell * previewScale));
        var gap = Math.Max(0, (int)Math.Round(outputGap * previewScale));
        var color = new MagickColor(values.TryGetValue("color", out var value) ? value : "#ffffff");
        using var canvas = new MagickImage(color, (uint)(columns * cell + (columns + 1) * gap), (uint)(rows * cell + (rows + 1) * gap));
        for (var i = 0; i < _items.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            using var source = new MagickImage(_items[i].Path, new MagickReadSettings { FrameIndex = 0, FrameCount = 1 });
            source.AutoOrient();
            source.Resize(new MagickGeometry((uint)cell, (uint)cell));
            source.BackgroundColor = color;
            source.Extent((uint)cell, (uint)cell, Gravity.Center);
            canvas.Composite(source, gap + (i % columns) * (cell + gap), gap + (i / columns) * (cell + gap), CompositeOperator.Over);
        }
        return canvas.ToByteArray(MagickFormat.Png);
    }

    private void StartRegion(object sender, MouseButtonEventArgs e)
    {
        if (_previewPng is null || _operation.Id is not ("image.crop" or "image.redact")) return;
        _dragOrigin = e.GetPosition(_surface);
        _surface.CaptureMouse();
        e.Handled = true;
    }

    private void MoveRegion(object sender, MouseEventArgs e)
    {
        if (_dragOrigin is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var end = e.GetPosition(_surface);
        var x = (int)Math.Clamp(Math.Min(start.X, end.X) / _scale, 0, _width - 1);
        var y = (int)Math.Clamp(Math.Min(start.Y, end.Y) / _scale, 0, _height - 1);
        var width = (int)Math.Clamp(Math.Abs(end.X - start.X) / _scale, 1, _width - x);
        var height = (int)Math.Clamp(Math.Abs(end.Y - start.Y) / _scale, 1, _height - y);
        SetRegion(x, y, width, height);
    }

    private void EndRegion(object sender, MouseButtonEventArgs e)
    {
        MoveRegion(sender, e);
        _dragOrigin = null;
        _surface.ReleaseMouseCapture();
    }

    private void SetRegion(int x, int y, int width, int height)
    {
        _manualSelection = true;
        _updatingFields = true;
        foreach (var (key, value) in new[] { ("x", x), ("y", y), ("width", width), ("height", height) })
            if (_boxes.TryGetValue(key, out var box)) box.Text = value.ToString(CultureInfo.InvariantCulture);
        _updatingFields = false;
        DrawRegion();
        SchedulePreview();
    }

    private void DrawRegion()
    {
        if (_operation.Id is not ("image.crop" or "image.redact")) return;
        _detectionOverlay.Children.Clear();
        foreach (var region in _detectedRegions)
        {
            var rectangle = new Rectangle { Width = region.Width * _scale, Height = region.Height * _scale, Stroke = Theme.Accent, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(40, 55, 240, 98)) };
            Canvas.SetLeft(rectangle, region.X * _scale);
            Canvas.SetTop(rectangle, region.Y * _scale);
            _detectionOverlay.Children.Add(rectangle);
        }
        _region.Visibility = _operation.Id == "image.redact" && !_manualSelection ? Visibility.Collapsed : Visibility.Visible;
        Canvas.SetLeft(_region, Number("x") * _scale);
        Canvas.SetTop(_region, Number("y") * _scale);
        _region.Width = Math.Max(1, (Number("width") > 0 ? Number("width") : _width - Number("x")) * _scale);
        _region.Height = Math.Max(1, (Number("height") > 0 ? Number("height") : _height - Number("y")) * _scale);
    }

    private int Number(string key) => _boxes.TryGetValue(key, out var box) && int.TryParse(box.Text, out var number) ? number : 0;
    private string Text(string key, string fallback) => _boxes.TryGetValue(key, out var box) && box.Text.Length > 0 ? box.Text : fallback;
    private static BitmapImage Bitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
