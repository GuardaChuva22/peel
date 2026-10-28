using System.Globalization;
using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using FileAction.Core;
using FileAction.Core.Backends;
using FileAction.Core.Operations;
using PdfSharp.Pdf.IO;
using ImageMagick;

namespace FileAction.Tests;

public sealed class LocalFeatureTests : IDisposable
{
    private readonly string _dir = OutputPaths.TempDirectory();

    [Fact]
    public async Task Pdf_table_becomes_real_cells_on_separate_page_sheets()
    {
        var source = MakePdf("table.pdf", [
            Text(50, 750, "Product") + Text(250, 750, "Quantity") + Text(370, 750, "Code") +
            Text(50, 725, "Green apple") + Text(250, 725, "12") + Text(370, 725, "00123") +
            Text(50, 700, "Pear") + Text(250, 700, "7") + Text(370, 700, "00999"),
            Text(50, 750, "Second page") + Text(50, 725, "Separate content")]);
        var op = Catalog.Build().Single(o => o.Id == "pdf.to.xlsx");
        var result = await op.ExecuteAsync([new FileItem(source)], new OperationContext { Tools = new(), OutputDirectory = _dir });
        Assert.Contains("review table alignment", result.Note);
        using var workbook = new XLWorkbook(Assert.Single(result.Outputs));
        Assert.Equal(2, workbook.Worksheets.Count);
        var table = workbook.Worksheet("Page 1");
        Assert.Equal("Product", table.Cell("A1").GetString());
        Assert.Equal("Quantity", table.Cell("B1").GetString());
        Assert.Equal("Green apple", table.Cell("A2").GetString());
        Assert.Equal(12, table.Cell("B2").GetDouble());
        Assert.Equal("00123", table.Cell("C2").GetString());
        Assert.Equal("Pear", table.Cell("A3").GetString());
        Assert.Equal(7, table.Cell("B3").GetDouble());
        Assert.Equal("Second page", workbook.Worksheet("Page 2").Cell("A1").GetString());
    }

    [Fact]
    public async Task Pdf_without_text_reports_OCR_and_leaves_no_output()
    {
        var source = MakePdf("scan.pdf", [""]);
        var outDir = Path.Combine(_dir, "empty-output");
        Directory.CreateDirectory(outDir);
        var exception = await Assert.ThrowsAsync<ConversionException>(() => PdfBackend.ToSpreadsheetAsync(new(source),
            new OperationContext { Tools = new(), OutputDirectory = outDir }));
        Assert.Contains("OCR", exception.Message);
        Assert.Empty(Directory.GetFiles(outDir));
        Assert.Contains("OCR", Assert.Throws<ConversionException>(() => PdfBackend.ExtractText(source, false)).Message);
    }

    [Fact]
    public void Organize_reorders_duplicates_removes_and_rotates_pages()
    {
        var source = MakePdf("pages.pdf", [Text(50, 750, "First"), Text(50, 750, "Second"), Text(50, 750, "Third")]);
        var output = Path.Combine(_dir, "organized.pdf");
        PdfBackend.Organize(source, output, "3,1-2,3", 90);
        using var result = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Equal(4, result.NumberOfPages);
        Assert.Equal(new[] { "Third", "First", "Second", "Third" }, result.GetPages().Select(p => p.Text));
        using var imported = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.All(imported.Pages.Cast<PdfSharp.Pdf.PdfPage>(), p => Assert.Equal(90, p.Rotate));
        // The original stays intact.
        using var original = UglyToad.PdfPig.PdfDocument.Open(source);
        Assert.Equal(3, original.NumberOfPages);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("1-9")]
    [InlineData("1,,2")]
    [InlineData("1-a")]
    public void Organize_rejects_invalid_page_numbers(string selection) =>
        Assert.Throws<ConversionException>(() => PdfBackend.ParsePages(selection, 3));

    [Fact]
    public void Pdf_metadata_can_be_replaced_and_removed()
    {
        var source = MakePdf("source.pdf", [Text(50, 750, "Kept content")]);
        var tagged = Path.Combine(_dir, "tagged.pdf");
        var stripped = Path.Combine(_dir, "stripped.pdf");
        PdfBackend.Metadata(source, tagged, false, "Quarterly report", "Sample Author", "Sales", "private");
        using (var document = PdfReader.Open(tagged, PdfDocumentOpenMode.Import))
        {
            Assert.Equal("Sample Author", document.Info.Author);
            Assert.Equal("Quarterly report", document.Info.Title);
        }
        PdfBackend.Metadata(tagged, stripped, true, "", "", "", "");
        using (var document = PdfReader.Open(stripped, PdfDocumentOpenMode.Import))
        {
            Assert.True(string.IsNullOrEmpty(document.Info.Author));
            Assert.True(string.IsNullOrEmpty(document.Info.Title));
            Assert.True(string.IsNullOrEmpty(document.Info.Keywords));
        }
        Assert.Contains("Kept content", PdfBackend.ExtractText(stripped, false));
        Assert.DoesNotContain("Sample Author", Encoding.ASCII.GetString(File.ReadAllBytes(stripped)));
    }

    [Fact]
    public async Task Pdf_to_PowerPoint_preserves_page_count_and_text_when_LibreOffice_is_available()
    {
        var tools = new ToolLocator();
        if (!tools.Has(ExternalTool.LibreOffice)) return;
        var source = MakePdf("slides.pdf", [Text(50, 750, "First slide"), Text(50, 750, "Second slide")]);
        var output = await OfficeBackend.ConvertAsync(new(source), "pptx", new OperationContext { Tools = tools, OutputDirectory = _dir }, "impress_pdf_import");
        using var zip = ZipFile.OpenRead(output);
        var slides = zip.Entries.Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, slides.Count);
        var xml = new StringBuilder();
        foreach (var slide in slides)
        {
            using var reader = new StreamReader(slide.Open());
            xml.Append(await reader.ReadToEndAsync());
        }
        Assert.Contains("First slide", xml.ToString());
        Assert.Contains("Second slide", xml.ToString());
    }

    [Fact]
    public void Pdf_metadata_stripping_removes_embedded_XMP_payload()
    {
        const string secret = "PrivateXmpAuthor28639";
        var source = MakePdf("xmp.pdf", [Text(50, 750, "Public content")],
            $"<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description xmlns:dc='http://purl.org/dc/elements/1.1/' dc:creator='{secret}'/></rdf:RDF></x:xmpmeta>");
        var output = Path.Combine(_dir, "no-xmp.pdf");
        PdfBackend.Metadata(source, output, true, "", "", "", "");
        Assert.DoesNotContain(secret, Encoding.ASCII.GetString(File.ReadAllBytes(output)));
        using (var parsed = PdfReader.Open(output, PdfDocumentOpenMode.Import))
            Assert.DoesNotContain(parsed.Internals.GetAllObjects().OfType<PdfSharp.Pdf.PdfDictionary>(),
                obj => obj.Stream is not null && Encoding.ASCII.GetString(obj.Stream.UnfilteredValue).Contains(secret, StringComparison.Ordinal));
        Assert.Contains("Public content", PdfBackend.ExtractText(output, false));
    }

    [Fact]
    public async Task Heic_export_is_gated_by_encoder_capability_and_reopens_when_supported()
    {
        var capabilities = MagickNET.SupportedFormats.Single(f => f.Format == MagickFormat.Heic);
        var source = Path.Combine(_dir, "source.png");
        using (var image = new MagickImage(MagickColors.Orange, 64, 48)) image.Write(source);
        var op = Catalog.Build().Single(o => o.Id == "image.to.heic");
        Assert.Equal(capabilities.SupportsWriting, op.IsAvailable(new(), [new(source)]));
        if (!capabilities.SupportsWriting) return;
        var result = await op.ExecuteAsync([new(source)], new OperationContext { Tools = new(), OutputDirectory = _dir });
        using var reopened = new MagickImage(Assert.Single(result.Outputs));
        Assert.Equal(MagickFormat.Heic, reopened.Format);
        Assert.Equal(64u, reopened.Width);
        Assert.Equal(48u, reopened.Height);
    }

    [Theory]
    [InlineData("txt", "png")]
    [InlineData("txt", "jpg")]
    [InlineData("md", "png")]
    public async Task Documents_render_as_readable_page_images(string format, string target)
    {
        var tools = new ToolLocator();
        if (!tools.Has(ExternalTool.LibreOffice) || format == "md" && !tools.Has(ExternalTool.Pandoc)) return;
        var source = Path.Combine(_dir, "Readable document." + format);
        await File.WriteAllTextAsync(source, format == "md" ? "# Document title\n\nReadable test content." : "Readable test content.");
        var result = await Catalog.Build().Single(o => o.Id == "doc.to." + target).ExecuteAsync([new(source)],
            new OperationContext { Tools = tools, OutputDirectory = Path.Combine(_dir, "rendered") });
        using var image = new MagickImage(Assert.Single(result.Outputs));
        Assert.True(image.Width > 500 && image.Height > 500);
        Assert.True(image.Statistics().Composite().StandardDeviation > .01, "The page must contain visible text, not an empty white bitmap.");
    }

    [Theory]
    [InlineData("mov")]
    [InlineData("mkv")]
    public async Task Gif_animation_converts_to_additional_video_containers(string target)
    {
        var tools = new ToolLocator();
        if (!tools.Has(ExternalTool.Ffmpeg)) return;
        var source = Path.Combine(_dir, "animation.gif");
        using (var frames = new MagickImageCollection())
        {
            frames.Add(new MagickImage(MagickColors.Red, 64, 48) { AnimationDelay = 25 });
            frames.Add(new MagickImage(MagickColors.Blue, 64, 48) { AnimationDelay = 25 });
            frames.Write(source, MagickFormat.Gif);
        }
        var result = await Catalog.Build().Single(o => o.Id == "gif.to." + target).ExecuteAsync([new(source)],
            new OperationContext { Tools = tools, OutputDirectory = _dir });
        var output = Assert.Single(result.Outputs);
        var decoding = new StringBuilder();
        await ProcessRunner.RunAsync(tools.Require(ExternalTool.Ffmpeg), ["-hide_banner", "-i", output, "-f", "null", "-"], default,
            line => decoding.AppendLine(line));
        Assert.Contains("64x48", decoding.ToString());
        Assert.Contains("Video:", decoding.ToString());
        Assert.DoesNotContain("Audio:", decoding.ToString());
    }

    [Fact]
    public async Task Image_crop_and_redaction_change_only_the_requested_region()
    {
        var source = Path.Combine(_dir, "sensitive.png");
        using (var image = new MagickImage(MagickColors.Red, 80, 60)) image.Write(source);
        var parameters = new Dictionary<string, string> { ["x"] = "10", ["y"] = "10", ["width"] = "20", ["height"] = "15", ["color"] = "#000000" };
        var context = new OperationContext { Tools = new(), OutputDirectory = _dir, Parameters = parameters };
        var redact = await Catalog.Build().Single(o => o.Id == "image.redact").ExecuteAsync([new(source)], context);
        using (var result = new MagickImage(Assert.Single(redact.Outputs)))
        using (var pixels = result.GetPixels())
        {
            Assert.Equal(80u, result.Width);
            Assert.Equal(60u, result.Height);
            Assert.Equal((byte)0, pixels.GetPixel(20, 15).ToColor()!.R);
            Assert.Equal((byte)255, pixels.GetPixel(60, 40).ToColor()!.R);
        }
        var crop = await Catalog.Build().Single(o => o.Id == "image.crop").ExecuteAsync([new(source)], context);
        using var cropped = new MagickImage(Assert.Single(crop.Outputs));
        Assert.Equal(20u, cropped.Width);
        Assert.Equal(15u, cropped.Height);
        using var original = new MagickImage(source);
        Assert.Equal(80u, original.Width);
    }

    [Fact]
    public async Task Collage_contains_both_source_images_in_separate_cells()
    {
        var red = Path.Combine(_dir, "red.png");
        var blue = Path.Combine(_dir, "blue.png");
        using (var image = new MagickImage(MagickColors.Red, 40, 40)) image.Write(red);
        using (var image = new MagickImage(MagickColors.Blue, 40, 40)) image.Write(blue);
        var result = await Catalog.Build().Single(o => o.Id == "image.collage").ExecuteAsync([new(red), new(blue)],
            new OperationContext { Tools = new(), OutputDirectory = _dir, Parameters = new Dictionary<string, string> { ["columns"] = "2", ["cell"] = "40", ["gap"] = "0" } });
        using var collage = new MagickImage(Assert.Single(result.Outputs));
        Assert.Equal(80u, collage.Width);
        Assert.Equal(40u, collage.Height);
        using var pixels = collage.GetPixels();
        Assert.Equal((byte)255, pixels.GetPixel(20, 20).ToColor()!.R);
        Assert.Equal((byte)255, pixels.GetPixel(60, 20).ToColor()!.B);
    }

    // A small standards-based fixture with a built-in Helvetica font. It avoids
    // dependence on LibreOffice, system font discovery or the conversion under test.
    private string MakePdf(string name, string[] contents, string? xmp = null)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" };
        var kids = new List<string>();
        foreach (var content in contents)
        {
            var pageId = objects.Count + 1;
            kids.Add($"{pageId} 0 R");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {pageId + 1} 0 R >>");
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
        }
        objects[1] = $"<< /Type /Pages /Count {contents.Length} /Kids [{string.Join(" ", kids)}] >>";
        if (xmp is not null)
        {
            objects[0] = $"<< /Type /Catalog /Pages 2 0 R /Metadata {objects.Count + 1} 0 R >>";
            objects.Add($"<< /Type /Metadata /Subtype /XML /Length {Encoding.ASCII.GetByteCount(xmp)} >>\nstream\n{xmp}\nendstream");
        }
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
            pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
        return path;
    }

    private static string Text(int x, int y, string text) => $"BT /F1 12 Tf 1 0 0 1 {x} {y} Tm ({text}) Tj ET\n";
    public void Dispose() => OutputPaths.TryDelete(_dir);
}
