using System.Globalization;
using PalmierPro.Services.Export;

namespace PalmierPro.App.ViewModels.Export;

/// Which glyph a queue-log row shows for `ExportJob.Status` — mirrors `exportStatusIcon(_:)`'s
/// switch (`Export/ExportView.swift`). `Spinner` covers both `Preparing` and `Canceling` (Mac uses
/// the same `ProgressView()` for both).
public enum ExportJobRowStatusIcon
{
    Waiting,
    Spinner,
    Exporting,
    Completed,
    Failed,
    Canceled,
}

/// The row's trailing icon-button, if any — mirrors `exportAction(_:)`'s switch. `CancelExport`
/// (`Waiting`, label "Remove from Queue", ✕ glyph) and `StopExport` (`Preparing`/`Exporting`,
/// label "Cancel Export", ▪ stop glyph) both route to `IExportQueue.Cancel` — the Mac's two
/// branches both call `exportQueue.cancel(job.id)` — but keep distinct glyphs/tooltips, matching
/// the Mac's split of `xmark` (Remove) from `stop.fill` (Cancel Export).
public enum ExportJobRowAction
{
    None,
    CancelExport,
    StopExport,
    Reveal,
    Dismiss,
}

/// Pure projection of one `ExportJob` into what a queue-log row displays — WinUI-free so
/// `ExportViewModel`'s row logic (icon/action per status, progress visibility) is testable under
/// plain `dotnet test`. Rebuilt fresh on every poll tick rather than kept in sync incrementally,
/// since `ExportJob` (Services.Export) carries no change notification of its own.
public sealed record ExportJobRowViewModel(
    Guid Id,
    string Filename,
    string Timestamp,
    string OutputPath,
    string? Tooltip,
    ExportJobRowStatusIcon StatusIcon,
    string StatusAccessibilityLabel,
    bool ShowsProgress,
    double Progress,
    string ProgressText,
    ExportJobRowAction Action,
    string ActionTooltip)
{
    public static ExportJobRowViewModel From(ExportJob job)
    {
        var (icon, action, actionTooltip) = job.Status switch
        {
            ExportJobStatus.Waiting => (ExportJobRowStatusIcon.Waiting, ExportJobRowAction.CancelExport, "Remove from Queue"),
            ExportJobStatus.Preparing => (ExportJobRowStatusIcon.Spinner, ExportJobRowAction.StopExport, "Cancel Export"),
            ExportJobStatus.Exporting => (ExportJobRowStatusIcon.Exporting, ExportJobRowAction.StopExport, "Cancel Export"),
            ExportJobStatus.Canceling => (ExportJobRowStatusIcon.Spinner, ExportJobRowAction.None, ""),
            ExportJobStatus.Completed => (ExportJobRowStatusIcon.Completed, ExportJobRowAction.Reveal, "Reveal in File Explorer"),
            ExportJobStatus.Failed => (ExportJobRowStatusIcon.Failed, ExportJobRowAction.Dismiss, "Dismiss"),
            ExportJobStatus.Canceled => (ExportJobRowStatusIcon.Canceled, ExportJobRowAction.Dismiss, "Dismiss"),
            _ => throw new ArgumentOutOfRangeException(nameof(job)),
        };

        var showsProgress = job.Status == ExportJobStatus.Exporting;
        return new ExportJobRowViewModel(
            Id: job.Id,
            Filename: job.Filename,
            Timestamp: job.CreatedAt.LocalDateTime.ToString("t", CultureInfo.CurrentCulture),
            OutputPath: job.OutputPath,
            Tooltip: job.Error ?? job.OutputPath,
            StatusIcon: icon,
            StatusAccessibilityLabel: CultureInfo.CurrentCulture.TextInfo.ToTitleCase(job.Status.RawValue()),
            ShowsProgress: showsProgress,
            Progress: job.Progress,
            ProgressText: showsProgress ? $"{(int)(job.Progress * 100)}%" : "",
            Action: action,
            ActionTooltip: actionTooltip);
    }
}
