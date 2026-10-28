using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Content;
using static FileAction.Core.Loc;

namespace FileAction.Core.Backends;

/// <summary>PDF rendering (PDFium), text extraction (PdfPig) and page operations (PDFsharp).</summary>
public static class PdfBackend
{
    public static int PageCount(string path)
    {
        using var stream = File.OpenRead(path);
        return PDFtoImage.Conversion.GetPageCount(stream);
    }

    /// <summary>
    /// Renders every page. A single page becomes "name.png"; more pages go into a folder "name (pages)".
    /// </summary>
    public static async Task<IEnumerable<string>> RenderAsync(FileItem item, string format, OperationContext ctx, IProgress<double> progress)
    {
        var bytes = await File.ReadAllBytesAsync(item.Path, ctx.CancellationToken).ConfigureAwait(false);
        var count = PDFtoImage.Conversion.GetPageCount(bytes);
        var options = new PDFtoImage.RenderOptions(Dpi: ctx.Settings.PdfDpi, BackgroundColor: SkiaSharp.SKColors.White, WithAnnotations: true, WithFormFill: true);
        var dir = ctx.OutputDirFor(item.Path);

        if (count == 1)
        {
            var single = await OutputPaths.ProduceAsync(dir, item.BaseName, format, tmp =>
                Task.Run(() => SavePage(tmp, bytes, 0, format, options, ctx.Settings.JpegQuality), ctx.CancellationToken)).ConfigureAwait(false);
            return [single];
        }

        var folder = OutputPaths.UniqueDirectory(dir, $"{item.BaseName} ({T("stránky", "pages")})");
        Directory.CreateDirectory(folder);
        var digits = count.ToString().Length;
        try
        {
            for (var i = 0; i < count; i++)
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(folder, $"{item.BaseName} - {(i + 1).ToString().PadLeft(digits, '0')}.{format}");
                await Task.Run(() => SavePage(path, bytes, i, format, options, ctx.Settings.JpegQuality), ctx.CancellationToken).ConfigureAwait(false);
                progress.Report((i + 1.0) / count);
            }
        }
        catch
        {
            OutputPaths.TryDelete(folder);
            throw;
        }
        return [folder];
    }

    /// <summary>Renders every page to "page-001.png"… in <paramref name="dir"/> (used for OCR).</summary>
    public static IReadOnlyList<string> RenderPagesToPng(string pdf, string dir, int dpi, CancellationToken ct)
    {
        var bytes = File.ReadAllBytes(pdf);
        var count = PDFtoImage.Conversion.GetPageCount(bytes);
        var options = new PDFtoImage.RenderOptions(Dpi: dpi, BackgroundColor: SkiaSharp.SKColors.White, WithAnnotations: true, WithFormFill: true);
        var pages = new List<string>();
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(dir, $"page-{i + 1:000}.png");
            SavePage(path, bytes, i, "png", options, 100);
            pages.Add(path);
        }
        return pages;
    }

    private static void SavePage(string path, byte[] pdf, int page, string format, PDFtoImage.RenderOptions options, int quality)
    {
        using var bitmap = PDFtoImage.Conversion.ToImage(pdf, page, options: options);
        using var file = File.Create(path);
        var encoded = format == "jpg" ? SkiaSharp.SKEncodedImageFormat.Jpeg
            : format == "webp" ? SkiaSharp.SKEncodedImageFormat.Webp
            : SkiaSharp.SKEncodedImageFormat.Png;
        bitmap.Encode(file, encoded, quality);
    }

    /// <summary>Plain text, pages separated by blank lines (or "---" rules for Markdown).</summary>
    public static string ExtractText(string path, bool markdown)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(path);
        var sb = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page).Trim();
            if (text.Length == 0)
                continue;
            if (sb.Length > 0)
                sb.Append(markdown ? "\n\n---\n\n" : "\n\n");
            sb.Append(markdown ? ToMarkdownParagraphs(text) : text);
        }
        if (sb.Length == 0)
            throw new ConversionException(T("PDF neobsahuje žádný text (nejspíš je to sken). Nejprve použijte OCR (např. Text Extractor v PowerToys).", "The PDF contains no text (probably a scan). Run OCR first, for example PowerToys Text Extractor."));
        return sb.ToString();
    }

    private static string ToMarkdownParagraphs(string text)
    {
        // Join wrapped lines into paragraphs; keep blank lines as paragraph breaks.
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        var paragraph = new StringBuilder();
        void Flush()
        {
            if (paragraph.Length == 0)
                return;
            sb.Append(paragraph.ToString().Trim()).Append("\n\n");
            paragraph.Clear();
        }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }
            if (paragraph.Length > 0)
            {
                if (paragraph[^1] == '-' && paragraph.Length > 1 && char.IsLetter(paragraph[^2]))
                    paragraph.Length--; // de-hyphenate
                else
                    paragraph.Append(' ');
            }
            paragraph.Append(line);
        }
        Flush();
        return sb.ToString().TrimEnd();
    }

    public static Task<string> ToTextAsync(FileItem item, string format, OperationContext ctx) =>
        OutputPaths.ProduceAsync(ctx.OutputDirFor(item.Path), item.BaseName, format, tmp =>
            Task.Run(() => File.WriteAllText(tmp, ExtractText(item.Path, format == "md"), new UTF8Encoding(false)), ctx.CancellationToken));

    /// <summary>One worksheet per page; whitespace separates table cells. This is text extraction, not layout reconstruction.</summary>
    public static Task<string> ToSpreadsheetAsync(FileItem item, OperationContext ctx) =>
        OutputPaths.ProduceAsync(ctx.OutputDirFor(item.Path), item.BaseName, "xlsx", tmp => Task.Run(() =>
        {
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(item.Path);
            var sheets = new List<SpreadsheetBackend.Sheet>();
            foreach (var page in pdf.GetPages())
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();
                sheets.Add(new SpreadsheetBackend.Sheet($"Page {page.Number}", ExtractRows(page.GetWords().ToList())));
            }
            if (!sheets.Any(s => s.Rows.Count > 0))
                throw new ConversionException(T("PDF neobsahuje žádný text (nejspíš je to sken). Nejprve použijte OCR (např. Text Extractor v PowerToys).", "The PDF contains no text (probably a scan). Run OCR first, for example PowerToys Text Extractor."));
            SpreadsheetBackend.WriteXlsx(sheets, tmp);
        }, ctx.CancellationToken));

    private sealed record TextCell(double Left, double Right, string Text);

    private static List<string[]> ExtractRows(List<Word> words)
    {
        // PdfPig coordinates are measured upwards from the bottom of the page.
        // Group by baseline/vertical centre rather than content stream order.
        var lines = new List<List<Word>>();
        foreach (var word in words.OrderByDescending(w => (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2))
        {
            var centre = (word.BoundingBox.Top + word.BoundingBox.Bottom) / 2;
            var line = lines.LastOrDefault();
            if (line is null || Math.Abs(centre - (line[0].BoundingBox.Top + line[0].BoundingBox.Bottom) / 2) > Math.Max(2, word.BoundingBox.Height * .45))
                lines.Add([word]);
            else
                line.Add(word);
        }
        var rows = new List<List<TextCell>>();
        foreach (var line in lines)
        {
            var ordered = line.OrderBy(w => w.BoundingBox.Left).ToList();
            var characterWidths = ordered.Where(w => w.Text.Length > 0).Select(w => w.BoundingBox.Width / w.Text.Length).Order().ToList();
            var gap = Math.Max(12, characterWidths.Count == 0 ? 12 : characterWidths[characterWidths.Count / 2] * 2.5);
            var cells = new List<TextCell>();
            foreach (var word in ordered)
            {
                if (cells.Count > 0 && word.BoundingBox.Left - cells[^1].Right < gap)
                    cells[^1] = cells[^1] with { Right = word.BoundingBox.Right, Text = cells[^1].Text + " " + word.Text };
                else
                    cells.Add(new(word.BoundingBox.Left, word.BoundingBox.Right, word.Text));
            }
            rows.Add(cells);
        }
        // Discover column alignment from multi-cell rows so a full-width title does
        // not accidentally merge all table columns. Empty cells remain empty.
        var anchors = new List<TextCell>();
        foreach (var cell in rows.Where(r => r.Count > 1).SelectMany(r => r).OrderBy(c => c.Left))
        {
            if (!anchors.Any(a => Math.Abs(a.Left - cell.Left) <= 12 || Math.Abs(a.Right - cell.Right) <= 12))
                anchors.Add(cell);
        }
        anchors = anchors.OrderBy(a => a.Left).ToList();
        return rows.Select(row =>
        {
            if (anchors.Count == 0)
                return new[] { string.Join(" ", row.Select(c => c.Text)) };
            var values = new string[anchors.Count];
            Array.Fill(values, "");
            foreach (var cell in row)
            {
                var column = Enumerable.Range(0, anchors.Count).MinBy(i => Math.Min(Math.Abs(anchors[i].Left - cell.Left), Math.Abs(anchors[i].Right - cell.Right)));
                values[column] = values[column].Length == 0 ? cell.Text : values[column] + " " + cell.Text;
            }
            return values;
        }).ToList();
    }

    /// <summary>PDF → DOCX/HTML/EPUB/ODT: extract text to Markdown, then let pandoc build the document.</summary>
    public static async Task<string> ToDocumentAsync(FileItem item, string format, OperationContext ctx)
    {
        var tempDir = OutputPaths.TempDirectory();
        try
        {
            var md = Path.Combine(tempDir, item.BaseName + ".md");
            await File.WriteAllTextAsync(md, ExtractText(item.Path, markdown: true), new UTF8Encoding(false), ctx.CancellationToken).ConfigureAwait(false);
            return await OutputPaths.ProduceAsync(ctx.OutputDirFor(item.Path), item.BaseName, format, tmp =>
                PandocBackend.ConvertAsync(ctx, md, "md", tmp, format)).ConfigureAwait(false);
        }
        finally
        {
            OutputPaths.TryDelete(tempDir);
        }
    }

    public static string Merge(IReadOnlyList<string> inputs, string output)
    {
        using var result = new PdfSharp.Pdf.PdfDocument();
        foreach (var input in inputs)
        {
            using var source = Open(input, PdfDocumentOpenMode.Import);
            foreach (var page in source.Pages)
                result.AddPage(page);
        }
        result.Save(output);
        return output;
    }

    public static async Task<IEnumerable<string>> SplitAsync(FileItem item, OperationContext ctx, IProgress<double> progress)
    {
        var dir = ctx.OutputDirFor(item.Path);
        var folder = OutputPaths.UniqueDirectory(dir, $"{item.BaseName} ({T("stránky", "pages")})");
        Directory.CreateDirectory(folder);
        try
        {
            await Task.Run(() =>
            {
                using var source = Open(item.Path, PdfDocumentOpenMode.Import);
                var count = source.PageCount;
                var digits = count.ToString().Length;
                for (var i = 0; i < count; i++)
                {
                    ctx.CancellationToken.ThrowIfCancellationRequested();
                    using var single = new PdfSharp.Pdf.PdfDocument();
                    single.AddPage(source.Pages[i]);
                    single.Save(Path.Combine(folder, $"{item.BaseName} - {(i + 1).ToString().PadLeft(digits, '0')}.pdf"));
                    progress.Report((i + 1.0) / count);
                }
            }, ctx.CancellationToken).ConfigureAwait(false);
        }
        catch
        {
            OutputPaths.TryDelete(folder);
            throw;
        }
        return [folder];
    }

    public static void Rotate(string input, string output, int degrees)
    {
        using var document = Open(input, PdfDocumentOpenMode.Modify);
        foreach (var page in document.Pages)
            page.Rotate = ((page.Rotate + degrees) % 360 + 360) % 360;
        document.Save(output);
    }

    /// <summary>Reorder, remove or duplicate pages using 1-based page numbers and inclusive ranges.</summary>
    public static void Organize(string input, string output, string selection, int degrees = 0)
    {
        if (degrees % 90 != 0)
            throw new ConversionException("Rotation must be a multiple of 90 degrees.");
        using var source = Open(input, PdfDocumentOpenMode.Import);
        var pages = ParsePages(selection, source.PageCount);
        using var result = new PdfSharp.Pdf.PdfDocument();
        foreach (var number in pages)
        {
            var page = result.AddPage(source.Pages[number - 1]);
            page.Rotate = ((page.Rotate + degrees) % 360 + 360) % 360;
        }
        result.Save(output);
    }

    public static IReadOnlyList<int> ParsePages(string selection, int count)
    {
        if (string.IsNullOrWhiteSpace(selection) || selection.Equals("all", StringComparison.OrdinalIgnoreCase))
            return Enumerable.Range(1, count).ToList();
        var pages = new List<int>();
        foreach (var raw in selection.Split(','))
        {
            var ends = raw.Trim().Split('-');
            if (ends.Length > 2 || !int.TryParse(ends[0], out var first) || first < 1 || first > count)
                throw new ConversionException($"Invalid page selection '{raw}'. Use page numbers from 1 to {count}, for example 3,1-2,3.");
            var last = first;
            if (ends.Length == 2 && (!int.TryParse(ends[1], out last) || last < 1 || last > count))
                throw new ConversionException($"Invalid page selection '{raw}'. The PDF has {count} pages.");
            var step = first <= last ? 1 : -1;
            for (var page = first; ; page += step)
            {
                pages.Add(page);
                if (page == last) break;
            }
        }
        return pages;
    }

    public static void Metadata(string input, string output, bool strip, string title, string author, string subject, string keywords)
    {
        using var document = Open(input, PdfDocumentOpenMode.Modify);
        if (strip)
        {
            // Remove both document info and the separate XMP metadata stream.
            document.Info.Elements.Clear();
            document.Internals.Catalog.Elements.Remove("/Metadata");
        }
        else
        {
            document.Info.Title = title;
            document.Info.Author = author;
            document.Info.Subject = subject;
            document.Info.Keywords = keywords;
        }
        document.Save(output);
    }

    /// <summary>Re-saves with compressed streams and without unused objects.</summary>
    public static void Optimize(string input, string output)
    {
        using var document = Open(input, PdfDocumentOpenMode.Modify);
        document.Options.CompressContentStreams = true;
        document.Options.NoCompression = false;
        document.Options.EnableCcittCompressionForBilevelImages = true;
        document.Save(output);
    }

    private static PdfSharp.Pdf.PdfDocument Open(string path, PdfDocumentOpenMode mode)
    {
        try
        {
            return PdfReader.Open(path, mode);
        }
        catch (PdfReaderException ex)
        {
            throw new ConversionException(T($"PDF {Path.GetFileName(path)} nelze otevřít: {ex.Message}", $"Cannot open PDF {Path.GetFileName(path)}: {ex.Message}"), ex);
        }
    }
}
