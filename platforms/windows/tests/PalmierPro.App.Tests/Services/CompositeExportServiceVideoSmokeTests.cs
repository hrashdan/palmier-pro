using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PalmierPro.App.Services;
using PalmierPro.Core.Models;
using PalmierPro.Services.Export;
using PalmierPro.Services.Project;
using Shouldly;
using Xunit;

namespace PalmierPro.App.Tests.Services;

/// End-to-end smoke test for the Video destination's real wiring (docs/export-v1.md §14 item 4):
/// a real <see cref="ProjectDocument"/> → <see cref="ExportRequest"/> → <see cref="ExportQueue"/>
/// → <see cref="CompositeExportService"/> → native `PE_ExportStart`, exactly the path
/// `ExportViewModel.StartExportAsync` drives through `ExportServices.Queue`
/// (`ExportServices.cs`/`CompositeExportService.cs`) — no WinUI involved (this project never
/// instantiates a WinUI type), but every non-UI link in that chain is real: a real
/// `PalmierEngine.dll`, a real `libx264` encode, and `ffprobe` validation of the muxed output.
/// This is the one destination `CompositeExportService`'s own unit-free routing switch had NO
/// coverage for before Stage F's exportLoop slice landed — a regression here previously surfaced
/// only as a `NotSupportedException` thrown at export time, invisible to `dotnet build`/every
/// other Video-adjacent test (`ExportViewModelTests` stubs `IExportQueue`; `ExportSessionTests`
/// drives native `TimelineSession.ExportVideo` directly, bypassing `CompositeExportService`
/// entirely).
[Trait("Category", "Media")]
public sealed class CompositeExportServiceVideoSmokeTests
{
    static CompositeExportServiceVideoSmokeTests()
    {
        // Deterministic codec_name regardless of GPU vendor — mirrors ExportSessionTests/
        // ExportEncoderTests; read natively via GetEnvironmentVariableA (export-v1.md §6/§15).
        Environment.SetEnvironmentVariable("PALMIERENGINE_FORCE_SW_ENCODE", "1");
    }

    [Fact]
    public async Task Enqueue_VideoRequest_RunsThroughRealQueueAndProducesProbeValidH264()
    {
        using var temp = new TempDirectory();
        var doc = await ProjectDocument.CreateNewAsync(temp.Path, "Export Smoke");

        const int width = 640, height = 360, fps = 30, durationFrames = 30; // 1.0 s @ 30 fps
        string sourcePath = Path.Combine(temp.Path, "source.mp4");
        MakeFixtureVideo(sourcePath, width, height, fps, seconds: 1.0);

        var entry = new MediaManifestEntry("CLIP-1", "CLIP-1", ClipType.Video, MediaSource.External(sourcePath),
            duration: durationFrames / (double)fps, sourceWidth: width, sourceHeight: height, hasAudio: true);
        doc.Manifest = new MediaManifest { Entries = [entry] };

        var timeline = doc.ProjectFile.Timelines[0];
        timeline.Width = width;
        timeline.Height = height;
        timeline.Fps = fps;
        timeline.Tracks =
        [
            new Track(ClipType.Video, [new Clip("CLIP-1", startFrame: 0, durationFrames: durationFrames) { Id = "TIMELINE-CLIP-1", MediaType = ClipType.Video, SourceClipType = ClipType.Video }])
            {
                Id = "TRACK-1",
            },
        ];

        string outputPath = Path.Combine(temp.Path, "smoke-export.mp4");
        var resolver = new MediaResolver(() => doc.Manifest, () => doc.PackagePath);
        var request = new ExportRequest(
            ExportDestinationKind.Video, doc.ProjectFile, timeline.Id, resolver, outputPath,
            Codec: ExportVideoCodec.H264, Resolution: ExportResolution.MatchTimeline);

        var queue = new ExportQueue(new CompositeExportService());
        var submission = queue.Enqueue(request, doc.PackagePath, ExportJobSource.Manual);
        submission.Started.ShouldBeTrue();

        var job = await WaitUntilFinishedAsync(queue, submission.JobId);

        job.Error.ShouldBeNull();
        job.Status.ShouldBe(ExportJobStatus.Completed);
        job.Progress.ShouldBe(1.0);

        File.Exists(outputPath).ShouldBeTrue("the real export queue must produce a file at the requested path");
        new FileInfo(outputPath).Length.ShouldBeGreaterThan(0);

        using JsonDocument probe = Probe(outputPath);
        JsonElement video = FindStream(probe.RootElement, "video");
        video.GetProperty("codec_name").GetString().ShouldBe("h264");
        video.GetProperty("width").GetInt32().ShouldBe(width);
        video.GetProperty("height").GetInt32().ShouldBe(height);
    }

    private static async Task<ExportJob> WaitUntilFinishedAsync(IExportQueue queue, Guid jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var job = queue.Jobs.First(j => j.Id == jobId);
            if (job.Status.IsFinished())
            {
                return job;
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
        throw new TimeoutException("Export did not finish within 60s.");
    }

    private static void MakeFixtureVideo(string path, int width, int height, int fps, double seconds)
    {
        var psi = new ProcessStartInfo(ResolveFfmpegExe())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add($"testsrc2=size={width}x{height}:rate={fps}:duration={seconds.ToString(CultureInfo.InvariantCulture)}");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add($"sine=frequency=1000:duration={seconds.ToString(CultureInfo.InvariantCulture)}");
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-shortest");
        psi.ArgumentList.Add(path);

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start ffmpeg.exe");
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg fixture generation failed (exit {process.ExitCode}):\n{stderr}");
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

    private static JsonDocument Probe(string path)
    {
        var psi = new ProcessStartInfo(ResolveFfprobeExe())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add("-print_format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("-show_streams");
        psi.ArgumentList.Add(path);

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start ffprobe.exe");
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, "ffprobe must succeed on the muxed output");
        return JsonDocument.Parse(stdout);
    }

    private static string ResolveFfmpegExe() => ResolveThirdPartyFfmpegExe("ffmpeg.exe");

    private static string ResolveFfprobeExe() => ResolveThirdPartyFfmpegExe("ffprobe.exe");

    private static string ResolveThirdPartyFfmpegExe(string exeName)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "third_party", "ffmpeg", "bin", exeName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            $"{exeName} not found under third_party/ffmpeg/bin — run scripts/ci-restore-ffmpeg.ps1 first.");
    }
}
