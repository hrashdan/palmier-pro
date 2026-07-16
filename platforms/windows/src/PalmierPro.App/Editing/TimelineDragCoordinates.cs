namespace PalmierPro.App.Editing;

/// Converts a WinUI `DragEventArgs.GetPosition` result into the timeline's DIP "screen" space.
///
/// WinUI 3's `DragEventArgs.GetPosition(relativeTo)` reports the pointer in PHYSICAL pixels — the
/// value is already multiplied by `XamlRoot.RasterizationScale` — unlike
/// `PointerRoutedEventArgs.GetCurrentPoint().Position` (and the tap event args), which report DIPs.
/// The Win2D canvas and every `TimelineGeometry` conversion (`DocXForScreen`, `ScreenXForFrame`,
/// `TrackY`, …) work in DIPs, so a raw drag position must be divided by the rasterization scale
/// before it enters that math. Skipping the divide makes the drop indicator/ghost land at
/// `scale * cursorX` instead of `cursorX`, i.e. a gap of `(scale - 1) * cursorX` that grows with
/// distance from the canvas's left edge — invisible at 100%, but a widening drift at 150%/200%.
/// Scroll offset and zoom cancel out of that error, which is why the gap tracks screen distance
/// rather than frame position. Pure/testable — no WinUI types.
public static class TimelineDragCoordinates
{
    /// Physical-pixel drag coordinate → DIP "screen" coordinate. A non-positive scale (never
    /// expected once the control is in a live visual tree) is treated as 1 so the value passes
    /// through unchanged rather than dividing by zero.
    public static double ScreenFromRaw(double rawPixels, double rasterizationScale) =>
        rasterizationScale > 0 ? rawPixels / rasterizationScale : rawPixels;

    /// Frame under a raw drag pointer X: physical px → DIP → document X (add scroll) → frame.
    public static int FrameAtRaw(TimelineGeometry geo, double rawX, double rasterizationScale, double scrollX) =>
        geo.FrameAt(ScreenFromRaw(rawX, rasterizationScale) + scrollX);

    /// Document Y under a raw drag pointer Y: physical px → DIP → document Y (add scroll).
    public static double DocYAtRaw(double rawY, double rasterizationScale, double scrollY) =>
        ScreenFromRaw(rawY, rasterizationScale) + scrollY;
}
