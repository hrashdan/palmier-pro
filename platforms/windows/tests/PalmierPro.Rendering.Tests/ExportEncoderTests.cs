using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace PalmierPro.Rendering.Tests;

// E5 export encoder/muxer (export-v1.md §6/§7/§9) — the native ExportEncoder driven end to end
// through PE_ExportEncodeForTest: per codec it synthesizes 60 moving-gradient frames + a stereo
// sine, encodes+muxes them (software-forced for a deterministic codec_name, §6/§15), and ffprobe
// validates codec / pix_fmt / duration / the AAC audio stream, plus strictly-monotonic video PTS.
//
// [Trait Media] (not GPU) — the encoder path is pure FFmpeg (no D3D device), so it runs under the
// standard `dotnet test --filter "Category!=GPU"` CI command. Needs PalmierEngine.dll + the FFmpeg
// runtime DLLs (staged into the test output by PalmierPro.Rendering.csproj) and ffprobe.exe (found
// under third_party/ffmpeg/bin the same way FfprobeSourceTimingReader resolves it).
public sealed class ExportEncoderTests
{
    private const int Fps = 30;
    private const int FrameCount = 60; // 2.0 s at 30 fps

    [Theory]
    [Trait("Category", "Media")]
    // codec, container, expected codec_name, expected pix_fmt, width, height (MatchTimeline-size + scaled)
    [InlineData(ExportCodec.H264, "mp4", "h264", "yuv420p", 320, 240)]
    [InlineData(ExportCodec.H264, "mp4", "h264", "yuv420p", 480, 270)]
    [InlineData(ExportCodec.H265, "mp4", "hevc", "yuv420p", 320, 240)]
    [InlineData(ExportCodec.H265, "mp4", "hevc", "yuv420p", 480, 270)]
    [InlineData(ExportCodec.ProRes, "mov", "prores", "yuv422p10le", 320, 240)]
    [InlineData(ExportCodec.ProRes, "mov", "prores", "yuv422p10le", 480, 270)]
    public void Encode_SixtyFramesPlusSine_ProbesCodecPixFmtDurationAudio(
        ExportCodec codec, string container, string expectedCodec, string expectedPixFmt, int width, int height)
    {
        string outputPath = Path.Combine(Path.GetTempPath(),
            $"palmier-export-{codec}-{width}x{height}-{Guid.NewGuid():N}.{container}");
        try
        {
            using var session = new EngineSession();
            ExportEncodeReport report = session.ExportEncodeForTest(
                codec, container, width, height, Fps, FrameCount,
                withAudio: true, sineHz: 440.0, outputPath: outputPath, forceSoftware: true);

            report.FramesEncoded.ShouldBe(FrameCount);
            report.UsedHardwareEncoder.ShouldBeFalse("forceSoftware must skip the hardware probe");
            report.EncoderName.ShouldBe(codec switch
            {
                ExportCodec.H264 => "libx264",
                ExportCodec.H265 => "libx265",
                ExportCodec.ProRes => "prores_ks",
                _ => throw new ArgumentOutOfRangeException(nameof(codec)),
            });

            File.Exists(outputPath).ShouldBeTrue();
            new FileInfo(outputPath).Length.ShouldBeGreaterThan(0);

            using JsonDocument probe = Probe(outputPath, "-show_format", "-show_streams");
            JsonElement root = probe.RootElement;

            JsonElement videoStream = FindStream(root, "video");
            videoStream.GetProperty("codec_name").GetString().ShouldBe(expectedCodec);
            videoStream.GetProperty("pix_fmt").GetString().ShouldBe(expectedPixFmt);
            videoStream.GetProperty("width").GetInt32().ShouldBe(width);
            videoStream.GetProperty("height").GetInt32().ShouldBe(height);

            JsonElement audioStream = FindStream(root, "audio");
            audioStream.GetProperty("codec_name").GetString().ShouldBe("aac");
            audioStream.GetProperty("channels").GetInt32().ShouldBe(2);
            int.Parse(audioStream.GetProperty("sample_rate").GetString()!, CultureInfo.InvariantCulture)
                .ShouldBe(48000);

            // 60 frames at 30 fps = 2.0 s; AAC priming/container rounding widens the window slightly.
            double duration = double.Parse(
                root.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
            duration.ShouldBeInRange(1.8, 2.6);

            // Video PTS strictly increases (monotonic) across every decoded frame.
            long[] pts = VideoFramePts(outputPath);
            pts.Length.ShouldBe(FrameCount);
            for (int i = 1; i < pts.Length; i++)
            {
                pts[i].ShouldBeGreaterThan(pts[i - 1], $"frame {i} PTS must exceed frame {i - 1}'s");
            }
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
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

    private static long[] VideoFramePts(string path)
    {
        using JsonDocument doc = Probe(path,
            "-select_streams", "v:0", "-show_frames", "-show_entries", "frame=pts");
        var result = new List<long>();
        foreach (JsonElement frame in doc.RootElement.GetProperty("frames").EnumerateArray())
        {
            if (frame.TryGetProperty("pts", out JsonElement ptsEl) && ptsEl.TryGetInt64(out long pts))
            {
                result.Add(pts);
            }
        }
        return result.ToArray();
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
