using PalmierPro.Services.Export;

namespace PalmierPro.App.ViewModels.Export;

/// UI-facing labels for the `Services.Export` option enums — kept here rather than on
/// `ExportOptions.cs` itself (E5's slice) since these strings are display-only, matching
/// `ExportOptions.swift`'s own `rawValue`/`containerLabel` text verbatim. `ExportVideoCodec` has
/// no `Hdr` case (see that enum's doc comment) — this file has nothing to special-case, which is
/// the point: an HDR option can't be constructed, so the picker can't offer a dead one.
public static class ExportOptionDisplay
{
    public static string DisplayName(this ExportVideoCodec codec) => codec switch
    {
        ExportVideoCodec.H264 => "H.264",
        ExportVideoCodec.H265 => "H.265",
        ExportVideoCodec.ProRes => "ProRes",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    public static string ContainerLabel(this ExportVideoCodec codec) => codec.ContainerFor() switch
    {
        ExportContainer.Mp4 => "MPEG-4 (.mp4)",
        ExportContainer.Mov => "QuickTime (.mov)",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    /// Rough encode bitrate, bytes/sec per output megapixel — mirrors `estimatedFileSize`'s
    /// per-codec table (`Export/ExportView.swift`) for the queue summary's "~123 MB" estimate.
    public static double EstimatedBytesPerSecondPerMegapixel(this ExportVideoCodec codec) => codec switch
    {
        ExportVideoCodec.H264 => 0.63e6,
        ExportVideoCodec.H265 => 0.32e6,
        ExportVideoCodec.ProRes => 9.0e6,
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    public static string DisplayName(this ExportResolution resolution) => resolution switch
    {
        ExportResolution.R720p => "720p",
        ExportResolution.R1080p => "1080p",
        ExportResolution.R1440p => "2K",
        ExportResolution.R4k => "4K",
        ExportResolution.MatchTimeline => "Match Timeline",
        _ => throw new ArgumentOutOfRangeException(nameof(resolution)),
    };
}
