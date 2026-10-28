using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using FileAction.Core;
using FileAction.Core.Backends;
using ImageMagick;

namespace FileAction.Tests;

/// <summary>Checks the media itself, rather than merely accepting a nonempty output file.</summary>
[Trait("Category", "Integration")]
public sealed class AdvancedSemanticTests : IClassFixture<AdvancedSemanticTests.Fixtures>
{
    public sealed class Fixtures : IDisposable
    {
        public string Directory { get; } = OutputPaths.TempDirectory();
        public ToolLocator Tools { get; } = new();
        public string Audio => Path.Combine(Directory, "stereo.wav");
        public string Video => Path.Combine(Directory, "with-audio.mp4");
        public string SilentVideo => Path.Combine(Directory, "silent.mp4");
        public string Ffmpeg { get; }
        public string Ffprobe { get; }

        public Fixtures()
        {
            Ffmpeg = Tools.Require(ExternalTool.Ffmpeg);
            Ffprobe = Path.Combine(Path.GetDirectoryName(Ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            Assert.True(File.Exists(Ffprobe), "Semantic tests require ffprobe beside ffmpeg.");
            Run("-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2", "-ac", "2", "-c:a", "pcm_s16le", Audio);
            Run("-f", "lavfi", "-i", "color=c=blue:size=160x96:rate=30:duration=2", "-i", Audio,
                "-c:v", "libx264", "-threads", "2", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", Video);
            Run("-f", "lavfi", "-i", "color=c=red:size=160x96:rate=30:duration=1", "-an",
                "-c:v", "libx264", "-threads", "2", "-pix_fmt", "yuv420p", SilentVideo);
        }

        private void Run(params string[] args) => ProcessRunner.RunAsync(Ffmpeg, ["-y", "-hide_banner", "-loglevel", "error", .. args],
            CancellationToken.None, timeout: TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

        public OperationContext Context(params (string Key, string Value)[] parameters) => new()
        {
            Tools = Tools,
            OutputDirectory = Path.Combine(Directory, "outputs"),
            Parameters = parameters.ToDictionary(pair => pair.Key, pair => pair.Value),
        };

        public async Task<JsonDocument> Probe(string path) => JsonDocument.Parse(await ProcessRunner.RunAsync(Ffprobe,
            ["-v", "error", "-show_entries", "format=duration:stream=codec_type,width,height,channels", "-of", "json", path], CancellationToken.None));

        public async Task<short[]> DecodeAudio(string path)
        {
            var raw = Path.Combine(Directory, Guid.NewGuid().ToString("N") + ".pcm");
            await ProcessRunner.RunAsync(Ffmpeg, ["-y", "-hide_banner", "-loglevel", "error", "-i", path,
                "-vn", "-ac", "1", "-ar", "48000", "-f", "s16le", raw], CancellationToken.None);
            var bytes = await File.ReadAllBytesAsync(raw);
            var samples = new short[bytes.Length / 2];
            for (var index = 0; index < samples.Length; index++)
                samples[index] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(index * 2, 2));
            File.Delete(raw);
            return samples;
        }

        public void Dispose() => OutputPaths.TryDelete(Directory);
    }

    private readonly Fixtures _fixture;
    public AdvancedSemanticTests(Fixtures fixture) => _fixture = fixture;

    [Fact]
    public async Task Background_from_JPEG_is_real_PNG_with_the_requested_border()
    {
        var source = Path.Combine(_fixture.Directory, "background-source.jpg");
        using (var image = new MagickImage(MagickColors.Red, 40, 30)) image.Write(source);
        var result = await AdvancedImageBackend.ExecuteAsync("image.background", [new FileItem(source)],
            _fixture.Context(("padding", "5"), ("color", "#00FF00")));
        var output = Assert.Single(result.Outputs);
        var bytes = await File.ReadAllBytesAsync(output);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes.Take(8));
        using var decoded = new MagickImage(output);
        Assert.Equal(50u, decoded.Width);
        Assert.Equal(40u, decoded.Height);
        using var pixels = decoded.GetPixels();
        var border = pixels.GetPixel(0, 0).ToColor()!;
        var center = pixels.GetPixel(25, 20).ToColor()!;
        Assert.Equal(Quantum.Max, border.G);
        Assert.Equal((ushort)0, border.R);
        Assert.True(center.R > Quantum.Max * 0.9 && center.G < Quantum.Max * 0.1);
    }

    [Fact]
    public async Task Speed_factor_two_halves_duration_and_retains_audio()
    {
        var result = await AdvancedMediaBackend.ExecuteAsync("video.speed", [new FileItem(_fixture.Video)], _fixture.Context(("factor", "2")));
        using var probe = await _fixture.Probe(Assert.Single(result.Outputs));
        Assert.InRange(Duration(probe), 0.85, 1.2);
        Assert.Contains(Streams(probe), stream => Type(stream) == "audio");
        Assert.Contains(Streams(probe), stream => Type(stream) == "video" && stream.GetProperty("width").GetInt32() == 160);
    }

    [Fact]
    public async Task Channel_conversion_produces_a_mono_audio_stream()
    {
        using (var sourceProbe = await _fixture.Probe(_fixture.Audio))
            Assert.Equal(2, Streams(sourceProbe).Single(stream => Type(stream) == "audio").GetProperty("channels").GetInt32());
        var result = await AdvancedMediaBackend.ExecuteAsync("audio.channels", [new FileItem(_fixture.Audio)], _fixture.Context(("channels", "1")));
        using var probe = await _fixture.Probe(Assert.Single(result.Outputs));
        Assert.Equal(1, Streams(probe).Single(stream => Type(stream) == "audio").GetProperty("channels").GetInt32());
        Assert.InRange(Duration(probe), 1.95, 2.05);
    }

    [Fact]
    public async Task Bleep_replaces_the_selected_range_with_the_requested_tone()
    {
        var result = await AdvancedMediaBackend.ExecuteAsync("audio.bleep", [new FileItem(_fixture.Audio)],
            _fixture.Context(("start", "0.5"), ("end", "1.5"), ("frequency", "1000")));
        var samples = await _fixture.DecodeAudio(Assert.Single(result.Outputs));
        Assert.InRange(samples.Length, 95_900, 96_100);
        var originalOutside = ToneAmplitude(samples, 0.1, 0.3, 440);
        var bleepOutside = ToneAmplitude(samples, 0.1, 0.3, 1000);
        var originalInside = ToneAmplitude(samples, 0.8, 1.2, 440);
        var bleepInside = ToneAmplitude(samples, 0.8, 1.2, 1000);
        Assert.True(originalOutside > bleepOutside * 10 && originalOutside > 0.04, "Outside the range, the original 440 Hz signal must remain.");
        Assert.True(bleepInside > originalInside * 10 && bleepInside > 0.005, "Inside the range, the 1000 Hz bleep must replace the original tone.");
    }

    [Fact]
    public async Task Join_inserts_silence_for_a_clip_without_audio_and_keeps_the_full_timeline()
    {
        var result = await AdvancedMediaBackend.ExecuteAsync("video.join",
            [new FileItem(_fixture.SilentVideo), new FileItem(_fixture.Video)], _fixture.Context(("width", "160"), ("height", "96")));
        var output = Assert.Single(result.Outputs);
        using var probe = await _fixture.Probe(output);
        Assert.InRange(Duration(probe), 2.9, 3.2);
        Assert.Contains(Streams(probe), stream => Type(stream) == "audio");
        Assert.Contains(Streams(probe), stream => Type(stream) == "video");
        var samples = await _fixture.DecodeAudio(output);
        Assert.True(Rms(samples, 0.2, 0.7) < 0.001, "The silent first clip must receive silence.");
        Assert.True(Rms(samples, 1.3, 1.8) > 0.02, "Audio from the second clip must start after the silent clip.");
    }

    [Fact]
    public async Task Waveform_visualizer_produces_both_a_video_and_the_source_audio()
    {
        var result = await AdvancedMediaBackend.ExecuteAsync("audio.visualize", [new FileItem(_fixture.Audio)],
            _fixture.Context(("width", "320"), ("height", "180"), ("color", "#FF8800")));
        var output = Assert.Single(result.Outputs);
        using var probe = await _fixture.Probe(output);
        Assert.InRange(Duration(probe), 1.9, 2.2);
        Assert.Contains(Streams(probe), stream => Type(stream) == "audio");
        var video = Streams(probe).Single(stream => Type(stream) == "video");
        Assert.Equal(320, video.GetProperty("width").GetInt32());
        Assert.Equal(180, video.GetProperty("height").GetInt32());
        var samples = await _fixture.DecodeAudio(output);
        Assert.True(ToneAmplitude(samples, 0.2, 0.7, 440) > 0.03, "The visualization must retain the audible source signal.");
    }

    private static JsonElement[] Streams(JsonDocument document) => document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
    private static string? Type(JsonElement stream) => stream.GetProperty("codec_type").GetString();
    private static double Duration(JsonDocument document) => double.Parse(document.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
    private static double Rms(short[] samples, double start, double end)
    {
        var first = (int)(start * 48000);
        var last = Math.Min(samples.Length, (int)(end * 48000));
        return Math.Sqrt(samples[first..last].Average(value => Math.Pow(value / 32768.0, 2)));
    }
    private static double ToneAmplitude(short[] samples, double start, double end, double frequency)
    {
        var first = (int)(start * 48000);
        var last = Math.Min(samples.Length, (int)(end * 48000));
        double real = 0, imaginary = 0;
        for (var index = first; index < last; index++)
        {
            var phase = 2 * Math.PI * frequency * index / 48000;
            real += samples[index] / 32768.0 * Math.Cos(phase);
            imaginary += samples[index] / 32768.0 * Math.Sin(phase);
        }
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / (last - first);
    }
}
