using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PalmierPro.Core;
using PalmierPro.Core.Models;
using PalmierPro.Services.Export;
using PalmierPro.Services.Project;

namespace PalmierPro.App.ViewModels.Export;

/// Backs ExportView: destination/codec/resolution/FCPXML pickers, the Export/Add-to-Queue submit
/// flow, and the queue-log row list. Ports `Export/ExportView.swift`'s `@State` bag plus the
/// pieces of `EditorViewModel` it reads (`timelines`, `activeTimelineId`, `mediaManifest`,
/// `exportQueueProjectID`) — collapsed onto <see cref="ProjectDocument"/> directly since Phase 1
/// has exactly one open document per window. WinUI-free so option matrices/enablement/row
/// projection/cancel routing are testable under plain `dotnet test`; the picker UI lives behind
/// <see cref="IExportOutputDialogService"/>.
public sealed partial class ExportViewModel : ObservableObject
{
    private readonly ProjectDocument _document;
    private readonly IExportQueue _exportQueue;
    private readonly IExportOutputDialogService _dialogs;

    [ObservableProperty]
    public partial ExportDestination Destination { get; set; } = ExportDestination.Video;

    [ObservableProperty]
    public partial string? SelectedTimelineId { get; set; }

    [ObservableProperty]
    public partial TimelineExportFormat TimelineFormat { get; set; } = TimelineExportFormat.Fcpxml;

    [ObservableProperty]
    public partial FcpxmlVersion FcpxmlVersion { get; set; } = FcpxmlVersionExtensions.Default;

    [ObservableProperty]
    public partial FcpxmlTarget FcpxmlTarget { get; set; } = FcpxmlTargetExtensions.Default;

    [ObservableProperty]
    public partial ExportVideoCodec Codec { get; set; } = ExportVideoCodec.H264;

    [ObservableProperty]
    public partial ExportResolution Resolution { get; set; } = ExportResolution.MatchTimeline;

    [ObservableProperty]
    public partial string? SubmissionError { get; set; }

    public int PalmierCollectCount { get; private set; }
    public int PalmierMissingCount { get; private set; }
    public long PalmierBytes { get; private set; }

    public IReadOnlyList<ExportJobRowViewModel> Rows { get; private set; } = [];
    public int PendingCount { get; private set; }
    public bool HasFinishedJobs { get; private set; }

    /// Raised when the "Close" button fires — the View closes its host ContentDialog.
    public event EventHandler? CloseRequested;

    /// Raised by a Completed row's "Reveal" action, carrying the output path — the View owns
    /// actually launching Explorer (WinUI-free ViewModel).
    public event EventHandler<string>? RevealRequested;

    public ExportViewModel(ProjectDocument document, IExportQueue exportQueue, IExportOutputDialogService dialogs)
    {
        _document = document;
        _exportQueue = exportQueue;
        _dialogs = dialogs;
        SelectedTimelineId = document.ProjectFile.ActiveTimelineId;
        ComputePalmierSummary();
        RefreshJobs();
    }

    // MARK: - Option matrices (per destination — see ExportViewModelTests)

    public IReadOnlyList<ExportDestination> Destinations => Enum.GetValues<ExportDestination>();
    public IReadOnlyList<ExportVideoCodec> Codecs => Enum.GetValues<ExportVideoCodec>();
    public IReadOnlyList<ExportResolution> Resolutions => Enum.GetValues<ExportResolution>();
    public IReadOnlyList<TimelineExportFormat> TimelineFormats => Enum.GetValues<TimelineExportFormat>();
    public IReadOnlyList<FcpxmlVersion> FcpxmlVersions => Enum.GetValues<FcpxmlVersion>();
    public IReadOnlyList<FcpxmlTarget> FcpxmlTargets => Enum.GetValues<FcpxmlTarget>();

    // MARK: - Derived state

    public IReadOnlyList<Timeline> Timelines => _document.ProjectFile.Timelines;

    /// Mirrors `editor.timelines.count > 1, destination != .palmierProject` (`Export/ExportView.swift`).
    public bool ShowsTimelinePicker => Timelines.Count > 1 && Destination != ExportDestination.PalmierProject;

    public Timeline ExportTimeline =>
        (SelectedTimelineId is { } id ? Timelines.FirstOrDefault(t => t.Id == id) : null) ?? Timelines[0];

    public string DurationText => TimeFormatting.FormatTimecode(ExportTimeline.TotalFrames, ExportTimeline.Fps);

    public string FrameRateText => $"{ExportTimeline.Fps} fps";

    public string ContainerLabel => Codec.ContainerLabel();

    public string ActionButtonText => _exportQueue.HasActivity ? "Add to Queue" : "Export";

    public string? PalmierMissingWarningText => PalmierMissingCount > 0
        ? $"{PalmierMissingCount} media file{(PalmierMissingCount == 1 ? "" : "s")} missing - they'll be skipped."
        : null;

    /// Bottom-bar summary strings after the always-shown duration — mirrors `exportSummary`'s
    /// per-destination switch (`Export/ExportView.swift`).
    public IReadOnlyList<string> SummaryParts => Destination switch
    {
        ExportDestination.Video => BuildVideoSummaryParts(),
        ExportDestination.Timeline => BuildTimelineSummaryParts(),
        ExportDestination.PalmierProject => BuildPalmierSummaryParts(),
        _ => throw new ArgumentOutOfRangeException(),
    };

    private IReadOnlyList<string> BuildVideoSummaryParts()
    {
        var timeline = ExportTimeline;
        var (w, h) = Resolution.RenderSize(timeline.Width, timeline.Height);
        return [$"~{EstimatedFileSizeText()}", $"{w}×{h}", Codec.ContainerLabel()];
    }

    private IReadOnlyList<string> BuildTimelineSummaryParts()
    {
        var timeline = ExportTimeline;
        return [$"{timeline.Width}×{timeline.Height}", TimelineFormat.ExtensionLabel()];
    }

    private IReadOnlyList<string> BuildPalmierSummaryParts() =>
        [$"~{FormatFileSize(PalmierBytes)}", $".{ProjectPackage.FileExtension}"];

    private string EstimatedFileSizeText()
    {
        var timeline = ExportTimeline;
        var seconds = (double)timeline.TotalFrames / Math.Max(1, timeline.Fps);
        var (w, h) = Resolution.RenderSize(timeline.Width, timeline.Height);
        var megapixels = (double)w * h / 1_000_000;
        var bytesPerSec = Codec.EstimatedBytesPerSecondPerMegapixel() * Math.Max(0.1, megapixels);
        return FormatFileSize((long)(bytesPerSec * seconds));
    }

    /// Decimal (1000-based) byte formatter — mirrors `ByteCountFormatter.string(countStyle: .file)`.
    private static string FormatFileSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1000 && unitIndex < units.Length - 1)
        {
            value /= 1000;
            unitIndex++;
        }
        return unitIndex == 0 ? $"{value:0} {units[0]}" : $"{value:0.#} {units[unitIndex]}";
    }

    /// Mirrors `computePalmierSummary()` (`Export/ExportView.swift`) — a quick estimate shown
    /// before the user commits to exporting a Palmier Project.
    private void ComputePalmierSummary()
    {
        int collect = 0, missing = 0;
        long bytes = 0;
        foreach (var entry in _document.Manifest.Entries)
        {
            var path = entry.Source.Kind == MediaSourceKind.External
                ? entry.Source.Path
                : Path.Combine(_document.PackagePath, entry.Source.Path);
            if (!File.Exists(path))
            {
                missing++;
                continue;
            }
            if (entry.Source.Kind == MediaSourceKind.External)
            {
                collect++;
            }
            try
            {
                bytes += new FileInfo(path).Length;
            }
            catch (IOException)
            {
                // Race with an external deletion between the Exists check and this — leave uncounted.
            }
        }
        PalmierCollectCount = collect;
        PalmierMissingCount = missing;
        PalmierBytes = bytes;
    }

    // MARK: - Queue log

    /// Rebuilds <see cref="Rows"/> from the live queue — call after any enqueue/cancel/remove and
    /// on the View's poll tick, since `ExportJob` (Services.Export) carries no change notification.
    public void RefreshJobs()
    {
        var jobs = _exportQueue.JobsFor(_document.PackagePath);
        PendingCount = jobs.Count(j => j.Status.IsPending());
        HasFinishedJobs = jobs.Any(j => j.Status.IsFinished());
        Rows = jobs.Reverse().Select(ExportJobRowViewModel.From).ToList();
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HasFinishedJobs));
        OnPropertyChanged(nameof(ActionButtonText));
        ClearFinishedCommand.NotifyCanExecuteChanged();
    }

    /// Routes a row's trailing icon-button — mirrors `exportAction(_:)`'s per-status command
    /// dispatch. `Waiting` (`CancelExport`) and `Preparing`/`Exporting` (`StopExport`) rows both
    /// cancel (see <see cref="ExportJobRowAction"/>'s doc comment); `Failed`/`Canceled` rows remove.
    public void InvokeRowAction(ExportJobRowViewModel row)
    {
        switch (row.Action)
        {
            case ExportJobRowAction.CancelExport:
            case ExportJobRowAction.StopExport:
                _exportQueue.Cancel(row.Id);
                break;
            case ExportJobRowAction.Dismiss:
                _exportQueue.Remove(row.Id);
                break;
            case ExportJobRowAction.Reveal:
                RevealRequested?.Invoke(this, row.OutputPath);
                break;
            case ExportJobRowAction.None:
                break;
        }
        RefreshJobs();
    }

    private bool CanClearFinished() => HasFinishedJobs;

    [RelayCommand(CanExecute = nameof(CanClearFinished))]
    private void ClearFinished()
    {
        _exportQueue.ClearFinished(_document.PackagePath);
        RefreshJobs();
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    // MARK: - Submit

    [RelayCommand]
    private async Task StartExportAsync()
    {
        if (Destination == ExportDestination.PalmierProject)
        {
            await StartPalmierExportAsync().ConfigureAwait(true);
            return;
        }

        SubmissionError = null;
        var timeline = ExportTimeline;
        var (destinationKind, extension, typeDescription) = Destination switch
        {
            ExportDestination.Video => (ExportDestinationKind.Video, Codec.ContainerFor().FileExtension(), Codec.ContainerLabel()),
            ExportDestination.Timeline => (TimelineFormat.DestinationKind(), TimelineFormat.FileExtension(), TimelineFormat.DisplayName()),
            _ => throw new ArgumentOutOfRangeException(),
        };

        var path = await _dialogs.PickSaveFileAsync($"{timeline.Name}.{extension}", extension, typeDescription).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        var resolver = new MediaResolver(() => _document.Manifest, () => _document.PackagePath);
        var missing = MediaResolver.MissingAssetIds(_document.Manifest.Entries, _document.PackagePath);
        var request = new ExportRequest(
            destinationKind, _document.ProjectFile, timeline.Id, resolver, path,
            Codec: Destination == ExportDestination.Video ? Codec : null,
            Resolution: Destination == ExportDestination.Video ? Resolution : null,
            FcpxmlVersion: destinationKind == ExportDestinationKind.Fcpxml ? FcpxmlVersion : null,
            FcpxmlTarget: destinationKind == ExportDestinationKind.Fcpxml ? FcpxmlTarget : null,
            MissingMediaRefs: missing);

        Submit(request);
    }

    private async Task StartPalmierExportAsync()
    {
        SubmissionError = null;
        var baseName = Path.GetFileNameWithoutExtension(_document.PackagePath);
        var location = await _dialogs.PickPalmierProjectLocationAsync(baseName).ConfigureAwait(true);
        if (location is null)
        {
            return;
        }

        var outputPath = ProjectPackage.PackagePath(location.Value.Directory, location.Value.Name);
        var resolver = new MediaResolver(() => _document.Manifest, () => _document.PackagePath);
        var request = new ExportRequest(
            ExportDestinationKind.PalmierProject, _document.ProjectFile, ExportTimeline.Id, resolver, outputPath,
            Manifest: _document.Manifest, GenerationLog: _document.GenerationLog, SourceProjectPath: _document.PackagePath);

        Submit(request);
    }

    private void Submit(ExportRequest request)
    {
        try
        {
            _exportQueue.Enqueue(request, _document.PackagePath, ExportJobSource.Manual);
        }
        catch (ExportDestinationInUseException ex)
        {
            SubmissionError = ex.Message;
        }
        RefreshJobs();
    }
}
