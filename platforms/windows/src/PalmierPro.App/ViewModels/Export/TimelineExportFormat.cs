using PalmierPro.Services.Export;

namespace PalmierPro.App.ViewModels.Export;

/// Ports `TimelineExportFormat: String` (`Export/ExportView.swift`) — the Timeline destination's
/// two interchange formats. `ExportDestinationKind` (not `ExportFormat`, which doesn't exist on
/// this port — see `ExportOptions.cs`) is what an <see cref="ExportRequest"/> actually carries.
public enum TimelineExportFormat
{
    Xmeml,
    Fcpxml,
}

public static class TimelineExportFormatExtensions
{
    public static ExportDestinationKind DestinationKind(this TimelineExportFormat format) => format switch
    {
        TimelineExportFormat.Xmeml => ExportDestinationKind.Xmeml,
        TimelineExportFormat.Fcpxml => ExportDestinationKind.Fcpxml,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string DisplayName(this TimelineExportFormat format) => format switch
    {
        TimelineExportFormat.Xmeml => "XMEML",
        TimelineExportFormat.Fcpxml => "FCPXML",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string ExtensionLabel(this TimelineExportFormat format) => format switch
    {
        TimelineExportFormat.Xmeml => ".xml",
        TimelineExportFormat.Fcpxml => ".fcpxml",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// XMEML has no user-selectable version (always v4); FCPXML's version is picked separately
    /// via <see cref="FcpxmlVersion"/> — empty string means "shown via the version picker instead."
    public static string VersionLabel(this TimelineExportFormat format) => format switch
    {
        TimelineExportFormat.Xmeml => "v4",
        TimelineExportFormat.Fcpxml => "",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string Summary(this TimelineExportFormat format) => format switch
    {
        TimelineExportFormat.Xmeml =>
            "Older interchange format, best when Premiere Pro is the destination. Supports basic edits and keyframes, but not text, color, or effects.",
        TimelineExportFormat.Fcpxml =>
            "Newer timeline format with better support for DaVinci Resolve and Final Cut Pro. Supports basic edits, keyframes, and text, but not color or effects.",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string CompatibilityLabel(this TimelineExportFormat format) => format switch
    {
        TimelineExportFormat.Xmeml => "Premiere Pro and DaVinci Resolve",
        TimelineExportFormat.Fcpxml => "DaVinci Resolve and Final Cut Pro",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string FileExtension(this TimelineExportFormat format) => format switch
    {
        TimelineExportFormat.Xmeml => "xml",
        TimelineExportFormat.Fcpxml => "fcpxml",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}
