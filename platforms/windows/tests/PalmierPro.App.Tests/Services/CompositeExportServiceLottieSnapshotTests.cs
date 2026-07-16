using PalmierPro.App.Services;
using PalmierPro.Core.Models;
using PalmierPro.Services.Engine;
using PalmierPro.Services.Export;
using PalmierPro.Services.Media;
using Shouldly;
using Xunit;

namespace PalmierPro.App.Tests.Services;

/// Regression coverage for the Phase-1-blocking Video-export gap: `CompositeExportService`'s
/// snapshot build used to call `TimelineSnapshotBuilder.Build(...)` WITHOUT an `ILottieBakeService`,
/// so `TryResolveLottieClip` short-circuited to `PendingLottieBakes` and every `.lottie` clip was
/// silently dropped from a dialog-route video export — even with a warm bake cache. These tests
/// drive `CompositeExportService.BuildExportSnapshotAsync` (the exact snapshot-build seam
/// `ExportVideoAsync` runs before handing native the JSON) against a fake bake service, so they
/// cover the fix end of the route without a live native session. The real bake -> encode ->
/// composite round trip lives in PalmierPro.Rendering.Tests/LottieCompositingEndToEndTests.cs.
[Trait("Category", "Media")]
public sealed class CompositeExportServiceLottieSnapshotTests
{
    private const int TimelineWidth = 1920;
    private const int TimelineHeight = 1080;

    /// The scenario that was broken: a warm cache must land the baked `.mov` in the export snapshot
    /// as an ordinary video clip — not vanish.
    [Fact]
    public async Task WarmCache_ExportSnapshotCarriesBakedLottieMediaPath()
    {
        using var dir = new TempDirectory();
        var (request, clip) = MakeLottieExportRequest(dir);
        const string bakedPath = @"C:\cache\LottieVideos\abc123_1920x1080_v1.mov";
        var bakeService = new FakeExportBakeService();
        bakeService.SeedWarm(clip.MediaRef, bakedPath);

        var built = await CompositeExportService.BuildExportSnapshotAsync(
            request, (TimelineWidth, TimelineHeight), bakeService, CancellationToken.None);

        built.PendingLottieBakes.ShouldBeEmpty();
        SnapshotClip snapshotClip = built.Snapshot.Tracks.ShouldHaveSingleItem().Clips.ShouldHaveSingleItem();
        snapshotClip.Type.ShouldBe(ClipType.Video, "a baked Lottie clip enters the snapshot as ordinary video — doc §10");
        snapshotClip.MediaPath.ShouldBe(bakedPath);
        snapshotClip.Id.ShouldBe(clip.Id);
        bakeService.BakeRequests.ShouldBeEmpty("a warm cache needs no re-bake — TryGetCachedPath alone resolves it");
    }

    /// Cold cache: mirrors the Mac's `CompositionBuilder.build` awaiting `LottieVideoGenerator.
    /// lottieVideo` before compositing — the export waits for the kicked-off bake, then the rebuild
    /// resolves the now-warm clip. The clip must NOT be dropped.
    [Fact]
    public async Task ColdCache_AwaitsBakeCompletion_ThenSnapshotCarriesBakedPath()
    {
        using var dir = new TempDirectory();
        var (request, clip) = MakeLottieExportRequest(dir);
        const string bakedPath = @"C:\cache\LottieVideos\def456_1920x1080_v1.mov";
        var bakeService = new FakeExportBakeService();
        bakeService.CompleteOnBake[clip.MediaRef] = bakedPath; // first BakeAsync seeds the cache + reports Completed

        var built = await CompositeExportService.BuildExportSnapshotAsync(
            request, (TimelineWidth, TimelineHeight), bakeService, CancellationToken.None);

        built.PendingLottieBakes.ShouldBeEmpty();
        SnapshotClip snapshotClip = built.Snapshot.Tracks.ShouldHaveSingleItem().Clips.ShouldHaveSingleItem();
        snapshotClip.MediaPath.ShouldBe(bakedPath);
        bakeService.BakeRequests.ShouldHaveSingleItem().MediaRef.ShouldBe(clip.MediaRef);
    }

    /// Cold cache whose bake FAILS: the export must neither hang nor throw — the clip is left out of
    /// the snapshot and the export proceeds, matching the Mac's `.unprocessable` posture
    /// (ExportService reports the ref but never fails the whole job).
    [Fact]
    public async Task ColdCache_BakeFails_ClipOmitted_AndExportSnapshotStillBuilds()
    {
        using var dir = new TempDirectory();
        var (request, clip) = MakeLottieExportRequest(dir);
        var bakeService = new FakeExportBakeService();
        bakeService.FailOnBake.Add(clip.MediaRef); // every BakeAsync reports Failed; cache never populated

        var built = await CompositeExportService.BuildExportSnapshotAsync(
            request, (TimelineWidth, TimelineHeight), bakeService, CancellationToken.None);

        built.Snapshot.Tracks.ShouldBeEmpty("a failed bake leaves the clip omitted — Mac .unprocessable skip-and-continue");
        built.OfflineMediaRefs.ShouldBeEmpty("a failed bake is a tracked gap, never a missing-file error — doc §10");
        bakeService.BakeRequests.ShouldNotBeEmpty();
    }

    private static (ExportRequest Request, Clip LottieClip) MakeLottieExportRequest(TempDirectory dir)
    {
        var clip = new Clip("lottie-1", startFrame: 0, durationFrames: 30)
        {
            Id = "LOTTIE-CLIP-1",
            MediaType = ClipType.Lottie,
            SourceClipType = ClipType.Lottie,
        };
        var track = new Track(ClipType.Video, [clip]) { Id = "TRACK-1" };
        var timeline = new Timeline { Id = "TL-1", Fps = 30, Width = TimelineWidth, Height = TimelineHeight, Tracks = [track] };
        var project = new ProjectFile([timeline], timeline.Id, [timeline.Id]);

        // ResolveUrl gates on File.Exists, so the Lottie source must be a real file on disk.
        string sourcePath = Path.Combine(dir.Path, $"{clip.MediaRef}.json");
        File.WriteAllText(sourcePath, "{}");
        var manifest = new MediaManifest
        {
            Entries = [new MediaManifestEntry(clip.MediaRef, clip.MediaRef, ClipType.Lottie, MediaSource.External(sourcePath), duration: 1)],
        };
        var resolver = new MediaResolver(() => manifest, () => null);
        var request = new ExportRequest(
            ExportDestinationKind.Video, project, timeline.Id, resolver, Path.Combine(dir.Path, "out.mp4"),
            Codec: ExportVideoCodec.H264, Resolution: ExportResolution.MatchTimeline);
        return (request, clip);
    }

    /// Deterministic, in-memory fake — no disk, no native. Fires `StatusChanged` synchronously from
    /// `BakeAsync` (the awaited event the export route listens on), which is enough to exercise the
    /// subscribe-before-build / await-then-rebuild loop without a real background bake.
    private sealed class FakeExportBakeService : ILottieBakeService
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, string> _cachedByRef = new(StringComparer.Ordinal);

        /// MediaRefs whose first `BakeAsync` seeds the cache with the mapped path and reports Completed.
        public Dictionary<string, string> CompleteOnBake { get; } = new(StringComparer.Ordinal);

        /// MediaRefs whose every `BakeAsync` reports Failed (cache stays cold).
        public HashSet<string> FailOnBake { get; } = new(StringComparer.Ordinal);

        public List<LottieBakeRequest> BakeRequests { get; } = [];

        public event EventHandler<LottieBakeStatusChangedEventArgs>? StatusChanged;

        public void SeedWarm(string mediaRef, string path)
        {
            lock (_gate)
            {
                _cachedByRef[mediaRef] = path;
            }
        }

        public string? TryGetCachedPath(LottieBakeRequest request)
        {
            lock (_gate)
            {
                return _cachedByRef.TryGetValue(request.MediaRef, out var p) ? p : null;
            }
        }

        public void BakeAsync(LottieBakeRequest request, CancellationToken ct = default)
        {
            lock (_gate)
            {
                BakeRequests.Add(request);
            }
            if (CompleteOnBake.TryGetValue(request.MediaRef, out var path))
            {
                lock (_gate)
                {
                    _cachedByRef[request.MediaRef] = path;
                }
                StatusChanged?.Invoke(this, new LottieBakeStatusChangedEventArgs(
                    request.MediaRef, request.Width, request.Height, LottieBakeStatus.Completed, path, null));
            }
            else if (FailOnBake.Contains(request.MediaRef))
            {
                StatusChanged?.Invoke(this, new LottieBakeStatusChangedEventArgs(
                    request.MediaRef, request.Width, request.Height, LottieBakeStatus.Failed, null, "bake failed (test)"));
            }
        }

        public LottieBakeStatus StatusFor(string mediaRef, int width, int height)
        {
            lock (_gate)
            {
                if (_cachedByRef.ContainsKey(mediaRef))
                {
                    return LottieBakeStatus.Completed;
                }
                return BakeRequests.Any(r => r.MediaRef == mediaRef) ? LottieBakeStatus.InProgress : LottieBakeStatus.NotStarted;
            }
        }
    }
}
