using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.VisualBasic.FileIO;

namespace NAITool;

public sealed partial class MainWindow
{
    private static readonly TimeSpan NewImageDeleteProtectionDuration = TimeSpan.FromSeconds(1);

    private bool UseRecycleBinForImageDeletion =>
        string.Equals(_settings.Settings.ImageDeleteBehavior, "RecycleBin", StringComparison.OrdinalIgnoreCase);

    private bool TryDeleteImageFileWithConfiguredBehavior(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return true;

        if (TryBlockNewImageDeletion(filePath))
            return false;

        if (UseRecycleBinForImageDeletion)
        {
            FileSystem.DeleteFile(
                filePath,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.DoNothing);
            return true;
        }

        File.Delete(filePath);
        return true;
    }

    private void ArmNewImageDeleteProtection(string? filePath)
    {
        _newImageDeleteProtectionPath = NormalizeImageProtectionPath(filePath);
        _newImageDeleteProtectionCoversUnsavedResult = string.IsNullOrWhiteSpace(filePath);
        _newImageDeleteProtectionUntilUtc = DateTimeOffset.UtcNow.Add(NewImageDeleteProtectionDuration);
    }

    private bool TryBlockNewImageDeletion(string? filePath, bool isCurrentPreviewAction = false)
    {
        if (!_settings.Settings.NewImageDeleteProtection ||
            DateTimeOffset.UtcNow >= _newImageDeleteProtectionUntilUtc)
        {
            return false;
        }

        string? normalizedPath = NormalizeImageProtectionPath(filePath);
        if (!string.IsNullOrEmpty(normalizedPath) &&
            !string.IsNullOrEmpty(_newImageDeleteProtectionPath) &&
            string.Equals(normalizedPath, _newImageDeleteProtectionPath, StringComparison.OrdinalIgnoreCase))
        {
            TxtStatus.Text = L("image.delete_protected_new_result");
            return true;
        }

        if (isCurrentPreviewAction)
        {
            string? currentPath = NormalizeImageProtectionPath(_currentGenImagePath);
            bool protectsCurrentSavedResult =
                !string.IsNullOrEmpty(currentPath) &&
                !string.IsNullOrEmpty(_newImageDeleteProtectionPath) &&
                string.Equals(currentPath, _newImageDeleteProtectionPath, StringComparison.OrdinalIgnoreCase);
            bool protectsCurrentUnsavedResult =
                _newImageDeleteProtectionCoversUnsavedResult &&
                string.IsNullOrWhiteSpace(_currentGenImagePath) &&
                _currentGenImageBytes != null;

            if (protectsCurrentSavedResult || protectsCurrentUnsavedResult)
            {
                TxtStatus.Text = L("image.delete_protected_new_result");
                return true;
            }
        }

        return false;
    }

    private static string? NormalizeImageProtectionPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        try
        {
            return Path.GetFullPath(filePath);
        }
        catch
        {
            return filePath;
        }
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
