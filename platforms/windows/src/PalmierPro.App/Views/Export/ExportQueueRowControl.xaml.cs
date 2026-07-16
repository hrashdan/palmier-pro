using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PalmierPro.App.Theme;
using PalmierPro.App.ViewModels.Export;
using PalmierPro.Core.Theme;

namespace PalmierPro.App.Views.Export;

/// Renders one ExportJobRowViewModel — a pure projection, so this control just paints it; no
/// ViewModel reference, no bindings. Rebuilt fresh (not reused) each poll tick by ExportView, same
/// "rebuild rather than incrementally sync" reasoning as ExportJobRowViewModel.From's own doc.
public sealed partial class ExportQueueRowControl : UserControl
{
    // Segoe Fluent Icons — \uXXXX escapes (not literal glyphs) so the codepoint is unambiguous in
    // source; same glyph family TransportBar.xaml/ProjectCard.xaml already use.
    private const string GlyphClock = "";
    private const string GlyphPlay = "";
    private const string GlyphCheckMark = "";
    private const string GlyphWarning = "";
    private const string GlyphCancel = "";
    private const string GlyphStop = ""; // Segoe Fluent "Stop" (filled square), matching the Mac's stop.fill
    private const string GlyphFolder = "";

    private ExportJobRowViewModel? _row;

    /// Raised on the trailing action-button click — ExportView routes this to
    /// ExportViewModel.InvokeRowAction, keeping this control ViewModel-free.
    public event EventHandler<ExportJobRowViewModel>? ActionRequested;

    public ExportQueueRowControl()
    {
        InitializeComponent();
        // {StaticResource} values feeding Thickness-typed properties don't coerce in WinUI XAML —
        // set here instead (AGENTS.md).
        RootBorder.BorderThickness = AppTheme.ThicknessOf(0, 0, 0, AppThemeTokens.BorderWidth.Hairline);
        RootGrid.Padding = AppTheme.ThicknessOf(
            AppThemeTokens.Spacing.Lg, AppThemeTokens.Spacing.Sm, AppThemeTokens.Spacing.Lg, AppThemeTokens.Spacing.Sm);
    }

    public void SetRow(ExportJobRowViewModel row)
    {
        _row = row;
        ToolTipService.SetToolTip(this, row.Tooltip);
        AutomationProperties.SetName(StatusIconHost, row.StatusAccessibilityLabel);

        TimestampText.Text = row.Timestamp;
        FilenameText.Text = row.Filename;

        RenderStatusIcon(row.StatusIcon);

        ProgressBarControl.Visibility = row.ShowsProgress ? Visibility.Visible : Visibility.Collapsed;
        ProgressBarControl.Value = row.ShowsProgress ? row.Progress * 100 : 0;
        ProgressText.Text = row.ProgressText;
        ProgressText.Visibility = row.ShowsProgress ? Visibility.Visible : Visibility.Collapsed;

        RenderAction(row.Action, row.ActionTooltip);
    }

    private void RenderStatusIcon(ExportJobRowStatusIcon icon)
    {
        var isSpinner = icon == ExportJobRowStatusIcon.Spinner;
        StatusSpinner.IsActive = isSpinner;
        StatusSpinner.Visibility = isSpinner ? Visibility.Visible : Visibility.Collapsed;
        StatusGlyph.Visibility = isSpinner ? Visibility.Collapsed : Visibility.Visible;
        if (isSpinner)
        {
            return;
        }
        var (glyph, brush) = icon switch
        {
            ExportJobRowStatusIcon.Waiting => (GlyphClock, AppTheme.Text.TertiaryBrush),
            ExportJobRowStatusIcon.Exporting => (GlyphPlay, AppTheme.Accent.PrimaryBrush),
            ExportJobRowStatusIcon.Completed => (GlyphCheckMark, AppTheme.Status.SuccessBrush),
            ExportJobRowStatusIcon.Failed => (GlyphWarning, AppTheme.Status.ErrorBrush),
            ExportJobRowStatusIcon.Canceled => (GlyphCancel, AppTheme.Text.MutedBrush),
            _ => (GlyphClock, AppTheme.Text.TertiaryBrush),
        };
        StatusGlyph.Glyph = glyph;
        StatusGlyph.Foreground = brush;
    }

    private void RenderAction(ExportJobRowAction action, string tooltip)
    {
        ActionButton.Visibility = action == ExportJobRowAction.None ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(ActionButton, tooltip);
        AutomationProperties.SetName(ActionButton, tooltip);
        ActionGlyph.Glyph = action switch
        {
            ExportJobRowAction.CancelExport => GlyphCancel,
            ExportJobRowAction.StopExport => GlyphStop,
            ExportJobRowAction.Reveal => GlyphFolder,
            ExportJobRowAction.Dismiss => GlyphCancel,
            _ => "",
        };
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_row is { } row)
        {
            ActionRequested?.Invoke(this, row);
        }
    }
}
