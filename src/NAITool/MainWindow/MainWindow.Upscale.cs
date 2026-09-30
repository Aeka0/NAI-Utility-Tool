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

    private bool IsNovelAiUpscaleSelected =>
        CboUpscaleProvider?.SelectedItem is ComboBoxItem item &&
        string.Equals(item.Tag?.ToString(), "novelai", StringComparison.Ordinal);

    private int GetNovelAiUpscaleScale() =>
        CboNovelAiUpscaleScale?.SelectedItem is ComboBoxItem item &&
        int.TryParse(item.Tag?.ToString(), out int scale) && scale == 2 ? 2 : 4;

    private void UpdateUpscaleStartButtonState()
    {
        if (BtnStartUpscale == null)
            return;
        bool providerReady = IsNovelAiUpscaleSelected || _upscaleModelInfos.Count > 0;
        BtnStartUpscale.IsEnabled = !_upscaleRunning && _upscaleInputImageBytes is { Length: > 0 } && providerReady;
    }

    private void RefreshUpscaleProviderControls()
    {
        if (PanelLocalUpscaleOptions == null || PanelNovelAiUpscaleOptions == null)
            return;
        bool official = IsNovelAiUpscaleSelected;
        PanelLocalUpscaleOptions.Visibility = official ? Visibility.Collapsed : Visibility.Visible;
        PanelNovelAiUpscaleOptions.Visibility = official ? Visibility.Visible : Visibility.Collapsed;
        _settings.Settings.UseNovelAiUpscale = official;
        UpdateUpscaleResolutionDisplay();
        UpdateUpscaleStartButtonState();
    }

    private void OnUpscaleProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PanelLocalUpscaleOptions == null)
            return;
        RefreshUpscaleProviderControls();
        _settings.Save();
    }

    private void OnNovelAiUpscaleScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtUpscaleInputRes == null)
            return;
        _settings.Settings.NovelAiUpscaleScale = GetNovelAiUpscaleScale();
        UpdateUpscaleResolutionDisplay();
        _settings.Save();
    }

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
            if (!IsNovelAiUpscaleSelected)
                TxtStatus.Text = Lf("upscale.put_model_into_dir", modelsDir);
        }
        else
        {
            CboUpscaleModel.IsEnabled = true;
            foreach (var m in _upscaleModelInfos)
                CboUpscaleModel.Items.Add(CreateTextComboBoxItem(m.DisplayName));
            CboUpscaleModel.SelectedIndex = 0;
        }

        CboUpscaleProvider.SelectedIndex = _settings.Settings.UseNovelAiUpscale ? 1 : 0;
        CboNovelAiUpscaleScale.SelectedIndex = _settings.Settings.NovelAiUpscaleScale == 2 ? 0 : 1;
        ApplyMenuTypography(CboUpscaleModel);
        ApplyMenuTypography(CboUpscaleProvider);
        ApplyMenuTypography(CboNovelAiUpscaleScale);
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
        double scale = IsNovelAiUpscaleSelected ? GetNovelAiUpscaleScale() : GetSelectedUpscaleScale();
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
        if (_upscaleSourceWidth <= 0 || _upscaleSourceHeight <= 0)
        {
            TxtUpscaleInputRes.Text = "—";
            TxtUpscaleOutputRes.Text = "—";
            return;
        }

        TxtUpscaleInputRes.Text = $"{_upscaleSourceWidth} × {_upscaleSourceHeight}";
        double scale = GetSelectedUpscaleScale();
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
            UpdateUpscaleStartButtonState();
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

    private async Task<bool> ConfirmNovelAiUpscaleAsync(int scale)
    {
        if (IsAssetProtectionPaidFeatureLimitEnabled())
        {
            TxtStatus.Text = "账号资产保护已禁止付费功能；请先在设置中解除限制。";
            return false;
        }
        if (string.IsNullOrEmpty(_settings.Settings.ApiToken))
        {
            OnNetworkSettings(this, new RoutedEventArgs());
            return false;
        }
        if (_upscaleSourceWidth > 1024 || _upscaleSourceHeight > 1024)
        {
            var limitDialog = new ContentDialog
            {
                Title = "NovelAI 官方超分",
                Content = new TextBlock
                {
                    Text = $"当前图片为 {_upscaleSourceWidth} × {_upscaleSourceHeight}。官方接口要求输入宽、高均不超过 1024 像素。",
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = L("common.ok"),
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
            };
            await limitDialog.ShowAsync();
            return false;
        }

        int outWidth = _upscaleSourceWidth * scale;
        int outHeight = _upscaleSourceHeight * scale;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "这会调用 NovelAI 官方付费超分接口，可能消耗 Image Anlas。实际扣费取决于账号订阅和输入尺寸。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{_upscaleSourceWidth} × {_upscaleSourceHeight}  →  {outWidth} × {outHeight}（{scale}×）\n将按当前 A/B 顺序选择空闲账号，实际使用账号会显示在左上角。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            FontSize = 12,
        });
        var dialog = new ContentDialog
        {
            Title = "确认 NovelAI 官方超分",
            Content = panel,
            PrimaryButtonText = "确认并开始",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        ApplyGoldAccentResources(dialog.Resources);
        dialog.Resources["ContentDialogMaxWidth"] = 520.0;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnStartUpscale(object sender, RoutedEventArgs e)
    {
        if (_upscaleInputImageBytes == null || _upscaleInputImageBytes.Length == 0)
        {
            TxtStatus.Text = L("upscale.drop_image_first");
            return;
        }

        if (_upscaleRunning) return;

        bool useNovelAi = IsNovelAiUpscaleSelected;
        int novelAiScale = GetNovelAiUpscaleScale();
        UpscaleService.UpscaleModelInfo? modelInfo = null;
        double targetScale = novelAiScale;
        if (useNovelAi)
        {
            if (!await ConfirmNovelAiUpscaleAsync(novelAiScale))
                return;
        }
        else
        {
            CommitUpscaleScaleInput();
            int modelIdx = CboUpscaleModel.SelectedIndex;
            if (modelIdx < 0 || modelIdx >= _upscaleModelInfos.Count)
                return;
            modelInfo = _upscaleModelInfos[modelIdx];
            targetScale = GetSelectedUpscaleScale();
        }

        _upscaleRunning = true;
        BtnStartUpscale.IsEnabled = false;
        CboUpscaleProvider.IsEnabled = false;
        CboNovelAiUpscaleScale.IsEnabled = false;
        CboUpscaleModel.IsEnabled = false;
        SliderUpscaleScale.IsEnabled = false;
        TxtUpscaleScaleValue.IsEnabled = false;
        SetUpscaleButtonText(L("button.upscaling"));
        UpscaleProgressBar.Visibility = Visibility.Visible;
        TxtStatus.Text = useNovelAi ? "正在调用 NovelAI 官方超分…" : L("status.upscale_loading_model");
        bool shouldUnloadModel = !useNovelAi && ShouldUnloadOnnxModelsAfterInference;
        string? selectedAccountLabel = null;
        bool requestCompleted = false;

        try
        {
            var inputBytes = _upscaleInputImageBytes;
            byte[] resultBytes;
            string providerLabel;
            if (useNovelAi)
            {
                DebugLog($"[Upscale] NovelAI start | Scale={novelAiScale}x | Input={_upscaleSourceWidth}x{_upscaleSourceHeight}");
                var (officialBytes, error) = await _naiService.UpscaleImageAsync(
                    Convert.ToBase64String(inputBytes),
                    _upscaleSourceWidth,
                    _upscaleSourceHeight,
                    novelAiScale,
                    accountSelected: label =>
                    {
                        selectedAccountLabel = label;
                        OnGenerationAccountStarted(label);
                    });
                if (error != null)
                    throw new InvalidOperationException(error);
                if (officialBytes == null)
                    throw new InvalidOperationException(L("generate.error.empty_result"));
                resultBytes = officialBytes;
                providerLabel = selectedAccountLabel == null
                    ? "NovelAI 官方"
                    : $"NovelAI 官方 · {selectedAccountLabel}";
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
                        UpscaleProgressBar.IsIndeterminate = false;
                        UpscaleProgressBar.Value = p * 100;
                    });
                });
                resultBytes = await _upscaleService.UpscaleAsync(inputBytes, targetScale, progress);
                providerLabel = _upscaleService.ExecutionProvider;
            }

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
            DebugLog($"[Upscale] Completed | Output={_upscaleSourceWidth}x{_upscaleSourceHeight} | Provider={providerLabel}");
            TxtStatus.Text = Lf("upscale.completed", _upscaleSourceWidth, _upscaleSourceHeight, providerLabel);

            if (shouldUnloadModel)
            {
                _upscaleService?.UnloadModel();
                shouldUnloadModel = false;
            }

            await PromptSaveUpscaleResultAsync(resultBytes);
            requestCompleted = true;
            if (useNovelAi)
                _ = RefreshAnlasInfoAsync(forceRefresh: true);
        }
        catch (Exception ex)
        {
            DebugLog($"[Upscale] Failed: {ex}");
            TxtStatus.Text = Lf("upscale.failed", ex.Message);
        }
        finally
        {
            if (useNovelAi)
                OnGenerationAccountFinished(selectedAccountLabel, requestCompleted);
            if (shouldUnloadModel)
                _upscaleService?.UnloadModel();
            _upscaleRunning = false;
            CboUpscaleProvider.IsEnabled = true;
            CboNovelAiUpscaleScale.IsEnabled = true;
            CboUpscaleModel.IsEnabled = _upscaleModelInfos.Count > 0;
            SliderUpscaleScale.IsEnabled = true;
            TxtUpscaleScaleValue.IsEnabled = true;
            UpdateUpscaleStartButtonState();
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
        UpdateUpscaleStartButtonState();
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
