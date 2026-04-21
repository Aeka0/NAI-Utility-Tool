using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using NAITool.Controls;
using NAITool.Models;
using NAITool.Services;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using System.Runtime.InteropServices.WindowsRuntime;

namespace NAITool;

public sealed partial class MainWindow
{
    private static bool IsPngFilePath(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".png", StringComparison.OrdinalIgnoreCase);

    private static string ResolveOverwriteSavePath(string filePath, out bool redirectedToPng)
    {
        redirectedToPng = !IsPngFilePath(filePath);
        return redirectedToPng ? Path.ChangeExtension(filePath, ".png") : filePath;
    }

    private static bool HasPngSignature(byte[] data) =>
        data.Length >= 8 &&
        data[0] == 137 &&
        data[1] == 80 &&
        data[2] == 78 &&
        data[3] == 71 &&
        data[4] == 13 &&
        data[5] == 10 &&
        data[6] == 26 &&
        data[7] == 10;

    private static byte[] EnsurePngEncoded(byte[] imageBytes)
    {
        if (HasPngSignature(imageBytes))
            return imageBytes;

        using var bitmap = SKBitmap.Decode(imageBytes);
        if (bitmap == null)
            return imageBytes;

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray() ?? imageBytes;
    }

    // ═══════════════════════════════════════════════════════════
    //  菜单事件
    // ═══════════════════════════════════════════════════════════

    private async void OnOpenImage(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".bmp");
        picker.FileTypeFilter.Add(".webp");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            if (_currentMode == AppMode.Inspect)
            {
                await LoadInspectImageAsync(file.Path);
            }
            else if (_currentMode == AppMode.Effects)
            {
                await LoadEffectsImageAsync(file.Path);
            }
            else if (_currentMode == AppMode.Upscale)
            {
                await LoadUpscaleImageAsync(file.Path);
            }
            else if (_currentMode == AppMode.I2I)
            {
                await MaskCanvas.LoadImageAsync(file);
            }
            else if (_currentMode == AppMode.ImageGeneration)
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(file.Path);
                    var meta = await Task.Run(() => ImageMetadataService.ReadFromBytes(bytes));
                    if (meta != null && (meta.IsNaiParsed || meta.IsSdFormat || meta.IsModelInference))
                        ApplyMetadataToGeneration(meta);
                    else
                        TxtStatus.Text = Lf("metadata.no_usable_generation_metadata", file.Name);
                }
                catch (Exception ex) { TxtStatus.Text = Lf("common.read_failed", ex.Message); }
            }
        }
    }

    private async void OnSaveOverwrite(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.Inspect)
        {
            await SaveInspectOverwriteAsync();
        }
        else if (_currentMode == AppMode.Effects)
        {
            await SaveEffectsOverwriteAsync();
        }
        else if (_currentMode == AppMode.I2I)
        {
            await SaveInpaintOverwriteAsync();
        }
        else
        {
            if (_currentGenImageBytes == null)
            { TxtStatus.Text = L("file.error.no_generated_result_to_save"); return; }
            if (!string.IsNullOrEmpty(_currentGenImagePath) && File.Exists(_currentGenImagePath))
            {
                TxtStatus.Text = Lf("file.saved_path", _currentGenImagePath);
            }
            else
            {
                TxtStatus.Text = L("generate.status.auto_saved_output");
            }
        }
    }

    private async Task SaveInpaintOverwriteAsync()
    {
        var filePath = MaskCanvas.LoadedFilePath;
        if (string.IsNullOrEmpty(filePath))
        { TxtStatus.Text = L("inpaint.error.no_image_to_save"); return; }

        byte[]? previewBytes;
        try { previewBytes = await BuildI2IRawSaveBytesAsync(); }
        catch (Exception ex) { TxtStatus.Text = Lf("inpaint.error.compose_preview_failed", ex.Message); return; }

        if (previewBytes == null || previewBytes.Length == 0)
        { TxtStatus.Text = L("file.error.no_image_content_to_save"); return; }

        var savePath = ResolveOverwriteSavePath(filePath, out bool redirectedToPng);

        if (!redirectedToPng)
        {
            string sizeWarning = "";
            try
            {
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Exists)
                {
                    using var origStream = File.OpenRead(filePath);
                    using var skBmp = SkiaSharp.SKBitmap.Decode(origStream);
                    if (skBmp != null)
                    {
                        using var newStream = new MemoryStream(previewBytes);
                        using var newBmp = SkiaSharp.SKBitmap.Decode(newStream);
                        if (newBmp != null && (skBmp.Width != newBmp.Width || skBmp.Height != newBmp.Height))
                            sizeWarning = Lf("file.confirm_save.size_warning", skBmp.Width, skBmp.Height, newBmp.Width, newBmp.Height);
                    }
                }
            }
            catch { }

            var dialog = new ContentDialog
            {
                Title = L("file.confirm_save.title"),
                Content = Lf("file.confirm_save.overwrite", filePath, sizeWarning),
                PrimaryButtonText = L("common.save"),
                CloseButtonText = L("common.cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }

        byte[]? bytesToSave;
        try { bytesToSave = await BuildI2ISaveBytesAsync(stripMetadata: false); }
        catch (Exception ex) { TxtStatus.Text = Lf("inpaint.error.compose_preview_failed", ex.Message); return; }

        if (bytesToSave == null || bytesToSave.Length == 0)
        { TxtStatus.Text = L("file.error.no_image_content_to_save"); return; }

        if (redirectedToPng)
            bytesToSave = await Task.Run(() => EnsurePngEncoded(bytesToSave));

        try
        {
            await File.WriteAllBytesAsync(savePath, bytesToSave);
            if (redirectedToPng)
                MaskCanvas.SetLoadedFilePath(savePath);
            MarkI2IWorkspaceClean();
            TxtStatus.Text = Lf("file.saved_path", savePath);
        }
        catch (Exception ex) { TxtStatus.Text = Lf("common.save_failed", ex.Message); }
    }

    private async Task<byte[]?> CreateCurrentFullImageBytes()
    {
        var device = MaskCanvas.GetDevice();
        if (device == null) return null;
        var doc = MaskCanvas.Document;
        if (doc.OriginalImage == null) return null;

        var offset = doc.PixelAlignedImageOffset;
        int canvasW = MaskCanvas.CanvasW, canvasH = MaskCanvas.CanvasH;
        float origW = doc.OriginalImage.SizeInPixels.Width;
        float origH = doc.OriginalImage.SizeInPixels.Height;

        float canvasInOrigX = -offset.X, canvasInOrigY = -offset.Y;
        float minX = Math.Min(0, canvasInOrigX);
        float minY = Math.Min(0, canvasInOrigY);
        float maxX = Math.Max(origW, canvasInOrigX + canvasW);
        float maxY = Math.Max(origH, canvasInOrigY + canvasH);
        int compositeW = (int)Math.Ceiling(maxX - minX);
        int compositeH = (int)Math.Ceiling(maxY - minY);

        using var composite = new CanvasRenderTarget(device, compositeW, compositeH, 96f);
        using (var ds = composite.CreateDrawingSession())
        {
            ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ds.DrawImage(doc.OriginalImage, -minX, -minY);
        }

        using var saveStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await composite.SaveAsync(saveStream, CanvasBitmapFileFormat.Png);
        saveStream.Seek(0);
        var bytes = new byte[saveStream.Size];
        using var reader = new Windows.Storage.Streams.DataReader(saveStream);
        await reader.LoadAsync((uint)saveStream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private async void OnSaveAs(object sender, RoutedEventArgs e)
    {
        await SaveAsInternal(stripMetadata: false);
    }

    private async void OnSaveAsStripped(object sender, RoutedEventArgs e)
    {
        await SaveAsInternal(stripMetadata: true);
    }

    private static string GetSuggestedSaveFileName(string? sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            var fileName = Path.GetFileNameWithoutExtension(sourcePath);
            if (!string.IsNullOrWhiteSpace(fileName))
                return fileName;
        }

        return $"nai_{DateTime.Now:yyyyMMdd_HHmmss}";
    }

    private async Task SaveImageBytesAsAsync(byte[]? imageBytes, bool stripMetadata, string? sourcePath = null)
    {
        if (imageBytes == null || imageBytes.Length == 0)
        {
            TxtStatus.Text = L("file.error.no_image_to_save");
            return;
        }

        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add(L("file.png_image"), new List<string> { ".png" });
        picker.SuggestedFileName = GetSuggestedSaveFileName(sourcePath);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSaveFileAsync();
        if (file == null)
            return;

        var bytesToSave = stripMetadata
            ? await Task.Run(() => ImageMetadataService.StripPngMetadata(imageBytes))
            : imageBytes;

        if (bytesToSave == null || bytesToSave.Length == 0)
        {
            TxtStatus.Text = L("file.error.no_image_to_save");
            return;
        }

        bytesToSave = await Task.Run(() => EnsurePngEncoded(bytesToSave));

        try
        {
            await Windows.Storage.FileIO.WriteBytesAsync(file, bytesToSave);
            TxtStatus.Text = stripMetadata
                ? Lf("file.saved_path_stripped", file.Path)
                : Lf("file.saved_path", file.Path);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("common.save_failed", ex.Message);
        }
    }

    private async Task SaveAsInternal(bool stripMetadata)
    {
        byte[]? bytesToSave = null;

        bool hasImageToSave = _currentMode switch
        {
            AppMode.Inspect => _inspectImageBytes != null,
            AppMode.Effects => _effectsImageBytes != null,
            AppMode.I2I => MaskCanvas.Document.OriginalImage != null ||
                           (MaskCanvas.IsInPreviewMode && _pendingResultBitmap != null),
            AppMode.Upscale => _upscaleInputImageBytes != null,
            _ => _currentGenImageBytes != null,
        };

        if (!hasImageToSave)
        { TxtStatus.Text = L("file.error.no_image_to_save"); return; }

        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add(L("file.png_image"), new List<string> { ".png" });
        picker.SuggestedFileName = $"nai_{DateTime.Now:yyyyMMdd_HHmmss}";
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file != null)
        {
            if (_currentMode == AppMode.Inspect)
            {
                bytesToSave = await GetInspectSaveBytesAsync(stripMetadata);
            }
            else if (_currentMode == AppMode.Effects)
            {
                bytesToSave = await GetEffectsSaveBytesAsync();
            }
            else if (_currentMode == AppMode.I2I)
            {
                try { bytesToSave = await BuildI2ISaveBytesAsync(stripMetadata); }
                catch (Exception ex) { TxtStatus.Text = Lf("inpaint.error.compose_preview_failed", ex.Message); return; }
            }
            else if (_currentMode == AppMode.Upscale)
            {
                bytesToSave = _upscaleInputImageBytes;
            }
            else
            {
                bytesToSave = stripMetadata && _currentGenImageBytes != null
                    ? await Task.Run(() => ImageMetadataService.StripPngMetadata(_currentGenImageBytes))
                    : _currentGenImageBytes;
            }

            if (bytesToSave == null || bytesToSave.Length == 0)
            { TxtStatus.Text = L("file.error.no_image_to_save"); return; }

            bytesToSave = await Task.Run(() => EnsurePngEncoded(bytesToSave));

            try
            {
                await Windows.Storage.FileIO.WriteBytesAsync(file, bytesToSave);
                if (_currentMode == AppMode.I2I)
                    MarkI2IWorkspaceClean();
                else if (_currentMode == AppMode.Upscale)
                {
                    _upscaleImagePath = file.Path;
                    MarkUpscaleWorkspaceClean();
                }
                else if (_currentMode == AppMode.Effects)
                {
                    _effectsImagePath = file.Path;
                    MarkEffectsWorkspaceClean();
                    RefreshEffectsPanel();
                }
                TxtStatus.Text = stripMetadata
                    ? Lf("file.saved_path_stripped", file.Path)
                    : Lf("file.saved_path", file.Path);
            }
            catch (Exception ex) { TxtStatus.Text = Lf("common.save_failed", ex.Message); }
        }
    }

    private async void OnExportCanvasMask(object sender, RoutedEventArgs e)
    {
        if (_currentMode != AppMode.I2I || MaskCanvas.Document?.OriginalImage == null)
        {
            TxtStatus.Text = L("i2i.export.error.no_image");
            return;
        }

        await ShowExportCanvasMaskDialogAsync();
    }

    private async Task<byte[]?> CreatePreviewCompositeBytes()
    {
        var device = MaskCanvas.GetDevice();
        if (device == null || _pendingResultBitmap == null) return null;

        var doc = MaskCanvas.Document;
        if (doc.OriginalImage == null) return _pendingResultBytes;

        var offset = doc.PixelAlignedImageOffset;
        int canvasW = MaskCanvas.CanvasW, canvasH = MaskCanvas.CanvasH;
        float origW = doc.OriginalImage.SizeInPixels.Width;
        float origH = doc.OriginalImage.SizeInPixels.Height;

        float canvasInOrigX = -offset.X, canvasInOrigY = -offset.Y;
        float minX = Math.Min(0, canvasInOrigX);
        float minY = Math.Min(0, canvasInOrigY);
        float maxX = Math.Max(origW, canvasInOrigX + canvasW);
        float maxY = Math.Max(origH, canvasInOrigY + canvasH);
        int compositeW = (int)Math.Ceiling(maxX - minX);
        int compositeH = (int)Math.Ceiling(maxY - minY);

        if (compositeW == canvasW && compositeH == canvasH && _pendingResultBytes != null)
            return _pendingResultBytes;

        using var composite = new CanvasRenderTarget(device, compositeW, compositeH, 96f);
        using (var ds = composite.CreateDrawingSession())
        {
            ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ds.DrawImage(doc.OriginalImage, -minX, -minY);
            ds.DrawImage(_pendingResultBitmap, canvasInOrigX - minX, canvasInOrigY - minY);
        }

        using var saveStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await composite.SaveAsync(saveStream, CanvasBitmapFileFormat.Png);
        saveStream.Seek(0);
        var bytes = new byte[saveStream.Size];
        using var reader = new Windows.Storage.Streams.DataReader(saveStream);
        await reader.LoadAsync((uint)saveStream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private async Task<byte[]?> BuildI2ISaveBytesAsync(bool stripMetadata)
    {
        byte[]? bytes;
        IReadOnlyDictionary<string, string>? textChunks;

        if (MaskCanvas.IsInPreviewMode && _pendingResultBitmap != null)
        {
            bytes = await CreatePreviewCompositeBytes();
            textChunks = _pendingResultTextChunks;
            if (!stripMetadata && bytes != null && ReferenceEquals(bytes, _pendingResultBytes))
                return bytes;
        }
        else
        {
            if (!stripMetadata && CanSaveCurrentI2IImageFromRawBytes())
                return _lastGeneratedImageBytes;

            bytes = await CreateCurrentFullImageBytes();
            textChunks = _i2iImageTextChunks;
        }

        if (bytes == null || bytes.Length == 0)
            return bytes;

        if (stripMetadata)
            return await Task.Run(() => ImageMetadataService.StripPngMetadata(bytes));

        return await Task.Run(() => ImageMetadataService.ReapplyNovelAiMetadata(bytes, textChunks));
    }

    private async Task<byte[]?> BuildI2IRawSaveBytesAsync()
    {
        if (MaskCanvas.IsInPreviewMode && _pendingResultBitmap != null)
            return await CreatePreviewCompositeBytes();

        if (_lastGeneratedImageBytes != null && _lastGeneratedImageBytes.Length > 0)
            return _lastGeneratedImageBytes;

        return await CreateCurrentFullImageBytes();
    }

    private bool CanSaveCurrentI2IImageFromRawBytes()
    {
        if (_lastGeneratedImageBytes == null || _lastGeneratedImageBytes.Length == 0)
            return false;
        if (_i2iImageTextChunks == null || _i2iImageTextChunks.Count == 0)
            return false;
        if (ImageMetadataService.ReadPngTextChunks(_lastGeneratedImageBytes).Count == 0)
            return false;

        var original = MaskCanvas.Document.OriginalImage;
        if (original == null)
            return false;

        var offset = MaskCanvas.Document.PixelAlignedImageOffset;
        return offset.X == 0 &&
               offset.Y == 0 &&
               (int)original.SizeInPixels.Width == MaskCanvas.CanvasW &&
               (int)original.SizeInPixels.Height == MaskCanvas.CanvasH;
    }

    private void OnOpenImageFolder(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.ImageGeneration)
        {
            if (!string.IsNullOrEmpty(_currentGenImagePath) && File.Exists(_currentGenImagePath))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_currentGenImagePath}\"");
                return;
            }
            var outputDir = OutputBaseDir;
            if (Directory.Exists(outputDir))
            {
                System.Diagnostics.Process.Start("explorer.exe", outputDir);
                return;
            }
            TxtStatus.Text = L("file.error.no_output_folder");
            return;
        }

        if (_currentMode == AppMode.Effects)
        {
            if (string.IsNullOrEmpty(_effectsImagePath) || !File.Exists(_effectsImagePath))
            { TxtStatus.Text = L("file.error.no_image_for_folder"); return; }
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_effectsImagePath}\"");
            return;
        }

        if (_currentMode == AppMode.Upscale)
        {
            if (string.IsNullOrEmpty(_upscaleImagePath) || !File.Exists(_upscaleImagePath))
            { TxtStatus.Text = L("file.error.no_image_for_folder"); return; }
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_upscaleImagePath}\"");
            return;
        }

        var path = MaskCanvas.LoadedFilePath;
        if (string.IsNullOrEmpty(path))
        { TxtStatus.Text = L("file.error.no_image_for_folder"); return; }
        var dir = Path.GetDirectoryName(path);
        if (dir != null && Directory.Exists(dir))
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        else
            TxtStatus.Text = L("file.error.folder_not_found");
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnReloadImage(object sender, RoutedEventArgs e)
    {
        await ReloadCurrentWorkspaceImageAsync();
    }

    private async Task ReloadCurrentWorkspaceImageAsync()
    {
        if (_currentMode == AppMode.I2I)
        {
            var path = MaskCanvas.LoadedFilePath;
            if (MaskCanvas.IsInPreviewMode)
            {
                TxtStatus.Text = L("image.reload.blocked_preview");
                return;
            }
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                TxtStatus.Text = L("image.reload.no_source");
                return;
            }

            bool wasDirty = MaskCanvas.HasWorkspaceChangesSinceClean();
            bool previewDirty = _i2iPreviewDirty;
            await MaskCanvas.ReloadImagePreservingWorkspaceAsync(path);
            if (!wasDirty && !previewDirty)
                MarkI2IWorkspaceClean();
            _i2iPreviewDirty = previewDirty;
            UpdateDynamicMenuStates();
            return;
        }

        if (_currentMode == AppMode.Upscale)
        {
            if (_upscaleRunning)
                return;
            if (string.IsNullOrWhiteSpace(_upscaleImagePath) || !File.Exists(_upscaleImagePath))
            {
                TxtStatus.Text = L("image.reload.no_source");
                return;
            }

            await LoadUpscaleImageAsync(_upscaleImagePath, preserveDirtyState: true);
            UpdateDynamicMenuStates();
            return;
        }

        if (_currentMode == AppMode.Effects)
        {
            if (string.IsNullOrWhiteSpace(_effectsImagePath) || !File.Exists(_effectsImagePath))
            {
                TxtStatus.Text = L("image.reload.no_source");
                return;
            }

            await ReloadEffectsImageAsync(_effectsImagePath);
            UpdateDynamicMenuStates();
        }
    }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.I2I && _i2iEditMode == I2IEditMode.Inpaint && !MaskCanvas.IsInPreviewMode)
        {
            MaskCanvas.PerformUndo();
            return;
        }

        if (_currentMode == AppMode.Effects && _effectsUndoStack.Count > 0)
        {
            _effectsRedoStack.Push(CaptureEffectsWorkspaceState());
            var state = _effectsUndoStack.Pop();
            await RestoreEffectsWorkspaceStateAsync(state);
            TxtStatus.Text = L("post.status.undo");
        }
    }

    private async void OnRedo(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.I2I && _i2iEditMode == I2IEditMode.Inpaint && !MaskCanvas.IsInPreviewMode)
        {
            MaskCanvas.PerformRedo();
            return;
        }

        if (_currentMode == AppMode.Effects && _effectsRedoStack.Count > 0)
        {
            _effectsUndoStack.Push(CaptureEffectsWorkspaceState());
            var state = _effectsRedoStack.Pop();
            await RestoreEffectsWorkspaceStateAsync(state);
            TxtStatus.Text = L("post.status.redo");
        }
    }

    private void OnClearMask(object sender, RoutedEventArgs e)
    { if (_currentMode == AppMode.I2I && _i2iEditMode == I2IEditMode.Inpaint && !MaskCanvas.IsInPreviewMode) MaskCanvas.ClearMask(); }

    private void OnFillEmpty(object sender, RoutedEventArgs e)
    {
        if (_currentMode != AppMode.I2I || _i2iEditMode != I2IEditMode.Inpaint || MaskCanvas.IsInPreviewMode) return;
        MaskCanvas.FillEmptyAreas();
        TxtStatus.Text = L("inpaint.mask.fill_empty_done");
    }

    private void OnInvertMask(object sender, RoutedEventArgs e)
    {
        if (_currentMode != AppMode.I2I || _i2iEditMode != I2IEditMode.Inpaint || MaskCanvas.IsInPreviewMode) return;
        MaskCanvas.InvertMask();
        TxtStatus.Text = L("inpaint.mask.inverted");
    }

    private void OnExpandMask(object sender, RoutedEventArgs e)
    {
        if (_currentMode != AppMode.I2I || _i2iEditMode != I2IEditMode.Inpaint || MaskCanvas.IsInPreviewMode) return;
        MaskCanvas.ExpandMask();
        TxtStatus.Text = L("inpaint.mask.expanded");
    }

    private void OnShrinkMask(object sender, RoutedEventArgs e)
    {
        if (_currentMode != AppMode.I2I || _i2iEditMode != I2IEditMode.Inpaint || MaskCanvas.IsInPreviewMode) return;
        MaskCanvas.ShrinkMask();
        TxtStatus.Text = L("inpaint.mask.shrunk");
    }

    private void OnTrimCanvas(object sender, RoutedEventArgs e)
    {
        if (_currentMode != AppMode.I2I) return;
        if (MaskCanvas.IsInPreviewMode) { TxtStatus.Text = L("inpaint.canvas.trim_blocked_preview"); return; }
        if (MaskCanvas.TrimCanvas())
            TxtStatus.Text = Lf("inpaint.canvas.trimmed", MaskCanvas.CanvasW, MaskCanvas.CanvasH);
        else
            TxtStatus.Text = L("inpaint.canvas.trim_empty");
    }

    private void OnFitToScreen(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.I2I) MaskCanvas.FitToScreen();
        else if (_currentMode == AppMode.Inspect) FitInspectPreviewToScreen();
        else if (_currentMode == AppMode.Upscale) FitUpscalePreviewToScreen();
        else if (_currentMode == AppMode.Effects) FitEffectsPreviewToScreen();
        else FitGenPreviewToScreen();
    }
    private void OnActualSize(object sender, RoutedEventArgs e)
    {
        float dpiScale = (float)(this.Content.XamlRoot?.RasterizationScale ?? 1.0);
        float trueZoom = 1.0f / dpiScale;
        if (_currentMode == AppMode.I2I) MaskCanvas.ActualSize();
        else if (_currentMode == AppMode.Inspect) InspectImageScroller.ChangeView(null, null, trueZoom);
        else if (_currentMode == AppMode.Upscale) UpscaleImageScroller.ChangeView(null, null, trueZoom);
        else if (_currentMode == AppMode.Effects) EffectsImageScroller.ChangeView(null, null, trueZoom);
        else GenImageScroller.ChangeView(null, null, trueZoom);
    }
    private void OnCenterView(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.I2I) MaskCanvas.CenterView();
        else if (_currentMode == AppMode.Inspect) FitInspectPreviewToScreen();
        else if (_currentMode == AppMode.Upscale) FitUpscalePreviewToScreen();
        else if (_currentMode == AppMode.Effects) CenterEffectsPreview();
        else FitGenPreviewToScreen();
    }

    private Microsoft.UI.Windowing.OverlappedPresenterState _lastPresenterState;
    private void OnMaskCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_currentMode != AppMode.I2I) return;
        if (AppWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
        {
            var state = p.State;
            if (state != _lastPresenterState)
            {
                _lastPresenterState = state;
                MaskCanvas.CenterView();
            }
        }
        QueueThumbnailRender();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.I2I) MaskCanvas.ZoomIn();
        else if (_currentMode == AppMode.Inspect)
            InspectImageScroller.ChangeView(null, null, InspectImageScroller.ZoomFactor * 1.25f);
        else if (_currentMode == AppMode.Upscale)
            UpscaleImageScroller.ChangeView(null, null, UpscaleImageScroller.ZoomFactor * 1.25f);
        else if (_currentMode == AppMode.Effects)
            EffectsImageScroller.ChangeView(null, null, EffectsImageScroller.ZoomFactor * 1.25f);
        else GenImageScroller.ChangeView(null, null, GenImageScroller.ZoomFactor * 1.25f);
    }
    private void OnZoomOut(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.I2I) MaskCanvas.ZoomOut();
        else if (_currentMode == AppMode.Inspect)
            InspectImageScroller.ChangeView(null, null, InspectImageScroller.ZoomFactor / 1.25f);
        else if (_currentMode == AppMode.Upscale)
            UpscaleImageScroller.ChangeView(null, null, UpscaleImageScroller.ZoomFactor / 1.25f);
        else if (_currentMode == AppMode.Effects)
            EffectsImageScroller.ChangeView(null, null, EffectsImageScroller.ZoomFactor / 1.25f);
        else GenImageScroller.ChangeView(null, null, GenImageScroller.ZoomFactor / 1.25f);
    }

    private void SetupPreviewScrollZoomAndDrag()
    {
        GenImageScroller.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);
        GenPreviewImage.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);
        InspectImageScroller.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);
        InspectPreviewImage.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);
        EffectsImageScroller.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);
        EffectsPreviewContent.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);
        EffectsOverlayCanvas.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);
        UpscaleImageScroller.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);
        UpscalePreviewImage.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);

        GenPreviewImage.PointerPressed += OnPreviewDragStart;
        GenPreviewImage.PointerMoved += OnPreviewDragMove;
        GenPreviewImage.PointerReleased += OnPreviewDragEnd;
        GenPreviewImage.PointerCanceled += OnPreviewDragEnd;
        GenPreviewImage.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnGenPreviewPointerPressed), true);

        InspectPreviewImage.PointerPressed += OnPreviewDragStart;
        InspectPreviewImage.PointerMoved += OnPreviewDragMove;
        InspectPreviewImage.PointerReleased += OnPreviewDragEnd;
        InspectPreviewImage.PointerCanceled += OnPreviewDragEnd;

        EffectsPreviewImage.PointerPressed += OnPreviewDragStart;
        EffectsPreviewImage.PointerMoved += OnPreviewDragMove;
        EffectsPreviewImage.PointerReleased += OnPreviewDragEnd;
        EffectsPreviewImage.PointerCanceled += OnPreviewDragEnd;

        UpscalePreviewImage.PointerPressed += OnPreviewDragStart;
        UpscalePreviewImage.PointerMoved += OnPreviewDragMove;
        UpscalePreviewImage.PointerReleased += OnPreviewDragEnd;
        UpscalePreviewImage.PointerCanceled += OnPreviewDragEnd;
    }

    private void OnPreviewWheelZoom(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled) return;
        var sv = GetPreviewScroller(sender);
        if (sv == null) return;
        var point = e.GetCurrentPoint(sv);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;

        float factor = delta > 0 ? 1.15f : (1f / 1.15f);
        float newZoom = Math.Clamp(sv.ZoomFactor * factor, sv.MinZoomFactor, sv.MaxZoomFactor);

        double mouseX = point.Position.X;
        double mouseY = point.Position.Y;
        double contentX = (sv.HorizontalOffset + mouseX) / sv.ZoomFactor;
        double contentY = (sv.VerticalOffset + mouseY) / sv.ZoomFactor;
        double newOffsetX = contentX * newZoom - mouseX;
        double newOffsetY = contentY * newZoom - mouseY;

        sv.ChangeView(Math.Max(0, newOffsetX), Math.Max(0, newOffsetY), newZoom, false);
        e.Handled = true;
    }

    private ScrollViewer? GetPreviewScroller(object? sender)
    {
        if (sender is ScrollViewer sv) return sv;
        if (ReferenceEquals(sender, GenPreviewImage)) return GenImageScroller;
        if (ReferenceEquals(sender, InspectPreviewImage)) return InspectImageScroller;
        if (ReferenceEquals(sender, EffectsPreviewContent) || ReferenceEquals(sender, EffectsOverlayCanvas))
            return EffectsImageScroller;
        if (ReferenceEquals(sender, UpscalePreviewImage)) return UpscaleImageScroller;
        return null;
    }

    private void OnPreviewDragStart(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement el) return;
        var props = e.GetCurrentPoint(el).Properties;
        if (!props.IsLeftButtonPressed) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control);
        if (ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        var sv = el switch
        {
            var _ when el == GenPreviewImage => GenImageScroller,
            var _ when el == InspectPreviewImage => InspectImageScroller,
            var _ when el == EffectsPreviewImage || el == EffectsOverlayCanvas => EffectsImageScroller,
            var _ when el == UpscalePreviewImage => UpscaleImageScroller,
            _ => InspectImageScroller,
        };
        _imgDragging = true;
        _imgDragScroller = sv;
        _imgDragStart = e.GetCurrentPoint(sv).Position;
        _imgDragStartH = sv.HorizontalOffset;
        _imgDragStartV = sv.VerticalOffset;
        el.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPreviewDragMove(object sender, PointerRoutedEventArgs e)
    {
        if (!_imgDragging || _imgDragScroller == null) return;
        var pos = e.GetCurrentPoint(_imgDragScroller).Position;
        double dx = _imgDragStart.X - pos.X;
        double dy = _imgDragStart.Y - pos.Y;
        _imgDragScroller.ChangeView(_imgDragStartH + dx, _imgDragStartV + dy, null, true);
        e.Handled = true;
    }

    private void OnPreviewDragEnd(object sender, PointerRoutedEventArgs e)
    {
        if (!_imgDragging) return;
        _imgDragging = false;
        _imgDragScroller = null;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private async void OnGenPreviewPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(sender as UIElement);
        if (!pt.Properties.IsLeftButtonPressed) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control);
        if (!ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        if (_currentGenImageBytes == null) return;

        await ApplyDroppedImageMetadata(_currentGenImageBytes, L("image.preview_label"), skipSeed: true);
        e.Handled = true;
    }

    private void OnAlign(object sender, RoutedEventArgs e)
    {
        if (_currentMode != AppMode.I2I || MaskCanvas.IsInPreviewMode) return;
        if (sender is MenuFlyoutItem item && item.Tag is string tag)
        {
            MaskCanvas.AlignImage(tag);
            TxtStatus.Text = L("image.aligned");
        }
    }

    private async Task ShowExportCanvasMaskDialogAsync()
    {
        var device = MaskCanvas.GetDevice();
        if (device == null || MaskCanvas.Document?.OriginalImage == null) return;

        var cbArea = new ComboBox
        {
            ItemsSource = new[] { L("i2i.export.area.canvas"), L("i2i.export.area.whole") },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var cbType = new ComboBox
        {
            ItemsSource = new[]
            {
                L("i2i.export.type.image"),
                L("i2i.export.type.mask"),
                L("i2i.export.type.merged"),
                L("i2i.export.type.separated")
            },
            SelectedIndex = 2, // Default to merged
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var cbColor = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 16)
        };

        void UpdateColorOptions()
        {
            if (cbType.SelectedIndex == 0) // Image Only
            {
                cbColor.ItemsSource = new[] { L("i2i.export.color.white_on_black") };
                cbColor.SelectedIndex = 0;
                cbColor.IsEnabled = false;
            }
            else if (cbType.SelectedIndex == 2) // Merged
            {
                cbColor.ItemsSource = new[]
                {
                    L("i2i.export.color.red"),
                    L("i2i.export.color.green"),
                    L("i2i.export.color.blue"),
                    L("i2i.export.color.yellow"),
                    L("i2i.export.color.cyan"),
                    L("i2i.export.color.magenta"),
                    L("i2i.export.color.black"),
                    L("i2i.export.color.white")
                };
                cbColor.SelectedIndex = 1; // Default to Green
                cbColor.IsEnabled = true;
            }
            else // Mask Only or Separated
            {
                cbColor.ItemsSource = new[]
                {
                    L("i2i.export.color.white_on_black"),
                    L("i2i.export.color.black_on_white")
                };
                cbColor.SelectedIndex = 0;
                cbColor.IsEnabled = true;
            }
        }

        cbType.SelectionChanged += (_, _) => UpdateColorOptions();
        UpdateColorOptions();

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = L("i2i.export.area") });
        panel.Children.Add(cbArea);
        panel.Children.Add(new TextBlock { Text = L("i2i.export.type") });
        panel.Children.Add(cbType);
        panel.Children.Add(new TextBlock { Text = L("i2i.export.color") });
        panel.Children.Add(cbColor);

        var dialog = new ContentDialog
        {
            Title = L("i2i.export.title"),
            Content = panel,
            PrimaryButtonText = L("i2i.export.btn_export"),
            CloseButtonText = L("i2i.export.btn_cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot
        };
        dialog.Resources["ContentDialogMaxWidth"] = 400.0;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // Export Logic
        bool isCanvasArea = cbArea.SelectedIndex == 0;
        int exportType = cbType.SelectedIndex; // 0: Image, 1: Mask, 2: Merged, 3: Separated
        int colorMode = cbColor.SelectedIndex;

        var picker = new FileSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        picker.FileTypeChoices.Add("PNG Image", new[] { ".png" });
        picker.SuggestedFileName = "export";
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        try
        {
            await ExportCanvasMaskInternalAsync(file, isCanvasArea, exportType, colorMode);
            TxtStatus.Text = Lf("file.saved_path", file.Path);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("common.save_failed", ex.Message);
        }
    }

    private async Task ExportCanvasMaskInternalAsync(StorageFile file, bool isCanvasArea, int exportType, int colorMode)
    {
        var device = MaskCanvas.GetDevice();
        var doc = MaskCanvas.Document;
        if (device == null || doc == null || doc.OriginalImage == null) return;

        int canvasW = MaskCanvas.CanvasW;
        int canvasH = MaskCanvas.CanvasH;
        int origW = (int)doc.OriginalImage.SizeInPixels.Width;
        int origH = (int)doc.OriginalImage.SizeInPixels.Height;

        int outW = isCanvasArea ? canvasW : origW;
        int outH = isCanvasArea ? canvasH : origH;

        Vector2 imageOffset = doc.PixelAlignedImageOffset;

        void DrawImage(CanvasDrawingSession ds)
        {
            if (isCanvasArea)
            {
                ds.DrawImage(doc.OriginalImage, imageOffset);
            }
            else
            {
                ds.DrawImage(doc.OriginalImage, Vector2.Zero);
            }
        }

        void DrawMask(CanvasDrawingSession ds, Windows.UI.Color color)
        {
            if (doc.MaskTarget == null) return;

            using var effect = new Microsoft.Graphics.Canvas.Effects.ColorMatrixEffect
            {
                Source = doc.MaskTarget,
                ColorMatrix = new Microsoft.Graphics.Canvas.Effects.Matrix5x4
                {
                    M11 = 0, M12 = 0, M13 = 0, M14 = 0,
                    M21 = 0, M22 = 0, M23 = 0, M24 = 0,
                    M31 = 0, M32 = 0, M33 = 0, M34 = 0,
                    M41 = color.R / 255f, M42 = color.G / 255f, M43 = color.B / 255f, M44 = color.A / 255f,
                    M51 = 0, M52 = 0, M53 = 0, M54 = 0
                }
            };

            if (isCanvasArea)
            {
                ds.DrawImage(effect, Vector2.Zero);
            }
            else
            {
                ds.DrawImage(effect, -imageOffset);
            }
        }

        async Task<byte[]> GetTargetBytesAsync(Action<CanvasDrawingSession> draw)
        {
            using var target = new CanvasRenderTarget(device, outW, outH, 96f);
            using (var ds = target.CreateDrawingSession())
            {
                draw(ds);
            }
            using var saveStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await target.SaveAsync(saveStream, CanvasBitmapFileFormat.Png);
            saveStream.Seek(0);
            var bytes = new byte[saveStream.Size];
            using var reader = new Windows.Storage.Streams.DataReader(saveStream);
            await reader.LoadAsync((uint)saveStream.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }

        if (exportType == 0) // Image Only
        {
            var bytes = await GetTargetBytesAsync(ds =>
            {
                ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                DrawImage(ds);
            });
            await Windows.Storage.FileIO.WriteBytesAsync(file, bytes);
        }
        else if (exportType == 1) // Mask Only
        {
            var fg = colorMode == 0 ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(255, 0, 0, 0);
            var bg = colorMode == 0 ? Windows.UI.Color.FromArgb(255, 0, 0, 0) : Windows.UI.Color.FromArgb(255, 255, 255, 255);
            var bytes = await GetTargetBytesAsync(ds =>
            {
                ds.Clear(bg);
                DrawMask(ds, fg);
            });
            await Windows.Storage.FileIO.WriteBytesAsync(file, bytes);
        }
        else if (exportType == 2) // Merged
        {
            var fg = colorMode switch
            {
                0 => Windows.UI.Color.FromArgb(255, 255, 0, 0),   // Red
                1 => Windows.UI.Color.FromArgb(255, 0, 255, 0),   // Green
                2 => Windows.UI.Color.FromArgb(255, 0, 0, 255),   // Blue
                3 => Windows.UI.Color.FromArgb(255, 255, 255, 0), // Yellow
                4 => Windows.UI.Color.FromArgb(255, 0, 255, 255), // Cyan
                5 => Windows.UI.Color.FromArgb(255, 255, 0, 255), // Magenta
                6 => Windows.UI.Color.FromArgb(255, 0, 0, 0),     // Black
                _ => Windows.UI.Color.FromArgb(255, 255, 255, 255)// White
            };

            var bytes = await GetTargetBytesAsync(ds =>
            {
                ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                DrawImage(ds);
                DrawMask(ds, fg);
            });
            await Windows.Storage.FileIO.WriteBytesAsync(file, bytes);
        }
        else if (exportType == 3) // Separated
        {
            // Save image
            var imgBytes = await GetTargetBytesAsync(ds =>
            {
                ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                DrawImage(ds);
            });
            await Windows.Storage.FileIO.WriteBytesAsync(file, imgBytes);

            // Save mask
            var fg = colorMode == 0 ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(255, 0, 0, 0);
            var bg = colorMode == 0 ? Windows.UI.Color.FromArgb(255, 0, 0, 0) : Windows.UI.Color.FromArgb(255, 255, 255, 255);
            var maskBytes = await GetTargetBytesAsync(ds =>
            {
                ds.Clear(bg);
                DrawMask(ds, fg);
            });
            
            string dir = Path.GetDirectoryName(file.Path) ?? "";
            string name = Path.GetFileNameWithoutExtension(file.Path);
            string ext = Path.GetExtension(file.Path);
            string maskPath = Path.Combine(dir, $"{name}_Mask{ext}");
            
            await File.WriteAllBytesAsync(maskPath, maskBytes);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  外观主题
    // ═══════════════════════════════════════════════════════════
}
