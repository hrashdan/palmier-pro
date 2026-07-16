using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using PalmierPro.App.Editing;
using PalmierPro.App.ViewModels.Editor;
using PalmierPro.Core.Interop;
using PalmierPro.Core.Models;
using Serilog;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace PalmierPro.App.Views.Timeline;

/// Drag-drop-IN half of TimelineCanvasControl — accepts a `PalmierPro.ClipRef` drag from the media
/// panel (`ClipRefDragFormat`) and `StorageItems` dropped from Explorer (import, then place).
/// Ports the drop-target-resolution slice of `performDragOperation`/`draggingEntered`/
/// `draggingUpdated` in TimelineView.swift, with the `resolveDropPlan`/`materialize`
/// visual/audio-track split simplified to `TimelineDropPlanner` — see its doc comment.
public sealed partial class TimelineCanvasControl
{
    private sealed record ExternalDropDrag(TrackDropTarget Target, int Frame, int DurationFrames);

    private List<MediaAsset>? _externalDragAssets;
    private SnapEngine.SnapState _externalSnapState;

    /// Throttle stamp (Environment.TickCount64 ms) for the always-on "dragdiag|" telemetry emitted
    /// on DragOver — diagnostic only, no bearing on drag behavior. See LogDragDiag.
    private long _lastDragDiagTickMs;

    /// Pointer position for a drop event in the control's DIP "screen" space. `DragEventArgs`
    /// reports physical pixels (scaled by `XamlRoot.RasterizationScale`) — unlike the pointer/tap
    /// event args used everywhere else — so it must be divided back down to DIPs before feeding the
    /// DIP-based geometry math, otherwise the drop indicator drifts from the cursor at >100% display
    /// scaling. See TimelineDragCoordinates.
    private Point DragScreenPosition(DragEventArgs e)
    {
        var pos = e.GetPosition(Canvas);
        var scale = Canvas.XamlRoot?.RasterizationScale ?? 1.0;
        return new Point(
            TimelineDragCoordinates.ScreenFromRaw(pos.X, scale),
            TimelineDragCoordinates.ScreenFromRaw(pos.Y, scale));
    }

    /// Diagnostic-only: logs one "dragdiag|enter|…" line with the static context (display scale,
    /// window origin, canvas offset) needed to interpret the per-move samples. Does not touch drag
    /// state or `AcceptedOperation` — acceptance is still resolved entirely in DragOver.
    private void Canvas_DragEnter(object sender, DragEventArgs e)
    {
        _lastDragDiagTickMs = 0;
        var scale = Canvas.XamlRoot?.RasterizationScale ?? 1.0;
        var win = TryGetWindowPosition();
        var off = TryGetCanvasOffset();
        Log.Information("{DragDiag}",
            $"dragdiag|enter" +
            $"|scale={F(scale)}" +
            $"|winPos={(win.ok ? $"{win.x},{win.y}" : "na")}" +
            $"|canvasOff={(off.ok ? $"{F(off.x)},{F(off.y)}" : "na")}" +
            $"|canvas={F(Canvas.ActualWidth)}x{F(Canvas.ActualHeight)}" +
            $"|scrollX={F(_scrollX)}|scrollY={F(_scrollY)}|ppf={F(_pixelsPerFrame)}");
    }

    private void Canvas_DragLeave(object sender, DragEventArgs e)
    {
        _drag = null;
        _externalDragAssets = null;
        SetExternalSnapX(null);
        RequestRedraw();
    }

    private async void Canvas_DragOver(object sender, DragEventArgs e)
    {
        if (_context is not { } ctx || Vm is not { } vm)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            if (_externalDragAssets is null)
            {
                _externalDragAssets = await ResolveClipRefAssetsAsync(ctx, e.DataView) ?? [];
                _externalSnapState = new SnapEngine.SnapState();
            }

            var acceptsStorageItems = e.DataView.Contains(StandardDataFormats.StorageItems);
            if (_externalDragAssets.Count == 0 && !acceptsStorageItems)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = _externalDragAssets.Count > 0 ? "Add to Timeline" : "Import";

            var geo = BuildGeometry();
            var pos = DragScreenPosition(e);
            var target = geo.DropTargetAt(DocYForScreen(pos.Y));
            var totalDur = _externalDragAssets.Count > 0
                ? _externalDragAssets.Sum(a => vm.ClipDurationFrames(a, null))
                : Math.Max(1, vm.Timeline.Fps * 3);
            var frame = ComputeExternalDropFrame(vm, geo, pos.X, totalDur);

            _drag = new ExternalDropDrag(target, frame, totalDur);
            RequestRedraw();

            var nowMs = Environment.TickCount64;
            if (nowMs - _lastDragDiagTickMs >= 100)
            {
                _lastDragDiagTickMs = nowMs;
                LogDragDiag("over", e, geo, pos, frame, target);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void Canvas_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _drag = null;
        SetExternalSnapX(null);
        if (_context is not { } ctx || Vm is not { } vm)
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var geo = BuildGeometry();
            var pos = DragScreenPosition(e);
            var target = geo.DropTargetAt(DocYForScreen(pos.Y));

            List<MediaAsset> assets;
            if (e.DataView.Contains(ClipRefDragFormat.FormatId))
            {
                assets = await ResolveClipRefAssetsAsync(ctx, e.DataView) ?? [];
            }
            else if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
                if (paths.Count == 0)
                {
                    return;
                }
                var summary = await ctx.ImportPathsAsync(paths);
                assets = [.. summary.Imported.Select(i => i.Asset)];
            }
            else
            {
                return;
            }
            if (assets.Count == 0)
            {
                return;
            }

            var totalDur = assets.Sum(a => vm.ClipDurationFrames(a, null));
            var frame = ComputeExternalDropFrame(vm, geo, pos.X, totalDur);
            LogDragDiag("drop", e, geo, pos, frame, target);
            PlaceExternalDrop(vm, target, frame, assets);
        }
        finally
        {
            deferral.Complete();
            _externalDragAssets = null;
            RequestRedraw();
        }
    }

    private async Task<List<MediaAsset>?> ResolveClipRefAssetsAsync(TimelineCanvasContext ctx, DataPackageView dataView)
    {
        if (!dataView.Contains(ClipRefDragFormat.FormatId))
        {
            return null;
        }
        var json = await dataView.GetTextAsync(ClipRefDragFormat.FormatId);
        var ids = ClipRefDragFormat.Deserialize(json);
        if (ids is null)
        {
            return [];
        }
        return [.. ids.Select(ctx.AssetResolver).Where(a => a is not null).Select(a => a!)];
    }

    private int ComputeExternalDropFrame(TimelineEditorViewModel vm, TimelineGeometry geo, double screenX, int totalDurationFrames)
    {
        var candidate = geo.FrameAt(DocXForScreen(screenX));
        if (_externalDragAssets is not { Count: > 0 })
        {
            SetExternalSnapX(null);
            return candidate;
        }
        var targets = SnapEngine.CollectTargets(vm.Timeline.Tracks);
        var state = _externalSnapState;
        var snap = SnapEngine.FindSnap(candidate, targets, ref state, TimelineInputConstants.Snap.ThresholdPixels, _pixelsPerFrame, [0, totalDurationFrames]);
        _externalSnapState = state;
        if (snap is { } s)
        {
            SetExternalSnapX(TimelineGeometry.Layout.HeaderWidth + s.X - _scrollX);
            return s.Frame - s.ProbeOffset;
        }
        SetExternalSnapX(null);
        return candidate;
    }

    /// Visual (non-audio) assets land on a video-zone track (creating one if the drop target isn't
    /// visual-compatible); audio-only assets land on the drop target if it's already an audio
    /// track, else the first free/auto-created audio track — mirrors `PlaceClip`'s own
    /// video-track-first bias for linked audio rather than forcing a second brand-new track next
    /// to the one just inserted for the visual assets.
    private void PlaceExternalDrop(TimelineEditorViewModel vm, TrackDropTarget target, int frame, List<MediaAsset> assets)
    {
        var visual = assets.Where(a => a.Type != ClipType.Audio).ToList();
        var audioOnly = assets.Where(a => a.Type == ClipType.Audio).ToList();
        if (visual.Count == 0 && audioOnly.Count == 0)
        {
            return;
        }

        vm.Document.UndoService.BeginGrouping();
        try
        {
            if (visual.Count > 0)
            {
                var trackTypes = vm.Timeline.Tracks.Select(t => t.Type).ToList();
                var placement = TimelineDropPlanner.ResolvePlacement(trackTypes, target, ClipType.Video);
                var idx = placement.NeedsNewTrack ? vm.InsertTrack(placement.InsertIndex, placement.PreferredType) : placement.ExistingIndex!.Value;
                vm.AddClips(visual, idx, frame);
            }
            if (audioOnly.Count > 0)
            {
                var trackTypes = vm.Timeline.Tracks.Select(t => t.Type).ToList();
                var placement = TimelineDropPlanner.ResolvePlacement(trackTypes, target, ClipType.Audio);
                var idx = placement.NeedsNewTrack
                    ? vm.ResolveOrCreateAudioTrack(frame, audioOnly.Sum(a => vm.ClipDurationFrames(a, null)))
                    : placement.ExistingIndex!.Value;
                vm.AddClips(audioOnly, idx, frame);
            }
        }
        finally
        {
            vm.Document.UndoService.EndGrouping();
            vm.Document.UndoService.SetActionName("Add Clips");
        }
    }

    // MARK: - Drag-drop diagnostics (telemetry only — no behavior change)

    /// Emits one always-on "dragdiag|" line capturing ground-truth cursor (GetCursorPos, physical
    /// screen px), window/canvas origin + rasterization scale, the raw + divided WinUI drag
    /// position, and the pipeline internals the drop indicator is derived from — enough to
    /// reconstruct, from a live repro, exactly where the gap between cursor and drop indicator
    /// enters the math. Purely observational: reads already-computed values, never mutates drag
    /// state. `phase` is "over" (throttled ~100ms) or "drop" (once).
    private void LogDragDiag(string phase, DragEventArgs e, TimelineGeometry geo, Point screenPos, int frame, TrackDropTarget target)
    {
        var raw = e.GetPosition(Canvas);
        var scale = Canvas.XamlRoot?.RasterizationScale ?? 1.0;
        var cur = TryGetCursorScreen();
        var win = TryGetWindowPosition();
        var off = TryGetCanvasOffset();
        var ghostX = ScreenXForFrame(geo, frame);
        var trackIdx = geo.TrackAt(DocYForScreen(screenPos.Y));
        var insertY = geo.InsertionLineY(target);
        var targetDesc = target switch
        {
            TrackDropTarget.ExistingTrack(var i) => $"Existing:{i}",
            TrackDropTarget.NewTrackAt(var i) => $"NewAt:{i}",
            _ => "?",
        };

        Log.Information("{DragDiag}",
            $"dragdiag|{phase}" +
            $"|t={Environment.TickCount64}" +
            $"|curPx={(cur.ok ? $"{cur.x},{cur.y}" : "na")}" +
            $"|winPos={(win.ok ? $"{win.x},{win.y}" : "na")}" +
            $"|scale={F(scale)}" +
            $"|canvasOff={(off.ok ? $"{F(off.x)},{F(off.y)}" : "na")}" +
            $"|rawGetPos={F(raw.X)},{F(raw.Y)}" +
            $"|screenDiv={F(screenPos.X)},{F(screenPos.Y)}" +
            $"|scrollX={F(_scrollX)}|scrollY={F(_scrollY)}|ppf={F(_pixelsPerFrame)}" +
            $"|frame={frame}|ghostX={F(ghostX)}" +
            $"|dropTarget={targetDesc}|trackIdx={trackIdx}" +
            $"|insertY={(insertY is { } iy ? F(iy) : "na")}" +
            $"|canvas={F(Canvas.ActualWidth)}x{F(Canvas.ActualHeight)}");
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// AppWindow origin in physical screen pixels via the XamlRoot's content island — independent of
    /// which window holds foreground (an Explorer→timeline drag keeps the source app foreground).
    private (bool ok, int x, int y) TryGetWindowPosition()
    {
        try
        {
            if (Canvas.XamlRoot?.ContentIslandEnvironment is { } env)
            {
                var appWindow = AppWindow.GetFromWindowId(env.AppWindowId);
                if (appWindow is not null)
                {
                    return (true, appWindow.Position.X, appWindow.Position.Y);
                }
            }
        }
        catch
        {
            // Diagnostics must never disturb the drag — fall through to "na".
        }
        return (false, 0, 0);
    }

    /// Canvas top-left in the XamlRoot's DIP space (TransformToVisual from the root content).
    private (bool ok, double x, double y) TryGetCanvasOffset()
    {
        try
        {
            if (Canvas.XamlRoot?.Content is UIElement root)
            {
                var p = Canvas.TransformToVisual(root).TransformPoint(new Point(0, 0));
                return (true, p.X, p.Y);
            }
        }
        catch
        {
            // Diagnostics must never disturb the drag — fall through to "na".
        }
        return (false, 0, 0);
    }

    private static (bool ok, int x, int y) TryGetCursorScreen()
    {
        try
        {
            if (GetCursorPos(out var p))
            {
                return (true, p.X, p.Y);
            }
        }
        catch
        {
            // Diagnostics must never disturb the drag — fall through to "na".
        }
        return (false, 0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    // DllImport (not the source-generated LibraryImport) so this stays compatible with the App
    // project's default AllowUnsafeBlocks=false — mirrors MainWindow's GetDpiForWindow P/Invoke.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
}
