namespace PalmierPro.Services.Export;

/// Queue-internal pairing of a public <see cref="ExportJob"/> (IExportQueue.cs) with the
/// <see cref="ExportRequest"/> and per-job <see cref="CancellationTokenSource"/> <see cref="ExportQueue"/>'s
/// worker loop needs to actually call <see cref="IExportService.ExportAsync"/> — kept out of the
/// public <see cref="ExportJob"/> shape since no caller outside the queue ever sees this. Mirrors
/// the Mac's private `operations`/`activeService` dictionaries (`Export/ExportQueue.swift:72,75`),
/// collapsed into one object per job instead of two parallel maps.
internal sealed class QueuedJob(ExportJob job, ExportRequest request)
{
    public ExportJob Job { get; } = job;
    public ExportRequest Request { get; } = request;

    /// Passed to `ExportAsync` once this job actually runs. Cancelling it before that call is made
    /// is what lets <see cref="ExportQueue"/> stop a `Preparing` job without ever invoking the
    /// service — <see cref="ExportQueue"/> checks <see cref="CancellationToken.IsCancellationRequested"/>
    /// immediately before the call.
    public CancellationTokenSource Cts { get; } = new();
}
