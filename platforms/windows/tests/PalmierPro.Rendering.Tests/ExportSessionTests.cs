using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace PalmierPro.Rendering.Tests;

// E5 export orchestration (export-v1.md §4-§9) — the shipping PE_ExportStart/PE_ExportCancel path
// driven end to end through TimelineSession.ExportVideo against a real fixture timeline: GPU
// compose -> RGB->NV12/yuv422p10le readback -> encoder/mux, plus AudioMixer::RenderRange audio.
// ffprobe validates the muxed output; separate tests cover the cancellation postcondition (§8/§9:
// no output, no leaked temp, session reusable) and error propagation (§9: unwritable path).
//
// [Trait Media] like ColorScopesTests — the compose path uses the GPU compositor (WARP-capable, so
// it runs under `dotnet test --filter "Category!=GPU"`). PALMIERENGINE_FORCE_SW_ENCODE=1 forces the
// software encoder so codec_name is deterministic across CI runners (§6/§15).
[Collection(MediaFixturesCollection.Name)]
public sealed class ExportSessionTests(MediaFixtures fixtures)
{
    private const int Fps = 30;

    static ExportSessionTests()
    {
        // Deterministic codec_name regardless of GPU vendor (§6/§15) — read natively via
        // GetEnvironmentVariableA, so a plain process env var reaches the encoder probe.
        Environment.SetEnvironmentVariable("PALMIERENGINE_FORCE_SW_ENCODE", "1");
    }

    [Theory]
    [Trait("Category", "Media")]
    // codec, container, expectedCodec, expectedPixFmt, expectedEncoder, width, height
    [InlineData("h264", "mp4", "h264", "yuv420p", "libx264", 640, 360)]   // MatchTimeline
    [InlineData("h264", "mp4", "h264", "yuv420p", "libx264", 1280, 720)]  // scaled up
    [InlineData("h265", "mp4", "hevc", "yuv420p", "libx265", 640, 360)]
    [InlineData("prores", "mov", "prores", "yuv422p10le", "prores_ks", 640, 360)]
    [InlineData("prores", "mov", "prores", "yuv422p10le", "prores_ks", 480, 270)] // scaled down
    public void Export_VideoWithAudioTimeline_ProducesProbeValidFile(
        string codec, string container, string expectedCodec, string expectedPixFmt, string expectedEncoder,
        int width, int height)
    {
        const int durationFrames = 60; // 2.0 s at 30 fps
        using var scratch = new ScratchDir();
        string outputPath = scratch.File($"export-{codec}-{width}x{height}", container);

        string snapshot = BuildSnapshot(width, height, Fps, durationFrames,
            videoPath: fixtures.VideoWithAudioPath, audioPath: fixtures.AudioOnlyPath, speed: 1.0);

        using var session = new EngineSession();
        using TimelineSession timeline = TimelineSession.Open(session, Encoding.UTF8.GetBytes(snapshot));

        var reports = new List<(ExportVideoPhase Phase, double Fraction)>();
        ExportEncodeReport report = timeline.ExportVideo(
            BuildOptions(codec, container, width, height, Fps, outputPath),
            (phase, fraction) => reports.Add((phase, fraction)));

        report.FramesEncoded.ShouldBe(durationFrames);
        report.UsedHardwareEncoder.ShouldBeFalse("PALMIERENGINE_FORCE_SW_ENCODE must skip the hardware probe");
        report.EncoderName.ShouldBe(expectedEncoder);

        // Phase fires exactly once (Exporting); progress climbs monotonically to 1.0 (§4.3).
        reports.ShouldContain(r => r.Phase == ExportVideoPhase.Exporting);
        reports.Count(r => r.Fraction == 0.0).ShouldBe(1, "the single onPhase report carries fraction 0");
        reports[^1].Fraction.ShouldBe(1.0, 1e-9);

        File.Exists(outputPath).ShouldBeTrue();
        new FileInfo(outputPath).Length.ShouldBeGreaterThan(0);

        using JsonDocument probe = Probe(outputPath, "-show_format", "-show_streams");
        JsonElement root = probe.RootElement;

        JsonElement video = FindStream(root, "video");
        video.GetProperty("codec_name").GetString().ShouldBe(expectedCodec);
        video.GetProperty("pix_fmt").GetString().ShouldBe(expectedPixFmt);
        video.GetProperty("width").GetInt32().ShouldBe(width);
        video.GetProperty("height").GetInt32().ShouldBe(height);

        JsonElement audio = FindStream(root, "audio");
        audio.GetProperty("codec_name").GetString().ShouldBe("aac");
        audio.GetProperty("channels").GetInt32().ShouldBe(2);
        int.Parse(audio.GetProperty("sample_rate").GetString()!, CultureInfo.InvariantCulture).ShouldBe(48000);

        double duration = double.Parse(
            root.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        duration.ShouldBeInRange(1.8, 2.6); // 2.0 s ± AAC priming / container rounding
    }

    [Fact]
    [Trait("Category", "Media")]
    public void Export_RetimedClipWithRetimedAudio_StaysInSyncAndProbesValid()
    {
        // A 2x-speed video + audio clip: 30 output frames (1.0 s) consuming the full 2.0 s source,
        // audio pitch-preserved through RetimeStretcher (the SAME mix preview uses, §7/§15).
        const int durationFrames = 30; // 1.0 s at 30 fps
        using var scratch = new ScratchDir();
        string outputPath = scratch.File("export-retimed", "mp4");

        string snapshot = BuildSnapshot(640, 360, Fps, durationFrames,
            videoPath: fixtures.VideoWithAudioPath, audioPath: fixtures.AudioOnlyPath, speed: 2.0);

        using var session = new EngineSession();
        using TimelineSession timeline = TimelineSession.Open(session, Encoding.UTF8.GetBytes(snapshot));

        ExportEncodeReport report = timeline.ExportVideo(BuildOptions("h264", "mp4", 640, 360, Fps, outputPath));
        report.FramesEncoded.ShouldBe(durationFrames);

        using JsonDocument probe = Probe(outputPath, "-show_format", "-show_streams");
        JsonElement root = probe.RootElement;
        FindStream(root, "video").GetProperty("codec_name").GetString().ShouldBe("h264");
        FindStream(root, "audio").GetProperty("codec_name").GetString().ShouldBe("aac");

        // Both streams span ~1.0 s — the retimed audio lands at the retimed duration, not the 2.0 s
        // source length (a retime-that-didn't-retime-audio bug would leave audio ~2x too long).
        double duration = double.Parse(
            root.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        duration.ShouldBeInRange(0.85, 1.35);
    }

    [Fact]
    [Trait("Category", "Media")]
    public void Export_VideoOnlyTimeline_OmitsAudioStream()
    {
        const int durationFrames = 45;
        using var scratch = new ScratchDir();
        string outputPath = scratch.File("export-video-only", "mp4");

        string snapshot = BuildSnapshot(640, 360, Fps, durationFrames,
            videoPath: fixtures.RedClipPath, audioPath: null, speed: 1.0);

        using var session = new EngineSession();
        using TimelineSession timeline = TimelineSession.Open(session, Encoding.UTF8.GetBytes(snapshot));

        timeline.ExportVideo(BuildOptions("h264", "mp4", 640, 360, Fps, outputPath));

        using JsonDocument probe = Probe(outputPath, "-show_streams");
        JsonElement root = probe.RootElement;
        root.GetProperty("streams").EnumerateArray()
            .Count(s => s.GetProperty("codec_type").GetString() == "video").ShouldBe(1);
        root.GetProperty("streams").EnumerateArray()
            .Any(s => s.GetProperty("codec_type").GetString() == "audio")
            .ShouldBeFalse("a timeline with no audio track must produce a video-only file");
    }

    [Fact]
    [Trait("Category", "Media")]
    public async Task Export_CancelledMidLoop_LeavesNoOutputNoTempAndSessionReusable()
    {
        const int durationFrames = 60;
        using var scratch = new ScratchDir();
        string outputPath = scratch.File("export-cancelled", "mp4");

        string snapshot = BuildSnapshot(640, 360, Fps, durationFrames,
            videoPath: fixtures.VideoWithAudioPath, audioPath: fixtures.AudioOnlyPath, speed: 1.0);

        using var session = new EngineSession();

        double lastFraction;
        using (TimelineSession timeline = TimelineSession.Open(session, Encoding.UTF8.GetBytes(snapshot)))
        {
            using var cts = new CancellationTokenSource();
            var fractions = new List<double>();
            // Cancel once progress crosses ~30% (§8) — the report fires on the export thread, so
            // cancelling here trips PE_ExportCancel and the loop stops within one more frame.
            Action<ExportVideoPhase, double> onReport = (phase, fraction) =>
            {
                if (phase == ExportVideoPhase.Exporting)
                {
                    fractions.Add(fraction);
                    if (fraction >= 0.30 && !cts.IsCancellationRequested)
                    {
                        cts.Cancel();
                    }
                }
            };

            Task export = Task.Run(() =>
                timeline.ExportVideo(BuildOptions("h264", "mp4", 640, 360, Fps, outputPath), onReport, cts.Token));

            await Should.ThrowAsync<OperationCanceledException>(async () => await export);

            lastFraction = fractions.Count > 0 ? fractions[^1] : 0;
        }

        // §9 postcondition: no file at outputPath, and no ".partial"-suffixed temp sibling survives.
        lastFraction.ShouldBeLessThan(1.0, "cancellation must land before the export completes");
        File.Exists(outputPath).ShouldBeFalse("a cancelled export must leave no output file");
        Directory.EnumerateFiles(scratch.Path, "*.partial*").ShouldBeEmpty("no staging temp may leak");

        // The session is reusable after a cancel: a fresh export on a new timeline succeeds.
        string reuseOutput = scratch.File("export-after-cancel", "mp4");
        string reuseSnapshot = BuildSnapshot(640, 360, Fps, 20,
            videoPath: fixtures.RedClipPath, audioPath: null, speed: 1.0);
        using TimelineSession reuse = TimelineSession.Open(session, Encoding.UTF8.GetBytes(reuseSnapshot));
        ExportEncodeReport report = reuse.ExportVideo(BuildOptions("h264", "mp4", 640, 360, Fps, reuseOutput));
        report.FramesEncoded.ShouldBe(20);
        File.Exists(reuseOutput).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Media")]
    public void Export_UnwritableOutputPath_FailsWithMessageAndNoFile()
    {
        // A directory that does not exist — avio_open on the staging temp (same dir) fails, so the
        // export fails with a message and never creates anything (§9).
        string missingDir = Path.Combine(Path.GetTempPath(), $"palmier-export-nope-{Guid.NewGuid():N}");
        string outputPath = Path.Combine(missingDir, "out.mp4");
        Directory.Exists(missingDir).ShouldBeFalse();

        string snapshot = BuildSnapshot(640, 360, Fps, 30,
            videoPath: fixtures.RedClipPath, audioPath: null, speed: 1.0);

        using var session = new EngineSession();
        using TimelineSession timeline = TimelineSession.Open(session, Encoding.UTF8.GetBytes(snapshot));

        var ex = Should.Throw<EngineException>(() =>
            timeline.ExportVideo(BuildOptions("h264", "mp4", 640, 360, Fps, outputPath)));
        ex.Message.ShouldNotBeNullOrEmpty();
        File.Exists(outputPath).ShouldBeFalse();
        Directory.Exists(missingDir).ShouldBeFalse("a failed export must not create the output directory");
    }

    [Fact]
    [Trait("Category", "Media")]
    public void Export_MismatchedSnapshotSize_FailsInvalidArgument()
    {
        using var scratch = new ScratchDir();
        string outputPath = scratch.File("export-mismatch", "mp4");
        string snapshot = BuildSnapshot(640, 360, Fps, 30,
            videoPath: fixtures.RedClipPath, audioPath: null, speed: 1.0);

        using var session = new EngineSession();
        using TimelineSession timeline = TimelineSession.Open(session, Encoding.UTF8.GetBytes(snapshot));

        // Options claim 1280x720 but the open snapshot is 640x360 — §4.1 requires they match.
        Should.Throw<EngineException>(() =>
            timeline.ExportVideo(BuildOptions("h264", "mp4", 1280, 720, Fps, outputPath)));
        File.Exists(outputPath).ShouldBeFalse();
    }

    private static string BuildOptions(string codec, string container, int width, int height, int fps, string outputPath) =>
        "{" +
        $"\"codec\":\"{codec}\"," +
        $"\"container\":\"{container}\"," +
        $"\"width\":{width}," +
        $"\"height\":{height}," +
        $"\"fps\":{fps}," +
        $"\"outputPath\":\"{JsonEscape(outputPath)}\"" +
        "}";

    private static string BuildSnapshot(
        int outputWidth, int outputHeight, int fps, int durationFrames, string videoPath, string? audioPath, double speed)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append($"\"version\":1,\"fps\":{{\"numerator\":{fps},\"denominator\":1}},");
        sb.Append($"\"outputWidth\":{outputWidth},\"outputHeight\":{outputHeight},");
        sb.Append("\"tracks\":[");
        sb.Append(Track("TRACK-VIDEO", "video", muted: false,
            Clip("CLIP-VIDEO", "video", durationFrames, speed, videoPath)));
        if (audioPath is not null)
        {
            sb.Append(',');
            sb.Append(Track("TRACK-AUDIO", "audio", muted: false,
                Clip("CLIP-AUDIO", "audio", durationFrames, speed, audioPath)));
        }
        sb.Append("]}");
        return sb.ToString();

        static string Track(string id, string type, bool muted, string clip) =>
            $"{{\"id\":\"{id}\",\"type\":\"{type}\",\"muted\":{(muted ? "true" : "false")},\"clips\":[{clip}]}}";

        static string Clip(string id, string type, int durationFrames, double speed, string mediaPath) =>
            "{" +
            $"\"id\":\"{id}\",\"type\":\"{type}\",\"startFrame\":0,\"durationFrames\":{durationFrames}," +
            $"\"trimStartFrame\":0,\"speed\":{speed.ToString(CultureInfo.InvariantCulture)}," +
            $"\"mediaPath\":\"{JsonEscape(mediaPath)}\",\"hasAlphaHint\":null,\"blendMode\":null," +
            "\"opacity\":{\"value\":1.0,\"keyframes\":null}," +
            "\"transform\":{\"value\":{\"centerX\":0.5,\"centerY\":0.5,\"width\":1.0,\"height\":1.0," +
            "\"rotation\":0.0,\"flipHorizontal\":false,\"flipVertical\":false},\"keyframes\":null}," +
            "\"crop\":{\"value\":{\"left\":0.0,\"top\":0.0,\"right\":0.0,\"bottom\":0.0},\"keyframes\":null}," +
            "\"volume\":{\"gain\":1.0,\"fadeInFrames\":0,\"fadeOutFrames\":0," +
            "\"fadeInInterpolation\":\"linear\",\"fadeOutInterpolation\":\"linear\",\"keyframes\":null}" +
            "}";
    }

    private static string JsonEscape(string s) => s.Replace("\\", "\\\\");

    private sealed class ScratchDir : IDisposable
    {
        public string Path { get; }

        public ScratchDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"palmier-export-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string File(string stem, string ext) => System.IO.Path.Combine(Path, $"{stem}.{ext}");

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private static JsonElement FindStream(JsonElement root, string codecType)
    {
        foreach (JsonElement stream in root.GetProperty("streams").EnumerateArray())
        {
            if (stream.GetProperty("codec_type").GetString() == codecType)
            {
                return stream.Clone();
            }
        }
        throw new Xunit.Sdk.XunitException($"no {codecType} stream present in the muxed output");
    }

    private static JsonDocument Probe(string path, params string[] extraArgs)
    {
        var psi = new ProcessStartInfo(ResolveFfprobe())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add("-print_format");
        psi.ArgumentList.Add("json");
        foreach (string arg in extraArgs)
        {
            psi.ArgumentList.Add(arg);
        }
        psi.ArgumentList.Add(path);

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start ffprobe.exe");
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, "ffprobe must succeed on the muxed output");
        return JsonDocument.Parse(stdout);
    }

    private static string ResolveFfprobe()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "third_party", "ffmpeg", "bin", "ffprobe.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "ffprobe.exe not found under third_party/ffmpeg/bin — run scripts/ci-restore-ffmpeg.ps1 first.");
    }
}
