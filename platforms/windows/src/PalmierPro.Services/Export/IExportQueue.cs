namespace PalmierPro.Services.Export;

/// Ports `ExportJobStatus: String` (`Export/ExportQueue.swift`) case-for-case, INCLUDING its raw
/// string values — <see cref="ExportJobStatusExtensions.RawValue"/> reproduces every one exactly
/// (`waiting` -> `"queued"`, `exporting` -> `"rendering"`, the rest identical to the C# case name
/// lowercased). Anything that serializes this status for logging/telemetry/UI automation text
/// (mirroring `PALMIER_AUTO_*`-style scripted assertions) MUST go through `RawValue()`, never
/// `ToString()`/`nameof()` — `Waiting`/`Exporting` are the two cases where those would silently
/// diverge from the Mac's wire value.
public enum ExportJobStatus
{
    Waiting,
    Preparing,
    Exporting,
    Canceling,
    Completed,
    Failed,
    Canceled,
}

public static class ExportJobStatusExtensions
{
    /// Exact match to `ExportJobStatus.rawValue` (`Export/ExportQueue.swift:8-15`).
    public static string RawValue(this ExportJobStatus status) => status switch
    {
        ExportJobStatus.Waiting => "queued",
        ExportJobStatus.Preparing => "preparing",
        ExportJobStatus.Exporting => "rendering",
        ExportJobStatus.Canceling => "canceling",
        ExportJobStatus.Completed => "completed",
        ExportJobStatus.Failed => "failed",
        ExportJobStatus.Canceled => "canceled",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// Mirrors `ExportJobStatus.isRunning`.
    public static bool IsRunning(this ExportJobStatus status) =>
        status is ExportJobStatus.Preparing or ExportJobStatus.Exporting or ExportJobStatus.Canceling;

    /// Mirrors `ExportJobStatus.isFinished`.
    public static bool IsFinished(this ExportJobStatus status) =>
        status is ExportJobStatus.Completed or ExportJobStatus.Failed or ExportJobStatus.Canceled;

    /// Mirrors `ExportJobStatus.isPending`.
    public static bool IsPending(this ExportJobStatus status) => status == ExportJobStatus.Waiting || status.IsRunning();
}

/// Mirrors `ExportJob` (`Export/ExportQueue.swift`) — one queued/running/finished export. `Status`/
/// `Progress`/`Error`/`Warnings` are mutated in place by the owning <see cref="IExportQueue"/>
/// implementation as the underlying <see cref="IExportService.ExportAsync"/> call reports progress
/// and completes/throws (docs/export-v1.md §11) — never by any other caller.
public sealed class ExportJob
{
    public required Guid Id { get; init; }
    public required string ProjectId { get; init; }
    public required string Filename { get; init; }
    public required ExportJobSource Source { get; init; }
    public required string OutputPath { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public ExportJobStatus Status { get; set; } = ExportJobStatus.Waiting;
    public double Progress { get; set; }
    public string? Error { get; set; }
    public List<string> Warnings { get; init; } = [];
}

/// Mirrors `ExportQueueSubmission` (`Export/ExportQueue.swift`) — returned synchronously from
/// <see cref="IExportQueue.Enqueue"/> so a caller can show "started" vs. "queued, position N" UI
/// immediately, without waiting on the job itself.
public sealed record ExportQueueSubmission(Guid JobId, bool Started, int QueuePosition);

/// Mirrors `ExportQueueError.destinationInUse` (`Export/ExportQueue.swift`) — thrown by
/// <see cref="IExportQueue.Enqueue"/> when <see cref="ExportRequest.OutputPath"/> is already
/// reserved by another waiting-or-running job (docs/export-v1.md §11's destination-reservation
/// semantics).
public sealed class ExportDestinationInUseException(string filename)
    : Exception($"An export to {filename} is already waiting or in progress.")
{
    public string Filename { get; } = filename;
}

/// C# mirror of `ExportQueue` (`Export/ExportQueue.swift`) — a cross-project-global singleton on
/// the Mac (`ExportQueue.shared`); the Windows port keeps that scope (docs/export-v1.md §11).
/// Reimplemented as a `Channel&lt;ExportRequest&gt;`-backed single-worker pump (docs/export-v1.md
/// §11) rather than the Mac's `Task`-per-active-job field: unlike the Mac (Swift concurrency, one
/// `@MainActor`-isolated class), a `Channel` gives the single-worker invariant for free from the
/// channel's own single-reader discipline instead of the Mac's manual `activeID`/`activeTask`
/// bookkeeping — but the OBSERVABLE contract (one job's status is ever `Preparing`/`Exporting`/
/// `Canceling` at a time; every other pending job stays `Waiting`) is identical.
///
/// One request is enqueued per <see cref="ExportRequest.Destination"/> value the Mac's two
/// `enqueueVideo`/`enqueuePalmierProject` methods used to split across — see
/// <see cref="ExportRequest"/>'s remarks for why one `Enqueue` overload suffices here.
public interface IExportQueue
{
    IReadOnlyList<ExportJob> Jobs { get; }

    /// Mirrors `ExportQueue.hasActivity`.
    bool HasActivity { get; }

    /// Mirrors `ExportQueue.isExportActive`.
    bool IsExportActive { get; }

    IReadOnlyList<ExportJob> JobsFor(string projectId);

    /// Mirrors `ExportQueue.isDestinationReserved(_:)` — true while ANY waiting-or-running job
    /// targets `outputPath` (path-equality after standardization, matching the Mac's
    /// `standardizedFileURL.path` comparison).
    bool IsDestinationReserved(string outputPath);

    /// Mirrors `ExportQueue.waitWhileExportActive()`.
    Task WaitWhileExportActiveAsync(CancellationToken ct = default);

    /// Mirrors `ExportQueue.enqueueVideo`/`enqueuePalmierProject`'s shared shape, unified into one
    /// overload (see <see cref="ExportRequest"/>'s remarks). Throws
    /// <see cref="ExportDestinationInUseException"/> synchronously (before any job is created,
    /// matching the Mac's `throws` — not `async throws` — signature on the same check) if
    /// <see cref="ExportRequest.OutputPath"/> is already reserved.
    ExportQueueSubmission Enqueue(ExportRequest request, string projectId, ExportJobSource source, IReadOnlyList<string>? warnings = null);

    /// Mirrors `ExportQueue.cancel(_:)`. A waiting job is cancelled immediately (in-place, no
    /// `IExportService` call was ever made); a preparing/exporting job's cancellation is forwarded
    /// to the in-flight `ExportAsync` call's `CancellationToken` and the job moves to `Canceling`
    /// until that call actually unwinds. Returns false for an already-finished or unknown job id.
    bool Cancel(Guid id);

    /// Mirrors `ExportQueue.remove(_:)` — only removes a job whose <see cref="ExportJobStatus.IsFinished"/> is true.
    void Remove(Guid id);

    /// Mirrors `ExportQueue.clearFinished(for:)`.
    void ClearFinished(string projectId);
}
