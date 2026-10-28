using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileAction.App.Actions;
using FileAction.App.Jobs;
using FileAction.App.UI;
using FileAction.App.Wheel;
using FileAction.Core;
using FileAction.Core.Operations;

namespace FileAction.App;

/// <summary>
/// <c>Peel.exe --self-test [folder]</c>: builds every window offscreen and renders previews to PNG.
/// Used by CI to catch UI construction errors on a real Windows machine.
/// </summary>
internal static class SelfTest
{
    /// <summary>Exercises isolated converters without constructing WPF or using the network/clipboard.</summary>
    public static int RunWorkerTests(string outDir)
    {
        var log = new List<string>();
        try
        {
            Directory.CreateDirectory(outDir);
            TestWorkersAsync(Path.GetFullPath(outDir), log).GetAwaiter().GetResult();
            File.WriteAllLines(Path.Combine(outDir, "worker-test.txt"), log);
            return 0;
        }
        catch (Exception ex)
        {
            log.Add("FAILED: " + ex);
            Directory.CreateDirectory(outDir);
            File.WriteAllLines(Path.Combine(outDir, "worker-test.txt"), log);
            return 1;
        }
    }

    private static async Task TestWorkersAsync(string outDir, List<string> log)
    {
        var input = Path.Combine(outDir, "worker-source.png");
        using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Coral, 96, 64))
            image.Write(input);
        var original = await File.ReadAllBytesAsync(input);
        var operation = Catalog.Build().Single(op => op.Id == "image.to.jpg");
        IReadOnlyList<FileItem> items = [new FileItem(input)];
        OperationContext Context(CancellationToken token = default) => new()
        {
            Tools = new ToolLocator(), OutputDirectory = Path.Combine(outDir, "results"), CancellationToken = token,
        };

        var jobs = Enumerable.Range(0, 6).Select(_ => WorkerHost.ExecuteAsync(operation, items, Context())).ToArray();
        var maxActive = WorkerHost.ActiveWorkerCount;
        while (jobs.Any(job => !job.IsCompleted))
        {
            maxActive = Math.Max(maxActive, WorkerHost.ActiveWorkerCount);
            Check(WorkerHost.ActiveWorkerCount <= 2, "at most two isolated conversions may run at once");
            await Task.Delay(10);
        }
        var results = await Task.WhenAll(jobs);
        var outputs = results.SelectMany(result => result.Outputs).ToArray();
        Check(maxActive == 2 && WorkerHost.ActiveWorkerCount == 0, "worker queue starts two jobs and releases all slots");
        Check(outputs.Length == 6 && outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 6, "concurrent conversions publish six unique files");
        foreach (var output in outputs)
        {
            using var decoded = new ImageMagick.MagickImage(output);
            Check(decoded.Width == 96 && decoded.Height == 64, "worker result is a valid complete image");
        }
        var sourceAfter = await File.ReadAllBytesAsync(input);
        Check(original.SequenceEqual(sourceAfter), "workers preserve the original source");
        log.Add("ok workers/queue-two-and-six-valid-unique-results");

        var first = WorkerHost.ExecuteAsync(operation, items, Context());
        var second = WorkerHost.ExecuteAsync(operation, items, Context());
        using (var cancel = new CancellationTokenSource())
        {
            var queued = WorkerHost.ExecuteAsync(operation, items, Context(cancel.Token));
            cancel.Cancel();
            var cancelled = false;
            try { await queued; }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "a queued job can be cancelled before a worker starts");
        }
        await Task.WhenAll(first, second);
        Check(WorkerHost.ActiveWorkerCount == 0, "queued cancellation does not leak a worker slot");
        log.Add("ok workers/queued-cancellation");

        var broken = Path.Combine(outDir, "broken.png");
        await File.WriteAllTextAsync(broken, "This is not a PNG image.");
        var failed = false;
        try { await WorkerHost.ExecuteAsync(operation, [new FileItem(broken)], Context()); }
        catch (ConversionException ex) { failed = !string.IsNullOrWhiteSpace(ex.Message); }
        Check(failed, "a codec failure returns a readable error to the parent");
        var recovered = await WorkerHost.ExecuteAsync(operation, items, Context());
        Check(recovered.Outputs.Count == 1 && File.Exists(recovered.Outputs[0]), "the next conversion succeeds after a worker failure");
        log.Add("ok workers/codec-error-and-recovery");

        var cleanup = Path.Combine(outDir, "cleanup-fixture");
        Directory.CreateDirectory(cleanup);
        var part = Path.Combine(cleanup, ".test.1234abcd.part.png");
        var partDirectory = Path.Combine(cleanup, ".pages.1234abcd.part");
        var source = Path.Combine(cleanup, ".source.1234abcd.part.png");
        var finished = Path.Combine(cleanup, "finished.jpg");
        await File.WriteAllTextAsync(part, "partial");
        await File.WriteAllTextAsync(source, "original");
        await File.WriteAllTextAsync(finished, "finished");
        Directory.CreateDirectory(partDirectory);
        await File.WriteAllTextAsync(Path.Combine(partDirectory, "unfinished.png"), "partial");
        var entries = new[]
        {
            new { path = part, isDirectory = false }, new { path = partDirectory, isDirectory = true },
            new { path = source, isDirectory = false }, new { path = finished, isDirectory = false },
        };
        await File.WriteAllLinesAsync(Path.Combine(cleanup, "temporary-paths.jsonl"),
            entries.Select(entry => System.Text.Json.JsonSerializer.Serialize(entry)).Append("{incomplete"));
        WorkerHost.CleanOwnedTemporaryPaths(cleanup, [source]);
        Check(!File.Exists(part) && !Directory.Exists(partDirectory), "worker-owned partial files and folders are cleaned");
        Check(File.ReadAllText(source) == "original" && File.Exists(finished), "cleanup preserves sources and completed outputs");
        log.Add("ok workers/partial-cleanup-and-source-protection");

        if (Ocr.OcrOperation.Available)
        {
            var textImage = Path.Combine(outDir, "privacy-text.png");
            using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.White, 600, 160))
            {
                image.Draw(new ImageMagick.Drawing.Drawables().Font("Arial").FontPointSize(48)
                    .FillColor(ImageMagick.MagickColors.Black).Text(30, 95, "PRIVATE 12345"));
                image.Write(textImage);
            }
            var boxes = await Ocr.ImagePrivacyDetector.DetectAsync(textImage, faces: false, text: true);
            Check(boxes.Count >= 2 && boxes.All(box => box.X >= 0 && box.Y >= 0 && box.X + box.Width <= 600 && box.Y + box.Height <= 160),
                "native OCR privacy detector returns bounded word rectangles");
            log.Add("ok privacy/native-ocr-word-rectangles");
        }
        else log.Add("skipped privacy OCR (Windows has no installed recognizer)");
        if (Windows.Media.FaceAnalysis.FaceDetector.IsSupported)
        {
            var boxes = await Ocr.ImagePrivacyDetector.DetectAsync(input, faces: true, text: false);
            Check(boxes.Count == 0, "a plain solid image has no detected faces");
            log.Add("ok privacy/native-face-detector-blank-image");
        }
        else log.Add("skipped privacy faces (unsupported by this Windows installation)");

        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var pending = ProcessRunner.RunAsync(powershell, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"], CancellationToken.None);
        WorkerHost.CancelAll();
        var terminated = false;
        try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (ConversionException) { terminated = true; }
        Check(terminated, "app shutdown terminates an already started child process");
        var blocked = false;
        try { await ProcessRunner.RunAsync(powershell, ["-NoProfile", "-Command", "exit 0"], CancellationToken.None); }
        catch (OperationCanceledException) { blocked = true; }
        Check(blocked, "no new child process may start after app shutdown");
        log.Add("ok workers/shutdown-kills-active-and-blocks-new-launches");
    }

    public static int Run(string outDir)
    {
        var log = new List<string>();
        try
        {
            Directory.CreateDirectory(outDir);
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            JobCard.Animate = false;
            foreach (var (language, dark, folder) in new[] { (Language.English, true, "en"), (Language.English, false, "en-light"), (Language.Czech, true, "cs") })
            {
                Loc.Current = language;
                Theme.Override = dark;
                Theme.Refresh();
                RenderAll(Path.Combine(outDir, folder), language == Language.English, log);
            }

            TestDrops(log);
            TestOcr(outDir, log);
            TestUpdateCheck(log);

            File.WriteAllLines(Path.Combine(outDir, "self-test.txt"), log);
            return 0;
        }
        catch (Exception ex)
        {
            log.Add("FAILED: " + ex);
            try
            {
                Directory.CreateDirectory(outDir);
                File.WriteAllLines(Path.Combine(outDir, "self-test.txt"), log);
            }
            catch (IOException)
            {
            }
            return 1;
        }
    }

    private static void RenderAll(string outDir, bool english, List<string> log)
    {
        Directory.CreateDirectory(outDir);
        string N(string cs, string en) => english ? en : cs;
        var settings = new AppSettings();
        var tools = new ToolLocator();
        var core = WheelController.BuildOperations();
        var actions = FileActions.Build(settings, () => { });
        IReadOnlyList<Operation> Source(WheelKind wheel, IReadOnlyList<FileItem> items) =>
            Catalog.For(wheel == WheelKind.Actions ? actions : core, wheel, items, tools);

        var samples = new (string Name, WheelMode Mode, WheelKind Wheel, string[] Files, int Highlight)[]
        {
            ("wheel-image-convert", WheelMode.Drag, WheelKind.Convert, [N(@"C:\Fotky\dovolená.jpg", @"C:\Photos\vacation.jpg")], 1),
            ("wheel-image-tools", WheelMode.Click, WheelKind.Tools, [N(@"C:\Fotky\dovolená.jpg", @"C:\Photos\vacation.jpg")], 0),
            ("wheel-video-convert", WheelMode.Drag, WheelKind.Convert, [N(@"C:\Videa\klip.mov", @"C:\Videos\clip.mov")], 0),
            ("wheel-video-tools", WheelMode.Drag, WheelKind.Tools, [N(@"C:\Videa\klip.mov", @"C:\Videos\clip.mov")], 3),
            ("wheel-pdf-convert", WheelMode.Drag, WheelKind.Convert, [N(@"C:\Dokumenty\smlouva.pdf", @"C:\Documents\contract.pdf")], 2),
            ("wheel-pdf-tools", WheelMode.Click, WheelKind.Tools, [@"C:\a.pdf", @"C:\b.pdf"], 0),
            ("wheel-sheet-convert", WheelMode.Drag, WheelKind.Convert, [N(@"C:\Data\tabulka.xlsx", @"C:\Data\sales.xlsx")], -1),
            ("wheel-actions", WheelMode.Drag, WheelKind.Actions, [N(@"C:\Fotky\dovolená.jpg", @"C:\Photos\vacation.jpg")], 4),
            ("wheel-waiting-for-drop", WheelMode.Drag, WheelKind.Convert, [], -1),
        };
        foreach (var s in samples)
        {
            var window = new WheelWindow(s.Mode, s.Wheel, s.Files.Select(f => new FileItem(f)).ToList(), settings, Source, (_, _) => { });
            window.RenderPreview(Path.Combine(outDir, s.Name + ".png"), s.Highlight);
            window.Close();
            log.Add($"ok {Path.GetFileName(outDir)}/{s.Name}");
        }

        SettingsWindow.Operations = () => [.. core, .. actions];
        var settingsWindow = new SettingsWindow(settings, tools, () => { });
        SaveWindow(settingsWindow, Path.Combine(outDir, "settings.png"), 560);
        log.Add($"ok {Path.GetFileName(outDir)}/settings");

        var jobs = new JobsWindow();
        var running = jobs.Add(N("Převod na MP4", "Convert to MP4"), N("klip.mov", "clip.mov"), () => { }, @"C:\x.mov");
        running.Report(0.42);
        jobs.Add(N("Komprimovat", "Compress"), N("dovolená.jpg", "vacation.jpg"), () => { }, @"C:\x.jpg").Complete([N(@"C:\Fotky\dovolená (komprimováno).jpg", @"C:\Photos\vacation (compressed).jpg")], null);
        jobs.Add(N("Převod na DOCX", "Convert to DOCX"), N("smlouva.pdf", "contract.pdf"), () => { }, @"C:\x.pdf").Fail(N("Chybí nástroj Pandoc. Nainstalujte ho příkazem: ", "Pandoc is missing. Install it with: ") + "winget install JohnMacFarlane.Pandoc");
        SaveWindow(jobs, Path.Combine(outDir, "jobs.png"), 380, desktop: true);
        jobs.Close();
        log.Add($"ok {Path.GetFileName(outDir)}/jobs");

        settings.FavoriteOperations = ["image.to.webp", "image.compress"];
        settings.HiddenOperations = ["image.to.tiff"];
        var editor = new WheelEditorWindow(settings, [.. core, .. actions], () => { });
        SaveWindow(editor, Path.Combine(outDir, "wheel-editor.png"), 640);
        log.Add($"ok {Path.GetFileName(outDir)}/wheel-editor");

        // Customised wheel and the "repeat last" view.
        var photo = new List<FileItem> { new(N(@"C:\Fotky\dovolená.jpg", @"C:\Photos\vacation.jpg")) };
        IReadOnlyList<Operation> Arranged(WheelKind wheel, IReadOnlyList<FileItem> items) =>
            WheelLayout.Arrange(Source(wheel, items), settings.HiddenOperations, settings.FavoriteOperations);
        var custom = new WheelWindow(WheelMode.Drag, WheelKind.Convert, photo, settings, Arranged, (_, _) => { });
        Check(custom.CurrentWheel == WheelKind.Convert, "custom wheel");
        custom.RenderPreview(Path.Combine(outDir, "wheel-customized.png"), 0);
        custom.Close();
        var webp = core.Single(o => o.Id == "image.to.webp");
        var repeat = new WheelWindow(WheelMode.Drag, WheelKind.Convert, photo, settings, Arranged, (_, _) => { }) { LastOperation = _ => webp };
        repeat.ToggleRepeat();
        repeat.RenderPreview(Path.Combine(outDir, "wheel-repeat.png"), 0);
        repeat.Close();
        log.Add($"ok {Path.GetFileName(outDir)}/wheel-repeat");

        var trim = core.Single(o => o.Id == "video.trim");
        var dialog = new ParameterDialog(trim, [new FileItem(N(@"C:\Videa\klip.mov", @"C:\Videos\clip.mov"))]);
        SaveWindow(dialog, Path.Combine(outDir, "trim-dialog.png"), 420);
        log.Add($"ok {Path.GetFileName(outDir)}/parameter dialog");
    }

    /// <summary>Virtual files (as browsers and Outlook drag them) and raw images become real files.</summary>
    private static void TestDrops(List<string> log)
    {
        var descriptor = new byte[4 + 592];
        BitConverter.GetBytes(1).CopyTo(descriptor, 0);
        System.Text.Encoding.Unicode.GetBytes("selftest-virtual.txt").CopyTo(descriptor, 4 + 72);
        var data = new DataObject();
        data.SetData("FileGroupDescriptorW", new MemoryStream(descriptor));
        data.SetData("FileContents", new MemoryStream(System.Text.Encoding.UTF8.GetBytes("hello")));

        var preview = DropContent.Read(data, materialize: false) ?? throw new InvalidOperationException("virtual file not recognised");
        Check(preview.Items.Single().Format == "txt", "virtual file preview has the right type");
        var dropped = DropContent.Read(data, materialize: true) ?? throw new InvalidOperationException("virtual file not saved");
        var path = dropped.Items.Single().Path;
        Check(File.ReadAllText(path) == "hello", "virtual file contents");
        File.Delete(path);
        log.Add("ok drop/virtual-file");

        var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 4 * 4], 16);
        var image = new DataObject(DataFormats.Bitmap, bitmap);
        var imageDrop = DropContent.Read(image, materialize: true) ?? throw new InvalidOperationException("image not saved");
        var png = imageDrop.Items.Single().Path;
        Check(File.Exists(png) && new FileInfo(png).Length > 0, "dropped image saved as PNG");
        File.Delete(png);
        log.Add("ok drop/image");

        var link = new DataObject(DataFormats.UnicodeText, "https://example.com/photos/cat.webp");
        var linkDrop = DropContent.Read(link, materialize: false) ?? throw new InvalidOperationException("link not recognised");
        Check(linkDrop.Items.Single().Format == "webp", "link keeps its file type");
        log.Add("ok drop/link");
    }

    /// <summary>Renders a line of text and reads it back with Windows OCR, when the runner has it.</summary>
    private static void TestOcr(string outDir, List<string> log)
    {
        if (!Ocr.OcrOperation.Available)
        {
            log.Add("skipped ocr (no recognizer languages on this machine)");
            return;
        }
        var text = new TextBlock
        {
            Text = "FileAction OCR 12345",
            FontSize = 48,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = Brushes.Black,
            Background = Brushes.White,
            Padding = new Thickness(30),
        };
        var png = Path.Combine(outDir, "ocr-input.png");
        SaveElement(text, png);
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
            ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages[0]);
        var result = Task.Run(() => Ocr.OcrOperation.RecognizeAsync(engine, png)).GetAwaiter().GetResult();
        Check(result.Contains("12345"), $"OCR read \"{result}\"");
        log.Add($"ok ocr: {result}");
    }

    /// <summary>Asks the real GitHub API, as the app does; logged only (the network may be unavailable).</summary>
    private static void TestUpdateCheck(List<string> log)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FileAction-selftest");
            var json = http.GetStringAsync(UpdateInfo.LatestReleaseApi).GetAwaiter().GetResult();
            var latest = UpdateInfo.Parse(json);
            log.Add(latest is null
                ? "update check: no usable release"
                : $"update check: latest {latest.Tag}, installer {(latest.InstallerUrl is null ? "missing" : "found")}, sha256 {(latest.InstallerSha256 is null ? "missing" : "present")}");
        }
        catch (Exception ex)
        {
            log.Add("update check skipped: " + ex.Message);
        }
    }

    private static void Check(bool condition, string what)
    {
        if (!condition)
            throw new InvalidOperationException("Self-test check failed: " + what);
    }

    private static void SaveWindow(Window window, string file, double width, bool desktop = false)
    {
        var content = (UIElement)window.Content;
        window.Content = null;
        var root = new Border
        {
            Width = width,
            Background = desktop ? Desktop() : window.Background ?? Brushes.White,
            Child = content,
        };
        if (content is ScrollViewer scroll)
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SaveElement(root, file);
    }

    /// <summary>A wallpaper-like backdrop for floating surfaces in previews.</summary>
    public static Brush Desktop() => Theme.IsDark
        ? new LinearGradientBrush(Color.FromRgb(0x1F, 0x2A, 0x44), Color.FromRgb(0x3A, 0x2A, 0x4A), 45)
        : new LinearGradientBrush(Color.FromRgb(0xC9, 0xD8, 0xEE), Color.FromRgb(0xE9, 0xD9, 0xEE), 45);

    public static void SaveElement(FrameworkElement element, string file)
    {
        element.Measure(new Size(element.Width > 0 ? element.Width : 800, double.PositiveInfinity));
        var size = element.DesiredSize;
        element.Arrange(new Rect(size));
        element.UpdateLayout();
        const double scale = 1.5;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }
}
