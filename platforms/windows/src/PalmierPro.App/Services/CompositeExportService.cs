using System.Text;
using System.Text.Json;
using PalmierPro.Core.Models;
using PalmierPro.Services.Engine;
using PalmierPro.Services.Export;
using PalmierPro.Services.Project;

namespace PalmierPro.App.Services;

/// Routes an `ExportRequest` to its destination's concrete exporter. Fcpxml/Xmeml/PalmierProject
/// are fully wired to the already-landed Stage C/M1 exporters (with this class's own
/// write-to-temp-then-move staging for the two XML destinations, since neither exporter stages
/// its own output — see docs/export-v1.md §9's "gap the implementation must close itself," left
/// open by that document's own scope). Video is wired to the native GPU export pipeline (E5,
/// `TimelineSession.ExportVideo` → `PE_ExportStart`) — see <see cref="ExportVideoAsync"/>.
public sealed class CompositeExportService : IExportService
{
    public Task<ExportResult> ExportAsync(ExportRequest request, IProgress<ExportProgress>? progress = null, CancellationToken ct = default) =>
        request.Destination switch
        {
            ExportDestinationKind.Fcpxml => ExportFcpxmlAsync(request, progress, ct),
            ExportDestinationKind.Xmeml => ExportXmemlAsync(request, progress, ct),
            ExportDestinationKind.PalmierProject => ExportPalmierProjectAsync(request, progress, ct),
            ExportDestinationKind.Video => ExportVideoAsync(request, progress, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };

    /// Video destination (docs/export-v1.md §4-§9): builds the export-resolution snapshot
    /// (<see cref="TimelineSnapshotBuilder"/>'s `renderSizeOverride`, §4.1), opens a
    /// <see cref="PalmierPro.Rendering.TimelineSession"/> on a FRESH <see cref="PalmierPro.Rendering.EngineSession"/>
    /// dedicated solely to this export — never a handle also driving live preview (§4.1) — and
    /// drives the whole synchronous compose→convert→encode loop via `TimelineSession.ExportVideo`.
    /// Native owns its own temp-file + atomic-rename discipline (§9): unlike the Fcpxml/Xmeml
    /// branches, this method does not stage `request.OutputPath` itself, only ensures its parent
    /// directory exists first (native's own staging file lives next to it and needs the directory
    /// to already be there — `ExportEncoder::Open`'s `avio_open` does not create directories).
    private static Task<ExportResult> ExportVideoAsync(ExportRequest request, IProgress<ExportProgress>? progress, CancellationToken ct) =>
        Task.Run(
            () =>
            {
                ct.ThrowIfCancellationRequested();
                var timeline = TimelineFor(request);
                var codec = request.Codec ?? throw new ArgumentException("Video export requires Codec.", nameof(request));
                var resolution = request.Resolution ?? throw new ArgumentException("Video export requires Resolution.", nameof(request));
                (int width, int height) = resolution.RenderSize(timeline.Width, timeline.Height);

                var built = TimelineSnapshotBuilder.Build(
                    request.Project, request.TimelineId, request.Resolver, renderSizeOverride: (width, height));
                byte[] snapshotJson = TimelineSnapshotSerializer.ToJsonBytes(built.Snapshot);

                var options = ExportOptions.Create(codec, width, height, timeline.Fps, request.OutputPath);
                string optionsJson = BuildOptionsJson(options);

                var directory = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using var engineSession = new PalmierPro.Rendering.EngineSession();
                using PalmierPro.Rendering.TimelineSession nativeTimeline =
                    PalmierPro.Rendering.TimelineSession.Open(engineSession, snapshotJson);

                void OnReport(PalmierPro.Rendering.ExportVideoPhase phase, double fraction) =>
                    progress?.Report(new ExportProgress(
                        phase == PalmierPro.Rendering.ExportVideoPhase.Preparing ? ExportPhase.Preparing : ExportPhase.Exporting,
                        fraction));

                var report = nativeTimeline.ExportVideo(optionsJson, OnReport, ct);

                return new ExportResult(
                    OutputWidth: width,
                    OutputHeight: height,
                    OfflineMediaRefs: built.OfflineMediaRefs,
                    UnprocessableMediaRefs: nativeTimeline.GetUnprocessableMediaRefs(),
                    EncoderUsed: report.EncoderName,
                    UsedHardwareEncoder: report.UsedHardwareEncoder);
            },
            ct);

    /// Hand-written, matching docs/export-v1.md §4.2's schema exactly (`Utf8JsonWriter` guarantees
    /// correct escaping of Windows path backslashes/quotes — the codebase's usual convention for
    /// every deterministic wire-JSON writer, e.g. `TimelineSnapshotSerializer`).
    private static string BuildOptionsJson(ExportOptions options)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("codec", CodecWireValue(options.Codec));
            writer.WriteString("container", options.Container.FileExtension());
            writer.WriteNumber("width", options.Width);
            writer.WriteNumber("height", options.Height);
            writer.WriteNumber("fps", options.Fps);
            writer.WriteString("outputPath", options.OutputPath);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string CodecWireValue(ExportVideoCodec codec) => codec switch
    {
        ExportVideoCodec.H264 => "h264",
        ExportVideoCodec.H265 => "h265",
        ExportVideoCodec.ProRes => "prores",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    private static async Task<ExportResult> ExportFcpxmlAsync(ExportRequest request, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new ExportProgress(ExportPhase.Preparing, 0));
        ct.ThrowIfCancellationRequested();
        await WithStagedOutputAsync(request.OutputPath, staging =>
            FcpxmlExporter.ExportAsync(
                TimelineFor(request), request.Resolver, ExportServices.SourceTimingReader, ExportServices.FontTraitResolver, staging,
                ResolveTimelineFor(request), request.FcpxmlVersion ?? FcpxmlVersionExtensions.Default, request.FcpxmlTarget ?? FcpxmlTargetExtensions.Default),
            ct).ConfigureAwait(false);
        progress?.Report(new ExportProgress(ExportPhase.Exporting, 1));
        return new ExportResult();
    }

    private static async Task<ExportResult> ExportXmemlAsync(ExportRequest request, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new ExportProgress(ExportPhase.Preparing, 0));
        ct.ThrowIfCancellationRequested();
        await WithStagedOutputAsync(request.OutputPath, staging =>
            XmemlExporter.ExportAsync(TimelineFor(request), request.Resolver, ExportServices.SourceTimingReader, staging, ResolveTimelineFor(request)),
            ct).ConfigureAwait(false);
        progress?.Report(new ExportProgress(ExportPhase.Exporting, 1));
        return new ExportResult();
    }

    private static Task<ExportResult> ExportPalmierProjectAsync(ExportRequest request, IProgress<ExportProgress>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            var reportProgress = new Progress<double>(p => progress?.Report(new ExportProgress(ExportPhase.Exporting, p)));
            var report = PalmierProjectExporter.Export(
                request.Project, request.Manifest ?? new MediaManifest(), request.GenerationLog ?? new GenerationLog(),
                request.SourceProjectPath, request.OutputPath, reportProgress, ct);
            return new ExportResult(
                PalmierCollectedCount: report.Collected.Count,
                PalmierMissingCount: report.Missing.Count,
                PalmierTotalBytesCopied: report.TotalBytes);
        }, ct);

    private static Timeline TimelineFor(ExportRequest request) =>
        request.Project.Timelines.First(t => t.Id == request.TimelineId);

    private static Func<string, Timeline?> ResolveTimelineFor(ExportRequest request) =>
        id => request.Project.Timelines.FirstOrDefault(t => t.Id == id);

    /// Writes to a sibling temp file, then atomically moves it into place, so a cancellation or
    /// failure mid-write never leaves a partial file at `outputPath` — the staging discipline
    /// `FcpxmlExporter`/`XmemlExporter` don't provide themselves (docs/export-v1.md §9).
    private static async Task WithStagedOutputAsync(string outputPath, Func<string, Task> writeAsync, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var staging = Path.Combine(string.IsNullOrEmpty(directory) ? "." : directory, $".{Path.GetFileName(outputPath)}-{Guid.NewGuid():N}.partial");
        try
        {
            await writeAsync(staging).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
            File.Move(staging, outputPath);
        }
        finally
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
    }
}
