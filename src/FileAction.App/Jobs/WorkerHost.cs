using System.Globalization;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FileAction.Core;
using FileAction.Core.Operations;

namespace FileAction.App.Jobs;

/// <summary>One disposable native process per conversion: a codec crash cannot take down the tray app.</summary>
internal static class WorkerHost
{
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private static readonly CancellationTokenSource Shutdown = new();
    private static readonly ConcurrentDictionary<string, string[]> ActiveDirectories = new(StringComparer.OrdinalIgnoreCase);
    private static int _activeWorkers;
    internal static int ActiveWorkerCount => Volatile.Read(ref _activeWorkers);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string ProgressPrefix = "FILEACTION_PROGRESS:";

    public static void CancelAll()
    {
        Shutdown.Cancel();
        // Issue the OS termination requests before OnExit returns and the tray process disappears.
        ProcessRunner.StopAll();
        foreach (var (directory, sources) in ActiveDirectories)
        {
            CleanOwnedTemporaryPaths(directory, sources);
            OutputPaths.TryDelete(directory);
        }
    }

    // Called before WPF, shell integration and the single-instance lock are initialized.
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || args[0] != "--worker")
            return false;
        if (args.Length != 3)
        {
            exitCode = 2;
            return true;
        }
        try
        {
            RunAsync(args[1], args[2]).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            exitCode = 1;
        }
        return true;
    }

    public static async Task<OperationResult> ExecuteAsync(Operation operation, IReadOnlyList<FileItem> items, OperationContext context)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, Shutdown.Token);
        var token = cancellation.Token;
        await Slots.WaitAsync(token).ConfigureAwait(false);
        Interlocked.Increment(ref _activeWorkers);
        string? directory = null;
        try
        {
            token.ThrowIfCancellationRequested();
            directory = Path.Combine(Path.GetTempPath(), "Peel", "workers", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var sourcePaths = items.Select(i => Path.GetFullPath(i.Path)).ToArray();
            ActiveDirectories[directory] = sourcePaths;
            var requestPath = Path.Combine(directory, "request.json");
            var responsePath = Path.Combine(directory, "response.json");
            var request = new WorkerRequest(operation.Id, sourcePaths,
                context.OutputDirectory, new Dictionary<string, string>(context.Parameters), context.Settings, Loc.Current);
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, Json), token).ConfigureAwait(false);

            var executable = Environment.ProcessPath ?? throw new ConversionException("Cannot locate the conversion worker executable.");
            var arguments = new List<string>();
            // Supports development via `dotnet FileAction.App.dll` as well as a published .exe.
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
                arguments.Add(Assembly.GetEntryAssembly()?.Location ?? throw new ConversionException("Cannot locate the app assembly."));
            arguments.AddRange(["--worker", requestPath, responsePath]);
            await ProcessRunner.RunAsync(executable, arguments, token, line =>
            {
                if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal)
                    && double.TryParse(line[ProgressPrefix.Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    context.Progress.Report(Math.Clamp(value, 0, 1));
            }).ConfigureAwait(false);

            if (!File.Exists(responsePath))
                throw new ConversionException("The conversion worker stopped without returning a result.");
            var response = JsonSerializer.Deserialize<WorkerResponse>(
                await File.ReadAllTextAsync(responsePath, token).ConfigureAwait(false), Json)
                ?? throw new ConversionException("The conversion worker returned an empty response.");
            if (response.Result is not null)
                return response.Result;
            throw new ConversionException(response.Error ?? "The conversion worker failed.");
        }
        finally
        {
            try
            {
                if (directory is not null)
                {
                    CleanOwnedTemporaryPaths(directory, items.Select(i => Path.GetFullPath(i.Path)));
                    OutputPaths.TryDelete(directory);
                    ActiveDirectories.TryRemove(directory, out _);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeWorkers);
                Slots.Release();
            }
        }
    }

    private static async Task RunAsync(string requestPath, string responsePath)
    {
        var manifest = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(requestPath))!, "temporary-paths.jsonl");
        var manifestLock = new object();
        void RecordTemporaryPath(string path, bool isDirectory)
        {
            lock (manifestLock)
                File.AppendAllText(manifest, JsonSerializer.Serialize(new OwnedTemporaryPath(path, isDirectory), Json) + Environment.NewLine);
        }
        OutputPaths.TemporaryPathCreated += RecordTemporaryPath;
        WorkerResponse response;
        try
        {
            var request = JsonSerializer.Deserialize<WorkerRequest>(await File.ReadAllTextAsync(requestPath).ConfigureAwait(false), Json)
                ?? throw new ConversionException("The worker request is empty.");
            if (request.Paths is not { Length: > 0 } || string.IsNullOrWhiteSpace(request.OperationId))
                throw new ConversionException("The worker request has no operation or files.");
            Loc.Current = request.Language;
            var items = request.Paths.Select(path => new FileItem(Path.GetFullPath(path))).ToArray();
            if (items.Any(item => !File.Exists(item.Path) && !Directory.Exists(item.Path)))
                throw new ConversionException("A source file no longer exists.");
            var operation = Catalog.Build().SingleOrDefault(op => op.Id == request.OperationId)
                ?? throw new ConversionException("Unknown conversion operation: " + request.OperationId);
            var tools = new ToolLocator();
            if (!operation.AppliesTo(items) || !operation.IsAvailable(tools, items))
                throw new ConversionException("This operation is not available for the selected files or its required tool is missing.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var context = new OperationContext
            {
                Tools = tools,
                OutputDirectory = request.OutputDir,
                Parameters = request.Parameters ?? new Dictionary<string, string>(),
                Settings = request.Settings ?? new Settings(),
                CancellationToken = timeout.Token,
                Progress = new WorkerProgress(),
            };
            response = new WorkerResponse(await operation.ExecuteAsync(items, context).ConfigureAwait(false), null);
        }
        catch (Exception ex)
        {
            response = new WorkerResponse(null, ex is OperationCanceledException
                ? "The conversion exceeded its 30-minute time limit."
                : ex.Message);
        }
        finally
        {
            OutputPaths.TemporaryPathCreated -= RecordTemporaryPath;
        }
        // Atomic response publication prevents the parent from reading a partially written result.
        var temp = responsePath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(response, Json)).ConfigureAwait(false);
        File.Move(temp, responsePath, overwrite: false);
    }

    internal static void CleanOwnedTemporaryPaths(string protocolDirectory, IEnumerable<string> sourcePaths)
    {
        var sources = sourcePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var manifest = Path.Combine(protocolDirectory, "temporary-paths.jsonl");
        try
        {
            if (!File.Exists(manifest))
                return;
            var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Peel"));
            foreach (var line in File.ReadLines(manifest))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<OwnedTemporaryPath>(line, Json);
                    if (entry is null || !Path.IsPathFullyQualified(entry.Path))
                        continue;
                    var full = Path.GetFullPath(entry.Path);
                    if (sources.Contains(full))
                        continue;
                    if (entry.IsDirectory)
                    {
                        // Only generated GUID folders directly inside our own temporary root.
                        var isStandardTemp = string.Equals(Path.GetDirectoryName(full), tempRoot, StringComparison.OrdinalIgnoreCase)
                            && Guid.TryParseExact(Path.GetFileName(full), "N", out _);
                        var isOutputTemp = Regex.IsMatch(Path.GetFileName(full), @"^\..+\.[0-9a-f]{8}\.part$");
                        if (!isStandardTemp && !isOutputTemp)
                            continue;
                        if (sources.Any(source => source.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                            continue;
                    }
                    else
                    {
                        if (Directory.Exists(full) || !Regex.IsMatch(Path.GetFileName(full), @"^\..+\.[0-9a-f]{8}\.part\.[^\\/:*?""<>|]+$"))
                            continue;
                    }
                    OutputPaths.TryDelete(full);
                }
                catch (JsonException) { } // A killed worker may leave its final manifest line incomplete.
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class WorkerProgress : IProgress<double>
    {
        private long _lastWrite;
        public void Report(double value)
        {
            var now = Environment.TickCount64;
            if (value < 1 && now - Interlocked.Read(ref _lastWrite) < 100)
                return;
            Interlocked.Exchange(ref _lastWrite, now);
            Console.Error.WriteLine(ProgressPrefix + Math.Clamp(value, 0, 1).ToString("R", CultureInfo.InvariantCulture));
        }
    }

    private sealed record WorkerRequest(string OperationId, string[] Paths, string? OutputDir,
        Dictionary<string, string>? Parameters, Settings? Settings, Language Language);
    private sealed record WorkerResponse(OperationResult? Result, string? Error);
    private sealed record OwnedTemporaryPath(string Path, bool IsDirectory);
}
