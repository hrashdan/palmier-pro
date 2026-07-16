using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PalmierPro.App.Theme;
using PalmierPro.App.ViewModels.Export;
using PalmierPro.Core.Theme;
using PalmierPro.Services.Export;
using CoreTimeline = PalmierPro.Core.Models.Timeline;

namespace PalmierPro.App.Views.Export;

/// Backs ExportView.xaml — ports Export/ExportView.swift's two-pane sheet. Classic code-behind
/// wiring (not `{x:Bind}`), same convention as MediaTabView/TransportBar: `Initialize(vm)` sets
/// `DataContext`-equivalent state, `Render()` re-paints everything on any ViewModel change, and a
/// `DispatcherQueueTimer` polls the queue log (AppThemeTokens.Export.QueueRefreshInterval — see
/// that token's doc comment for why: unlike the Mac's `@Observable`, `ExportJob` here has no
/// change notification of its own).
public sealed partial class ExportView : UserControl
{
    private ExportViewModel? _viewModel;
    private DispatcherQueueTimer? _pollTimer;
    private bool _isSyncingSelection;

    private List<ComboItem<ExportVideoCodec>> _codecItems = [];
    private List<ComboItem<ExportResolution>> _resolutionItems = [];
    private List<ComboItem<FcpxmlTarget>> _fcpxmlTargetItems = [];
    private List<ComboItem<FcpxmlVersion>> _fcpxmlVersionItems = [];
    private List<ComboItem<CoreTimeline>> _timelineItems = [];
    private IReadOnlyList<string> _timelineItemsKey = [];

    public ExportView()
    {
        InitializeComponent();
        ApplyStaticTokens();
        Loaded += (_, _) => StartPolling();
        Unloaded += (_, _) => StopPolling();
    }

    public void Initialize(ExportViewModel viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.RevealRequested -= OnRevealRequested;
        }
        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.RevealRequested += OnRevealRequested;

        DestinationRadioButtons.ItemsSource = _viewModel.Destinations.Select(d => d.DisplayName()).ToList();
        _codecItems = [.. _viewModel.Codecs.Select(c => new ComboItem<ExportVideoCodec>(c, c.DisplayName()))];
        _resolutionItems = [.. _viewModel.Resolutions.Select(r => new ComboItem<ExportResolution>(r, r.DisplayName()))];
        _fcpxmlTargetItems = [.. _viewModel.FcpxmlTargets.Select(t => new ComboItem<FcpxmlTarget>(t, t.DisplayName()))];
        _fcpxmlVersionItems = [.. _viewModel.FcpxmlVersions.Select(v => new ComboItem<FcpxmlVersion>(v, v.RawValue()))];
        CodecComboBox.ItemsSource = _codecItems;
        ResolutionComboBox.ItemsSource = _resolutionItems;
        FcpxmlTargetComboBox.ItemsSource = _fcpxmlTargetItems;
        FcpxmlVersionComboBox.ItemsSource = _fcpxmlVersionItems;

        Render();
    }

    // {StaticResource} values feeding Thickness/CornerRadius-typed properties don't coerce in
    // WinUI XAML — set here instead (AGENTS.md).
    private void ApplyStaticTokens()
    {
        RootPanel.Background = AppTheme.Background.SurfaceBrush;
        // Fixed sheet height (Export/ExportView.swift's `.frame(height:)`) — without it the two
        // ScrollViewers' *-rows have no bounded height and the log pane collapses to the settings
        // column's content height (AppThemeTokens.Export.SheetHeight).
        RootPanel.Height = AppThemeTokens.Export.SheetHeight;
        HeaderText.Padding = AppTheme.ThicknessOf(AppThemeTokens.Spacing.Xl, AppThemeTokens.Spacing.Md, AppThemeTokens.Spacing.Xl, AppThemeTokens.Spacing.Md);
        SettingsStack.Padding = AppTheme.UniformThickness(AppThemeTokens.Spacing.Xl);
        SettingsStack.Spacing = AppThemeTokens.Spacing.Md;
        BottomBar.Padding = AppTheme.ThicknessOf(AppThemeTokens.Spacing.Xl, AppThemeTokens.Spacing.Lg, AppThemeTokens.Spacing.Xl, AppThemeTokens.Spacing.Lg);
        BottomBar.ColumnSpacing = AppThemeTokens.Spacing.Md;
        LogHeader.Padding = AppTheme.ThicknessOf(AppThemeTokens.Spacing.Lg, AppThemeTokens.Spacing.Md, AppThemeTokens.Spacing.Lg, AppThemeTokens.Spacing.Md);
        LogHeader.ColumnSpacing = AppThemeTokens.Spacing.Sm;
        SummaryPanel.Spacing = AppThemeTokens.Spacing.Lg;
        VideoSettingsPanel.Spacing = AppThemeTokens.Spacing.Zero;
        TimelineSettingsPanel.Spacing = AppThemeTokens.Spacing.Md;
        PalmierSettingsPanel.Spacing = AppThemeTokens.Spacing.Xs;
        LogStack.Spacing = AppThemeTokens.Spacing.Zero;
        FcpxmlSubRow.Spacing = AppThemeTokens.Spacing.Sm;
        FcpxmlSubRow.Padding = AppTheme.ThicknessOf(AppThemeTokens.IconSize.Sm + AppThemeTokens.Spacing.Md, AppThemeTokens.Spacing.Sm, 0, 0);

        var cardRadius = AppTheme.UniformCornerRadius(AppThemeTokens.Radius.Sm);
        var cardPadding = AppTheme.UniformThickness(AppThemeTokens.Spacing.Md);
        var cardBorder = AppTheme.UniformThickness(AppThemeTokens.BorderWidth.Thin);
        foreach (var card in new[] { XmemlCard, FcpxmlCard })
        {
            card.CornerRadius = cardRadius;
            card.Padding = cardPadding;
            card.BorderThickness = cardBorder;
        }
    }

    // MARK: - Render

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(Render);

    private void Render()
    {
        if (_viewModel is not { } vm)
        {
            return;
        }
        _isSyncingSelection = true;
        try
        {
            var destinations = vm.Destinations;
            DestinationRadioButtons.SelectedIndex = destinations.ToList().IndexOf(vm.Destination);

            var showsTimelinePicker = vm.ShowsTimelinePicker;
            TimelinePickerRow.Visibility = showsTimelinePicker ? Visibility.Visible : Visibility.Collapsed;
            TimelinePickerDivider.Visibility = showsTimelinePicker ? Visibility.Visible : Visibility.Collapsed;
            if (showsTimelinePicker)
            {
                SyncTimelineItems(vm);
                TimelineComboBox.SelectedItem = _timelineItems.FirstOrDefault(i => i.Value.Id == vm.ExportTimeline.Id);
            }

            VideoSettingsPanel.Visibility = vm.Destination == ExportDestination.Video ? Visibility.Visible : Visibility.Collapsed;
            TimelineSettingsPanel.Visibility = vm.Destination == ExportDestination.Timeline ? Visibility.Visible : Visibility.Collapsed;
            PalmierSettingsPanel.Visibility = vm.Destination == ExportDestination.PalmierProject ? Visibility.Visible : Visibility.Collapsed;

            FileTypeText.Text = vm.ContainerLabel;
            FrameRateText.Text = vm.FrameRateText;
            CodecComboBox.SelectedItem = _codecItems.FirstOrDefault(i => i.Value == vm.Codec);
            ResolutionComboBox.SelectedItem = _resolutionItems.FirstOrDefault(i => i.Value == vm.Resolution);

            RenderTimelineCards(vm);

            PalmierMissingWarningText.Text = vm.PalmierMissingWarningText ?? "";
            PalmierMissingWarningText.Visibility = vm.PalmierMissingWarningText is null ? Visibility.Collapsed : Visibility.Visible;

            SubmissionErrorText.Text = vm.SubmissionError ?? "";
            SubmissionErrorText.Visibility = string.IsNullOrEmpty(vm.SubmissionError) ? Visibility.Collapsed : Visibility.Visible;

            RenderSummary(vm);
            ExportActionButton.Content = vm.ActionButtonText;

            PendingCountText.Text = vm.PendingCount > 0 ? vm.PendingCount.ToString() : "";
            PendingCountText.Visibility = vm.PendingCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            ClearFinishedButton.IsEnabled = vm.HasFinishedJobs;

            RenderLog(vm);
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    // Unlike the static codec/resolution combos (built once in Initialize), the timeline set is
    // data that could change — but Render() runs ~6x/sec off the poll timer, and reassigning a
    // ComboBox.ItemsSource collapses an open dropdown mid-selection. Rebuild the item list only
    // when the timeline identities actually change; Render() then moves only SelectedItem.
    private void SyncTimelineItems(ExportViewModel vm)
    {
        var key = vm.Timelines.Select(t => t.Id).ToList();
        if (_timelineItemsKey.SequenceEqual(key))
        {
            return;
        }
        _timelineItems = [.. vm.Timelines.Select(t => new ComboItem<CoreTimeline>(t, t.Name))];
        _timelineItemsKey = key;
        TimelineComboBox.ItemsSource = _timelineItems;
    }

    private void RenderTimelineCards(ExportViewModel vm)
    {
        var xmemlSelected = vm.TimelineFormat == TimelineExportFormat.Xmeml;
        var fcpxmlSelected = vm.TimelineFormat == TimelineExportFormat.Fcpxml;

        SetCardSelected(XmemlCard, XmemlSelectedGlyph, xmemlSelected);
        SetCardSelected(FcpxmlCard, FcpxmlSelectedGlyph, fcpxmlSelected);

        FcpxmlSubRow.Visibility = fcpxmlSelected ? Visibility.Visible : Visibility.Collapsed;
        if (!fcpxmlSelected)
        {
            return;
        }
        FcpxmlTargetComboBox.SelectedItem = _fcpxmlTargetItems.FirstOrDefault(i => i.Value == vm.FcpxmlTarget);
        FcpxmlVersionComboBox.SelectedItem = _fcpxmlVersionItems.FirstOrDefault(i => i.Value == vm.FcpxmlVersion);
        FcpxmlCompatibilityNoteText.Text = vm.FcpxmlVersion.CompatibilityNote();
    }

    private static void SetCardSelected(Border card, FontIcon selectedGlyph, bool selected)
    {
        card.Background = selected ? AppTheme.Background.ProminentBrush : AppTheme.Background.RaisedBrush;
        card.BorderBrush = selected ? AppTheme.Border.PrimaryBrush : AppTheme.Border.SubtleBrush;
        selectedGlyph.Foreground = AppTheme.Accent.PrimaryBrush;
        selectedGlyph.Glyph = ""; // CheckMark
        selectedGlyph.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    }

    /// Simplified from the Mac's per-part SF Symbol (clock/doc/shippingbox): plain muted text,
    /// duration first, remaining format-specific parts joined by " · " — same information, one
    /// fewer glyph-per-part to hand-map onto Segoe Fluent Icons.
    private void RenderSummary(ExportViewModel vm)
    {
        SummaryPanel.Children.Clear();
        SummaryPanel.Children.Add(MutedText(vm.DurationText));
        SummaryPanel.Children.Add(MutedText(string.Join(" · ", vm.SummaryParts)));
    }

    private static TextBlock MutedText(string text) => new()
    {
        Text = text,
        FontSize = AppThemeTokens.FontSize.Xs,
        Foreground = AppTheme.Text.MutedBrush,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void RenderLog(ExportViewModel vm)
    {
        var hasJobs = vm.Rows.Count > 0;
        EmptyLogText.Visibility = hasJobs ? Visibility.Collapsed : Visibility.Visible;
        LogScrollViewer.Visibility = hasJobs ? Visibility.Visible : Visibility.Collapsed;

        LogStack.Children.Clear();
        foreach (var row in vm.Rows)
        {
            var control = new ExportQueueRowControl();
            control.SetRow(row);
            control.ActionRequested += OnRowActionRequested;
            LogStack.Children.Add(control);
        }
    }

    private void OnRowActionRequested(object? sender, ExportJobRowViewModel row)
    {
        if (sender is ExportQueueRowControl control)
        {
            control.ActionRequested -= OnRowActionRequested;
        }
        _viewModel?.InvokeRowAction(row);
    }

    private void OnRevealRequested(object? sender, string outputPath)
    {
        try
        {
            Process.Start("explorer.exe", $"/select,\"{outputPath}\"");
        }
        catch (Exception)
        {
            // Best-effort — Explorer failing to launch isn't fatal to a completed export.
        }
    }

    // MARK: - Poll timer (see AppThemeTokens.Export.QueueRefreshInterval's doc comment)

    private void StartPolling()
    {
        if (_pollTimer is not null)
        {
            return;
        }
        _pollTimer = DispatcherQueue.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromSeconds(AppThemeTokens.Export.QueueRefreshInterval);
        _pollTimer.IsRepeating = true;
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();
    }

    private void StopPolling()
    {
        if (_pollTimer is null)
        {
            return;
        }
        _pollTimer.Stop();
        _pollTimer.Tick -= PollTimer_Tick;
        _pollTimer = null;
    }

    private void PollTimer_Tick(DispatcherQueueTimer sender, object args) => _viewModel?.RefreshJobs();

    // MARK: - Input handlers

    private void DestinationRadioButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSelection || _viewModel is not { } vm || DestinationRadioButtons.SelectedIndex < 0)
        {
            return;
        }
        vm.Destination = vm.Destinations[DestinationRadioButtons.SelectedIndex];
    }

    private void TimelineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSelection || _viewModel is not { } vm)
        {
            return;
        }
        if (TimelineComboBox.SelectedItem is ComboItem<CoreTimeline> item)
        {
            vm.SelectedTimelineId = item.Value.Id;
        }
    }

    private void CodecComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSelection || _viewModel is not { } vm)
        {
            return;
        }
        if (CodecComboBox.SelectedItem is ComboItem<ExportVideoCodec> item)
        {
            vm.Codec = item.Value;
        }
    }

    private void ResolutionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSelection || _viewModel is not { } vm)
        {
            return;
        }
        if (ResolutionComboBox.SelectedItem is ComboItem<ExportResolution> item)
        {
            vm.Resolution = item.Value;
        }
    }

    private void FcpxmlTargetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSelection || _viewModel is not { } vm)
        {
            return;
        }
        if (FcpxmlTargetComboBox.SelectedItem is ComboItem<FcpxmlTarget> item)
        {
            vm.FcpxmlTarget = item.Value;
        }
    }

    private void FcpxmlVersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSelection || _viewModel is not { } vm)
        {
            return;
        }
        if (FcpxmlVersionComboBox.SelectedItem is ComboItem<FcpxmlVersion> item)
        {
            vm.FcpxmlVersion = item.Value;
        }
    }

    private void XmemlCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_viewModel is { } vm)
        {
            vm.TimelineFormat = TimelineExportFormat.Xmeml;
        }
    }

    private void FcpxmlCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_viewModel is { } vm)
        {
            vm.TimelineFormat = TimelineExportFormat.Fcpxml;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => _viewModel?.CloseCommand.Execute(null);

    private async void ExportActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is { } vm && vm.StartExportCommand.CanExecute(null))
        {
            await vm.StartExportCommand.ExecuteAsync(null);
        }
    }

    private void ClearFinishedButton_Click(object sender, RoutedEventArgs e) => _viewModel?.ClearFinishedCommand.Execute(null);

    private readonly record struct ComboItem<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }
}
