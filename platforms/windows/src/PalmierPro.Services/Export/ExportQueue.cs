using System.Threading.Channels;

namespace PalmierPro.Services.Export;

/// Concrete <see cref="IExportQueue"/> — a `Channel&lt;QueuedJob&gt;`-backed single-worker pump
/// (docs/export-v1.md §11). One background loop (<see cref="PumpAsync"/>) is the only caller of
/// <see cref="IExportService.ExportAsync"/>; every other member just mutates <see cref="_all"/>/
/// <see cref="_byId"/> under <see cref="_lock"/> and, for a new job, writes to the channel.
///
/// The one piece of state the channel's own FIFO ordering can't give for free is <see cref="Enqueue"/>'s
/// synchronous <c>Started</c> result (docs/export-v1.md §11 / `ExportQueueSubmission`) — a caller must
/// know INSIDE the same call whether its job just became active, matching the Mac's non-async
/// `enqueue(...)` returning that fact synchronously (`Export/ExportQueue.swift:216-224`, `startNext()`
/// called before returning). So <see cref="Activate"/> — "claim this job as the active one" — runs
/// both here (when the queue was idle) and inside <see cref="PumpAsync"/> (when a job dequeued still
/// waiting is the next one up); either way, at most one job is ever claimed at a time, since both
/// call sites hold <see cref="_lock"/> and check <see cref="_activeId"/> first.
public sealed class ExportQueue : IExportQueue
{
    private readonly IExportService _service;
    private readonly Channel<QueuedJob> _channel = Channel.CreateUnbounded<QueuedJob>();
    private readonly List<QueuedJob> _all = [];
    private readonly Dictionary<Guid, QueuedJob> _byId = [];
    private readonly object _lock = new();
    private Guid? _activeId;

    public ExportQueue(IExportService service)
    {
        _service = service;
        _ = Task.Run(PumpAsync);
    }

    public IReadOnlyList<ExportJob> Jobs
    {
        get { lock (_lock) return _all.Select(q => q.Job).ToList(); }
    }

    /// Mirrors `ExportQueue.hasActivity`.
    public bool HasActivity
    {
        get { lock (_lock) return _all.Any(q => q.Job.Status.IsPending()); }
    }

    /// Mirrors `ExportQueue.isExportActive`.
    public bool IsExportActive
    {
        get { lock (_lock) return _activeId is not null; }
    }

    public IReadOnlyList<ExportJob> JobsFor(string projectId)
    {
        lock (_lock) return _all.Where(q => q.Job.ProjectId == projectId).Select(q => q.Job).ToList();
    }

    public bool IsDestinationReserved(string outputPath)
    {
        lock (_lock) return IsDestinationReservedLocked(outputPath);
    }

    public async Task WaitWhileExportActiveAsync(CancellationToken ct = default)
    {
        while (IsExportActive)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
    }

    public ExportQueueSubmission Enqueue(
        ExportRequest request, string projectId, ExportJobSource source, IReadOnlyList<string>? warnings = null)
    {
        var filename = Path.GetFileName(request.OutputPath);
        QueuedJob queued;
        bool started;
        int queuePosition;
        lock (_lock)
        {
            if (IsDestinationReservedLocked(request.OutputPath))
            {
                throw new ExportDestinationInUseException(filename);
            }

            var job = new ExportJob
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Filename = filename,
                Source = source,
                OutputPath = request.OutputPath,
                CreatedAt = DateTimeOffset.Now,
            };
            job.Warnings.AddRange(warnings ?? []);

            queued = new QueuedJob(job, request);
            _all.Add(queued);
            _byId.Add(job.Id, queued);

            started = _activeId is null;
            if (started)
            {
                Activate(queued);
            }
            queuePosition = WaitingPositionLocked(job.Id);
        }

        _channel.Writer.TryWrite(queued);
        return new ExportQueueSubmission(queued.Job.Id, started, queuePosition);
    }

    /// Mirrors `ExportQueue.cancel(_:)`. A `.waiting` job is marked `.canceled` immediately; the
    /// still-queued <see cref="QueuedJob"/> is skipped, not run, when <see cref="PumpAsync"/> reaches
    /// it (docs/export-v1.md §11). A `.preparing`/`.exporting` job moves to `.canceling` and its
    /// token is cancelled — <see cref="PumpAsync"/>/the in-flight `ExportAsync` call resolve the rest.
    public bool Cancel(Guid id)
    {
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var queued))
            {
                return false;
            }
            switch (queued.Job.Status)
            {
                case ExportJobStatus.Waiting:
                    queued.Job.Status = ExportJobStatus.Canceled;
                    queued.Cts.Cancel();
                    return true;
                case ExportJobStatus.Preparing or ExportJobStatus.Exporting:
                    queued.Job.Status = ExportJobStatus.Canceling;
                    queued.Cts.Cancel();
                    return true;
                case ExportJobStatus.Canceling:
                    return true;
                default:
                    return false;
            }
        }
    }

    public void Remove(Guid id)
    {
        lock (_lock)
        {
            if (_byId.TryGetValue(id, out var queued) && queued.Job.Status.IsFinished())
            {
                _all.Remove(queued);
                _byId.Remove(id);
            }
        }
    }

    public void ClearFinished(string projectId)
    {
        lock (_lock)
        {
            var finished = _all.Where(q => q.Job.ProjectId == projectId && q.Job.Status.IsFinished()).ToList();
            foreach (var queued in finished)
            {
                _all.Remove(queued);
                _byId.Remove(queued.Job.Id);
            }
        }
    }

    private bool IsDestinationReservedLocked(string outputPath)
    {
        var standardized = Standardize(outputPath);
        return _all.Any(q => q.Job.Status.IsPending() &&
            string.Equals(Standardize(q.Job.OutputPath), standardized, StringComparison.OrdinalIgnoreCase));
    }

    /// 1-based position among `.waiting` jobs, in enqueue order; 0 if `id` isn't currently waiting
    /// (already active, or unknown) — mirrors `waiting.firstIndex(where:).map { $0 + 1 } ?? 0`.
    private int WaitingPositionLocked(Guid id)
    {
        var position = 0;
        foreach (var queued in _all)
        {
            if (queued.Job.Status != ExportJobStatus.Waiting)
            {
                continue;
            }
            position += 1;
            if (queued.Job.Id == id)
            {
                return position;
            }
        }
        return 0;
    }

    /// Windows paths compare case-insensitively; mirrors the Mac's `standardizedFileURL.path`
    /// equality check with `Path.GetFullPath` + ordinal-ignore-case in place of URL standardization.
    private static string Standardize(string path) => Path.GetFullPath(path);

    /// Caller must hold <see cref="_lock"/>.
    private void Activate(QueuedJob queued)
    {
        queued.Job.Status = ExportJobStatus.Preparing;
        _activeId = queued.Job.Id;
    }

    /// The single dedicated reader — the only place `IExportService.ExportAsync` is ever called.
    /// Every enqueued job passes through here even when <see cref="Enqueue"/> already activated it
    /// synchronously; a job still `.waiting` when it reaches the front of the channel is claimed
    /// here instead (a job enqueued while another was active). A job that is neither is one that got
    /// cancelled while still waiting — skipped without ever calling the service.
    private async Task PumpAsync()
    {
        await foreach (var queued in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            bool shouldRun;
            lock (_lock)
            {
                if (queued.Job.Status == ExportJobStatus.Waiting)
                {
                    Activate(queued);
                }
                shouldRun = _activeId == queued.Job.Id;
            }

            if (!shouldRun)
            {
                queued.Cts.Dispose();
                continue;
            }

            await RunAsync(queued).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(QueuedJob queued)
    {
        var progress = new Progress<ExportProgress>(report => OnProgress(queued.Job, report));
        ExportResult? result = null;
        Exception? failure = null;
        var canceled = false;
        try
        {
            queued.Cts.Token.ThrowIfCancellationRequested();
            result = await _service.ExportAsync(queued.Request, progress, queued.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        Finish(queued, result, failure, canceled);
    }

    /// Mirrors `ExportQueue.update(_ phase:)`/`update(_ progress:)` collapsed into one handler since
    /// <see cref="ExportProgress"/> bundles both (docs/export-v1.md §11): a progress report racing a
    /// just-issued cancel must not un-set `.canceling`, but the fraction still applies.
    private void OnProgress(ExportJob job, ExportProgress report)
    {
        lock (_lock)
        {
            if (!job.Status.IsPending())
            {
                return;
            }
            if (job.Status != ExportJobStatus.Canceling)
            {
                job.Status = report.Phase == ExportPhase.Preparing ? ExportJobStatus.Preparing : ExportJobStatus.Exporting;
            }
            job.Progress = Math.Clamp(report.Fraction, 0, 1);
        }
    }

    private void Finish(QueuedJob queued, ExportResult? result, Exception? failure, bool canceled)
    {
        var job = queued.Job;
        lock (_lock)
        {
            if (canceled)
            {
                job.Status = ExportJobStatus.Canceled;
            }
            else if (failure is not null)
            {
                job.Status = ExportJobStatus.Failed;
                job.Error = failure.Message;
            }
            else
            {
                job.Status = ExportJobStatus.Completed;
                job.Progress = 1;
                ApplyPalmierWarnings(job, queued.Request, result);
            }

            queued.Cts.Dispose();
            if (_activeId == job.Id)
            {
                _activeId = null;
            }
        }
    }

    /// Mirrors `ExportQueue.finish(_:status:service:)`'s `service.lastPalmierReport` overwrite
    /// (`Export/ExportQueue.swift:282-285`) — only the PalmierProject destination's warnings are
    /// ever replaced post-run; every other destination's `Warnings` stays exactly what the caller
    /// passed to <see cref="Enqueue"/>.
    private static void ApplyPalmierWarnings(ExportJob job, ExportRequest request, ExportResult? result)
    {
        if (request.Destination != ExportDestinationKind.PalmierProject || result is not { PalmierMissingCount: > 0 } r)
        {
            return;
        }
        job.Warnings.Clear();
        var files = r.PalmierMissingCount == 1 ? "media file was" : "media files were";
        job.Warnings.Add($"{r.PalmierMissingCount} {files} missing and could not be included.");
    }
}
