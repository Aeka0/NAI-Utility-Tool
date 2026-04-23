using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.VisualBasic.FileIO;

namespace NAITool;

public sealed partial class MainWindow
{
    private bool UseRecycleBinForImageDeletion =>
        string.Equals(_settings.Settings.ImageDeleteBehavior, "RecycleBin", StringComparison.OrdinalIgnoreCase);

    private void DeleteImageFileWithConfiguredBehavior(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        if (UseRecycleBinForImageDeletion)
        {
            FileSystem.DeleteFile(
                filePath,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.DoNothing);
            return;
        }

        File.Delete(filePath);
    }

    private void ClearCurrentGenPreview()
    {
        _currentGenImageBytes = null;
        _currentGenImagePath = null;
        GenPreviewImage.Source = null;
        GenPlaceholder.Visibility = Visibility.Visible;
        UpdateDynamicMenuStates();
    }
}
