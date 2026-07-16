using PalmierPro.Core.Models;
using PalmierPro.Services.Export;
using Shouldly;
using Xunit;

namespace PalmierPro.Services.Tests.Export;

/// Ported from Tests/PalmierProTests/Export/ExportQueueTests.swift. The Mac version injects an
/// arbitrary closure per job (`enqueueForTesting`, receiving a live `ExportService`); this port
/// drives the same scenarios through <see cref="FakeExportService"/> instead, since
/// <see cref="IExportQueue.Enqueue"/> always calls the ONE <see cref="IExportService.ExportAsync"/>
/// (docs/export-v1.md §11) rather than taking a raw operation — status is derived purely from that
/// call's return value/exception, exactly as `ExportQueue.run(_:)` derives its own richer status
/// from `ExportService`'s post-call state.
public sealed class ExportQueueTests
{
    [Fact]
    public async Task Enqueue_RunsJobsInFifoOrder()
    {
        var dir = ExportFixtures.NewTempDir();
        var events = new List<string>();
        var service = new FakeExportService(async (request, _, _) =>
        {
            var name = Path.GetFileNameWithoutExtension(request.OutputPath);
            events.Add($"{name}-start");
            await Task.Yield();
            events.Add($"{name}-finish");
            return new ExportResult();
        });
        var queue = new ExportQueue(service);

        var first = queue.Enqueue(Request("first.mov", dir), "project", ExportJobSource.Manual);
        var second = queue.Enqueue(Request("second.mov", dir), "project", ExportJobSource.Manual);

        first.Started.ShouldBeTrue();
        second.Started.ShouldBeFalse();
        second.QueuePosition.ShouldBe(1);

        (await WaitUntilAsync(() => !queue.HasActivity)).ShouldBeTrue();
        events.ShouldBe(["first-start", "first-finish", "second-start", "second-finish"]);
    }

    [Fact]
    public async Task Cancel_PreparingJob_NeverRunsItAndAdvancesToTheNextJob()
    {
        var dir = ExportFixtures.NewTempDir();
        var nextRan = false;
        var service = new FakeExportService(async (request, _, ct) =>
        {
            if (request.OutputPath.Contains("active", StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            nextRan = true;
            return new ExportResult();
        });
        var queue = new ExportQueue(service);

        var active = queue.Enqueue(Request("active.mov", dir), "project", ExportJobSource.Manual);
        var next = queue.Enqueue(Request("next.mov", dir), "project", ExportJobSource.Manual);

        queue.Cancel(active.JobId).ShouldBeTrue();

        (await WaitUntilAsync(() => Job(queue, active.JobId).Status == ExportJobStatus.Canceled
            && Job(queue, next.JobId).Status == ExportJobStatus.Completed)).ShouldBeTrue();
        nextRan.ShouldBeTrue();
    }

    [Fact]
    public async Task Cancel_WaitingJob_MarksCanceledImmediatelyAndNeverRunsIt()
    {
        var dir = ExportFixtures.NewTempDir();
        var waitingRan = false;
        var service = new FakeExportService(async (request, _, ct) =>
        {
            if (request.OutputPath.Contains("blocker", StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            else
            {
                waitingRan = true;
            }
            return new ExportResult();
        });
        var queue = new ExportQueue(service);

        var blocker = queue.Enqueue(Request("blocker.mov", dir), "project", ExportJobSource.Manual);
        var waiting = queue.Enqueue(Request("waiting.mov", dir), "project", ExportJobSource.Manual);

        queue.Cancel(waiting.JobId).ShouldBeTrue();
        Job(queue, waiting.JobId).Status.ShouldBe(ExportJobStatus.Canceled);

        queue.Cancel(blocker.JobId).ShouldBeTrue();
        (await WaitUntilAsync(() => !queue.HasActivity)).ShouldBeTrue();
        waitingRan.ShouldBeFalse();
    }

    [Fact]
    public void Enqueue_SameDestinationTwiceWhileFirstIsPending_Throws()
    {
        var dir = ExportFixtures.NewTempDir();
        var service = new FakeExportService(async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return new ExportResult(); });
        var queue = new ExportQueue(service);
        var path = Path.Combine(dir, "same.mov");

        queue.Enqueue(Request("same.mov", dir), "project", ExportJobSource.Manual);
        queue.IsDestinationReserved(path).ShouldBeTrue();

        var ex = Should.Throw<ExportDestinationInUseException>(
            () => queue.Enqueue(Request("same.mov", dir), "project", ExportJobSource.Manual));
        ex.Filename.ShouldBe("same.mov");
    }

    [Fact]
    public async Task Progress_UpdatesPhaseAndFraction_ThenCompletesAtFullProgress()
    {
        var dir = ExportFixtures.NewTempDir();
        var gate = new TaskCompletionSource();
        var service = new FakeExportService(async (_, progress, ct) =>
        {
            progress?.Report(new ExportProgress(ExportPhase.Preparing, 0));
            await Task.Delay(10, ct);
            progress?.Report(new ExportProgress(ExportPhase.Exporting, 0.5));
            await gate.Task.WaitAsync(ct);
            return new ExportResult();
        });
        var queue = new ExportQueue(service);
        var submission = queue.Enqueue(Request("progress.mov", dir), "project", ExportJobSource.Manual);

        (await WaitUntilAsync(() => Job(queue, submission.JobId).Status == ExportJobStatus.Exporting
            && Job(queue, submission.JobId).Progress == 0.5)).ShouldBeTrue();

        gate.SetResult();

        (await WaitUntilAsync(() => Job(queue, submission.JobId).Status == ExportJobStatus.Completed)).ShouldBeTrue();
        Job(queue, submission.JobId).Progress.ShouldBe(1);
    }

    [Fact]
    public async Task ExportAsyncThrowing_MarksTheJobFailedWithTheExceptionMessage()
    {
        var dir = ExportFixtures.NewTempDir();
        var service = new FakeExportService((_, _, _) => throw new InvalidOperationException("disk full"));
        var queue = new ExportQueue(service);
        var submission = queue.Enqueue(Request("fails.mov", dir), "project", ExportJobSource.Manual);

        (await WaitUntilAsync(() => Job(queue, submission.JobId).Status == ExportJobStatus.Failed)).ShouldBeTrue();
        Job(queue, submission.JobId).Error.ShouldBe("disk full");
    }

    [Fact]
    public async Task ClearFinished_OnlyRemovesFinishedJobsForThatProject()
    {
        var dir = ExportFixtures.NewTempDir();
        var service = new FakeExportService((_, _, _) => Task.FromResult(new ExportResult()));
        var queue = new ExportQueue(service);

        var a = queue.Enqueue(Request("a.mov", dir), "project-a", ExportJobSource.Manual);
        var b = queue.Enqueue(Request("b.mov", dir), "project-b", ExportJobSource.Manual);
        (await WaitUntilAsync(() => !queue.HasActivity)).ShouldBeTrue();

        queue.JobsFor("project-a").Select(j => j.Id).ShouldBe([a.JobId]);
        queue.JobsFor("project-b").Select(j => j.Id).ShouldBe([b.JobId]);

        queue.ClearFinished("project-a");

        queue.JobsFor("project-a").ShouldBeEmpty();
        queue.JobsFor("project-b").Select(j => j.Id).ShouldBe([b.JobId]);
    }

    [Fact]
    public async Task Remove_OnlyRemovesAFinishedJob()
    {
        var dir = ExportFixtures.NewTempDir();
        var gate = new TaskCompletionSource();
        var service = new FakeExportService(async (_, _, ct) => { await gate.Task.WaitAsync(ct); return new ExportResult(); });
        var queue = new ExportQueue(service);
        var submission = queue.Enqueue(Request("remove.mov", dir), "project", ExportJobSource.Manual);

        queue.Remove(submission.JobId);
        queue.Jobs.ShouldContain(j => j.Id == submission.JobId);

        gate.SetResult();
        (await WaitUntilAsync(() => Job(queue, submission.JobId).Status == ExportJobStatus.Completed)).ShouldBeTrue();

        queue.Remove(submission.JobId);
        queue.Jobs.ShouldNotContain(j => j.Id == submission.JobId);
    }

    [Fact]
    public void Cancel_UnknownJobId_ReturnsFalse()
    {
        var service = new FakeExportService((_, _, _) => Task.FromResult(new ExportResult()));
        var queue = new ExportQueue(service);

        queue.Cancel(Guid.NewGuid()).ShouldBeFalse();
    }

    [Fact]
    public async Task Cancel_AlreadyFinishedJob_ReturnsFalse()
    {
        var dir = ExportFixtures.NewTempDir();
        var service = new FakeExportService((_, _, _) => Task.FromResult(new ExportResult()));
        var queue = new ExportQueue(service);
        var submission = queue.Enqueue(Request("finished.mov", dir), "project", ExportJobSource.Manual);

        (await WaitUntilAsync(() => Job(queue, submission.JobId).Status == ExportJobStatus.Completed)).ShouldBeTrue();

        queue.Cancel(submission.JobId).ShouldBeFalse();
    }

    /// docs/export-v1.md's instruction to route Fcpxml/Xmeml/PalmierProject through the queue too
    /// (§3/§11), not just the Video destination — `ExportQueue` neither knows nor cares which of
    /// the four it's running; every request reaches `IExportService.ExportAsync` the same way.
    [Fact]
    public async Task Enqueue_AcceptsAllFourDestinationKinds_AndRunsThemAllToCompletionInOrder()
    {
        var dir = ExportFixtures.NewTempDir();
        var seen = new List<ExportDestinationKind>();
        var service = new FakeExportService((request, _, _) =>
        {
            seen.Add(request.Destination);
            return Task.FromResult(new ExportResult());
        });
        var queue = new ExportQueue(service);

        queue.Enqueue(Request("timeline.mp4", dir, ExportDestinationKind.Video), "project", ExportJobSource.Manual);
        queue.Enqueue(Request("timeline.fcpxml", dir, ExportDestinationKind.Fcpxml), "project", ExportJobSource.Manual);
        queue.Enqueue(Request("timeline.xml", dir, ExportDestinationKind.Xmeml), "project", ExportJobSource.Manual);
        queue.Enqueue(Request("project.palmier", dir, ExportDestinationKind.PalmierProject), "project", ExportJobSource.Manual);

        (await WaitUntilAsync(() => !queue.HasActivity)).ShouldBeTrue();

        queue.Jobs.ShouldAllBe(j => j.Status == ExportJobStatus.Completed);
        seen.ShouldBe([
            ExportDestinationKind.Video, ExportDestinationKind.Fcpxml, ExportDestinationKind.Xmeml, ExportDestinationKind.PalmierProject,
        ]);
    }

    /// Mirrors `ExportQueue.finish`'s `service.lastPalmierReport` overwrite (`Export/ExportQueue.swift:282-285`).
    [Fact]
    public async Task Finish_PalmierProjectDestination_OverwritesCallerWarningsFromTheResult()
    {
        var dir = ExportFixtures.NewTempDir();
        var service = new FakeExportService((_, _, _) => Task.FromResult(new ExportResult(PalmierMissingCount: 2)));
        var queue = new ExportQueue(service);

        var submission = queue.Enqueue(
            Request("pkg.palmier", dir, ExportDestinationKind.PalmierProject), "project", ExportJobSource.Manual,
            warnings: ["stale warning"]);

        (await WaitUntilAsync(() => Job(queue, submission.JobId).Status == ExportJobStatus.Completed)).ShouldBeTrue();
        Job(queue, submission.JobId).Warnings.ShouldBe(["2 media files were missing and could not be included."]);
    }

    /// Every other destination's `Warnings` is exactly what the caller passed to `Enqueue` — only
    /// PalmierProject's gets recomputed from the result (docs/export-v1.md §11).
    [Fact]
    public async Task Finish_VideoDestination_KeepsTheCallerSuppliedWarnings()
    {
        var dir = ExportFixtures.NewTempDir();
        var service = new FakeExportService((_, _, _) => Task.FromResult(new ExportResult()));
        var queue = new ExportQueue(service);

        var submission = queue.Enqueue(
            Request("clip.mp4", dir, ExportDestinationKind.Video), "project", ExportJobSource.Manual,
            warnings: ["1 clip is offline."]);

        (await WaitUntilAsync(() => Job(queue, submission.JobId).Status == ExportJobStatus.Completed)).ShouldBeTrue();
        Job(queue, submission.JobId).Warnings.ShouldBe(["1 clip is offline."]);
    }

    private static ExportJob Job(ExportQueue queue, Guid id) => queue.Jobs.First(j => j.Id == id);

    private static ExportRequest Request(string filename, string dir, ExportDestinationKind destination = ExportDestinationKind.Video)
    {
        var timeline = new Timeline();
        var project = new ProjectFile([timeline]);
        var resolver = new MediaResolver(() => new MediaManifest(), () => null);
        return new ExportRequest(
            destination, project, timeline.Id, resolver, Path.Combine(dir, filename),
            Codec: destination == ExportDestinationKind.Video ? ExportVideoCodec.H264 : null,
            Resolution: destination == ExportDestinationKind.Video ? ExportResolution.MatchTimeline : null,
            FcpxmlVersion: destination == ExportDestinationKind.Fcpxml ? FcpxmlVersionExtensions.Default : null,
            FcpxmlTarget: destination == ExportDestinationKind.Fcpxml ? FcpxmlTargetExtensions.Default : null,
            Manifest: destination == ExportDestinationKind.PalmierProject ? new MediaManifest() : null,
            GenerationLog: destination == ExportDestinationKind.PalmierProject ? new GenerationLog() : null);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return condition();
            }
            await Task.Delay(10);
        }
        return true;
    }
}

/// A test double standing in for the not-yet-built `VideoExportService`/routing implementation
/// (docs/export-v1.md §14 item 4, out of this slice) — lets `ExportQueueTests` drive the pump/
/// cancel/status-derivation logic in isolation from the real native and XML-exporter call chains.
internal sealed class FakeExportService(
    Func<ExportRequest, IProgress<ExportProgress>?, CancellationToken, Task<ExportResult>> handler) : IExportService
{
    public Task<ExportResult> ExportAsync(ExportRequest request, IProgress<ExportProgress>? progress = null, CancellationToken ct = default) =>
        handler(request, progress, ct);
}
