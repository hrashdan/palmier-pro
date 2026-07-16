using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PalmierPro.App.ViewModels.Export;
using PalmierPro.Services;
using WinRT.Interop;
using Windows.Storage.Pickers;

namespace PalmierPro.App.Services;

/// Windows.Storage picker-backed IExportOutputDialogService — ports the NSSavePanel flows in
/// ExportView.swift's `startExport()`/`startPalmierExport()`. Automation-scriptable via
/// PALMIER_AUTO_SAVE_PATH, same convention as ProjectDialogService.
public sealed class ExportDialogService(Window window) : IExportOutputDialogService
{
    public async Task<string?> PickSaveFileAsync(string suggestedFileName, string extension, string fileTypeDescription)
    {
        if (AutomationMode.Enabled)
        {
            return AutomationMode.NextSavePath();
        }
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
            SuggestedFileName = suggestedFileName,
        };
        picker.FileTypeChoices.Add(fileTypeDescription, [$".{extension}"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public async Task<(string Directory, string Name)?> PickPalmierProjectLocationAsync(string suggestedName)
    {
        if (AutomationMode.Enabled)
        {
            var path = AutomationMode.NextSavePath();
            var directory = string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
            var name = string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);
            return string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name) ? null : (directory, name);
        }
        var folder = await PickFolderAsync();
        if (folder is null)
        {
            return null;
        }
        var promptedName = await PromptForNameAsync(suggestedName);
        return promptedName is null ? null : (folder, promptedName);
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async Task<string?> PromptForNameAsync(string suggestedName)
    {
        var textBox = new TextBox { Text = suggestedName };
        textBox.SelectAll();
        var dialog = new ContentDialog
        {
            Title = "Export Palmier Project",
            Content = textBox,
            PrimaryButtonText = "Export",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = window.Content.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }
        var trimmed = textBox.Text.Trim();
        return trimmed.Length == 0 ? suggestedName : trimmed;
    }
}
