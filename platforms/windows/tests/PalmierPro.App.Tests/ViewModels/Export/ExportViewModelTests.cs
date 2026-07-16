using PalmierPro.App.ViewModels.Export;
using PalmierPro.Services.Export;
using PalmierPro.Services.Project;
using Shouldly;
using Xunit;

namespace PalmierPro.App.Tests.ViewModels.Export;

/// Scriptable stand-in for IExportQueue — records every call so tests can assert routing without
/// a real background pump (mirrors FakeProjectDialogService's role for IProjectDialogService).
internal sealed class FakeExportQueue : IExportQueue
{
    public List<ExportJob> JobsList { get; } = [];
    public List<Guid> Cancelled { get; } = [];
    public List<Guid> Removed { get; } = [];
    public List<string> ClearedProjectIds { get; } = [];
    public ExportRequest? LastRequest { get; private set; }
    public string? LastProjectId { get; private set; }
    public bool HasActivity { get; set; }
    public bool IsExportActive => false;
    public Exception? ThrowOnEnqueue { get; set; }

    public IReadOnlyList<ExportJob> Jobs => JobsList;
    public IReadOnlyList<ExportJob> JobsFor(string projectId) => [.. JobsList.Where(j => j.ProjectId == projectId)];
    public bool IsDestinationReserved(string outputPath) => false;
    public Task WaitWhileExportActiveAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ExportQueueSubmission Enqueue(ExportRequest request, string projectId, ExportJobSource source, IReadOnlyList<string>? warnings = null)
    {
        LastRequest = request;
        LastProjectId = projectId;
        if (ThrowOnEnqueue is { } ex)
        {
            throw ex;
        }
        var job = new ExportJob
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Filename = Path.GetFileName(request.OutputPath),
            Source = source,
            OutputPath = request.OutputPath,
            CreatedAt = DateTimeOffset.Now,
        };
        JobsList.Add(job);
        return new ExportQueueSubmission(job.Id, true, 0);
    }

    public bool Cancel(Guid id)
    {
        Cancelled.Add(id);
        return true;
    }

    public void Remove(Guid id) => Removed.Add(id);

    public void ClearFinished(string projectId) => ClearedProjectIds.Add(projectId);
}

/// Scriptable stand-in for IExportOutputDialogService — same rationale as FakeExportQueue.
internal sealed class FakeExportOutputDialogService : IExportOutputDialogService
{
    public string? NextSavePath { get; set; }
    public (string Directory, string Name)? NextPalmierLocation { get; set; }

    public Task<string?> PickSaveFileAsync(string suggestedFileName, string extension, string fileTypeDescription) =>
        Task.FromResult(NextSavePath);

    public Task<(string Directory, string Name)?> PickPalmierProjectLocationAsync(string suggestedName) =>
        Task.FromResult(NextPalmierLocation);
}

public sealed class ExportViewModelTests
{
    private static async Task<ExportViewModel> MakeViewModelAsync(
        string tempDir, FakeExportQueue? queue = null, FakeExportOutputDialogService? dialogs = null)
    {
        var doc = await ProjectDocument.CreateNewAsync(tempDir, "Export Fixture");
        return new ExportViewModel(doc, queue ?? new FakeExportQueue(), dialogs ?? new FakeExportOutputDialogService());
    }

    // MARK: - Option matrices per destination

    [Fact]
    public async Task Codecs_excludes_HDR_and_matches_the_v1_matrix_exactly()
    {
        using var temp = new TempDirectory();
        var vm = await MakeViewModelAsync(temp.Path);

        vm.Codecs.ShouldBe([ExportVideoCodec.H264, ExportVideoCodec.H265, ExportVideoCodec.ProRes]);
    }

    [Fact]
    public async Task Resolutions_includes_MatchTimeline_alongside_the_four_fixed_presets()
    {
        using var temp = new TempDirectory();
        var vm = await MakeViewModelAsync(temp.Path);

        vm.Resolutions.ShouldBe([
            ExportResolution.R720p, ExportResolution.R1080p, ExportResolution.R1440p,
            ExportResolution.R4k, ExportResolution.MatchTimeline,
        ]);
    }

    [Fact]
    public async Task TimelineFormats_offers_exactly_Xmeml_and_Fcpxml()
    {
        using var temp = new TempDirectory();
        var vm = await MakeViewModelAsync(temp.Path);

        vm.TimelineFormats.ShouldBe([TimelineExportFormat.Xmeml, TimelineExportFormat.Fcpxml]);
    }

    [Fact]
    public async Task Destinations_offers_Video_Timeline_and_PalmierProject_in_that_order()
    {
        using var temp = new TempDirectory();
        var vm = await MakeViewModelAsync(temp.Path);

        vm.Destinations.ShouldBe([ExportDestination.Video, ExportDestination.Timeline, ExportDestination.PalmierProject]);
    }

    // MARK: - Enablement

    [Theory]
    [InlineData(ExportDestination.Video, true)]
    [InlineData(ExportDestination.Timeline, true)]
    [InlineData(ExportDestination.PalmierProject, false)]
    public async Task ShowsTimelinePicker_is_false_for_PalmierProject_even_with_multiple_timelines(ExportDestination destination, bool expected)
    {
        using var temp = new TempDirectory();
        var doc = await ProjectDocument.CreateNewAsync(temp.Path, "Multi Timeline");
        doc.ProjectFile.Timelines.Add(new PalmierPro.Core.Models.Timeline { Name = "Second" });
        var vm = new ExportViewModel(doc, new FakeExportQueue(), new FakeExportOutputDialogService()) { Destination = destination };

        vm.ShowsTimelinePicker.ShouldBe(expected);
    }

    [Fact]
    public async Task ShowsTimelinePicker_is_false_with_only_one_timeline_regardless_of_destination()
    {
        using var temp = new TempDirectory();
        var vm = await MakeViewModelAsync(temp.Path);
        vm.Destination = ExportDestination.Video;

        vm.ShowsTimelinePicker.ShouldBeFalse();
    }

    [Fact]
    public async Task ActionButtonText_reflects_whether_the_queue_already_has_activity()
    {
        using var temp = new TempDirectory();
        var queue = new FakeExportQueue { HasActivity = false };
        var vm = await MakeViewModelAsync(temp.Path, queue);
        vm.ActionButtonText.ShouldBe("Export");

        queue.HasActivity = true;
        vm.ActionButtonText.ShouldBe("Add to Queue");
    }

    [Fact]
    public async Task ClearFinishedCommand_tracks_HasFinishedJobs()
    {
        using var temp = new TempDirectory();
        var doc = await ProjectDocument.CreateNewAsync(temp.Path, "Clear Finished");
        var queue = new FakeExportQueue();
        var vm = new ExportViewModel(doc, queue, new FakeExportOutputDialogService());
        vm.ClearFinishedCommand.CanExecute(null).ShouldBeFalse();

        queue.JobsList.Add(new ExportJob
        {
            Id = Guid.NewGuid(), ProjectId = doc.PackagePath, Filename = "a.mp4",
            Source = ExportJobSource.Manual, OutputPath = "a.mp4", CreatedAt = DateTimeOffset.Now, Status = ExportJobStatus.Completed,
        });
        vm.RefreshJobs();

        vm.ClearFinishedCommand.CanExecute(null).ShouldBeTrue();
        vm.ClearFinishedCommand.Execute(null);
        queue.ClearedProjectIds.ShouldBe([doc.PackagePath]);
    }

    // MARK: - Job-row state projection (ExportJobRowViewModel.From — pure, no queue needed)

    [Theory]
    [InlineData(ExportJobStatus.Waiting, ExportJobRowStatusIcon.Waiting, ExportJobRowAction.CancelExport, false)]
    [InlineData(ExportJobStatus.Preparing, ExportJobRowStatusIcon.Spinner, ExportJobRowAction.StopExport, false)]
    [InlineData(ExportJobStatus.Exporting, ExportJobRowStatusIcon.Exporting, ExportJobRowAction.StopExport, true)]
    [InlineData(ExportJobStatus.Canceling, ExportJobRowStatusIcon.Spinner, ExportJobRowAction.None, false)]
    [InlineData(ExportJobStatus.Completed, ExportJobRowStatusIcon.Completed, ExportJobRowAction.Reveal, false)]
    [InlineData(ExportJobStatus.Failed, ExportJobRowStatusIcon.Failed, ExportJobRowAction.Dismiss, false)]
    [InlineData(ExportJobStatus.Canceled, ExportJobRowStatusIcon.Canceled, ExportJobRowAction.Dismiss, false)]
    public void From_projects_every_status_to_its_icon_action_and_progress_visibility(
        ExportJobStatus status, ExportJobRowStatusIcon expectedIcon, ExportJobRowAction expectedAction, bool expectedShowsProgress)
    {
        var job = new ExportJob
        {
            Id = Guid.NewGuid(), ProjectId = "p", Filename = "clip.mp4", Source = ExportJobSource.Manual,
            OutputPath = @"C:\out\clip.mp4", CreatedAt = DateTimeOffset.Now, Status = status, Progress = 0.42,
        };

        var row = ExportJobRowViewModel.From(job);

        row.StatusIcon.ShouldBe(expectedIcon);
        row.Action.ShouldBe(expectedAction);
        row.ShowsProgress.ShouldBe(expectedShowsProgress);
        if (expectedShowsProgress)
        {
            row.ProgressText.ShouldBe("42%");
        }
        else
        {
            row.ProgressText.ShouldBe("");
        }
    }

    [Fact]
    public void From_uses_the_error_message_as_tooltip_when_present_else_the_output_path()
    {
        var failed = new ExportJob
        {
            Id = Guid.NewGuid(), ProjectId = "p", Filename = "clip.mp4", Source = ExportJobSource.Manual,
            OutputPath = @"C:\out\clip.mp4", CreatedAt = DateTimeOffset.Now, Status = ExportJobStatus.Failed, Error = "disk full",
        };
        ExportJobRowViewModel.From(failed).Tooltip.ShouldBe("disk full");

        var waiting = new ExportJob
        {
            Id = Guid.NewGuid(), ProjectId = "p", Filename = "clip.mp4", Source = ExportJobSource.Manual,
            OutputPath = @"C:\out\clip.mp4", CreatedAt = DateTimeOffset.Now, Status = ExportJobStatus.Waiting,
        };
        ExportJobRowViewModel.From(waiting).Tooltip.ShouldBe(@"C:\out\clip.mp4");
    }

    /// A waiting row's trailing button removes it from the queue (✕), a running row's stops the
    /// export (■) — distinct actions/labels, so the control can render distinct glyphs, matching the
    /// Mac's `xmark` vs `stop.fill` split (Export/ExportView.swift). Both still route to Cancel.
    [Fact]
    public void From_gives_waiting_and_running_rows_distinct_cancel_affordances()
    {
        static ExportJob Job(ExportJobStatus status) => new()
        {
            Id = Guid.NewGuid(), ProjectId = "p", Filename = "clip.mp4", Source = ExportJobSource.Manual,
            OutputPath = @"C:\out\clip.mp4", CreatedAt = DateTimeOffset.Now, Status = status,
        };

        var waiting = ExportJobRowViewModel.From(Job(ExportJobStatus.Waiting));
        waiting.Action.ShouldBe(ExportJobRowAction.CancelExport);
        waiting.ActionTooltip.ShouldBe("Remove from Queue");

        foreach (var running in new[] { ExportJobStatus.Preparing, ExportJobStatus.Exporting })
        {
            var row = ExportJobRowViewModel.From(Job(running));
            row.Action.ShouldBe(ExportJobRowAction.StopExport);
            row.ActionTooltip.ShouldBe("Cancel Export");
        }
    }

    // MARK: - Cancel command routing (ExportViewModel.InvokeRowAction)

    [Theory]
    [InlineData(ExportJobRowAction.CancelExport)]
    [InlineData(ExportJobRowAction.StopExport)]
    public async Task InvokeRowAction_CancelExport_and_StopExport_both_route_to_queue_Cancel(ExportJobRowAction action)
    {
        using var temp = new TempDirectory();
        var queue = new FakeExportQueue();
        var vm = await MakeViewModelAsync(temp.Path, queue);
        var id = Guid.NewGuid();
        var row = MakeRow(id, action);

        vm.InvokeRowAction(row);

        queue.Cancelled.ShouldBe([id]);
        queue.Removed.ShouldBeEmpty();
    }

    [Fact]
    public async Task InvokeRowAction_Dismiss_routes_to_queue_Remove_not_Cancel()
    {
        using var temp = new TempDirectory();
        var queue = new FakeExportQueue();
        var vm = await MakeViewModelAsync(temp.Path, queue);
        var id = Guid.NewGuid();
        var row = MakeRow(id, ExportJobRowAction.Dismiss);

        vm.InvokeRowAction(row);

        queue.Removed.ShouldBe([id]);
        queue.Cancelled.ShouldBeEmpty();
    }

    [Fact]
    public async Task InvokeRowAction_Reveal_raises_RevealRequested_with_the_output_path_and_touches_neither_Cancel_nor_Remove()
    {
        using var temp = new TempDirectory();
        var queue = new FakeExportQueue();
        var vm = await MakeViewModelAsync(temp.Path, queue);
        var id = Guid.NewGuid();
        var row = MakeRow(id, ExportJobRowAction.Reveal, outputPath: @"C:\exports\final.mp4");
        string? revealed = null;
        vm.RevealRequested += (_, path) => revealed = path;

        vm.InvokeRowAction(row);

        revealed.ShouldBe(@"C:\exports\final.mp4");
        queue.Cancelled.ShouldBeEmpty();
        queue.Removed.ShouldBeEmpty();
    }

    [Fact]
    public async Task InvokeRowAction_None_calls_neither_Cancel_nor_Remove_nor_raises_Reveal()
    {
        using var temp = new TempDirectory();
        var queue = new FakeExportQueue();
        var vm = await MakeViewModelAsync(temp.Path, queue);
        var row = MakeRow(Guid.NewGuid(), ExportJobRowAction.None);
        var revealedCount = 0;
        vm.RevealRequested += (_, _) => revealedCount++;

        vm.InvokeRowAction(row);

        queue.Cancelled.ShouldBeEmpty();
        queue.Removed.ShouldBeEmpty();
        revealedCount.ShouldBe(0);
    }

    private static ExportJobRowViewModel MakeRow(Guid id, ExportJobRowAction action, string outputPath = "out.mp4") =>
        new(id, "out.mp4", "12:00", outputPath, null, ExportJobRowStatusIcon.Waiting, "Queued", false, 0, "", action, "");
}
