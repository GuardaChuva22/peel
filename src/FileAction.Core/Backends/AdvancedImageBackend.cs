using System.Globalization;
using ImageMagick;

namespace FileAction.Core.Backends;

public static class AdvancedImageBackend
{
    public static async Task<OperationResult> ExecuteAsync(string action, IReadOnlyList<FileItem> items, OperationContext ctx)
    {
        if (action == "image.collage")
        {
            var columns = Int(ctx, "columns", 2, 1, 12);
            var cell = Int(ctx, "cell", 640, 32, 4096);
            var gap = Int(ctx, "gap", 16, 0, 1024);
            var rows = (items.Count + columns - 1) / columns;
            var width = checked(columns * cell + (columns + 1) * gap);
            var height = checked(rows * cell + (rows + 1) * gap);
            if ((long)width * height > 100_000_000) throw new ConversionException("The collage would be too large. Reduce cell size or the number of images.");
            var path = await OutputPaths.ProduceAsync(ctx.OutputDirFor(items[0].Path), items[0].BaseName + " (collage)", "png", output => Task.Run(() =>
            {
                using var canvas = new MagickImage(Color(ctx.Param("color", "#FFFFFF")), (uint)width, (uint)height);
                for (var index = 0; index < items.Count; index++)
                {
                    ctx.CancellationToken.ThrowIfCancellationRequested();
                    using var image = new MagickImage(items[index].Path, new MagickReadSettings { FrameIndex = 0, FrameCount = 1 });
                    image.AutoOrient();
                    image.Resize(new MagickGeometry((uint)cell, (uint)cell));
                    var x = gap + (index % columns) * (cell + gap) + (cell - (int)image.Width) / 2;
                    var y = gap + (index / columns) * (cell + gap) + (cell - (int)image.Height) / 2;
                    canvas.Composite(image, x, y, CompositeOperator.Over);
                    ctx.Progress.Report((index + 1.0) / items.Count);
                }
                canvas.Write(output, MagickFormat.Png);
            }, ctx.CancellationToken));
            return OperationResult.Of([path]);
        }
        var outputs = new List<string>();
        foreach (var item in items)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            var format = action == "image.background" ? "png" : ImageBackend.OutputFormatFor(item.Format);
            var suffix = action.Split('.')[1];
            var path = await OutputPaths.ProduceAsync(ctx.OutputDirFor(item.Path), item.BaseName + " (" + suffix + ")", format,
                output => action == "image.background"
                    ? Task.Run(() =>
                    {
                        using var image = new MagickImage(item.Path, new MagickReadSettings { FrameIndex = 0, FrameCount = 1 });
                        image.AutoOrient();
                        Apply(action, image, ctx);
                        ctx.CancellationToken.ThrowIfCancellationRequested();
                        image.Write(output, MagickFormat.Png);
                    }, ctx.CancellationToken)
                    : ImageBackend.EditAsync(item.Path, output, image => Apply(action, image, ctx), ctx.Settings.JpegQuality, ctx.CancellationToken));
            outputs.Add(path);
            ctx.Progress.Report((double)outputs.Count / items.Count);
        }
        return OperationResult.Of(outputs);
    }

    public static void Apply(string action, MagickImage image, OperationContext ctx)
    {
        if (action == "image.redact" && ctx.Param("regions").Length > 0)
        {
            var entries = ctx.Param("regions").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (entries.Length > 10000) throw new ConversionException("Too many redaction regions.");
            foreach (var entry in entries)
            {
                var parts = entry.Split(',');
                if (parts.Length != 4 || parts.Any(part => !int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))) throw new ConversionException("Invalid redaction region.");
                var coordinates = parts.Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
                var x = coordinates[0]; var y = coordinates[1]; var w = coordinates[2]; var h = coordinates[3];
                if (x < 0 || y < 0 || w <= 0 || h <= 0 || (long)x + w > image.Width || (long)y + h > image.Height) throw new ConversionException("A redaction region extends beyond the image.");
                image.Draw(new ImageMagick.Drawing.Drawables().FillColor(Color(ctx.Param("color", "#000000"))).StrokeColor(MagickColors.Transparent).Rectangle(x, y, x + w - 1, y + h - 1));
            }
            return;
        }
        switch (action)
        {
            case "image.adjust":
                image.BrightnessContrast(new Percentage(Number(ctx, "brightness", 0, -100, 100)), new Percentage(Number(ctx, "contrast", 0, -100, 100)));
                image.Modulate(new Percentage(100), new Percentage(Number(ctx, "saturation", 100, 0, 200)), new Percentage(100));
                break;
            case "image.crop":
            case "image.redact":
                var x = Int(ctx, "x", 0, 0, (int)image.Width - 1);
                var y = Int(ctx, "y", 0, 0, (int)image.Height - 1);
                var w = Int(ctx, "width", 0, 0, (int)image.Width);
                var h = Int(ctx, "height", 0, 0, (int)image.Height);
                w = w == 0 ? (int)image.Width - x : w;
                h = h == 0 ? (int)image.Height - y : h;
                if (x + w > image.Width || y + h > image.Height) throw new ConversionException("The selected rectangle extends beyond the image.");
                if (action == "image.crop")
                {
                    image.Crop(new MagickGeometry(x, y, (uint)w, (uint)h));
                    image.ResetPage();
                }
                else image.Draw(new ImageMagick.Drawing.Drawables().FillColor(Color(ctx.Param("color", "#000000"))).StrokeColor(MagickColors.Transparent).Rectangle(x, y, x + w - 1, y + h - 1));
                break;
            case "image.annotate":
                var text = ctx.Param("text");
                if (text.Length == 0) throw new ConversionException("Enter the annotation text.");
                image.Draw(new ImageMagick.Drawing.Drawables().Font("Arial").FontPointSize(Number(ctx, "size", 36, 6, 1000)).FillColor(Color(ctx.Param("color", "#FFFFFF")))
                    .StrokeColor(MagickColors.Black).StrokeWidth(1).Text(Number(ctx, "x", 24, 0, image.Width), Number(ctx, "y", 64, 0, image.Height), text));
                break;
            case "image.background":
                var padding = Int(ctx, "padding", 48, 0, 4096);
                image.BackgroundColor = Color(ctx.Param("color", "#FFFFFF"));
                image.Alpha(AlphaAction.Remove);
                image.BorderColor = image.BackgroundColor;
                image.Border((uint)padding);
                break;
            case "image.metadata":
                image.SetAttribute("label", ctx.Param("title"));
                image.SetAttribute("artist", ctx.Param("artist"));
                image.SetAttribute("comment", ctx.Param("comment"));
                break;
            default: throw new ConversionException("Unknown image tool.");
        }
    }

    private static MagickColor Color(string text)
    {
        try { return new MagickColor(text); }
        catch (Exception) { throw new ConversionException("Enter a valid color, such as #FFFFFF."); }
    }
    internal static int Int(OperationContext ctx, string key, int fallback, int min, int max) => checked((int)Number(ctx, key, fallback, min, max));
    internal static double Number(OperationContext ctx, string key, double fallback, double min, double max)
    {
        var text = ctx.Param(key, fallback.ToString(CultureInfo.InvariantCulture));
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number < min || number > max)
            throw new ConversionException($"{key}: enter a number between {min} and {max}.");
        return number;
    }
}
