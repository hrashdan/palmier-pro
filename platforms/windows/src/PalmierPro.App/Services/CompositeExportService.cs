using System.Text;
using System.Text.Json;
using PalmierPro.Core.Models;
using PalmierPro.Services.Engine;
using PalmierPro.Services.Export;
using PalmierPro.Services.Media;
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
    private readonly Func<PalmierPro.Rendering.EngineSession, ILottieBakeService> _lottieBakeServiceFactory;

    /// <paramref name="lottieBakeServiceFactory"/> builds the Lottie bake service the Video route
    /// threads into <see cref="TimelineSnapshotBuilder.Build"/> (mirroring how live preview already
    /// passes one — `PreviewViewModel.RebuildAsync`), defaulting to a real
    /// <see cref="LottieBakeService"/> bound to the export's OWN fresh
    /// <see cref="PalmierPro.Rendering.EngineSession"/> — never the live-preview handle
    /// (docs/export-v1.md §4.1) — and reading the same on-disk `LottieVideos` cache the live document
    /// already populated. Overridable so tests (and any future non-default composition) can inject a
    /// fake without a native session. See <see cref="ExportVideoAsync"/> / <see cref="BuildExportSnapshotAsync"/>.
    public CompositeExportService(Func<PalmierPro.Rendering.EngineSession, ILottieBakeService>? lottieBakeServiceFactory = null) =>
        _lottieBakeServiceFactory = lottieBakeServiceFactory ?? (session => new LottieBakeService(session));

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
    private Task<ExportResult> ExportVideoAsync(ExportRequest request, IProgress<ExportProgress>? progress, CancellationToken ct) =>
        Task.Run(
            async () =>
            {
                ct.ThrowIfCancellationRequested();
                var timeline = TimelineFor(request);
                var codec = request.Codec ?? throw new ArgumentException("Video export requires Codec.", nameof(request));
                var resolution = request.Resolution ?? throw new ArgumentException("Video export requires Resolution.", nameof(request));
                (int width, int height) = resolution.RenderSize(timeline.Width, timeline.Height);

                var directory = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // The EngineSession is created FIRST (before the snapshot) so the Lottie bake service
                // can bind to it: unlike the pre-fix ordering, the snapshot build now depends on a
                // live session so a `.lottie` clip's baked `.mov` is resolved into the snapshot rather
                // than silently dropped (was: `Build(...)` with no `lottieBakeService`, so every Lottie
                // clip short-circuited to PendingLottieBakes and vanished from the export).
                using var engineSession = new PalmierPro.Rendering.EngineSession();
                var lottieBakeService = _lottieBakeServiceFactory(engineSession);
                var built = await BuildExportSnapshotAsync(request, (width, height), lottieBakeService, ct).ConfigureAwait(false);
                byte[] snapshotJson = TimelineSnapshotSerializer.ToJsonBytes(built.Snapshot);

                var options = ExportOptions.Create(codec, width, height, timeline.Fps, request.OutputPath);
                string optionsJson = BuildOptionsJson(options);

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

    /// Builds the export-resolution snapshot, threading <paramref name="bakeService"/> into
    /// <see cref="TimelineSnapshotBuilder.Build"/> exactly as live preview does — so a `.lottie` clip
    /// whose bake is already cached resolves to its baked `.mov` inline instead of being dropped
    /// (the Phase-1 gap this method closes).
    ///
    /// Cold-cache behavior — deliberately matched to the Mac's `ExportService`, NOT invented here:
    /// the Mac composites through `CompositionBuilder.build`, which for each Lottie clip AWAITS
    /// `LottieVideoGenerator.lottieVideo` (`CompositionBuilder.swift:353-364`) — a synchronous,
    /// blocking bake-if-not-cached — before the export session is created. So a cold cache does not
    /// drop the clip; it blocks the export until the bake finishes. We reproduce that: the first
    /// <see cref="TimelineSnapshotBuilder.Build"/> kicks off a `BakeAsync` for every un-cached Lottie
    /// clip (reported via <see cref="TimelineSnapshotBuildResult.PendingLottieBakes"/>); we await
    /// those bakes reaching a terminal state via <see cref="ILottieBakeService.StatusChanged"/>, then
    /// rebuild so the now-warm cache resolves them. A bake that FAILS is left omitted from the
    /// snapshot — the Mac's identical posture for unprocessable media (`CompositionBuilder.loadSource`
    /// returns `.unprocessable`, the composition/export continues without that clip and merely reports
    /// the ref; `ExportService` never fails the whole job over it).
    internal static async Task<TimelineSnapshotBuildResult> BuildExportSnapshotAsync(
        ExportRequest request, (int Width, int Height) renderSize, ILottieBakeService? bakeService, CancellationToken ct)
    {
        TimelineSnapshotBuildResult Build() => TimelineSnapshotBuilder.Build(
            request.Project, request.TimelineId, request.Resolver, bakeService, renderSize);

        if (bakeService is null)
        {
            return Build();
        }

        // MediaRefs whose bake has reached Completed/Failed since we subscribed. A HashSet guarded by
        // its own lock — StatusChanged can fire from a bake worker thread.
        var terminal = new HashSet<string>();
        using var bakeSettled = new SemaphoreSlim(0);
        void OnStatusChanged(object? sender, LottieBakeStatusChangedEventArgs e)
        {
            if (e.Status is LottieBakeStatus.Completed or LottieBakeStatus.Failed)
            {
                lock (terminal)
                {
                    terminal.Add(e.MediaRef);
                }
                bakeSettled.Release();
            }
        }

        // Subscribe BEFORE the first Build: Build synchronously calls BakeAsync for each un-cached
        // clip, and a fast bake could reach a terminal state before the wait loop below would start —
        // subscribing first guarantees no completion slips through that gap and strands the wait.
        bakeService.StatusChanged += OnStatusChanged;
        try
        {
            var built = Build();
            while (built.PendingLottieBakes.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                bool allSettled;
                lock (terminal)
                {
                    allSettled = built.PendingLottieBakes.All(terminal.Contains);
                }
                if (allSettled)
                {
                    // Every pending bake finished (Completed or Failed). Rebuild once: Completed refs
                    // now resolve from the warm cache inline; a Failed ref's cache lookup still misses,
                    // so it re-enters PendingLottieBakes and stays omitted from the snapshot — Mac
                    // `.unprocessable` parity (skip the clip, let the export proceed). We return this
                    // rebuild directly rather than re-entering the loop, so a permanently-failing bake
                    // can never spin here.
                    return Build();
                }
                await bakeSettled.WaitAsync(ct).ConfigureAwait(false);
            }
            return built;
        }
        finally
        {
            bakeService.StatusChanged -= OnStatusChanged;
        }
    }

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
