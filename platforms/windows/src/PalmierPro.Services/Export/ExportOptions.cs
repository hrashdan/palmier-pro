namespace PalmierPro.Services.Export;

/// Ports `ExportFormat`'s video-codec cases (`Export/ExportOptions.swift`). Deliberately excludes
/// HDR (`hevcHDR`) — the plan's Phase 1 disposition for HEVC 10-bit HDR is "deferred fast-follow,
/// export picker must handle its absence explicitly, not render a dead option" (see docs/export-v1.md
/// §13). Do not add a Hdr case here as a placeholder; a future fast-follow adds a real one once
/// the render path exists.
public enum ExportVideoCodec
{
    H264,
    H265,
    ProRes,
}

/// The only two containers the v1 export pipeline ever writes to — one per <see cref="ExportVideoCodec"/>
/// pairing (h264/h265 -> Mp4, ProRes -> Mov). Carried explicitly in <see cref="ExportOptions"/> (and
/// the wire JSON, docs/export-v1.md §4.2) rather than derived natively from codec alone, so the
/// codec->container mapping has exactly one source of truth (this enum's extension method) instead
/// of a second native-side table that could drift from it.
public enum ExportContainer
{
    Mp4,
    Mov,
}

public static class ExportContainerExtensions
{
    public static string FileExtension(this ExportContainer container) => container switch
    {
        ExportContainer.Mp4 => "mp4",
        ExportContainer.Mov => "mov",
        _ => throw new ArgumentOutOfRangeException(nameof(container)),
    };

    /// The one valid (codec, container) pairing per codec — mirrors `ExportFormat.fileExtension`
    /// (`Export/ExportOptions.swift`) for the three v1 codecs. PE_ExportStart independently
    /// rejects any other pairing (docs/export-v1.md §4.2) — this is the C#-side source of truth
    /// that keeps a caller from ever constructing the mismatched kind in the first place.
    public static ExportContainer ContainerFor(this ExportVideoCodec codec) => codec switch
    {
        ExportVideoCodec.H264 or ExportVideoCodec.H265 => ExportContainer.Mp4,
        ExportVideoCodec.ProRes => ExportContainer.Mov,
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };
}

/// Ports `ExportResolution` (`Export/ExportOptions.swift`) field-for-field, including
/// `matchTimeline`. Resolution to concrete pixels happens ENTIRELY on the C# side
/// (<see cref="ExportResolutionExtensions.RenderSize"/>) — native never receives the symbolic
/// name, only the resolved width/height (docs/export-v1.md §4.2).
public enum ExportResolution
{
    R720p,
    R1080p,
    R1440p,
    R4k,
    MatchTimeline,
}

public static class ExportResolutionExtensions
{
    /// Short-side target in pixels, or null for <see cref="ExportResolution.MatchTimeline"/> (no
    /// scaling — mirrors `ExportResolution.shortSidePixels`).
    public static int? ShortSidePixels(this ExportResolution resolution) => resolution switch
    {
        ExportResolution.R720p => 720,
        ExportResolution.R1080p => 1080,
        ExportResolution.R1440p => 1440,
        ExportResolution.R4k => 2160,
        ExportResolution.MatchTimeline => null,
        _ => throw new ArgumentOutOfRangeException(nameof(resolution)),
    };

    /// Ports `ExportResolution.renderSize(for:)` exactly: scales `(canvasWidth, canvasHeight)` so
    /// its SHORT side equals <see cref="ShortSidePixels"/>, preserving aspect ratio, then rounds
    /// each dimension down to the nearest even integer (H.264/HEVC/ProRes 4:2:0/4:2:2 chroma
    /// subsampling requirement — an odd dimension has no valid chroma-plane size). Returns
    /// `(canvasWidth, canvasHeight)` clamped to even, unscaled, for <see cref="ExportResolution.MatchTimeline"/>.
    public static (int Width, int Height) RenderSize(this ExportResolution resolution, int canvasWidth, int canvasHeight)
    {
        int? shortSide = resolution.ShortSidePixels();
        if (shortSide is null)
        {
            return EvenSize(canvasWidth, canvasHeight);
        }
        int canvasShort = Math.Min(canvasWidth, canvasHeight);
        if (canvasShort <= 0)
        {
            return EvenSize(canvasWidth, canvasHeight);
        }
        double scale = (double)shortSide.Value / canvasShort;
        return EvenSize((int)Math.Round(canvasWidth * scale), (int)Math.Round(canvasHeight * scale));
    }

    private static (int Width, int Height) EvenSize(int width, int height) =>
        (Math.Max(2, (width / 2) * 2), Math.Max(2, (height / 2) * 2));
}

/// Which of the export matrix's three fundamentally different pipelines a request targets
/// (docs/export-v1.md §3). Not a 1:1 mirror of `ExportFormat`'s six Swift cases — those conflate
/// "which pipeline" (video vs. XML vs. package) with "which video codec," which
/// <see cref="ExportRequest"/> keeps as two separate fields instead (<see cref="ExportDestinationKind.Video"/>
/// + <see cref="ExportOptions.Codec"/>).
public enum ExportDestinationKind
{
    /// h264 | h265 | prores, via native PE_ExportStart (docs/export-v1.md §4-§9).
    Video,
    /// `FcpxmlExporter.ExportAsync` (Stage C, already landed).
    Fcpxml,
    /// `XmemlExporter.ExportAsync` (Stage C, already landed).
    Xmeml,
    /// `PalmierProjectExporter.Export` (already landed).
    PalmierProject,
}

/// Ports `ExportJobSource` (`Export/ExportQueue.swift`) verbatim — distinguishes a user-initiated
/// export from one an agent tool kicked off, purely for analytics/notification routing
/// (docs/export-v1.md §11); has no effect on the pipeline itself.
public enum ExportJobSource
{
    Manual,
    Agent,
}

/// The exact payload serialized to native PE_ExportStart's `utf8OptionsJson` (docs/export-v1.md
/// §4.2) — `Width`/`Height` are ALREADY resolved (see <see cref="ExportResolutionExtensions.RenderSize"/>;
/// `MatchTimeline` never reaches this type as a symbolic value), and `Fps` is always the source
/// timeline's own `Fps` verbatim (v1 has no independent export-framerate picker, matching the
/// Mac — the field exists so a future frame-rate-conversion feature is a value change, not a
/// schema change, mirroring docs/timeline-snapshot-v1.md §1's identical `fps` reservation
/// rationale). `Container` is redundant with `Codec` today (see
/// <see cref="ExportContainerExtensions.ContainerFor"/>) but carried explicitly rather than
/// re-derived native-side — see that method's remarks.
public sealed record ExportOptions(
    ExportVideoCodec Codec,
    ExportContainer Container,
    int Width,
    int Height,
    int Fps,
    string OutputPath)
{
    public static ExportOptions Create(ExportVideoCodec codec, int width, int height, int fps, string outputPath) =>
        new(codec, codec.ContainerFor(), width, height, fps, outputPath);
}
