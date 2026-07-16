namespace PalmierPro.App.ViewModels.Export;

/// Ports `ExportDestination: String` (`Export/ExportView.swift`) — the three top-level export
/// targets shown as the picker's first row.
public enum ExportDestination
{
    Video,
    Timeline,
    PalmierProject,
}

public static class ExportDestinationExtensions
{
    public static string DisplayName(this ExportDestination destination) => destination switch
    {
        ExportDestination.Video => "Video",
        ExportDestination.Timeline => "Timeline",
        ExportDestination.PalmierProject => "Palmier Project",
        _ => throw new ArgumentOutOfRangeException(nameof(destination)),
    };
}
