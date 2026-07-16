using PalmierPro.Core.Export;
using PalmierPro.Services.Export;

namespace PalmierPro.App.Services;

/// Composition point for the export pipeline. Stage F's Export dialog wires
/// `ShellViewModel.ExportCommand` to `ExportView`/`ExportViewModel`, which reads <see cref="Queue"/>.
/// Lazy: the timing-reader/font-resolver shims touch OS state (a process launch, the system font
/// collection) a menu registration step shouldn't pay for before Export is actually used;
/// <see cref="Queue"/> is a process-wide singleton — mirrors the Mac's `ExportQueue.shared`
/// (`Export/ExportQueue.swift`), which docs/export-v1.md §11 says this port keeps the scope of.
public static class ExportServices
{
    private static readonly Lazy<ISourceTimingReader> LazySourceTimingReader = new(() => new FfprobeSourceTimingReader());
    private static readonly Lazy<IFontTraitResolver> LazyFontTraitResolver = new(() => new DirectWriteFontTraitResolver());
    private static readonly Lazy<IExportQueue> LazyQueue = new(() => new ExportQueue(new CompositeExportService()));

    public static ISourceTimingReader SourceTimingReader => LazySourceTimingReader.Value;
    public static IFontTraitResolver FontTraitResolver => LazyFontTraitResolver.Value;
    public static IExportQueue Queue => LazyQueue.Value;
}
