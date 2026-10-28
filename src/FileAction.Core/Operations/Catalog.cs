using System.Globalization;
using FileAction.Core.Backends;
using static FileAction.Core.Operations.Helpers;
using static FileAction.Core.Loc;

namespace FileAction.Core.Operations;

/// <summary>Every conversion and tool the wheels can offer. The app adds its own file actions.</summary>
public static class Catalog
{
    public static IReadOnlyList<Operation> Build()
    {
        var ops = new List<Operation>();
        AddImages(ops);
        AddAudio(ops);
        AddVideo(ops);
        AddPdf(ops);
        AddDocuments(ops);
        AddSlides(ops);
        AddSpreadsheets(ops);
        AddSubtitles(ops);
        AddArchives(ops);
        ops.AddRange(AdvancedCatalog.Build());
        return ops;
    }

    /// <summary>Operations for a wheel that apply to the dropped items and whose tools are installed.</summary>
    public static IReadOnlyList<Operation> For(IEnumerable<Operation> all, WheelKind wheel, IReadOnlyList<FileItem> items, ToolLocator tools) =>
        all.Where(o => o.Wheel == wheel && o.AppliesTo(items) && o.IsAvailable(tools, items)).ToList();

    // ---------------------------------------------------------------- images

    private static void AddImages(List<Operation> ops)
    {
        foreach (var target in Formats.ImageOutputs.Concat(["heic"]))
        {
            ops.Add(new PerFileOperation($"image.to.{target}", WheelKind.Convert, Formats.Display(target), Glyphs.Photo, Formats.Images,
                async (item, ctx, _) => [await Produce(item, ctx, target, tmp => ImageBackend.ConvertAsync(item.Path, tmp, target, ctx.Settings, ctx.CancellationToken))],
                targetFormat: target, subtitle: target == "pdf" ? T("dokument", "document") : null)
            {
                AvailableFor = (_, _) => target != "heic" || ImageMagick.MagickNET.SupportedFormats.Any(f => f.Format == ImageMagick.MagickFormat.Heic && f.SupportsWriting),
            });
        }
        foreach (var target in new[] { "mp4", "webm", "mov", "mkv" })
            ops.Add(new PerFileOperation($"gif.to.{target}", WheelKind.Convert, Formats.Display(target), Glyphs.Video, ["gif"],
                async (item, ctx, p) => [await FfmpegBackend.ConvertAsync(item, target, ctx, p)], subtitle: "video")
            { Requires = [ExternalTool.Ffmpeg] });

        ImageTool(ops, "image.compress", T("Komprimovat", "Compress"), Glyphs.Shrink, T("komprimováno", "compressed"),
            (item, ctx, tmp) => ImageBackend.CompressAsync(item.Path, tmp, ctx.Settings.CompressQuality, ctx.CancellationToken));
        ImageEdit(ops, "image.half", T("Zmenšit", "Shrink"), Glyphs.Shrink, "50 %", T("zmenšeno", "smaller"), img => ImageBackend.ResizePercent(img, 50));
        ImageEdit(ops, "image.max1920", "Max 1920 px", Glyphs.Shrink, T("na web", "for web"), "1920", img => ImageBackend.ResizeMax(img, 1920));
        ImageEdit(ops, "image.rotate.right", T("Otočit", "Rotate"), Glyphs.Rotate, T("doprava", "right"), T("otočeno", "rotated"), img => img.Rotate(90));
        ImageEdit(ops, "image.rotate.left", T("Otočit", "Rotate"), Glyphs.Rotate, T("doleva", "left"), T("otočeno", "rotated"), img => img.Rotate(-90));
        ImageEdit(ops, "image.flip", T("Zrcadlit", "Mirror"), Glyphs.Flip, null, T("zrcadleno", "mirrored"), img => img.Flop());
        ImageEdit(ops, "image.square", T("Čtverec", "Square"), Glyphs.Crop, T("oříznout", "crop"), T("čtverec", "square"), ImageBackend.CropSquare);
        ImageEdit(ops, "image.grayscale", T("Černobíle", "Grayscale"), Glyphs.Color, null, T("černobíle", "grayscale"), ImageBackend.Grayscale);
        ImageEdit(ops, "image.strip", T("Bez metadat", "No metadata"), Glyphs.Clear, "GPS, EXIF", T("bez metadat", "no metadata"), img => img.Strip());
        ops.Add(new BatchOperation("image.merge.pdf", WheelKind.Tools, T("Spojit do PDF", "Merge to PDF"), Glyphs.Merge,
            BatchOperation.AllOf(Formats.Images, min: 2),
            async (items, ctx) => [await OutputPaths.ProduceAsync(ctx.OutputDirFor(items[0].Path), items[0].BaseName, "pdf",
                tmp => ImageBackend.ImagesToPdfAsync(items.Select(i => i.Path).OrderBy(p => p, NaturalComparer.Instance).ToList(), tmp, ctx.Settings.JpegQuality, ctx.CancellationToken))]));
    }

    private static void ImageTool(List<Operation> ops, string id, string label, string glyph, string suffix, Func<FileItem, OperationContext, string, Task> run, string? subtitle = null) =>
        ops.Add(new PerFileOperation(id, WheelKind.Tools, label, glyph, Formats.Images,
            async (item, ctx, _) => [await OutputPaths.ProduceAsync(ctx.OutputDirFor(item.Path), $"{item.BaseName} ({suffix})",
                ImageBackend.OutputFormatFor(item.Format), tmp => run(item, ctx, tmp))], subtitle: subtitle));

    private static void ImageEdit(List<Operation> ops, string id, string label, string glyph, string? subtitle, string suffix, Action<ImageMagick.MagickImage> edit) =>
        ImageTool(ops, id, label, glyph, suffix, (item, ctx, tmp) => ImageBackend.EditAsync(item.Path, tmp, edit, ctx.Settings.JpegQuality, ctx.CancellationToken), subtitle);

    // ---------------------------------------------------------------- audio

    private static void AddAudio(List<Operation> ops)
    {
        foreach (var target in Formats.AudioOutputs)
        {
            ops.Add(new PerFileOperation($"audio.to.{target}", WheelKind.Convert, Formats.Display(target), Glyphs.Audio, Formats.Audio,
                async (item, ctx, p) => [await FfmpegBackend.ConvertAsync(item, target, ctx, p)], targetFormat: target)
            { Requires = [ExternalTool.Ffmpeg] });
        }

        Ffmpeg(ops, "audio.compress", T("Komprimovat", "Compress"), Glyphs.Shrink, Formats.Audio, T("komprimováno", "compressed"),
            (item, ctx) => ["-vn", "-c:a", "libmp3lame", "-b:a", "128k"], outputFormat: _ => "mp3", subtitle: "MP3 128k");
        Ffmpeg(ops, "audio.normalize", T("Normalizovat", "Normalize"), Glyphs.Equalizer, Formats.Audio, T("normalizováno", "normalized"),
            (item, ctx) => ["-af", "loudnorm=I=-16:TP=-1.5:LRA=11", .. FfmpegBackend.CodecArgs(FfmpegBackend.SafeContainer(item.Format), ctx.Settings)],
            subtitle: T("hlasitost", "loudness"));
        ops.Add(Trim("audio.trim", Formats.Audio));
        ops.Add(new BatchOperation("audio.concat", WheelKind.Tools, T("Spojit", "Join"), Glyphs.Merge, BatchOperation.AllOf(Formats.Audio, min: 2),
            async (items, ctx) =>
            {
                var sorted = items.OrderBy(i => i.Path, NaturalComparer.Instance).ToList();
                var format = FfmpegBackend.SafeContainer(sorted[0].Format);
                var graph = string.Concat(Enumerable.Range(0, sorted.Count).Select(i => $"[{i}:a]")) + $"concat=n={sorted.Count}:v=0:a=1[a]";
                return [await OutputPaths.ProduceAsync(ctx.OutputDirFor(sorted[0].Path), $"{sorted[0].BaseName} ({T("spojeno", "joined")})", format, tmp =>
                    FfmpegBackend.RunAsync(ctx, sorted.Select(i => i.Path).ToList(),
                        ["-filter_complex", graph, "-map", "[a]", .. FfmpegBackend.CodecArgs(format, ctx.Settings)], tmp, ctx.Progress))];
            })
        { Requires = [ExternalTool.Ffmpeg] });
    }

    // ---------------------------------------------------------------- video

    private static void AddVideo(List<Operation> ops)
    {
        foreach (var target in Formats.VideoOutputs)
        {
            ops.Add(new PerFileOperation($"video.to.{target}", WheelKind.Convert, Formats.Display(target), target == "mp3" ? Glyphs.Audio : Glyphs.Video, Formats.Video,
                async (item, ctx, p) => [await FfmpegBackend.ConvertAsync(item, target, ctx, p)], targetFormat: target,
                subtitle: target switch { "mp3" => T("jen zvuk", "audio only"), "gif" => T("animace", "animation"), _ => null })
            { Requires = [ExternalTool.Ffmpeg] });
        }

        Ffmpeg(ops, "video.compress", T("Komprimovat", "Compress"), Glyphs.Shrink, Formats.Video, T("komprimováno", "compressed"),
            (item, ctx) => H264(ctx.Settings.CompressVideoCrf, "slow", null, "128k"), outputFormat: _ => "mp4", subtitle: "H.264");
        Ffmpeg(ops, "video.720p", "720p", Glyphs.Shrink, Formats.Video, "720p",
            (item, ctx) => H264(ctx.Settings.VideoCrf, "medium", "scale=-2:'min(720,ih)'", "160k"), outputFormat: _ => "mp4", subtitle: T("zmenšit", "downscale"));
        Ffmpeg(ops, "video.mute", T("Ztlumit", "Mute"), Glyphs.Mute, Formats.Video, T("bez zvuku", "muted"),
            (item, ctx) => FfmpegBackend.SafeContainer(item.Format) == item.Format
                ? ["-map", "0:v", "-c:v", "copy", "-an"]
                : [.. H264(ctx.Settings.VideoCrf, "medium", null, null), "-an"],
            subtitle: T("bez zvuku", "muted"));
        ops.Add(Trim("video.trim", Formats.Video));
        Ffmpeg(ops, "video.rotate", T("Otočit", "Rotate"), Glyphs.Rotate, Formats.Video, T("otočeno", "rotated"),
            (item, ctx) => H264(ctx.Settings.VideoCrf, "medium", "transpose=1", "160k"), outputFormat: _ => "mp4", subtitle: T("doprava", "right"));
        Ffmpeg(ops, "video.speed2", T("Zrychlit", "Speed up"), Glyphs.Play, Formats.Video, "2×",
            (item, ctx) => [.. H264(ctx.Settings.VideoCrf, "medium", "setpts=0.5*PTS", "160k"), "-af", "atempo=2.0"],
            outputFormat: _ => "mp4", subtitle: "2×");
        Ffmpeg(ops, "video.thumbnail", T("Náhled", "Thumbnail"), Glyphs.Camera, Formats.Video, T("náhled", "thumbnail"),
            (item, ctx) => ["-vf", "thumbnail=150", "-frames:v", "1", "-update", "1"], outputFormat: _ => "png", subtitle: T("snímek PNG", "PNG frame"));
    }

    private static string[] H264(int crf, string preset, string? filter, string? audioBitrate)
    {
        var vf = (filter is null ? "" : filter + ",") + "scale=trunc(iw/2)*2:trunc(ih/2)*2";
        var args = new List<string> { "-c:v", "libx264", "-preset", preset, "-crf", crf.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p", "-vf", vf, "-movflags", "+faststart" };
        if (audioBitrate is not null)
            args.AddRange(["-c:a", "aac", "-b:a", audioBitrate]);
        return [.. args];
    }

    private static void Ffmpeg(List<Operation> ops, string id, string label, string glyph, ISet<string> inputs, string suffix,
        Func<FileItem, OperationContext, IEnumerable<string>> args, Func<FileItem, string>? outputFormat = null, string? subtitle = null) =>
        ops.Add(new PerFileOperation(id, WheelKind.Tools, label, glyph, inputs,
            async (item, ctx, p) => [await FfmpegBackend.TransformAsync(item, suffix, ctx, p, args(item, ctx), outputFormat?.Invoke(item))],
            subtitle: subtitle)
        { Requires = [ExternalTool.Ffmpeg] });

    private static Operation Trim(string id, ISet<string> inputs) =>
        new PerFileOperation(id, WheelKind.Tools, T("Oříznout", "Trim"), Glyphs.Stopwatch, inputs, async (item, ctx, p) =>
        {
            var start = FfmpegBackend.ParseTime(ctx.Param("start", "0"));
            var endText = ctx.Param("end");
            double? length = endText.Length > 0 ? FfmpegBackend.ParseTime(endText) - start : null;
            if (length is <= 0)
                throw new ConversionException(T("Konec musí být až za začátkem.", "The end must come after the start."));
            var format = FfmpegBackend.SafeContainer(item.Format);
            var args = new List<string>();
            if (length is not null)
                args.AddRange(["-t", length.Value.ToString("0.###", CultureInfo.InvariantCulture)]);
            args.AddRange(Formats.Video.Contains(item.Format)
                ? H264(ctx.Settings.VideoCrf, "medium", null, "160k")
                : FfmpegBackend.CodecArgs(format, ctx.Settings));
            return [await FfmpegBackend.TransformAsync(item, T("oříznuto", "trimmed"), ctx, p, args,
                Formats.Video.Contains(item.Format) ? "mp4" : format,
                inputArgs: start > 0 ? ["-ss", start.ToString("0.###", CultureInfo.InvariantCulture)] : null,
                duration: length)];
        }, subtitle: T("začátek–konec", "start–end"))
        {
            Requires = [ExternalTool.Ffmpeg],
            Parameters =
            [
                new ParameterSpec("start", T("Začátek", "Start"), "0:00", T("např. 0:30 nebo 1:02:03", "e.g. 0:30 or 1:02:03")),
                new ParameterSpec("end", T("Konec", "End"), "", T("prázdné = do konce", "empty = until the end")),
            ],
        };

    // ---------------------------------------------------------------- PDF

    private static void AddPdf(List<Operation> ops)
    {
        ops.Add(PdfDoc("docx", "Word"));
        ops.Add(new PdfSpreadsheetOperation());
        foreach (var image in new[] { "jpg", "png" })
        {
            ops.Add(new PerFileOperation($"pdf.to.{image}", WheelKind.Convert, Formats.Display(image), Glyphs.Photo, Formats.Pdf,
                (item, ctx, p) => PdfBackend.RenderAsync(item, image, ctx, p), subtitle: T("všechny strany", "all pages")));
        }
        ops.Add(new PerFileOperation("pdf.to.pptx", WheelKind.Convert, "PPTX", Glyphs.Page, Formats.Pdf,
            async (item, ctx, _) => [await OfficeBackend.ConvertAsync(item, "pptx", ctx, "impress_pdf_import")], subtitle: "PowerPoint")
        { Requires = [ExternalTool.LibreOffice] });
        foreach (var text in new[] { "txt", "md" })
        {
            ops.Add(new PerFileOperation($"pdf.to.{text}", WheelKind.Convert, Formats.Display(text), Glyphs.Text, Formats.Pdf,
                async (item, ctx, _) => [await PdfBackend.ToTextAsync(item, text, ctx)], subtitle: text == "md" ? "Markdown" : "text"));
        }
        ops.Add(PdfDoc("html", "web"));
        ops.Add(PdfDoc("epub", T("e-kniha", "e-book")));

        ops.Add(new BatchOperation("pdf.merge", WheelKind.Tools, T("Spojit PDF", "Merge PDFs"), Glyphs.Merge, BatchOperation.AllOf(Formats.Pdf, min: 2),
            async (items, ctx) =>
            {
                var sorted = items.Select(i => i.Path).OrderBy(p => p, NaturalComparer.Instance).ToList();
                return [await OutputPaths.ProduceAsync(ctx.OutputDirFor(sorted[0]), $"{items[0].BaseName} ({T("spojeno", "merged")})", "pdf",
                    tmp => Task.Run(() => PdfBackend.Merge(sorted, tmp), ctx.CancellationToken))];
            }));
        ops.Add(new PerFileOperation("pdf.split", WheelKind.Tools, T("Rozdělit", "Split"), Glyphs.Split, Formats.Pdf,
            (item, ctx, p) => PdfBackend.SplitAsync(item, ctx, p), subtitle: T("po stranách", "into pages")));
        ops.Add(new PerFileOperation("pdf.rotate", WheelKind.Tools, T("Otočit", "Rotate"), Glyphs.Rotate, Formats.Pdf,
            async (item, ctx, _) => [await OutputPaths.ProduceAsync(ctx.OutputDirFor(item.Path), $"{item.BaseName} ({T("otočeno", "rotated")})", "pdf",
                tmp => Task.Run(() => PdfBackend.Rotate(item.Path, tmp, 90), ctx.CancellationToken))], subtitle: T("doprava", "right")));
        ops.Add(new PerFileOperation("pdf.optimize", WheelKind.Tools, T("Optimalizovat", "Optimize"), Glyphs.Shrink, Formats.Pdf,
            async (item, ctx, _) => [await OutputPaths.ProduceAsync(ctx.OutputDirFor(item.Path), $"{item.BaseName} ({T("optimalizováno", "optimized")})", "pdf",
                tmp => Task.Run(() => PdfBackend.Optimize(item.Path, tmp), ctx.CancellationToken))]));
        ops.Add(new PerFileOperation("pdf.organize", WheelKind.Tools, "Organize pages", Glyphs.Page, Formats.Pdf,
            async (item, ctx, _) =>
            {
                if (!int.TryParse(ctx.Param("rotation", "0"), out var rotation))
                    throw new ConversionException("Enter a rotation in degrees: 0, 90, 180 or 270.");
                return [await OutputPaths.ProduceAsync(ctx.OutputDirFor(item.Path), $"{item.BaseName} (organized)", "pdf",
                    tmp => Task.Run(() => PdfBackend.Organize(item.Path, tmp, ctx.Param("pages", "all"), rotation), ctx.CancellationToken))];
            }, subtitle: "reorder, duplicate, remove")
        {
            Parameters = [new("pages", "Pages in output order", "all", "Example: 3,1-2,3. Reverse ranges work too: 5-1."),
                new("rotation", "Rotate selected pages (degrees)", "0", "0, 90, 180 or 270")],
        });
        ops.Add(new PerFileOperation("pdf.metadata", WheelKind.Tools, "Edit metadata", Glyphs.Document, Formats.Pdf,
            async (item, ctx, _) => [await OutputPaths.ProduceAsync(ctx.OutputDirFor(item.Path), $"{item.BaseName} (metadata)", "pdf",
                tmp => Task.Run(() => PdfBackend.Metadata(item.Path, tmp, false, ctx.Param("title"), ctx.Param("author"), ctx.Param("subject"), ctx.Param("keywords")), ctx.CancellationToken))],
            subtitle: "replace document tags")
        {
            Parameters = [new("title", "Title", ""), new("author", "Author", ""), new("subject", "Subject", ""), new("keywords", "Keywords", "")],
        });
        ops.Add(new PerFileOperation("pdf.strip", WheelKind.Tools, "Remove metadata", Glyphs.Clear, Formats.Pdf,
            async (item, ctx, _) => [await OutputPaths.ProduceAsync(ctx.OutputDirFor(item.Path), $"{item.BaseName} (no metadata)", "pdf",
                tmp => Task.Run(() => PdfBackend.Metadata(item.Path, tmp, true, "", "", "", ""), ctx.CancellationToken))], subtitle: "document info and XMP"));
    }

    private sealed class PdfSpreadsheetOperation : Operation
    {
        private readonly PerFileOperation _inner = new("pdf.to.xlsx", WheelKind.Convert, "XLSX", Glyphs.Table, Formats.Pdf,
            async (item, ctx, _) => [await PdfBackend.ToSpreadsheetAsync(item, ctx)]);

        public PdfSpreadsheetOperation() : base("pdf.to.xlsx", WheelKind.Convert, "XLSX", Glyphs.Table, "Extract text; review alignment") { }
        public override bool AppliesTo(IReadOnlyList<FileItem> items) => _inner.AppliesTo(items);
        public override async Task<OperationResult> ExecuteAsync(IReadOnlyList<FileItem> items, OperationContext ctx) =>
            (await _inner.ExecuteAsync(items, ctx)) with { Note = "Extracted PDF text; review table alignment. Images and scanned text are not included." };
    }

    private static Operation PdfDoc(string target, string subtitle) =>
        new PerFileOperation($"pdf.to.{target}", WheelKind.Convert, Formats.Display(target), Glyphs.Document, Formats.Pdf,
            async (item, ctx, _) => [await PdfBackend.ToDocumentAsync(item, target, ctx)], subtitle: subtitle)
        { Requires = [ExternalTool.Pandoc] };

    // ---------------------------------------------------------------- documents

    private static void AddDocuments(List<Operation> ops)
    {
        foreach (var target in Formats.DocumentOutputs)
        {
            ops.Add(new PerFileOperation($"doc.to.{target}", WheelKind.Convert, Formats.Display(target), Glyphs.Document, Formats.Documents,
                async (item, ctx, _) => [await DocumentAsync(item, target, ctx)], targetFormat: target,
                subtitle: target switch { "docx" => "Word", "md" => "Markdown", "epub" => T("e-kniha", "e-book"), "txt" => "text", "html" => "web", _ => null })
            {
                AvailableFor = (tools, item) => DocumentRoute(item.Format, target, tools) is not null,
            });
        }
        foreach (var target in new[] { "png", "jpg" })
        {
            ops.Add(new PerFileOperation($"doc.to.{target}", WheelKind.Convert, Formats.Display(target), Glyphs.Photo, Formats.Documents,
                async (item, ctx, progress) =>
                {
                    var temporary = OutputPaths.TempDirectory();
                    try
                    {
                        var pdf = await DocumentAsync(item, "pdf", new OperationContextOverride(ctx, temporary).Context);
                        return await PdfBackend.RenderAsync(new FileItem(pdf), target,
                            new OperationContextOverride(ctx, ctx.OutputDirFor(item.Path)).Context, progress);
                    }
                    finally
                    {
                        OutputPaths.TryDelete(temporary);
                    }
                }, subtitle: "all pages")
            {
                AvailableFor = (tools, item) => DocumentRoute(item.Format, "pdf", tools) is not null,
            });
        }
    }

    private enum DocRoute { Pandoc, Office, PandocThenOffice, OfficeThenPandoc }

    private static DocRoute? DocumentRoute(string input, string target, ToolLocator tools)
    {
        var pandoc = tools.Has(ExternalTool.Pandoc);
        var office = tools.Has(ExternalTool.LibreOffice);
        bool pandocReads = Formats.PandocInputs.Contains(input), officeReads = input is "doc" or "docx" or "odt" or "rtf" or "txt" or "html";
        bool pandocWrites = target != "pdf", officeWrites = target is "pdf" or "docx" or "odt" or "txt" or "html" || (target == "rtf" && input != "html");

        // LibreOffice keeps layout better between word-processor formats; pandoc is better at everything else.
        if (office && officeReads && officeWrites && (target is "pdf" or "docx" or "odt" or "rtf" || !pandoc || !pandocReads))
            return DocRoute.Office;
        if (pandoc && pandocReads && pandocWrites)
            return DocRoute.Pandoc;
        if (pandoc && office && pandocReads && target == "pdf")
            return DocRoute.PandocThenOffice;
        if (pandoc && office && officeReads && pandocWrites)
            return DocRoute.OfficeThenPandoc;
        return null;
    }

    private static async Task<string> DocumentAsync(FileItem item, string target, OperationContext ctx)
    {
        var route = DocumentRoute(item.Format, target, ctx.Tools) ?? throw new ConversionException(
            T($"Pro převod {Formats.Display(item.Format)} → {Formats.Display(target)} je potřeba Pandoc nebo LibreOffice.", $"Converting {Formats.Display(item.Format)} → {Formats.Display(target)} needs Pandoc or LibreOffice."));
        var dir = ctx.OutputDirFor(item.Path);
        switch (route)
        {
            case DocRoute.Office:
                return await OfficeBackend.ConvertAsync(item, target, ctx);
            case DocRoute.Pandoc:
                return await OutputPaths.ProduceAsync(dir, item.BaseName, target, tmp => PandocBackend.ConvertAsync(ctx, item.Path, item.Format, tmp, target));
            case DocRoute.PandocThenOffice:
            {
                var temp = OutputPaths.TempDirectory();
                try
                {
                    var docx = Path.Combine(temp, item.BaseName + ".docx");
                    await PandocBackend.ConvertAsync(ctx, item.Path, item.Format, docx, "docx");
                    return await OfficeBackend.ConvertAsync(new FileItem(docx), target, new OperationContextOverride(ctx, dir).Context);
                }
                finally
                {
                    OutputPaths.TryDelete(temp);
                }
            }
            default:
            {
                var docx = await OfficeBackend.ConvertToTempAsync(ctx, item.Path, "docx");
                try
                {
                    return await OutputPaths.ProduceAsync(dir, item.BaseName, target, tmp => PandocBackend.ConvertAsync(ctx, docx, "docx", tmp, target));
                }
                finally
                {
                    OutputPaths.TryDelete(Path.GetDirectoryName(docx)!);
                }
            }
        }
    }

    // ---------------------------------------------------------------- slides

    private static void AddSlides(List<Operation> ops)
    {
        foreach (var target in Formats.SlideOutputs)
        {
            ops.Add(new PerFileOperation($"slides.to.{target}", WheelKind.Convert, Formats.Display(target), target is "png" or "jpg" ? Glyphs.Photo : Glyphs.Page,
                Formats.Slides,
                async (item, ctx, p) =>
                {
                    if (target is "png" or "jpg")
                    {
                        var pdf = await OfficeBackend.ConvertToTempAsync(ctx, item.Path, "pdf");
                        try
                        {
                            // Render the temporary PDF but name the output after the presentation.
                            var renamed = Path.Combine(Path.GetDirectoryName(pdf)!, item.BaseName + ".pdf");
                            if (!string.Equals(renamed, pdf, StringComparison.Ordinal))
                                File.Move(pdf, renamed);
                            return await PdfBackend.RenderAsync(new FileItem(renamed), target, new OperationContextOverride(ctx, ctx.OutputDirFor(item.Path)).Context, p);
                        }
                        finally
                        {
                            OutputPaths.TryDelete(Path.GetDirectoryName(pdf)!);
                        }
                    }
                    return [await OfficeBackend.ConvertAsync(item, target, ctx)];
                }, targetFormat: target, subtitle: target is "png" or "jpg" ? T("všechny snímky", "all slides") : null)
            { Requires = [ExternalTool.LibreOffice] });
        }
    }

    // ---------------------------------------------------------------- spreadsheets

    private static void AddSpreadsheets(List<Operation> ops)
    {
        var native = new HashSet<string>(["xlsx", "xlsm", "xls", "csv", "tsv"], StringComparer.OrdinalIgnoreCase);
        foreach (var target in Formats.SpreadsheetOutputs)
        {
            var nativeTarget = target is "xlsx" or "csv" or "tsv";
            ops.Add(new PerFileOperation($"sheet.to.{target}", WheelKind.Convert, Formats.Display(target), Glyphs.Table, Formats.Spreadsheets,
                async (item, ctx, _) => nativeTarget && native.Contains(item.Format)
                    ? await SpreadsheetBackend.ConvertAsync(item, target, ctx)
                    : [await OfficeBackend.ConvertAsync(item, target, ctx)],
                targetFormat: target, subtitle: target == "xlsx" ? "Excel" : null)
            {
                AvailableFor = (tools, item) => (nativeTarget && native.Contains(item.Format)) || tools.Has(ExternalTool.LibreOffice),
            });
        }
    }

    // ---------------------------------------------------------------- subtitles

    private static void AddSubtitles(List<Operation> ops)
    {
        foreach (var target in Formats.SubtitleOutputs)
        {
            ops.Add(new PerFileOperation($"subs.to.{target}", WheelKind.Convert, Formats.Display(target), Glyphs.Subtitles, Formats.Subtitles,
                async (item, ctx, _) => [await SubtitleBackend.ConvertAsync(item, target, ctx)], targetFormat: target,
                subtitle: target == "txt" ? T("jen text", "text only") : null));
        }
    }

    // ---------------------------------------------------------------- archives

    private static void AddArchives(List<Operation> ops)
    {
        ops.Add(new PerFileOperation("archive.extract", WheelKind.Convert, T("Rozbalit", "Extract"), Glyphs.Extract, Formats.Archives,
            async (item, ctx, _) => [await ArchiveBackend.ExtractAsync(item.Path, ctx.OutputDirFor(item.Path), ctx.CancellationToken)]));
        foreach (var target in Formats.ArchiveOutputs)
        {
            ops.Add(new PerFileOperation($"archive.to.{target}", WheelKind.Convert, Formats.Display(target), Glyphs.Pack, Formats.Archives,
                async (item, ctx, _) => [await ArchiveBackend.RepackAsync(item, target, ctx)], targetFormat: target));
        }

        foreach (var (target, label) in new[] { ("zip", "ZIP"), ("tar.gz", "TAR.GZ") })
        {
            ops.Add(new BatchOperation($"pack.{target}", WheelKind.Tools, T("Zabalit", "Archive"), Glyphs.Pack, _ => true,
                async (items, ctx) => [await OutputPaths.ProduceAsync(ctx.OutputDirFor(items[0].Path), ArchiveBackend.ArchiveNameFor(items), target,
                    tmp => ArchiveBackend.PackAsync(items.Select(i => i.Path).ToList(), tmp, target, ctx.CancellationToken))],
                subtitle: label));
        }
    }
}

internal static class Helpers
{
    public static Task<string> Produce(FileItem item, OperationContext ctx, string format, Func<string, Task> produce) =>
        OutputPaths.ProduceAsync(ctx.OutputDirFor(item.Path), item.BaseName, format, produce);
}

/// <summary>Copy of a context that writes into a fixed folder (for multi-step conversions via temp files).</summary>
internal sealed class OperationContextOverride(OperationContext source, string outputDirectory)
{
    public OperationContext Context { get; } = new()
    {
        Tools = source.Tools,
        Progress = source.Progress,
        CancellationToken = source.CancellationToken,
        OutputDirectory = outputDirectory,
        Parameters = source.Parameters,
        Settings = source.Settings,
    };
}

/// <summary>Sorts "page 2" before "page 10".</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var si = i;
                var sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                var c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                if (c != 0)
                    return c;
            }
            else
            {
                var c = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
                if (c != 0)
                    return c;
                i++;
                j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
