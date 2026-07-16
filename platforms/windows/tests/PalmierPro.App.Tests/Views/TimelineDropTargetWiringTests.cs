using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace PalmierPro.App.Tests.Views;

/// Guards where the timeline's external-drop wiring lives. The timeline body is a Win2D
/// CanvasControl — a sealed UserControl whose content is a CanvasImageSource (a compositor-
/// presented SurfaceImageSource). That surface takes pointer input fine but is skipped by the XAML
/// drag-drop hit-test, so an external drag over it never raises DragEnter and the whole timeline
/// rejects the drop (the live-repro symptom: zero "dragdiag|" telemetry, OS no-drop cursor). The
/// fix hangs AllowDrop + the four Drag* handlers on the plain, brush-backed RootGrid — a reliable
/// drop target the bubbling drag events resolve up to — and keeps them OFF the CanvasControl. These
/// tests pin that split so a future edit can't silently move the wiring back onto the canvas and
/// re-break the drop. Parsed as plain XML (XDocument, like ThemeParityTests) — `dotnet test` has no
/// WinUI host to load the real control (see platforms/windows/AGENTS.md, TestSupport.cs).
public class TimelineDropTargetWiringTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] DragHandlers = ["DragEnter", "DragOver", "DragLeave", "Drop"];

    private static XElement NamedElement(string name, [CallerFilePath] string here = "")
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(here)!, "..", "..", "..",
            "src", "PalmierPro.App", "Views", "Timeline", "TimelineCanvasControl.xaml"));
        return XDocument.Load(path).Descendants()
            .Single(e => e.Attribute(X + "Name")?.Value == name);
    }

    [Fact]
    public void RootGridIsTheDropTarget()
    {
        var rootGrid = NamedElement("RootGrid");
        rootGrid.Name.LocalName.ShouldBe("Grid");
        rootGrid.Attribute("AllowDrop")?.Value.ShouldBe("True", "RootGrid must allow drops.");
        foreach (var handler in DragHandlers)
        {
            rootGrid.Attribute(handler)?.Value.ShouldBe(
                $"Canvas_{handler}", $"RootGrid must wire {handler} to the drop handler.");
        }
    }

    [Fact]
    public void CanvasControlCarriesNoDropWiring()
    {
        var canvas = NamedElement("Canvas");
        canvas.Name.LocalName.ShouldBe("CanvasControl");
        canvas.Attribute("AllowDrop").ShouldBeNull(
            "AllowDrop must not sit on the CanvasControl — the XAML drag hit-test skips its SurfaceImageSource surface, so drops there never fire.");
        foreach (var handler in DragHandlers)
        {
            canvas.Attribute(handler).ShouldBeNull(
                $"{handler} must not sit on the CanvasControl (see class remarks — it re-breaks the drop).");
        }
    }
}
