using PalmierPro.Core.Models;

namespace PalmierPro.Services.Export;

/// C# mirror of native `PE_ExportPhase` (`palmier_engine.h`) — and, one-to-one, of the Mac
/// `ExportService.Phase` enum (`Export/ExportService.swift`), which likewise has only these two
/// cases. See docs/export-v1.md §4.3/§12 for how <see cref="ExportJobStatus"/>'s richer set of
/// states (IExportQueue.cs) is derived purely from this one split, exactly as the Mac's
/// `ExportQueue.update(_ phase:)` already derives its own.
public enum ExportPhase
{
    Preparing,
    Exporting,
}

/// One `IProgress&lt;T&gt;` report from <see cref="IExportService.ExportAsync"/> — bundles phase +
/// fraction into a single value because `IProgress&lt;T&gt;` only ever reports one `T`
/// (docs/export-v1.md §4.3). `Fraction` is meaningful only once <c>Phase == Exporting</c>; it is
/// always `0` during <see cref="ExportPhase.Preparing"/> (matches the Mac's `ExportService.reset()`
/// zeroing `progress` before setting phase).
public readonly record struct ExportProgress(ExportPhase Phase, double Fraction);

/// Union of every field any <see cref="ExportDestinationKind"/> needs, mirroring the Mac's own
/// spread across `ExportQueue.enqueueVideo`/`enqueuePalmierProject`'s parameter lists
/// (`Export/ExportQueue.swift`) collapsed into one request shape (docs/export-v1.md §10) — the
/// concrete `IExportService` implementation reads only the subset relevant to
/// <see cref="Destination"/>, exactly as the Mac's `ExportService.export()` branches internally on
/// `format` before touching format-specific state.
///
/// `Project`/`TimelineId`/`Resolver` are the common substrate every destination is built from —
/// this is deliberately `ProjectFile` + a timeline id, NOT a pre-selected `Timeline` object, because
/// `TimelineSnapshotBuilder.Build` (video destination) and `FcpxmlExporter.ExportAsync`/
/// `XmemlExporter.ExportAsync`'s `resolveTimeline` closure (XML destinations) both need to walk
/// OTHER timelines in the same project too (nested `.sequence` resolution) — a bare `Timeline`
/// can't supply that, but `ProjectFile.Timelines` trivially can.
public sealed record ExportRequest(
    ExportDestinationKind Destination,
    ProjectFile Project,
    string TimelineId,
    MediaResolver Resolver,
    string OutputPath,

    // Video destination only (docs/export-v1.md §4). Required iff Destination == Video.
    ExportVideoCodec? Codec = null,
    ExportResolution? Resolution = null,

    // Timeline destinations only (Fcpxml/Xmeml). Required iff Destination is one of those two.
    FcpxmlVersion? FcpxmlVersion = null,
    FcpxmlTarget? FcpxmlTarget = null,

    // PalmierProject destination only. Required iff Destination == PalmierProject.
    MediaManifest? Manifest = null,
    GenerationLog? GenerationLog = null,
    string? SourceProjectPath = null,

    /// Media refs already known unresolvable before this request was built (mirrors the Mac's
    /// `missingMediaRefs` parameter — an editor-session-level cache of prior resolution failures,
    /// not something this request computes itself).
    IReadOnlySet<string>? MissingMediaRefs = null);

/// Mirrors `ExportRunReport` (video) unioned with `PalmierProjectExporter.Report`'s warning-relevant
/// fields (`Export/ExportService.swift`, `Export/PalmierProjectExporter.swift`) into one result
/// shape, since <see cref="IExportService.ExportAsync"/> is one unified entry point across every
/// destination — a field only <see cref="ExportRequest.Destination"/>-relevant fields are non-null.
///
/// `EncoderUsed`/`UsedHardwareEncoder` come straight from native `PE_ExportResult` (video only) —
/// diagnostics/telemetry, never branched on (docs/export-v1.md §6).
public sealed record ExportResult(
    int? OutputWidth = null,
    int? OutputHeight = null,
    IReadOnlySet<string>? OfflineMediaRefs = null,
    IReadOnlySet<string>? UnprocessableMediaRefs = null,
    string? EncoderUsed = null,
    bool? UsedHardwareEncoder = null,
    int? PalmierCollectedCount = null,
    int? PalmierMissingCount = null,
    long? PalmierTotalBytesCopied = null);

/// The C# mirror of the Mac's `ExportService.export()`/`exportPalmierProject()` pair
/// (`Export/ExportService.swift`), unified into one entry point across the whole export matrix
/// (docs/export-v1.md §3/§10). One call handles all four <see cref="ExportDestinationKind"/> values —
/// the concrete implementation routes internally, exactly as the Mac's `export()` branches on
/// `format` before doing anything format-specific.
///
/// Unlike the Mac (which never throws — `error`/`wasCancelled` are polled properties on
/// `ExportService` after the call returns), this follows the idiomatic C# convention every other
/// long-running native-backed call in this codebase already uses (e.g. `EngineSession.BakeLottieVideo`):
/// cancellation throws <see cref="OperationCanceledException"/>, failure throws (an export-specific
/// exception), success returns an <see cref="ExportResult"/>. <see cref="IExportQueue"/>'s
/// implementation (IExportQueue.cs) is what maps that exception-or-result shape onto the Mac's own
/// richer <see cref="ExportJobStatus"/> set — the same re-derivation the Mac's `ExportQueue.run(_:)`
/// already performs from `ExportService`'s post-call state.
///
/// Postcondition (docs/export-v1.md §9): on success, `request.OutputPath` exists and is complete;
/// on ANY exception (including cancellation), `request.OutputPath` does not exist at all — for the
/// <see cref="ExportDestinationKind.Video"/> and <see cref="ExportDestinationKind.PalmierProject"/>
/// destinations this is a native/already-landed guarantee (native PE_ExportStart's own temp-file
/// discipline; `PalmierProjectExporter.Export`'s own staging directory) that this interface's
/// implementation must NOT weaken; for Fcpxml/Xmeml it is a gap the implementation must close itself
/// (docs/export-v1.md §9 — `FcpxmlExporter.ExportAsync`/`XmemlExporter.ExportAsync` write directly
/// today, with no staging of their own).
public interface IExportService
{
    Task<ExportResult> ExportAsync(ExportRequest request, IProgress<ExportProgress>? progress = null, CancellationToken ct = default);
}
