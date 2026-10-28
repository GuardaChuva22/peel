using System.Globalization;
using System.Text.Json;

namespace FileAction.Core.Backends;

public static class AdvancedMediaBackend
{
    private static string F(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
    private static double N(OperationContext ctx, string name, double fallback, double min, double max) => AdvancedImageBackend.Number(ctx, name, fallback, min, max);
    private static string[] Encode(OperationContext ctx) => ["-c:v", "libx264", "-preset", "medium", "-crf", ctx.Settings.VideoCrf.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p", "-threads", "4", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart"];

    public static async Task<OperationResult> ExecuteAsync(string action, IReadOnlyList<FileItem> items, OperationContext ctx)
    {
        if (action == "video.join") return await JoinAsync(items, ctx);
        var outputs = new List<string>();
        foreach (var item in items)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            var video = Formats.Video.Contains(item.Format);
            var format = video ? "mp4" : FfmpegBackend.SafeContainer(item.Format);
            var args = new List<string>();
            IEnumerable<string>? before = null;
            switch (action)
            {
                case "video.crop":
                    var info = await ProbeAsync(item.Path, ctx);
                    var x = (int)N(ctx, "x", 0, 0, info.Width - 1);
                    var y = (int)N(ctx, "y", 0, 0, info.Height - 1);
                    var width = (int)N(ctx, "width", 0, 0, info.Width);
                    var height = (int)N(ctx, "height", 0, 0, info.Height);
                    width = width == 0 ? info.Width - x : width;
                    height = height == 0 ? info.Height - y : height;
                    if (x + width > info.Width || y + height > info.Height || width < 2 || height < 2) throw new ConversionException("The crop must fit inside the video and be at least 2 × 2 pixels.");
                    args.AddRange(["-vf", $"crop={width}:{height}:{x}:{y},scale=trunc(iw/2)*2:trunc(ih/2)*2", .. Encode(ctx)]);
                    break;
                case "video.speed":
                    var factor = N(ctx, "factor", 1.5, 0.25, 4);
                    var tempo = new List<string>();
                    var remainder = factor;
                    while (remainder > 2) { tempo.Add("atempo=2"); remainder /= 2; }
                    while (remainder < 0.5) { tempo.Add("atempo=0.5"); remainder *= 2; }
                    tempo.Add("atempo=" + F(remainder));
                    args.AddRange(["-vf", "setpts=PTS/" + F(factor) + ",scale=trunc(iw/2)*2:trunc(ih/2)*2"]);
                    if ((await ProbeAsync(item.Path, ctx)).HasAudio) args.AddRange(["-af", string.Join(',', tempo)]);
                    args.AddRange(Encode(ctx));
                    break;
                case "video.snapshot":
                    format = "png";
                    before = ["-ss", F(FfmpegBackend.ParseTime(ctx.Param("time", "0")))];
                    args.AddRange(["-frames:v", "1", "-update", "1"]);
                    break;
                case "video.split":
                    outputs.AddRange(await SplitAsync(item, ctx));
                    continue;
                case "video.redact":
                    var probe = await ProbeAsync(item.Path, ctx);
                    var left = (int)N(ctx, "x", 0, 0, probe.Width - 1);
                    var top = (int)N(ctx, "y", 0, 0, probe.Height - 1);
                    var rw = (int)N(ctx, "width", 0, 0, probe.Width);
                    var rh = (int)N(ctx, "height", 0, 0, probe.Height);
                    rw = rw == 0 ? probe.Width - left : rw;
                    rh = rh == 0 ? probe.Height - top : rh;
                    if (left + rw > probe.Width || top + rh > probe.Height) throw new ConversionException("The redaction rectangle extends beyond the video.");
                    var start = FfmpegBackend.ParseTime(ctx.Param("start", "0"));
                    var end = ctx.Param("end").Length == 0 ? probe.Duration : FfmpegBackend.ParseTime(ctx.Param("end"));
                    if (end <= start) throw new ConversionException("The end must come after the start.");
                    args.AddRange(["-vf", $"drawbox=x={left}:y={top}:w={rw}:h={rh}:color=black:t=fill:enable='between(t,{F(start)},{F(end)})',scale=trunc(iw/2)*2:trunc(ih/2)*2", .. Encode(ctx)]);
                    break;
                case "audio.visualize":
                    format = "mp4";
                    var vw = Even((int)N(ctx, "width", 1920, 64, 3840));
                    var vh = Even((int)N(ctx, "height", 1080, 64, 3840));
                    var color = ctx.Param("color", "#FF8A1F");
                    if (!System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$")) throw new ConversionException("Enter a waveform color such as #FF8A1F.");
                    args.AddRange(["-filter_complex", $"[0:a]showwaves=s={vw}x{vh}:mode=line:rate=30:colors=0x{color[1..]},format=yuv420p[v]", "-map", "[v]", "-map", "0:a:0", .. Encode(ctx), "-shortest"]);
                    break;
                case "audio.channels":
                    var channels = (int)N(ctx, "channels", 2, 1, 2);
                    args.AddRange(["-ac", channels.ToString(CultureInfo.InvariantCulture), .. FfmpegBackend.CodecArgs(format, ctx.Settings)]);
                    break;
                case "audio.bleep":
                    var from = FfmpegBackend.ParseTime(ctx.Param("start", "0"));
                    var until = FfmpegBackend.ParseTime(ctx.Param("end", "1"));
                    var duration = (await ProbeAsync(item.Path, ctx)).Duration;
                    if (until <= from || until > duration + 0.05) throw new ConversionException("Choose a bleep range within the recording.");
                    var hz = N(ctx, "frequency", 1000, 100, 5000);
                    var graph = $"[0:a]volume=0:enable='between(t,{F(from)},{F(until)})'[a];sine=frequency={F(hz)}:sample_rate=48000:duration={F(until-from)},volume=0.2,adelay={F(from*1000)}:all=1[b];[a][b]amix=inputs=2:duration=first:normalize=0[out]";
                    args.AddRange(["-filter_complex", graph, "-map", "[out]", .. FfmpegBackend.CodecArgs(format, ctx.Settings)]);
                    break;
                case "audio.silence":
                    var threshold = N(ctx, "threshold", -45, -90, -5);
                    args.AddRange(["-af", $"silenceremove=start_periods=1:start_duration=0.1:start_threshold={F(threshold)}dB,areverse,silenceremove=start_periods=1:start_duration=0.1:start_threshold={F(threshold)}dB,areverse", .. FfmpegBackend.CodecArgs(format, ctx.Settings)]);
                    break;
                case "audio.metadata":
                case "video.metadata":
                case "audio.strip":
                case "video.strip":
                    format = item.Format;
                    args.AddRange(["-map", "0", "-c", "copy"]);
                    if (action.EndsWith(".strip", StringComparison.Ordinal)) args.AddRange(["-map_metadata", "-1"]);
                    else foreach (var key in new[] { "title", "artist", "album", "comment" }) args.AddRange(["-metadata", key + "=" + ctx.Param(key)]);
                    break;
                default: throw new ConversionException("Unknown media tool.");
            }
            var result = await OutputPaths.ProduceAsync(ctx.OutputDirFor(item.Path), item.BaseName + " (" + action.Split('.')[1] + ")", format,
                output => FfmpegBackend.RunAsync(ctx, [item.Path], ["-filter_complex_threads", "2", .. args], output, ctx.Progress, before));
            outputs.Add(result);
        }
        return OperationResult.Of(outputs);
    }

    private static int Even(int value) => Math.Max(2, value / 2 * 2);
    private static async Task<OperationResult> JoinAsync(IReadOnlyList<FileItem> items, OperationContext ctx)
    {
        var width = Even((int)N(ctx, "width", 1920, 64, 3840));
        var height = Even((int)N(ctx, "height", 1080, 64, 3840));
        var graph = new List<string>();
        var streams = new List<string>();
        for (var index = 0; index < items.Count; index++)
        {
            var info = await ProbeAsync(items[index].Path, ctx);
            graph.Add($"[{index}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p,setpts=PTS-STARTPTS[v{index}]");
            graph.Add(info.HasAudio
                ? $"[{index}:a:0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,apad,atrim=duration={F(info.Duration)},asetpts=PTS-STARTPTS[a{index}]"
                : $"anullsrc=r=48000:cl=stereo:d={F(info.Duration)}[a{index}]");
            streams.Add($"[v{index}][a{index}]");
        }
        graph.Add(string.Concat(streams) + $"concat=n={items.Count}:v=1:a=1[v][a]");
        var output = await OutputPaths.ProduceAsync(ctx.OutputDirFor(items[0].Path), items[0].BaseName + " (joined)", "mp4", path =>
            FfmpegBackend.RunAsync(ctx, items.Select(i => i.Path).ToArray(), ["-filter_complex_threads", "2", "-filter_complex", string.Join(';', graph), "-map", "[v]", "-map", "[a]", .. Encode(ctx)], path, ctx.Progress));
        return OperationResult.Of([output]);
    }

    private static async Task<IEnumerable<string>> SplitAsync(FileItem item, OperationContext ctx)
    {
        var duration = (await ProbeAsync(item.Path, ctx)).Duration;
        var points = ctx.Param("points").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(FfmpegBackend.ParseTime).ToArray();
        if (points.Length == 0 || points.Length > 100 || points.Any(p => p <= 0 || p >= duration) || !points.SequenceEqual(points.Distinct().Order()))
            throw new ConversionException("Enter increasing split times within the video, such as 0:30,1:00 (maximum 100 points).");
        var cuts = new[] { 0.0 }.Concat(points).Append(duration).ToArray();
        var directory = await OutputPaths.ProduceDirectoryAsync(ctx.OutputDirFor(item.Path), item.BaseName + " (clips)", async temp =>
        {
            for (var index = 0; index < cuts.Length - 1; index++)
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();
                var output = Path.Combine(temp, $"{item.BaseName} - {index + 1:000}.mp4");
                await FfmpegBackend.RunAsync(ctx, [item.Path], ["-t", F(cuts[index + 1] - cuts[index]), .. Encode(ctx)], output,
                    ctx.SubProgress(index, cuts.Length - 1), ["-ss", F(cuts[index])], cuts[index + 1] - cuts[index]);
            }
        });
        return [directory];
    }

    private sealed record Probe(double Duration, bool HasAudio, int Width, int Height);
    private static async Task<Probe> ProbeAsync(string input, OperationContext ctx)
    {
        var ffmpeg = ctx.Tools.Require(ExternalTool.Ffmpeg);
        var executable = Path.Combine(Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (!File.Exists(executable)) throw new ConversionException("The media inspector ffprobe is missing next to ffmpeg.");
        var json = await ProcessRunner.RunAsync(executable, ["-v", "error", "-show_entries", "format=duration:stream=codec_type,width,height", "-of", "json", input], ctx.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video");
        var duration = doc.RootElement.GetProperty("format").GetProperty("duration").GetString();
        if (!double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0 || !double.IsFinite(seconds)) throw new ConversionException("The file has no readable duration.");
        return new Probe(seconds, streams.Any(s => s.GetProperty("codec_type").GetString() == "audio"), video.ValueKind == JsonValueKind.Undefined ? 0 : video.GetProperty("width").GetInt32(), video.ValueKind == JsonValueKind.Undefined ? 0 : video.GetProperty("height").GetInt32());
    }
}
