namespace FileAction.Core.Operations;

/// <summary>Processes each dropped file independently.</summary>
public sealed class PerFileOperation : Operation
{
    public delegate Task<IEnumerable<string>> FileHandler(FileItem item, OperationContext ctx, IProgress<double> progress);

    private readonly HashSet<string> _inputs;
    private readonly string? _targetFormat;
    private readonly FileHandler _handler;

    /// <param name="targetFormat">When set, the option is hidden for files that already have this format.</param>
    public PerFileOperation(
        string id,
        WheelKind wheel,
        string label,
        string glyph,
        IEnumerable<string> inputs,
        FileHandler handler,
        string? targetFormat = null,
        string? subtitle = null)
        : base(id, wheel, label, glyph, subtitle)
    {
        _inputs = new HashSet<string>(inputs, StringComparer.OrdinalIgnoreCase);
        _targetFormat = targetFormat;
        _handler = handler;
    }

    /// <summary>Also accept folders (e.g. "pack to ZIP").</summary>
    public bool AcceptsDirectories { get; init; }

    /// <summary>Accept any file regardless of its format.</summary>
    public bool AcceptsAnyFile { get; init; }

    /// <summary>Per-file tool check for operations whose backend depends on the input format.</summary>
    public Func<ToolLocator, FileItem, bool>? AvailableFor { get; init; }

    public override bool IsAvailable(ToolLocator tools, IReadOnlyList<FileItem> items) =>
        base.IsAvailable(tools, items) && (AvailableFor is null || items.All(i => AvailableFor(tools, i)));

    public override bool AppliesTo(IReadOnlyList<FileItem> items)
    {
        if (items.Count == 0)
            return false;
        var anyNeedsWork = false;
        foreach (var item in items)
        {
            if (item.IsDirectory)
            {
                if (!AcceptsDirectories)
                    return false;
                anyNeedsWork = true;
                continue;
            }
            var format = item.Format;
            if (!AcceptsAnyFile && !_inputs.Contains(format))
                return false;
            if (_targetFormat is null || !string.Equals(format, _targetFormat, StringComparison.OrdinalIgnoreCase))
                anyNeedsWork = true;
        }
        return anyNeedsWork;
    }

    public override async Task<OperationResult> ExecuteAsync(IReadOnlyList<FileItem> items, OperationContext ctx)
    {
        var work = items
            .Where(i => _targetFormat is null || i.IsDirectory || !string.Equals(i.Format, _targetFormat, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var outputs = new List<string>();
        var errors = new List<string>();
        for (var i = 0; i < work.Count; i++)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            var progress = ctx.SubProgress(i, work.Count);
            try
            {
                outputs.AddRange(await _handler(work[i], ctx, progress).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && work.Count > 1)
            {
                // Keep going with the rest of a batch; report failures at the end.
                errors.Add($"{work[i].Name}: {ex.Message}");
            }
            progress.Report(1);
        }
        if (errors.Count > 0 && outputs.Count == 0)
            throw new ConversionException(string.Join(Environment.NewLine, errors));
        return new OperationResult(outputs, errors.Count > 0 ? string.Join(Environment.NewLine, errors) : null);
    }
}

/// <summary>Processes all dropped files together (merge, pack, …).</summary>
public sealed class BatchOperation : Operation
{
    public delegate Task<IEnumerable<string>> BatchHandler(IReadOnlyList<FileItem> items, OperationContext ctx);

    private readonly Func<IReadOnlyList<FileItem>, bool> _applies;
    private readonly BatchHandler _handler;

    public BatchOperation(
        string id,
        WheelKind wheel,
        string label,
        string glyph,
        Func<IReadOnlyList<FileItem>, bool> applies,
        BatchHandler handler,
        string? subtitle = null)
        : base(id, wheel, label, glyph, subtitle)
    {
        _applies = applies;
        _handler = handler;
    }

    public override bool AppliesTo(IReadOnlyList<FileItem> items) => items.Count > 0 && _applies(items);

    public override async Task<OperationResult> ExecuteAsync(IReadOnlyList<FileItem> items, OperationContext ctx) =>
        OperationResult.Of(await _handler(items, ctx).ConfigureAwait(false));

    /// <summary>True when there are at least <paramref name="min"/> files and all match <paramref name="formats"/>.</summary>
    public static Func<IReadOnlyList<FileItem>, bool> AllOf(ISet<string> formats, int min = 1) =>
        items => items.Count >= min && items.All(i => !i.IsDirectory && formats.Contains(i.Format));
}
