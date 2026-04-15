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
    private async void SendImageToI2I(byte[] imageBytes, string? sourcePath = null)
    {
        try
        {
            SaveCurrentPromptToBuffer();
            _lastGeneratedImageBytes = null;
            _i2iImageTextChunks = await Task.Run(() => ImageMetadataService.ReadRoundTripTextChunks(imageBytes));
            _pendingResultTextChunks = null;

            var meta = await Task.Run(() => ImageMetadataService.ReadFromBytes(imageBytes));

            string sendPos, sendNeg, sendStyle;
            if (meta != null && (meta.IsNaiParsed || meta.IsSdFormat))
            {
                sendPos = meta.PositivePrompt;
                sendNeg = meta.NegativePrompt;
                sendStyle = "";

                if (meta.IsNaiParsed)
                {
                    if (meta.CharacterPrompts.Count > 0)
                        SetGenCharactersFromMetadata(meta);
                    ApplyReferenceDataFromMetadata(meta);
                    RefreshCharacterPanel();
                }
            }
            else
            {
                sendPos = _genPositivePrompt;
                sendNeg = _genNegativePrompt;
                sendStyle = _genStylePrompt;
            }

            var device = MaskCanvas.GetDevice() ?? CanvasDevice.GetSharedDevice();

            CanvasBitmap bitmap;
            using (var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream())
            {
                using var writer = new Windows.Storage.Streams.DataWriter(ms);
                writer.WriteBytes(imageBytes);
                await writer.StoreAsync();
                writer.DetachStream();
                ms.Seek(0);
                bitmap = await CanvasBitmap.LoadAsync(device, ms, 96f);
            }

            int imgW = (int)bitmap.SizeInPixels.Width;
            int imgH = (int)bitmap.SizeInPixels.Height;

            _i2iPositivePrompt = sendPos;
            _i2iNegativePrompt = sendNeg;
            _i2iStylePrompt = sendStyle;

            var importSize = MaskCanvasControl.ResolveImportCanvasSize(
                imgW, imgH, IsAssetProtectionSizeLimitEnabled());
            int canvasW = importSize.W;
            int canvasH = importSize.H;
            bool sizeApplied = canvasW == imgW && canvasH == imgH;

            _customWidth = canvasW;
            _customHeight = canvasH;
            SetSizeInputsSilently(_customWidth, _customHeight);
            int idx = Array.FindIndex(MaskCanvasControl.CanvasPresets,
                p => p.W == canvasW && p.H == canvasH);
            if (idx >= 0) CboSize.SelectedIndex = idx;

            SwitchMode(AppMode.I2I);
            MaskCanvas.InitializeCanvas(canvasW, canvasH);
            string? reloadPath = !string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath)
                ? sourcePath
                : null;
            MaskCanvas.LoadImageFromBitmap(bitmap, reloadPath);
            MaskCanvas.FitToScreen();

            UpdatePromptHighlights();

            TxtStatus.Text = sizeApplied
                ? Lf("i2i.sent_with_synced_size", imgW, imgH)
                : Lf("i2i.sent_with_canvas_size", imgW, imgH, canvasW, canvasH);
        }
        catch (Exception ex) { TxtStatus.Text = Lf("i2i.send_failed", ex.Message); }
    }

    // ═══════════════════════════════════════════════════════════
    //  重绘模式生成
    // ═══════════════════════════════════════════════════════════

    private async Task<bool> DoInpaintGenerateAsync(bool forceRandomSeed = false)
    {
        if (_i2iEditMode == I2IEditMode.Denoise)
            return await DoDenoiseGenerateAsync(forceRandomSeed);

        if (MaskCanvas.IsInPreviewMode)
        {
            return await RedoInpaintGenerateAsync(forceRandomSeed);
        }
        if (MaskCanvas.Document.MaskTarget == null || MaskCanvas.GetDevice() == null)
        { TxtStatus.Text = L("inpaint.error.canvas_not_initialized"); return false; }
        if (!MaskCanvas.HasMaskContent())
        { TxtStatus.Text = L("inpaint.error.mask_required"); return false; }

        bool keepGenerateButtonInteractive = _continuousGenRunning;
        if (!keepGenerateButtonInteractive)
            BtnGenerate.IsEnabled = false;
        _generateRequestRunning = true;
        UpdateBtnGenerateForApiKey();
        TxtStatus.Text = L("generate.status.generating");
        var ip = _settings.Settings.InpaintParameters;
        int restoreSeed = ip.Seed;

        try
        {
            _generateCts?.Cancel();
            _generateCts = new CancellationTokenSource();
            var ct = _generateCts.Token;
            var device = MaskCanvas.GetDevice()!;

            var exportImage = MaskCanvas.Document.CreateCompositeForExport(device);
            if (exportImage == null) { TxtStatus.Text = L("inpaint.error.export_image_failed"); BtnGenerate.IsEnabled = true; return false; }

            var imageBase64 = await NovelAIService.EncodeRenderTargetAsync(exportImage, isMask: false);
            var maskBase64 = await NovelAIService.EncodeRenderTargetAsync(MaskCanvas.Document.MaskTarget!, isMask: true);

            exportImage.Dispose();
            if (!TryValidateReferenceRequest(out string referenceError))
            {
                TxtStatus.Text = referenceError;
                return false;
            }

            int actualSeed;
            while (true)
            {
                actualSeed = (!forceRandomSeed && ip.Seed > 0) ? ip.Seed : Random.Shared.Next(1, int.MaxValue);
                ip.Seed = actualSeed;
                var wildcardContext = CreateWildcardContext(actualSeed, ip.Model);
                var (prompt, negPrompt) = GetPrompts(wildcardContext);
                if (_genCharacters.Count > 0) ApplyCharCountPrefixStrip();
                if (_genVibeTransfers.Count > 0 && _genPreciseReferences.Count == 0)
                {
                    string? encodeError = await EnsureVibesEncodedAsync(ip.Model, ct);
                    if (encodeError != null) { TxtStatus.Text = encodeError; return false; }
                }
                var chars = (_genCharacters.Count > 0 && !IsCurrentModelV3()) ? GetCharacterData(wildcardContext) : null;
                var vibes = GetVibeTransferData();
                var preciseReferences = GetPreciseReferenceData();
                var signature = BuildI2IGenerationRequestSignature(
                    "i2i-inpaint",
                    ip,
                    MaskCanvas.CanvasW,
                    MaskCanvas.CanvasH,
                    actualSeed,
                    prompt,
                    negPrompt,
                    chars,
                    vibes,
                    preciseReferences,
                    imageBase64,
                    maskBase64,
                    MaskCanvas.Document.PixelAlignedImageOffset);
                var duplicateDecision = await CheckDuplicateGenerationRequestAsync(signature, restoreSeed);
                if (duplicateDecision == DuplicateGenerationDecision.Cancel)
                    return false;
                if (duplicateDecision == DuplicateGenerationDecision.ProceedWithRandomSeed)
                {
                    restoreSeed = 0;
                    forceRandomSeed = true;
                    continue;
                }

                RememberLastGenerationRequest(signature);
                DebugLog($"[Inpaint] Start | Model={ip.Model} | Seed={actualSeed}");

                var resultBitmap = await SendInpaintRequestAsync(imageBase64, maskBase64, prompt, negPrompt, wildcardContext, ct);
                _lastUsedSeed = actualSeed;

                if (resultBitmap == null) return false;

                MaskCanvas.SetPreview(resultBitmap);
                _i2iPreviewDirty = true;
                ShowResultBar();
                if (!keepGenerateButtonInteractive)
                    BtnGenerate.IsEnabled = true;
                _ = RefreshAnlasInfoAsync(forceRefresh: true);
                DebugLog($"[Inpaint] Completed | Seed={actualSeed}");
                TxtStatus.Text = L("generate.status.completed");
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            DebugLog("[Inpaint] Cancelled");
            TxtStatus.Text = L("generate.status.cancelled");
            if (!keepGenerateButtonInteractive)
                BtnGenerate.IsEnabled = true;
            return false;
        }
        catch (Exception ex)
        {
            DebugLog($"[Inpaint] Failed: {ex}");
            TxtStatus.Text = Lf("generate.status.failed", ex.Message);
            if (!keepGenerateButtonInteractive)
                BtnGenerate.IsEnabled = true;
            return false;
        }
        finally
        {
            _generateRequestRunning = false;
            UpdateBtnGenerateForApiKey();
            ip.Seed = restoreSeed;
        }
    }

    private async Task<CanvasBitmap?> SendInpaintRequestAsync(
        string imageBase64, string maskBase64,
        string prompt, string negPrompt, WildcardExpandContext wildcardContext, CancellationToken ct)
    {
        var device = MaskCanvas.GetDevice()!;
        if (!TryValidateReferenceRequest(out string referenceError))
        {
            TxtStatus.Text = referenceError;
            BtnGenerate.IsEnabled = true;
            return null;
        }
        if (_genCharacters.Count > 0) ApplyCharCountPrefixStrip();
        var chars = (_genCharacters.Count > 0 && !IsCurrentModelV3()) ? GetCharacterData(wildcardContext) : null;
        if (_genVibeTransfers.Count > 0 && _genPreciseReferences.Count == 0)
        {
            string? encodeError = await EnsureVibesEncodedAsync(_settings.Settings.GenParameters.Model, ct);
            if (encodeError != null) { TxtStatus.Text = encodeError; return null; }
        }
        var vibes = GetVibeTransferData();
        var preciseReferences = GetPreciseReferenceData();

        int streamPreviewVer = 0;
        CanvasBitmap? streamPreviewBmp = null;
        IProgress<byte[]>? progress = _settings.Settings.StreamGeneration
            ? new Progress<byte[]>(async bytes =>
            {
                int myVer = ++streamPreviewVer;
                try
                {
                    using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    using var w = new Windows.Storage.Streams.DataWriter(ms);
                    w.WriteBytes(bytes);
                    await w.StoreAsync();
                    w.DetachStream();
                    ms.Seek(0);
                    var bmp = await CanvasBitmap.LoadAsync(device, ms, 96f);
                    if (myVer != streamPreviewVer) { bmp.Dispose(); return; }
                    var old = streamPreviewBmp;
                    streamPreviewBmp = bmp;
                    MaskCanvas.SetPreview(bmp);
                    old?.Dispose();
                }
                catch { }
            })
            : null;

        var (imageBytes, error) = await _naiService.InpaintAsync(
            imageBase64, maskBase64,
            MaskCanvas.CanvasW, MaskCanvas.CanvasH,
            prompt, negPrompt, chars, vibes, preciseReferences, progress, ct);

        ++streamPreviewVer;

        if (error != null) { DebugLog($"[Inpaint] API error: {error}"); TxtStatus.Text = error; BtnGenerate.IsEnabled = true; streamPreviewBmp?.Dispose(); return null; }
        if (imageBytes == null) { DebugLog("[Inpaint] API returned no image"); TxtStatus.Text = L("generate.error.empty_result"); BtnGenerate.IsEnabled = true; streamPreviewBmp?.Dispose(); return null; }

        _pendingResultBytes = imageBytes;
        _pendingResultTextChunks = await Task.Run(() => ImageMetadataService.ReadRoundTripTextChunks(imageBytes));
        _pendingResultBitmap?.Dispose();

        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using var writer = new Windows.Storage.Streams.DataWriter(stream);
        writer.WriteBytes(imageBytes);
        await writer.StoreAsync();
        stream.Seek(0);
        _pendingResultBitmap = await CanvasBitmap.LoadAsync(device, stream, 96f);
        streamPreviewBmp?.Dispose();
        return _pendingResultBitmap;
    }

    private async Task<bool> DoDenoiseGenerateAsync(bool forceRandomSeed = false)
    {
        if (MaskCanvas.IsInPreviewMode)
        {
            return await RedoInpaintGenerateAsync(forceRandomSeed);
        }
        if (MaskCanvas.Document.MaskTarget == null || MaskCanvas.GetDevice() == null)
        { TxtStatus.Text = L("inpaint.error.canvas_not_initialized"); return false; }
        if (MaskCanvas.Document.OriginalImage == null)
        { TxtStatus.Text = L("i2i.error.no_image_to_send"); return false; }

        bool keepGenerateButtonInteractive = _continuousGenRunning;
        if (!keepGenerateButtonInteractive)
            BtnGenerate.IsEnabled = false;
        _generateRequestRunning = true;
        UpdateBtnGenerateForApiKey();
        TxtStatus.Text = L("generate.status.generating");
        var dp = _settings.Settings.I2IDenoiseParameters;
        int restoreSeed = dp.Seed;

        try
        {
            _generateCts?.Cancel();
            _generateCts = new CancellationTokenSource();
            var ct = _generateCts.Token;
            var device = MaskCanvas.GetDevice()!;

            var exportImage = MaskCanvas.Document.CreateCompositeForExport(device);
            if (exportImage == null) { TxtStatus.Text = L("inpaint.error.export_image_failed"); BtnGenerate.IsEnabled = true; return false; }

            var imageBase64 = await NovelAIService.EncodeRenderTargetAsync(exportImage, isMask: false);
            exportImage.Dispose();
            if (!TryValidateReferenceRequest(out string referenceError))
            {
                TxtStatus.Text = referenceError;
                return false;
            }

            int actualSeed;
            while (true)
            {
                actualSeed = (!forceRandomSeed && dp.Seed > 0) ? dp.Seed : Random.Shared.Next(1, int.MaxValue);
                dp.Seed = actualSeed;
                var wildcardContext = CreateWildcardContext(actualSeed, dp.Model);
                var (prompt, negPrompt) = GetPrompts(wildcardContext);
                if (_genCharacters.Count > 0) ApplyCharCountPrefixStrip();
                if (_genVibeTransfers.Count > 0 && _genPreciseReferences.Count == 0)
                {
                    string? encodeError = await EnsureVibesEncodedAsync(dp.Model, ct);
                    if (encodeError != null) { TxtStatus.Text = encodeError; return false; }
                }
                var chars = (_genCharacters.Count > 0 && !IsCurrentModelV3()) ? GetCharacterData(wildcardContext) : null;
                var vibes = GetVibeTransferData();
                var preciseReferences = GetPreciseReferenceData();
                var signature = BuildI2IGenerationRequestSignature(
                    "i2i-denoise",
                    dp,
                    MaskCanvas.CanvasW,
                    MaskCanvas.CanvasH,
                    actualSeed,
                    prompt,
                    negPrompt,
                    chars,
                    vibes,
                    preciseReferences,
                    imageBase64,
                    null,
                    MaskCanvas.Document.PixelAlignedImageOffset);
                var duplicateDecision = await CheckDuplicateGenerationRequestAsync(signature, restoreSeed);
                if (duplicateDecision == DuplicateGenerationDecision.Cancel)
                    return false;
                if (duplicateDecision == DuplicateGenerationDecision.ProceedWithRandomSeed)
                {
                    restoreSeed = 0;
                    forceRandomSeed = true;
                    continue;
                }

                RememberLastGenerationRequest(signature);
                DebugLog($"[Denoise] Start | Model={dp.Model} | Seed={actualSeed} | Strength={dp.DenoiseStrength:0.##} | Noise={dp.DenoiseNoise:0.##}");

                var resultBitmap = await SendDenoiseRequestAsync(imageBase64, prompt, negPrompt, wildcardContext, ct);
                _lastUsedSeed = actualSeed;

                if (resultBitmap == null) return false;

                MaskCanvas.SetPreview(resultBitmap);
                _i2iPreviewDirty = true;
                ShowResultBar();
                if (!keepGenerateButtonInteractive)
                    BtnGenerate.IsEnabled = true;
                _ = RefreshAnlasInfoAsync(forceRefresh: true);
                DebugLog($"[Denoise] Completed | Seed={actualSeed}");
                TxtStatus.Text = L("generate.status.completed");
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            DebugLog("[Denoise] Cancelled");
            TxtStatus.Text = L("generate.status.cancelled");
            if (!keepGenerateButtonInteractive)
                BtnGenerate.IsEnabled = true;
            return false;
        }
        catch (Exception ex)
        {
            DebugLog($"[Denoise] Failed: {ex}");
            TxtStatus.Text = Lf("generate.status.failed", ex.Message);
            if (!keepGenerateButtonInteractive)
                BtnGenerate.IsEnabled = true;
            return false;
        }
        finally
        {
            _generateRequestRunning = false;
            UpdateBtnGenerateForApiKey();
            dp.Seed = restoreSeed;
        }
    }

    private async Task<CanvasBitmap?> SendDenoiseRequestAsync(
        string imageBase64,
        string prompt, string negPrompt, WildcardExpandContext wildcardContext, CancellationToken ct)
    {
        var device = MaskCanvas.GetDevice()!;
        if (!TryValidateReferenceRequest(out string referenceError))
        {
            TxtStatus.Text = referenceError;
            BtnGenerate.IsEnabled = true;
            return null;
        }
        if (_genCharacters.Count > 0) ApplyCharCountPrefixStrip();
        var chars = (_genCharacters.Count > 0 && !IsCurrentModelV3()) ? GetCharacterData(wildcardContext) : null;
        if (_genVibeTransfers.Count > 0 && _genPreciseReferences.Count == 0)
        {
            string? encodeError = await EnsureVibesEncodedAsync(_settings.Settings.GenParameters.Model, ct);
            if (encodeError != null) { TxtStatus.Text = encodeError; return null; }
        }
        var vibes = GetVibeTransferData();
        var preciseReferences = GetPreciseReferenceData();

        int streamPreviewVer = 0;
        CanvasBitmap? streamPreviewBmp = null;
        IProgress<byte[]>? progress = _settings.Settings.StreamGeneration
            ? new Progress<byte[]>(async bytes =>
            {
                int myVer = ++streamPreviewVer;
                try
                {
                    using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    using var w = new Windows.Storage.Streams.DataWriter(ms);
                    w.WriteBytes(bytes);
                    await w.StoreAsync();
                    w.DetachStream();
                    ms.Seek(0);
                    var bmp = await CanvasBitmap.LoadAsync(device, ms, 96f);
                    if (myVer != streamPreviewVer) { bmp.Dispose(); return; }
                    var old = streamPreviewBmp;
                    streamPreviewBmp = bmp;
                    MaskCanvas.SetPreview(bmp);
                    old?.Dispose();
                }
                catch { }
            })
            : null;

        var (imageBytes, error) = await _naiService.ImageToImageAsync(
            imageBase64,
            MaskCanvas.CanvasW, MaskCanvas.CanvasH,
            prompt, negPrompt, chars, vibes, preciseReferences, progress, ct);

        ++streamPreviewVer;

        if (error != null) { DebugLog($"[Denoise] API error: {error}"); TxtStatus.Text = error; BtnGenerate.IsEnabled = true; streamPreviewBmp?.Dispose(); return null; }
        if (imageBytes == null) { DebugLog("[Denoise] API returned no image"); TxtStatus.Text = L("generate.error.empty_result"); BtnGenerate.IsEnabled = true; streamPreviewBmp?.Dispose(); return null; }

        _pendingResultBytes = imageBytes;
        _pendingResultTextChunks = await Task.Run(() => ImageMetadataService.ReadRoundTripTextChunks(imageBytes));
        _pendingResultBitmap?.Dispose();

        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using var writer = new Windows.Storage.Streams.DataWriter(stream);
        writer.WriteBytes(imageBytes);
        await writer.StoreAsync();
        stream.Seek(0);
        _pendingResultBitmap = await CanvasBitmap.LoadAsync(device, stream, 96f);
        streamPreviewBmp?.Dispose();
        return _pendingResultBitmap;
    }

    // ═══════════════════════════════════════════════════════════
    //  重绘预览操作：应用 / 重做 / 舍弃
    // ═══════════════════════════════════════════════════════════

    private async void OnApplyResult(object sender, RoutedEventArgs e)
    {
        if (_pendingResultBitmap == null) return;
        try
        {
            await ApplyInpaintResultAsync();
            TxtStatus.Text = L("inpaint.result_applied");
        }
        catch (Exception ex) { TxtStatus.Text = Lf("inpaint.apply_failed", ex.Message); }
    }

    private async Task<bool> RedoInpaintGenerateAsync(bool forceRandomSeed = false)
    {
        var editMode = _i2iEditMode;
        if (MaskCanvas.Document.MaskTarget == null || MaskCanvas.GetDevice() == null)
        { TxtStatus.Text = L("inpaint.error.canvas_not_initialized"); return false; }
        if (editMode == I2IEditMode.Inpaint && !MaskCanvas.HasMaskContent())
        { TxtStatus.Text = L("inpaint.error.mask_required"); return false; }
        if (editMode == I2IEditMode.Denoise && MaskCanvas.Document.OriginalImage == null)
        { TxtStatus.Text = L("i2i.error.no_image_to_send"); return false; }

        BtnGenerate.IsEnabled = false;
        _generateRequestRunning = true;
        UpdateBtnGenerateForApiKey();
        SetResultBarEnabled(false);
        TxtStatus.Text = L("generate.status.regenerating");
        var ip = editMode == I2IEditMode.Denoise
            ? _settings.Settings.I2IDenoiseParameters
            : _settings.Settings.InpaintParameters;
        int restoreSeed = ip.Seed;

        try
        {
            _generateCts?.Cancel();
            _generateCts = new CancellationTokenSource();
            var ct = _generateCts.Token;
            var device = MaskCanvas.GetDevice()!;

            using var exportImage = MaskCanvas.Document.CreateCompositeForExport(device);
            if (exportImage == null) { TxtStatus.Text = L("inpaint.error.export_image_failed"); BtnGenerate.IsEnabled = true; return false; }

            var imageBase64 = await NovelAIService.EncodeRenderTargetAsync(exportImage, isMask: false);
            string? maskBase64 = editMode == I2IEditMode.Inpaint
                ? await NovelAIService.EncodeRenderTargetAsync(MaskCanvas.Document.MaskTarget!, isMask: true)
                : null;
            if (!TryValidateReferenceRequest(out string referenceError))
            {
                TxtStatus.Text = referenceError;
                return false;
            }

            int actualSeed;
            while (true)
            {
                actualSeed = (!forceRandomSeed && ip.Seed > 0) ? ip.Seed : Random.Shared.Next(1, int.MaxValue);
                ip.Seed = actualSeed;
                var wildcardContext = CreateWildcardContext(actualSeed, ip.Model);
                var (prompt, negPrompt) = GetPrompts(wildcardContext);
                if (_genCharacters.Count > 0) ApplyCharCountPrefixStrip();
                if (_genVibeTransfers.Count > 0 && _genPreciseReferences.Count == 0)
                {
                    string? encodeError = await EnsureVibesEncodedAsync(ip.Model, ct);
                    if (encodeError != null) { TxtStatus.Text = encodeError; return false; }
                }
                var chars = (_genCharacters.Count > 0 && !IsCurrentModelV3()) ? GetCharacterData(wildcardContext) : null;
                var vibes = GetVibeTransferData();
                var preciseReferences = GetPreciseReferenceData();
                var signature = BuildI2IGenerationRequestSignature(
                    editMode == I2IEditMode.Denoise ? "i2i-denoise" : "i2i-inpaint",
                    ip,
                    MaskCanvas.CanvasW,
                    MaskCanvas.CanvasH,
                    actualSeed,
                    prompt,
                    negPrompt,
                    chars,
                    vibes,
                    preciseReferences,
                    imageBase64,
                    maskBase64,
                    MaskCanvas.Document.PixelAlignedImageOffset);
                var duplicateDecision = await CheckDuplicateGenerationRequestAsync(signature, restoreSeed);
                if (duplicateDecision == DuplicateGenerationDecision.Cancel)
                    return false;
                if (duplicateDecision == DuplicateGenerationDecision.ProceedWithRandomSeed)
                {
                    restoreSeed = 0;
                    forceRandomSeed = true;
                    continue;
                }

                RememberLastGenerationRequest(signature);
                var resultBitmap = editMode == I2IEditMode.Denoise
                    ? await SendDenoiseRequestAsync(imageBase64, prompt, negPrompt, wildcardContext, ct)
                    : await SendInpaintRequestAsync(
                        imageBase64, maskBase64!,
                        prompt, negPrompt, wildcardContext, ct);
                _lastUsedSeed = actualSeed;

                if (resultBitmap != null)
                {
                    MaskCanvas.SetPreview(resultBitmap);
                    _i2iPreviewDirty = true;
                    _ = RefreshAnlasInfoAsync(forceRefresh: true);
                    TxtStatus.Text = L("generate.status.regenerated");
                    return true;
                }

                break;
            }
        }
        catch (OperationCanceledException) { TxtStatus.Text = L("generate.status.cancelled"); }
        catch (Exception ex) { TxtStatus.Text = Lf("generate.status.regenerate_failed", ex.Message); }
        finally
        {
            _generateRequestRunning = false;
            UpdateBtnGenerateForApiKey();
            ip.Seed = restoreSeed;
            SetResultBarEnabled(true);
        }
        return false;
    }

    private async void OnRedoGenerate(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_settings.Settings.ApiToken))
        {
            OnNetworkSettings(sender, e);
            return;
        }

        SyncPromptGenerationInputsToState();
        _settings.Save();
        await RedoInpaintGenerateAsync();
    }

    private void OnDiscardResult(object sender, RoutedEventArgs e)
    {
        ExitPreviewMode();
        UpdateI2IRedoButtonWarning();
        TxtStatus.Text = L("generate.status.discarded");
    }

    private void ExitPreviewMode()
    {
        MaskCanvas.ClearPreview();
        _i2iPreviewDirty = false;
        _pendingResultBitmap?.Dispose();
        _pendingResultBitmap = null;
        _pendingResultBytes = null;
        _pendingResultTextChunks = null;
        ResultActionBar.Visibility = Visibility.Collapsed;
        BtnGenerate.IsEnabled = true;
    }

    private void SetResultBarEnabled(bool enabled)
    {
        foreach (var child in ((StackPanel)((Border)ResultActionBar.Children[0]).Child).Children)
        {
            if (child is Button btn) btn.IsEnabled = enabled;
        }
    }

    private void ShowResultBar()
    {
        ResultBarTranslate.X = 0;
        ResultBarTranslate.Y = 0;
        ResultActionBar.Visibility = Visibility.Visible;
        UpdateI2IRedoButtonWarning();
    }

    private void OnResultBarDrag(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        ResultBarTranslate.X += e.Delta.Translation.X;
        ResultBarTranslate.Y += e.Delta.Translation.Y;
    }

    private void OnComparePressed(object sender, PointerRoutedEventArgs e)
    {
        MaskCanvas.IsComparing = true;
        (sender as UIElement)?.CapturePointer(e.Pointer);
    }

    private void OnCompareReleased(object sender, PointerRoutedEventArgs e)
    {
        MaskCanvas.IsComparing = false;
    }

    private async Task ApplyInpaintResultAsync()
    {
        var device = MaskCanvas.GetDevice();
        var pendingBitmap = _pendingResultBitmap;
        var pendingBytes = _pendingResultBytes;
        var pendingTextChunks = _pendingResultTextChunks != null
            ? new Dictionary<string, string>(_pendingResultTextChunks, StringComparer.Ordinal)
            : null;
        if (device == null || pendingBitmap == null) return;

        var doc = MaskCanvas.Document;
        var offset = doc.PixelAlignedImageOffset;
        int canvasW = MaskCanvas.CanvasW;
        int canvasH = MaskCanvas.CanvasH;

        if (doc.OriginalImage == null)
        {
            doc.SetOriginalImage(pendingBitmap);
            _lastGeneratedImageBytes = pendingBytes;
            _i2iImageTextChunks = pendingTextChunks;
            _pendingResultBitmap = null;
        }
        else
        {
            float origW = doc.OriginalImage.SizeInPixels.Width;
            float origH = doc.OriginalImage.SizeInPixels.Height;

            float canvasInOrigX = -offset.X;
            float canvasInOrigY = -offset.Y;

            float minX = Math.Min(0, canvasInOrigX);
            float minY = Math.Min(0, canvasInOrigY);
            float maxX = Math.Max(origW, canvasInOrigX + canvasW);
            float maxY = Math.Max(origH, canvasInOrigY + canvasH);

            int compositeW = (int)Math.Ceiling(maxX - minX);
            int compositeH = (int)Math.Ceiling(maxY - minY);
            var newOffset = new Vector2(offset.X + minX, offset.Y + minY);

            if (compositeW == canvasW && compositeH == canvasH)
            {
                doc.SetOriginalImage(pendingBitmap);
                doc.ImageOffset = newOffset;
                _lastGeneratedImageBytes = pendingBytes;
                _i2iImageTextChunks = pendingTextChunks;
                _pendingResultBitmap = null;
            }
            else
            {
                using var composite = new CanvasRenderTarget(device, compositeW, compositeH, 96f);
                using (var ds = composite.CreateDrawingSession())
                {
                    ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                    ds.DrawImage(doc.OriginalImage, -minX, -minY);
                    ds.DrawImage(pendingBitmap, canvasInOrigX - minX, canvasInOrigY - minY);
                }

                byte[] compBytes;
                using (var saveStream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
                {
                    await composite.SaveAsync(saveStream, CanvasBitmapFileFormat.Png);
                    saveStream.Seek(0);
                    compBytes = new byte[saveStream.Size];
                    using var reader = new Windows.Storage.Streams.DataReader(saveStream);
                    await reader.LoadAsync((uint)saveStream.Size);
                    reader.ReadBytes(compBytes);
                }

                _lastGeneratedImageBytes = compBytes;
                _i2iImageTextChunks = pendingTextChunks;

                CanvasBitmap newOriginal;
                using (var loadStream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
                {
                    using var writer = new Windows.Storage.Streams.DataWriter(loadStream);
                    writer.WriteBytes(compBytes);
                    await writer.StoreAsync();
                    writer.DetachStream();
                    loadStream.Seek(0);
                    newOriginal = await CanvasBitmap.LoadAsync(device, loadStream, 96f);
                }

                doc.SetOriginalImage(newOriginal);
                doc.ImageOffset = newOffset;

                pendingBitmap.Dispose();
                _pendingResultBitmap = null;
            }
        }

        MaskCanvas.ClearPreview();
        _pendingResultBytes = null;
        _pendingResultTextChunks = null;
        doc.ClearMask();
        MaskCanvas.UndoMgr.Clear();
        ResultActionBar.Visibility = Visibility.Collapsed;
        BtnGenerate.IsEnabled = true;
        MaskCanvas.RefreshCanvas();
    }
}
