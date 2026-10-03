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
using Microsoft.UI.Xaml.Controls.Primitives;
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
    private bool _updatingUpscaleScaleControls;

    private bool _upscaleLoading;
    private bool _updatingUpscaleProvider;

    private bool IsNovelAiUpscaleSelected => _settings.Settings.UseNovelAiUpscale;

    private void UpdateUpscaleStartButtonState()
    {
        if (BtnStartUpscale == null || TxtUpscaleValidation == null || this.Content == null) return;
        bool official = IsNovelAiUpscaleSelected;
        bool busy = _upscaleRunning || _upscaleLoading;
        bool hasImage = _upscaleInputImageBytes is { Length: > 0 };
        int? cost = hasImage ? NovelAiUpscaleRules.EstimateAnlas(_upscaleSourceWidth, _upscaleSourceHeight) : null;
        string? error = official && IsAssetProtectionPaidFeatureLimitEnabled()
            ? L("upscale.official.protection_blocked")
            : official && hasImage && cost == null ? L("upscale.official.size_limit") : null;
        TxtUpscaleValidation.Text = error ?? "";
        TxtUpscaleValidation.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        BtnStartUpscale.IsEnabled = !busy && hasImage && error == null && (official || _upscaleModelInfos.Count > 0);
        CboUpscaleProvider.IsEnabled = !busy;
        CboUpscaleModel.IsEnabled = !busy && _upscaleModelInfos.Count > 0;
        SliderUpscaleScale.IsEnabled = !busy;
        TxtUpscaleScaleValue.IsEnabled = !busy;

        if (official && cost is > 0)
        {
            BtnStartUpscale.Content = CreateAnlasActionButtonContent(
                L(_upscaleRunning ? "button.upscaling" : "button.start_upscale"), cost.Value);
            ApplyGoldAccentButtonStyle(BtnStartUpscale);
        }
        else
        {
            SetUpscaleButtonText(L(_upscaleRunning ? "button.upscaling" : "button.start_upscale"));
            ClearGoldAccentButtonStyle(BtnStartUpscale);
        }
    }

    private void RefreshUpscaleProviderControls()
    {
        if (PanelLocalUpscaleOptions == null || TxtOfficialUpscaleScale == null) return;
        PanelLocalUpscaleOptions.Visibility = IsNovelAiUpscaleSelected ? Visibility.Collapsed : Visibility.Visible;
        TxtOfficialUpscaleScale.Visibility = IsNovelAiUpscaleSelected ? Visibility.Visible : Visibility.Collapsed;
        UpdateUpscaleResolutionDisplay();
    }

    private void OnUpscaleProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUpscaleProvider || PanelLocalUpscaleOptions == null || TxtUpscaleOutputRes == null) return;
        _settings.Settings.UseNovelAiUpscale = CboUpscaleProvider.SelectedIndex == 1;
        RefreshUpscaleProviderControls();
        _settings.Save();
    }

    private void PopulateUpscaleModelList()
    {
        if (_upscaleRunning || _upscaleLoading) return;
        _updatingUpscaleProvider = true;
        try { CboUpscaleProvider.SelectedIndex = IsNovelAiUpscaleSelected ? 1 : 0; }
        finally { _updatingUpscaleProvider = false; }
        CboUpscaleModel.Items.Clear();
        var modelsDir = Path.Combine(ModelsDir, "upscaler");
        _upscaleModelInfos = UpscaleService.ScanModels(modelsDir);

        if (_upscaleModelInfos.Count == 0)
        {
            CboUpscaleModel.Items.Add(CreateTextComboBoxItem(L("upscale.model_not_found")));
            if (!IsNovelAiUpscaleSelected) TxtStatus.Text = Lf("upscale.put_model_into_dir", modelsDir);
        }
        else
        {
            foreach (var model in _upscaleModelInfos)
                CboUpscaleModel.Items.Add(CreateTextComboBoxItem(model.DisplayName));
        }
        CboUpscaleModel.SelectedIndex = 0;
        ApplyMenuTypography(CboUpscaleModel);
        ApplyMenuTypography(CboUpscaleProvider);
        RefreshUpscaleProviderControls();
    }

    private void OnUpscaleModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtUpscaleInputRes == null) return;
        if (CboUpscaleModel.SelectedIndex < 0 || _upscaleModelInfos.Count == 0) return;
        UpdateUpscaleResolutionDisplay();
    }

    private void OnUpscaleScaleChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingUpscaleScaleControls)
            return;
        if (TxtUpscaleInputRes == null) return;
        UpdateUpscaleResolutionDisplay();
    }

    private void OnUpscaleScaleInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingUpscaleScaleControls)
            return;
        if (TxtUpscaleInputRes == null)
            return;
        if (!TryParseUpscaleScaleInput(TxtUpscaleScaleValue.Text, out double scale))
            return;
        if (scale < UpscaleService.MinTargetScale || scale > UpscaleService.MaxTargetScale)
            return;

        SetSelectedUpscaleScale(scale, updateText: false);
        UpdateUpscaleResolutionDisplay();
    }

    private void OnUpscaleScaleInputLostFocus(object sender, RoutedEventArgs e)
    {
        CommitUpscaleScaleInput();
    }

    private void OnUpscaleScaleInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;

        CommitUpscaleScaleInput();
        e.Handled = true;
    }

    private double GetSelectedUpscaleScale()
    {
        if (SliderUpscaleScale == null)
            return UpscaleService.DefaultTargetScale;

        return UpscaleService.NormalizeTargetScale(SliderUpscaleScale.Value);
    }

    private static string FormatUpscaleScale(double scale)
    {
        return UpscaleService.NormalizeTargetScale(scale).ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static bool TryParseUpscaleScaleInput(string? text, out double scale)
    {
        text = (text ?? string.Empty).Trim();
        if (text.EndsWith("x", StringComparison.OrdinalIgnoreCase))
            text = text[..^1].Trim();

        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out scale)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out scale);
    }

    private void SetSelectedUpscaleScale(double scale, bool updateText)
    {
        scale = UpscaleService.NormalizeTargetScale(scale);
        _updatingUpscaleScaleControls = true;
        try
        {
            if (SliderUpscaleScale != null && Math.Abs(SliderUpscaleScale.Value - scale) > 0.001)
                SliderUpscaleScale.Value = scale;
            if (updateText && TxtUpscaleScaleValue != null)
                TxtUpscaleScaleValue.Text = FormatUpscaleScale(scale);
        }
        finally
        {
            _updatingUpscaleScaleControls = false;
        }
    }

    private void CommitUpscaleScaleInput()
    {
        double scale = GetSelectedUpscaleScale();
        if (TryParseUpscaleScaleInput(TxtUpscaleScaleValue?.Text, out double parsed))
            scale = parsed;

        SetSelectedUpscaleScale(scale, updateText: true);
        UpdateUpscaleResolutionDisplay();
    }

    private void UpdateUpscaleScaleValueDisplay(bool forceText = false)
    {
        if (SliderUpscaleScale == null || TxtUpscaleScaleValue == null)
            return;

        double scale = GetSelectedUpscaleScale();
        bool updateText = forceText || TxtUpscaleScaleValue.FocusState == FocusState.Unfocused;
        SetSelectedUpscaleScale(scale, updateText);
    }

    private void UpdateUpscaleResolutionDisplay()
    {
        UpdateUpscaleScaleValueDisplay();
        UpdateUpscaleStartButtonState();
        if (_upscaleSourceWidth <= 0 || _upscaleSourceHeight <= 0)
        {
            TxtUpscaleInputRes.Text = "—";
            TxtUpscaleOutputRes.Text = "—";
            return;
        }

        TxtUpscaleInputRes.Text = $"{_upscaleSourceWidth} × {_upscaleSourceHeight}";
        double scale = IsNovelAiUpscaleSelected ? NovelAiUpscaleRules.Scale : GetSelectedUpscaleScale();
        int outW = Math.Max(1, (int)Math.Round(_upscaleSourceWidth * scale, MidpointRounding.AwayFromZero));
        int outH = Math.Max(1, (int)Math.Round(_upscaleSourceHeight * scale, MidpointRounding.AwayFromZero));
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
        if (_upscaleRunning || _upscaleLoading) return;
        _upscaleLoading = true;
        UpdateUpscaleStartButtonState();
        try
        {
            bool wasDirty = _upscaleWorkspaceDirty;
            var bytes = await File.ReadAllBytesAsync(filePath);
            if (!await SetUpscaleImageAsync(bytes, filePath)) return;
            if (preserveDirtyState) _upscaleWorkspaceDirty = wasDirty;
            else MarkUpscaleWorkspaceClean();
            TxtStatus.Text = preserveDirtyState
                ? Lf("image.reload.loaded", Path.GetFileName(filePath), _upscaleSourceWidth, _upscaleSourceHeight)
                : Lf("upscale.loaded", Path.GetFileName(filePath), _upscaleSourceWidth, _upscaleSourceHeight);
        }
        catch (Exception ex) { TxtStatus.Text = Lf("common.load_failed", ex.Message); }
        finally
        {
            _upscaleLoading = false;
            UpdateUpscaleStartButtonState();
        }
    }

    private async Task<bool> SetUpscaleImageAsync(byte[] bytes, string? sourcePath)
    {
        var dimensions = await Task.Run(() =>
        {
            bool valid = TryGetImageDimensions(bytes, out int width, out int height);
            return (Valid: valid, Width: width, Height: height);
        });
        if (!dimensions.Valid)
        {
            TxtStatus.Text = L("upscale.error.decode_failed");
            return false;
        }
        await ShowUpscalePreviewAsync(bytes);
        _upscaleInputImageBytes = bytes;
        _upscaleImagePath = sourcePath;
        _upscaleSourceWidth = dimensions.Width;
        _upscaleSourceHeight = dimensions.Height;
        UpdateUpscaleResolutionDisplay();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FitUpscalePreviewToScreen);
        return true;
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
        if (_upscaleRunning || _upscaleLoading) return;
        if (_upscaleInputImageBytes is not { Length: > 0 })
        {
            TxtStatus.Text = L("upscale.drop_image_first");
            return;
        }
        bool official = IsNovelAiUpscaleSelected;
        if (official)
        {
            if (IsAssetProtectionPaidFeatureLimitEnabled())
            { TxtStatus.Text = L("upscale.official.protection_blocked"); return; }
            if (NovelAiUpscaleRules.EstimateAnlas(_upscaleSourceWidth, _upscaleSourceHeight) == null)
            { TxtStatus.Text = L("upscale.official.size_limit"); return; }
            if (!_settings.HasApiTokens)
            { OnApiSettings(this, new RoutedEventArgs()); return; }
        }
        else CommitUpscaleScaleInput();

        int modelIdx = CboUpscaleModel.SelectedIndex;
        if (!official && (modelIdx < 0 || modelIdx >= _upscaleModelInfos.Count)) return;
        var modelInfo = official ? null : _upscaleModelInfos[modelIdx];
        var inputBytes = _upscaleInputImageBytes;
        double targetScale = official ? NovelAiUpscaleRules.Scale : GetSelectedUpscaleScale();
        _upscaleRunning = true;
        UpdateUpscaleStartButtonState();
        UpscaleProgressBar.Visibility = Visibility.Visible;
        TxtStatus.Text = L(official ? "upscale.official.running" : "status.upscale_loading_model");
        bool shouldUnloadModel = !official && ShouldUnloadOnnxModelsAfterInference;

        try
        {
            byte[] resultBytes;
            string provider;
            if (official)
            {
                DebugLog($"[Upscale] Start | Provider=NovelAI | Input={_upscaleSourceWidth}x{_upscaleSourceHeight}");
                var result = await _naiService.UpscaleImageAsync(inputBytes);
                if (result.ImageBytes == null)
                { TxtStatus.Text = Lf("upscale.failed", result.Error ?? L("api.error.empty_zip")); return; }
                resultBytes = result.ImageBytes;
                provider = L("upscale.official.name");
            }
            else
            {
                _upscaleService ??= new UpscaleService();
                bool preferCpu = PreferCpuForOnnxInference;
                DebugLog($"[Upscale] Start | Model={modelInfo!.DisplayName} | TargetScale={FormatUpscaleScale(targetScale)}x | Device={(preferCpu ? "CPU" : "Prefer GPU")} | Input={_upscaleSourceWidth}x{_upscaleSourceHeight}");
                await Task.Run(() => _upscaleService.LoadModel(modelInfo.FilePath, preferCpu));
                DebugLog($"[Upscale] Model loaded | Provider={_upscaleService.ExecutionProvider} | NativeScale={_upscaleService.ModelScale}x");
                TxtStatus.Text = L("status.upscale_running");
                var progress = new Progress<double>(p =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!_upscaleRunning) return;
                        UpscaleProgressBar.IsIndeterminate = false;
                        UpscaleProgressBar.Value = p * 100;
                    });
                });
                resultBytes = await _upscaleService.UpscaleAsync(inputBytes, targetScale, progress);
                provider = _upscaleService.ExecutionProvider;
            }

            if (!await SetUpscaleImageAsync(resultBytes, null)) return;
            _upscaleWorkspaceDirty = true;
            DebugLog($"[Upscale] Completed | Output={_upscaleSourceWidth}x{_upscaleSourceHeight} | Provider={provider}");
            TxtStatus.Text = Lf("upscale.completed", _upscaleSourceWidth, _upscaleSourceHeight, provider);
            if (shouldUnloadModel)
            {
                _upscaleService?.UnloadModel();
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
            if (shouldUnloadModel) _upscaleService?.UnloadModel();
            _upscaleRunning = false;
            UpdateUpscaleStartButtonState();
            UpscaleProgressBar.Visibility = Visibility.Collapsed;
            UpscaleProgressBar.IsIndeterminate = true;
            UpscaleProgressBar.Value = 0;
            if (official) _ = RefreshAnlasInfoAsync(forceRefresh: true);
            UpdateDynamicMenuStates();
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
            var bytesToSave = await PrepareImageBytesForSaveAsync(resultBytes, stripMetadata: false);
            await File.WriteAllBytesAsync(file.Path, bytesToSave);
            _upscaleImagePath = file.Path;
            MarkUpscaleWorkspaceClean();
            TxtStatus.Text = ShouldStripSavedImageMetadata(false)
                ? Lf("file.saved_path_stripped", file.Path)
                : Lf("file.saved_path", file.Path);
        }
    }

    private async Task SendBytesToUpscaleAsync(byte[] bytes, string? sourcePath = null)
    {
        if (_upscaleRunning || _upscaleLoading) return;
        SwitchMode(AppMode.Upscale);
        _upscaleLoading = true;
        UpdateUpscaleStartButtonState();
        try
        {
            string? path = !string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath) ? sourcePath : null;
            if (!await SetUpscaleImageAsync(bytes, path)) return;
            MarkUpscaleWorkspaceClean();
            TxtStatus.Text = sourcePath != null
                ? Lf("upscale.sent_with_name", Path.GetFileName(sourcePath)) : L("upscale.sent");
        }
        catch (Exception ex) { TxtStatus.Text = Lf("upscale.send_failed", ex.Message); }
        finally
        {
            _upscaleLoading = false;
            UpdateUpscaleStartButtonState();
        }
    }

    private async void OnSendToUpscaleFromGen(object sender, RoutedEventArgs e)
    {
        if (_currentGenImageBytes == null)
        {
            TxtStatus.Text = L("generate.error.no_result_to_send");
            return;
        }
        SetGenResultBarRequested(false);
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
