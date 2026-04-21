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
    // ═══════════════════════════════════════════════════════════
    //  超分工作区
    // ═══════════════════════════════════════════════════════════

    private List<UpscaleService.UpscaleModelInfo> _upscaleModelInfos = new();

    private void PopulateUpscaleModelList()
    {
        CboUpscaleModel.Items.Clear();
        var modelsDir = Path.Combine(ModelsDir, "upscaler");
        _upscaleModelInfos = UpscaleService.ScanModels(modelsDir);

        if (_upscaleModelInfos.Count == 0)
        {
            CboUpscaleModel.Items.Add(CreateTextComboBoxItem(L("upscale.model_not_found")));
            CboUpscaleModel.SelectedIndex = 0;
            CboUpscaleModel.IsEnabled = false;
            BtnStartUpscale.IsEnabled = false;
            TxtStatus.Text = Lf("upscale.put_model_into_dir", modelsDir);
            return;
        }

        CboUpscaleModel.IsEnabled = true;
        foreach (var m in _upscaleModelInfos)
            CboUpscaleModel.Items.Add(CreateTextComboBoxItem(m.DisplayName));

        CboUpscaleModel.SelectedIndex = 0;
        ApplyMenuTypography(CboUpscaleModel);
    }

    private void OnUpscaleModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtUpscaleInputRes == null) return;
        if (CboUpscaleModel.SelectedIndex < 0 || _upscaleModelInfos.Count == 0) return;
        UpdateUpscaleResolutionDisplay();
    }

    private void OnUpscaleScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtUpscaleInputRes == null) return;
        UpdateUpscaleResolutionDisplay();
    }

    private int GetSelectedUpscaleScale()
    {
        if (CboUpscaleScale.SelectedItem is ComboBoxItem item && item.Tag is string tag
            && int.TryParse(tag, out int scale))
            return scale;
        return 4;
    }

    private void UpdateUpscaleResolutionDisplay()
    {
        if (_upscaleSourceWidth <= 0 || _upscaleSourceHeight <= 0)
        {
            TxtUpscaleInputRes.Text = "—";
            TxtUpscaleOutputRes.Text = "—";
            return;
        }

        TxtUpscaleInputRes.Text = $"{_upscaleSourceWidth} × {_upscaleSourceHeight}";
        int scale = GetSelectedUpscaleScale();
        int outW = _upscaleSourceWidth * scale;
        int outH = _upscaleSourceHeight * scale;
        TxtUpscaleOutputRes.Text = $"{outW} × {outH}";
    }

    private void OnUpscaleDragOver(object sender, DragEventArgs e)
    {
        TryAcceptImageFileDrag(e);
    }

    private async void OnUpscaleDrop(object sender, DragEventArgs e)
    {
        if (IsSuperDropEnabled || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        var file = await GetFirstDroppedImageFileAsync(e, includeBmp: true);
        if (file != null)
        {
            await LoadUpscaleImageAsync(file.Path);
            return;
        }

        TxtStatus.Text = L("file.unsupported_format_upscale");
    }

    private async Task LoadUpscaleImageAsync(string filePath, bool preserveDirtyState = false)
    {
        try
        {
            bool wasDirty = _upscaleWorkspaceDirty;
            var bytes = await File.ReadAllBytesAsync(filePath);
            _upscaleInputImageBytes = bytes;
            _upscaleImagePath = filePath;

            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap == null)
            {
                TxtStatus.Text = L("upscale.error.decode_failed");
                return;
            }

            _upscaleSourceWidth = bitmap.Width;
            _upscaleSourceHeight = bitmap.Height;

            await ShowUpscalePreviewAsync(bytes);
            UpdateUpscaleResolutionDisplay();
            BtnStartUpscale.IsEnabled = _upscaleModelInfos.Count > 0;
            if (preserveDirtyState)
                _upscaleWorkspaceDirty = wasDirty;
            else
                MarkUpscaleWorkspaceClean();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => FitUpscalePreviewToScreen());
            TxtStatus.Text = preserveDirtyState
                ? Lf("image.reload.loaded", Path.GetFileName(filePath), _upscaleSourceWidth, _upscaleSourceHeight)
                : Lf("upscale.loaded", Path.GetFileName(filePath), _upscaleSourceWidth, _upscaleSourceHeight);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("common.load_failed", ex.Message);
        }
    }

    private async Task ShowUpscalePreviewAsync(byte[] bytes)
    {
        var bitmapImage = new BitmapImage();
        using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using var writer = new Windows.Storage.Streams.DataWriter(ms);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        writer.DetachStream();
        ms.Seek(0);
        await bitmapImage.SetSourceAsync(ms);
        UpscalePreviewImage.Source = bitmapImage;
        UpscalePlaceholder.Visibility = Visibility.Collapsed;
    }

    private void FitUpscalePreviewToScreen()
    {
        if (UpscalePreviewImage.Source is not BitmapImage bmp) return;
        double imgW = bmp.PixelWidth;
        double imgH = bmp.PixelHeight;
        if (imgW <= 0 || imgH <= 0) return;

        double viewW = UpscaleImageScroller.ViewportWidth;
        double viewH = UpscaleImageScroller.ViewportHeight;
        if (viewW <= 0 || viewH <= 0) return;

        float zoom = (float)Math.Min(viewW / imgW, viewH / imgH);
        zoom = Math.Min(zoom, 1.0f);
        UpscaleImageScroller.ChangeView(0, 0, zoom);
    }

    private async void OnStartUpscale(object sender, RoutedEventArgs e)
    {
        if (_upscaleInputImageBytes == null || _upscaleInputImageBytes.Length == 0)
        {
            TxtStatus.Text = L("upscale.drop_image_first");
            return;
        }

        if (_upscaleRunning) return;

        int modelIdx = CboUpscaleModel.SelectedIndex;
        if (modelIdx < 0 || modelIdx >= _upscaleModelInfos.Count) return;

        var modelInfo = _upscaleModelInfos[modelIdx];
        _upscaleRunning = true;
        BtnStartUpscale.IsEnabled = false;
        SetUpscaleButtonText(L("button.upscaling"));
        UpscaleProgressBar.Visibility = Visibility.Visible;
        TxtStatus.Text = L("status.upscale_loading_model");
        bool shouldUnloadModel = ShouldUnloadOnnxModelsAfterInference;

        try
        {
            _upscaleService ??= new UpscaleService();
            var inputBytes = _upscaleInputImageBytes;
            bool preferCpu = PreferCpuForOnnxInference;

            DebugLog($"[Upscale] Start | Model={modelInfo.DisplayName} | Device={(preferCpu ? "CPU" : "Prefer GPU")} | Input={_upscaleSourceWidth}x{_upscaleSourceHeight}");

            await Task.Run(() => _upscaleService.LoadModel(modelInfo.FilePath, preferCpu));
            DebugLog($"[Upscale] Model loaded | Provider={_upscaleService.ExecutionProvider} | Scale={_upscaleService.ModelScale}x");
            TxtStatus.Text = L("status.upscale_running");

            var progress = new Progress<double>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpscaleProgressBar.IsIndeterminate = false;
                    UpscaleProgressBar.Value = p * 100;
                });
            });

            var resultBytes = await _upscaleService.UpscaleAsync(inputBytes, progress);

            using var resultBitmap = SKBitmap.Decode(resultBytes);
            if (resultBitmap != null)
            {
                _upscaleSourceWidth = resultBitmap.Width;
                _upscaleSourceHeight = resultBitmap.Height;
                _upscaleInputImageBytes = resultBytes;
                _upscaleImagePath = null;
            }

            await ShowUpscalePreviewAsync(resultBytes);
            UpdateUpscaleResolutionDisplay();
            _upscaleWorkspaceDirty = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => FitUpscalePreviewToScreen());
            DebugLog($"[Upscale] Completed | Output={_upscaleSourceWidth}x{_upscaleSourceHeight} | Provider={_upscaleService.ExecutionProvider}");
            TxtStatus.Text = Lf("upscale.completed", _upscaleSourceWidth, _upscaleSourceHeight, _upscaleService.ExecutionProvider);

            if (shouldUnloadModel)
            {
                _upscaleService.UnloadModel();
                shouldUnloadModel = false;
            }

            await PromptSaveUpscaleResultAsync(resultBytes);
        }
        catch (Exception ex)
        {
            DebugLog($"[Upscale] Failed: {ex}");
            TxtStatus.Text = Lf("upscale.failed", ex.Message);
        }
        finally
        {
            if (shouldUnloadModel)
                _upscaleService?.UnloadModel();
            _upscaleRunning = false;
            BtnStartUpscale.IsEnabled = true;
            SetUpscaleButtonText(L("button.start_upscale"));
            UpscaleProgressBar.Visibility = Visibility.Collapsed;
            UpscaleProgressBar.IsIndeterminate = true;
            UpscaleProgressBar.Value = 0;
        }
    }

    private void SetUpscaleButtonText(string text)
    {
        BtnStartUpscale.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uECE9", FontSize = 16 },
                new TextBlock { Text = text },
            }
        };
    }

    private async Task PromptSaveUpscaleResultAsync(byte[] resultBytes)
    {
        var savePicker = new FileSavePicker();
        savePicker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        savePicker.FileTypeChoices.Add(L("file.png_image"), new List<string> { ".png" });
        savePicker.SuggestedFileName = $"upscaled_{DateTime.Now:yyyyMMdd_HHmmss}";

        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(savePicker, hwnd);

        var file = await savePicker.PickSaveFileAsync();
        if (file != null)
        {
            await File.WriteAllBytesAsync(file.Path, resultBytes);
            _upscaleImagePath = file.Path;
            MarkUpscaleWorkspaceClean();
            TxtStatus.Text = Lf("file.saved_path", file.Path);
        }
    }

    private async Task SendBytesToUpscaleAsync(byte[] bytes, string? sourcePath = null)
    {
        SwitchMode(AppMode.Upscale);

        _upscaleInputImageBytes = bytes;
        _upscaleImagePath = !string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath)
            ? sourcePath
            : null;
        using var bitmap = SKBitmap.Decode(bytes);
        if (bitmap != null)
        {
            _upscaleSourceWidth = bitmap.Width;
            _upscaleSourceHeight = bitmap.Height;
        }

        await ShowUpscalePreviewAsync(bytes);
        UpdateUpscaleResolutionDisplay();
        BtnStartUpscale.IsEnabled = _upscaleModelInfos.Count > 0;
        MarkUpscaleWorkspaceClean();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => FitUpscalePreviewToScreen());
        TxtStatus.Text = sourcePath != null
            ? Lf("upscale.sent_with_name", Path.GetFileName(sourcePath))
            : L("upscale.sent");
    }

    private async void OnSendToUpscaleFromGen(object sender, RoutedEventArgs e)
    {
        if (_currentGenImageBytes == null)
        {
            TxtStatus.Text = L("generate.error.no_result_to_send");
            return;
        }
        GenResultBar.Visibility = Visibility.Collapsed;
        await SendBytesToUpscaleAsync(_currentGenImageBytes, _currentGenImagePath);
    }

    private async void OnSendToUpscaleFromI2I(object sender, RoutedEventArgs e)
    {
        try
        {
            byte[]? bytesToSend;
            if (MaskCanvas.IsInPreviewMode && _pendingResultBitmap != null)
            {
                await ApplyInpaintResultAsync();
                bytesToSend = _lastGeneratedImageBytes ?? await CreateCurrentFullImageBytes();
            }
            else
            {
                bytesToSend = await CreateCurrentFullImageBytes();
            }

            if (bytesToSend == null || bytesToSend.Length == 0)
            {
                TxtStatus.Text = L("upscale.error.no_image_to_send");
                return;
            }

            await SendBytesToUpscaleAsync(bytesToSend, MaskCanvas.LoadedFilePath);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("upscale.send_failed", ex.Message);
        }
    }

    private void OnSendToI2IFromUpscale(object sender, RoutedEventArgs e)
    {
        if (_upscaleInputImageBytes == null)
        {
            TxtStatus.Text = L("image.no_image_to_send");
            return;
        }
        SendImageToI2I(_upscaleInputImageBytes, _upscaleImagePath);
    }

    private async void OnSendToEffectsFromUpscale(object sender, RoutedEventArgs e)
    {
        if (_upscaleInputImageBytes == null)
        {
            TxtStatus.Text = L("image.no_image_to_send");
            return;
        }
        await SendBytesToEffectsAsync(_upscaleInputImageBytes);
    }

    private async void OnHistorySendToUpscale(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string filePath)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(filePath);
                await SendBytesToUpscaleAsync(bytes, filePath);
            }
            catch (Exception ex) { TxtStatus.Text = Lf("upscale.send_failed", ex.Message); }
        }
    }
}
