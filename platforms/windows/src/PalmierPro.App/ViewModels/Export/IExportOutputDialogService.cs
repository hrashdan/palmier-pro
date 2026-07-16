namespace PalmierPro.App.ViewModels.Export;

/// Picker seam ExportViewModel depends on instead of Windows.Storage.Pickers directly — mirrors
/// IProjectDialogService's/IMediaImportDialogService's rationale, keeping the ViewModel
/// instantiable under plain `dotnet test`. The real implementation (Services/ExportDialogService.cs)
/// answers automation-scriptable via PALMIER_AUTO_SAVE_PATH, same as ProjectDialogService.
public interface IExportOutputDialogService
{
    /// Video/FCPXML/XMEML destinations: a single output file. `extension` has no leading dot.
    /// Null means the user cancelled.
    Task<string?> PickSaveFileAsync(string suggestedFileName, string extension, string fileTypeDescription);

    /// PalmierProject destination: a `.palmier` package is a directory on Windows (no
    /// UTType-package concept), so — like IProjectDialogService.PickProjectLocationAsync — this is
    /// a folder pick plus a name prompt rather than one native save dialog. Null means cancelled.
    Task<(string Directory, string Name)?> PickPalmierProjectLocationAsync(string suggestedName);
}
