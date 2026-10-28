using FileAction.Core.Backends;

namespace FileAction.Core;

/// <summary>Additional native tools for the local build. Parameters are shared with visual editors.</summary>
public static class AdvancedCatalog
{
    public static IReadOnlyList<Operation> Build()
    {
        var operations = new List<Operation>();
        void Add(string id, string label, string glyph, ISet<string> formats, ParameterSpec[] parameters, bool media = false, int min = 1)
            => operations.Add(new AdvancedOperation(id, label, glyph, formats, media, min)
                { Parameters = parameters, Requires = media ? [ExternalTool.Ffmpeg] : [] });
        ParameterSpec P(string key, string label, string value, string? hint = null) => new(key, label, value, hint);
        ParameterSpec[] Rect() => [P("x", "Left (px)", "0"), P("y", "Top (px)", "0"), P("width", "Width (px)", "0", "0 = remaining width"), P("height", "Height (px)", "0", "0 = remaining height")];
        Add("image.adjust", "Adjust photo", Glyphs.Color, Formats.Images,
            [P("brightness", "Brightness", "0", "−100 to 100"), P("contrast", "Contrast", "0", "−100 to 100"), P("saturation", "Saturation (%)", "100", "0 to 200")]);
        Add("image.crop", "Crop", Glyphs.Crop, Formats.Images, Rect());
        Add("image.annotate", "Annotate", Glyphs.Text, Formats.Images,
            [P("text", "Text", "Note"), P("x", "Left (px)", "8"), P("y", "Baseline (px)", "24"), P("size", "Text size (px)", "24"), P("color", "Text color", "#FFFFFF")]);
        Add("image.background", "Background", Glyphs.Photo, Formats.Images,
            [P("color", "Background color", "#FFFFFF"), P("padding", "Padding (px)", "48")]);
        Add("image.redact", "Redact", Glyphs.Clear, Formats.Images, [.. Rect(), P("color", "Mask color", "#000000")]);
        Add("image.collage", "Collage", Glyphs.Photo, Formats.Images,
            [P("columns", "Columns", "2"), P("cell", "Cell size (px)", "640"), P("gap", "Spacing (px)", "16"), P("color", "Background color", "#FFFFFF")], min: 2);
        Add("image.metadata", "Edit metadata", Glyphs.Text, Formats.Images,
            [P("title", "Title", ""), P("artist", "Artist", ""), P("comment", "Comment", "")]);
        Add("video.crop", "Crop", Glyphs.Crop, Formats.Video, Rect(), media: true);
        Add("video.speed", "Speed", Glyphs.Play, Formats.Video, [P("factor", "Playback speed", "1.5", "0.25 to 4; audio pitch is preserved")], media: true);
        Add("video.join", "Join videos", Glyphs.Merge, Formats.Video, [P("width", "Output width", "1920"), P("height", "Output height", "1080")], media: true, min: 2);
        Add("video.split", "Split video", Glyphs.Split, Formats.Video, [P("points", "Split times", "0:30,1:00", "Comma-separated times; e.g. 0:30,1:45")], media: true);
        Add("video.snapshot", "Snapshot", Glyphs.Camera, Formats.Video, [P("time", "Time", "0:00", "e.g. 0:30.5")], media: true);
        Add("video.redact", "Redact", Glyphs.Clear, Formats.Video, [.. Rect(), P("start", "Start time", "0"), P("end", "End time", "", "Empty = until the end")], media: true);
        Add("audio.visualize", "Visualizer", Glyphs.Equalizer, Formats.Audio,
            [P("width", "Video width", "1920"), P("height", "Video height", "1080"), P("color", "Waveform color", "#FF8A1F")], media: true);
        Add("audio.channels", "Channels", Glyphs.Audio, Formats.Audio, [P("channels", "Channels", "2", "1 = mono, 2 = stereo")], media: true);
        Add("audio.bleep", "Bleep", Glyphs.Mute, Formats.Audio, [P("start", "Start time", "0"), P("end", "End time", "1"), P("frequency", "Bleep frequency (Hz)", "1000")], media: true);
        Add("audio.silence", "Trim silence", Glyphs.Stopwatch, Formats.Audio, [P("threshold", "Silence threshold (dB)", "-45", "Only removes silence at the beginning and end")], media: true);
        foreach (var family in new[] { "audio", "video" })
        {
            var formats = family == "audio" ? Formats.Audio : Formats.Video;
            Add(family + ".metadata", "Edit metadata", Glyphs.Text, formats,
                [P("title", "Title", ""), P("artist", "Artist", ""), P("album", "Album", ""), P("comment", "Comment", "")], media: true);
            Add(family + ".strip", "No metadata", Glyphs.Clear, formats, [], media: true);
        }
        return operations;
    }

    private sealed class AdvancedOperation(string id, string label, string glyph, ISet<string> formats, bool media, int min)
        : Operation(id, WheelKind.Tools, label, glyph)
    {
        public override bool AppliesTo(IReadOnlyList<FileItem> items) => items.Count >= min && items.All(i => !i.IsDirectory && formats.Contains(i.Format));
        public override Task<OperationResult> ExecuteAsync(IReadOnlyList<FileItem> items, OperationContext ctx) =>
            media ? AdvancedMediaBackend.ExecuteAsync(Id, items, ctx) : AdvancedImageBackend.ExecuteAsync(Id, items, ctx);
    }
}
