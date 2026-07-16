using PalmierPro.App.Editing;
using Shouldly;
using Xunit;

namespace PalmierPro.App.Tests.Editing;

/// Regression coverage for the media-panel → timeline drag drift at >100% display scaling.
/// `DragEventArgs.GetPosition` reports physical pixels; TimelineDragCoordinates divides by the
/// rasterization scale so the drop indicator/ghost tracks the cursor at any scale, scroll, or zoom.
public class TimelineDragCoordinatesTests
{
    // Header 100, ppf 4 (the app default). Three 50px tracks: baseY = ruler(24)+dropZone(60) = 84.
    private static TimelineGeometry Geometry(double pxPerFrame = 4) =>
        new(pxPerFrame, [50, 50, 50], TimelineGeometry.Layout.HeaderWidth);

    [Theory]
    [InlineData(1.0, 900)]
    [InlineData(1.5, 1350)]
    [InlineData(2.0, 1800)]
    public void ScreenFromRawUndoesRasterizationScale(double scale, double rawPixels)
    {
        // The cursor sits at DIP screen-x 900 regardless of scale; the raw drag value is scale*900.
        TimelineDragCoordinates.ScreenFromRaw(rawPixels, scale).ShouldBe(900, tolerance: 1e-9);
    }

    [Fact]
    public void NonPositiveScaleIsTreatedAsIdentity()
    {
        TimelineDragCoordinates.ScreenFromRaw(1350, 0).ShouldBe(1350);
        TimelineDragCoordinates.ScreenFromRaw(1350, -1).ShouldBe(1350);
    }

    // The exact user-reported scenario: cursor at DIP viewport-x 900, 150% scaling, scroll 400.
    // The ghost must land back under the cursor (DIP 900); the old raw-as-DIP path drifts far right.
    [Fact]
    public void GhostLandsUnderCursor_150Percent_Scrolled()
    {
        var geo = Geometry();
        const double scale = 1.5;
        const double scrollX = 400;
        const double cursorViewportX = 900;         // where the drag thumbnail visually sits
        var rawX = cursorViewportX * scale;         // 1350 — what DragEventArgs hands us

        var frame = TimelineDragCoordinates.FrameAtRaw(geo, rawX, scale, scrollX);
        frame.ShouldBe(300);                        // (900 + 400 - 100) / 4

        // Ghost screen-x = XForFrame(frame) - scrollX (control's ScreenXForFrame).
        var ghostScreenX = geo.XForFrame(frame) - scrollX;
        ghostScreenX.ShouldBe(cursorViewportX, tolerance: geo.PixelsPerFrame);

        // The pre-fix path treated the raw physical value as if it were already DIPs.
        var buggyFrame = geo.FrameAt(rawX + scrollX);
        var buggyGhostX = geo.XForFrame(buggyFrame) - scrollX;
        var gap = buggyGhostX - cursorViewportX;
        gap.ShouldBeGreaterThan(400);               // ≈ (scale-1) * cursorViewportX = 450
    }

    // Gap is proportional to screen distance and independent of scroll — hence "grows as you drag
    // right." Two scroll offsets, same cursor position, must yield the same corrected frame delta.
    [Theory]
    [InlineData(0)]
    [InlineData(400)]
    [InlineData(1200)]
    public void ScrollCancelsFromCorrectedMapping(double scrollX)
    {
        var geo = Geometry();
        const double scale = 1.5;
        const double cursorViewportX = 640;
        var rawX = cursorViewportX * scale;

        var frame = TimelineDragCoordinates.FrameAtRaw(geo, rawX, scale, scrollX);
        var ghostScreenX = geo.XForFrame(frame) - scrollX;
        ghostScreenX.ShouldBe(cursorViewportX, tolerance: geo.PixelsPerFrame);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void GhostTracksCursorAtEveryScale(double scale)
    {
        var geo = Geometry();
        const double scrollX = 260;
        const double cursorViewportX = 720;
        var rawX = cursorViewportX * scale;

        var frame = TimelineDragCoordinates.FrameAtRaw(geo, rawX, scale, scrollX);
        var ghostScreenX = geo.XForFrame(frame) - scrollX;
        ghostScreenX.ShouldBe(cursorViewportX, tolerance: geo.PixelsPerFrame);
    }

    // Zoom in (larger ppf) must not reintroduce drift: the frame changes but the ghost still lands
    // under the cursor.
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(30)]
    public void GhostTracksCursorAtEveryZoom(double pxPerFrame)
    {
        var geo = Geometry(pxPerFrame);
        const double scale = 1.5;
        const double scrollX = 512;
        const double cursorViewportX = 856;
        var rawX = cursorViewportX * scale;

        var frame = TimelineDragCoordinates.FrameAtRaw(geo, rawX, scale, scrollX);
        var ghostScreenX = geo.XForFrame(frame) - scrollX;
        ghostScreenX.ShouldBe(cursorViewportX, tolerance: geo.PixelsPerFrame);
    }

    // Vertical axis: the drop target (which track / insertion line) must resolve from the DIP Y,
    // not the physical Y. At 150% a cursor over track 1 would otherwise resolve to a lower track.
    [Fact]
    public void DropTargetResolvesFromDipY_NotPhysicalY()
    {
        var geo = Geometry();
        const double scale = 1.5;
        const double cursorViewportY = 140;         // inside track 1 body [134, 184)

        var docY = TimelineDragCoordinates.DocYAtRaw(cursorViewportY * scale, scale, 0);
        docY.ShouldBe(cursorViewportY, tolerance: 1e-9);
        geo.TrackAt(docY).ShouldBe(1);

        // Pre-fix: physical Y (210) lands past the last track's body.
        var buggyDocY = cursorViewportY * scale;     // treated as DIP screen-y
        geo.TrackAt(buggyDocY).ShouldNotBe(1);
    }

    [Fact]
    public void DocYAtRawAddsScrollAfterUnscaling()
    {
        // 150%, scrollY 84: cursor at DIP 60 → doc Y 144.
        TimelineDragCoordinates.DocYAtRaw(60 * 1.5, 1.5, 84).ShouldBe(144, tolerance: 1e-9);
    }
}
