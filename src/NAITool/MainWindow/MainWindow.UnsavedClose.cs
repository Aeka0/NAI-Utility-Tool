using System;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NAITool;

public sealed partial class MainWindow
{
    private bool _allowCloseWithUnsavedWorkspace;
    private bool _closeConfirmationOpen;
    private bool _i2iPreviewDirty;
    private bool _upscaleWorkspaceDirty;
    private bool _effectsWorkspaceDirty;

    private void SetupCloseConfirmation()
    {
        if (AppWindow != null)
            AppWindow.Closing += OnAppWindowClosing;
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowCloseWithUnsavedWorkspace || !HasUnsavedWorkspaceChanges(out _))
            return;

        args.Cancel = true;

        if (_closeConfirmationOpen)
            return;

        _ = ConfirmCloseWithUnsavedWorkspaceAsync();
    }

    private async Task ConfirmCloseWithUnsavedWorkspaceAsync()
    {
        if (_closeConfirmationOpen)
            return;

        _closeConfirmationOpen = true;
        try
        {
            if (!HasUnsavedWorkspaceChanges(out string workspaceName))
                return;

            var dialog = new ContentDialog
            {
                Title = L("dialog.unsaved_close.title"),
                Content = Lf("dialog.unsaved_close.content", workspaceName),
                PrimaryButtonText = L("dialog.unsaved_close.close"),
                CloseButtonText = L("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            _allowCloseWithUnsavedWorkspace = true;
            CloseAdvancedParamsWindow();
            Close();
        }
        finally
        {
            _closeConfirmationOpen = false;
        }
    }

    private bool HasUnsavedWorkspaceChanges(out string workspaceName)
    {
        workspaceName = _currentMode switch
        {
            AppMode.I2I => L("mode.i2i"),
            AppMode.Upscale => L("mode.upscale"),
            AppMode.Effects => L("mode.post"),
            _ => "",
        };

        return _currentMode switch
        {
            AppMode.I2I => HasUnsavedI2IWorkspaceChanges(),
            AppMode.Upscale => _upscaleInputImageBytes != null && _upscaleWorkspaceDirty,
            AppMode.Effects => _effectsImageBytes != null && _effectsWorkspaceDirty,
            _ => false,
        };
    }

    private bool HasUnsavedI2IWorkspaceChanges()
    {
        bool hasImage = MaskCanvas.Document.OriginalImage != null
            || (MaskCanvas.IsInPreviewMode && _pendingResultBitmap != null);

        return hasImage && (_i2iPreviewDirty || MaskCanvas.HasWorkspaceChangesSinceClean());
    }

    private void MarkI2IWorkspaceClean()
    {
        _i2iPreviewDirty = false;
        MaskCanvas.MarkWorkspaceClean();
    }

    private void MarkUpscaleWorkspaceClean()
    {
        _upscaleWorkspaceDirty = false;
    }

    private void MarkEffectsWorkspaceDirty()
    {
        if (_effectsImageBytes != null)
            _effectsWorkspaceDirty = true;
    }

    private void MarkEffectsWorkspaceClean()
    {
        _effectsWorkspaceDirty = false;
    }
}
