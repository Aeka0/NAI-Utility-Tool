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
    private async void SendImageToInpaint(byte[] imageBytes)
    {
        try
        {
            SaveCurrentPromptToBuffer();
            var (sendW, sendH) = GetSelectedSize();

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

            _inpaintPositivePrompt = sendPos;
            _inpaintNegativePrompt = sendNeg;
            _inpaintStylePrompt = sendStyle;

            bool imgMatchesPreset = Array.Exists(MaskCanvasControl.CanvasPresets,
                p => p.W == imgW && p.H == imgH);

            int canvasW, canvasH;
            bool sizeApplied;

            if (_settings.Settings.MaxMode)
            {
                canvasW = imgW;
                canvasH = imgH;
                _customWidth = imgW;
                _customHeight = imgH;
                NbMaxWidth.Value = imgW;
                NbMaxHeight.Value = imgH;
                sizeApplied = true;
            }
            else if (imgMatchesPreset)
            {
                canvasW = imgW;
                canvasH = imgH;
                _customWidth = imgW;
                _customHeight = imgH;
                int idx = Array.FindIndex(MaskCanvasControl.CanvasPresets,
                    p => p.W == imgW && p.H == imgH);
                    if (idx >= 0) CboSize.SelectedIndex = idx;
                sizeApplied = true;
                }
                else
                {
                if (CboSize.SelectedIndex >= 0 &&
                    CboSize.SelectedIndex < MaskCanvasControl.CanvasPresets.Length)
                {
                    var preset = MaskCanvasControl.CanvasPresets[CboSize.SelectedIndex];
                    canvasW = preset.W;
                    canvasH = preset.H;
                }
                else
                {
                    canvasW = sendW;
                    canvasH = sendH;
                }
                sizeApplied = false;
            }

            SwitchMode(AppMode.Inpaint);
            MaskCanvas.InitializeCanvas(canvasW, canvasH);
            MaskCanvas.LoadImageFromBitmap(bitmap);
            MaskCanvas.FitToScreen();

            UpdatePromptHighlights();

            TxtStatus.Text = sizeApplied
                ? Lf("inpaint.sent_with_synced_size", imgW, imgH)
                : Lf("inpaint.sent_with_canvas_size", imgW, imgH, canvasW, canvasH);
        }
        catch (Exception ex) { TxtStatus.Text = Lf("inpaint.send_failed", ex.Message); }
    }

    // ═══════════════════════════════════════════════════════════
    //  重绘模式生成
    // ═══════════════════════════════════════════════════════════

    private async Task<bool> DoInpaintGenerateAsync(bool forceRandomSeed = false)
    {
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
        int origSeed = ip.Seed;

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

            _cachedImageBase64 = imageBase64;
            _cachedMaskBase64 = maskBase64;
            int actualSeed = (!forceRandomSeed && ip.Seed > 0) ? ip.Seed : Random.Shared.Next(1, int.MaxValue);
            ip.Seed = actualSeed;
            var wildcardContext = CreateWildcardContext(actualSeed, ip.Model);
            var (prompt, negPrompt) = GetPrompts(wildcardContext);
            _cachedPrompt = prompt;
            _cachedNegPrompt = negPrompt;

            DebugLog($"[Inpaint] Start | Model={ip.Model} | Seed={actualSeed}");

            var resultBitmap = await SendInpaintRequestAsync(imageBase64, maskBase64, prompt, negPrompt, wildcardContext, ct);
            _lastUsedSeed = actualSeed;

            if (resultBitmap == null) return false;

            MaskCanvas.SetPreview(resultBitmap);
            ShowResultBar();
            if (!keepGenerateButtonInteractive)
                BtnGenerate.IsEnabled = true;
            _ = RefreshAnlasInfoAsync(forceRefresh: true);
            DebugLog($"[Inpaint] Completed | Seed={actualSeed}");
            TxtStatus.Text = L("generate.status.completed");
            return true;
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
            ip.Seed = origSeed;
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
        var vibes = GetVibeTransferData();
        var preciseReferences = GetPreciseReferenceData();
        var (imageBytes, error) = await _naiService.InpaintAsync(
            imageBase64, maskBase64,
            MaskCanvas.CanvasW, MaskCanvas.CanvasH,
            prompt, negPrompt, chars, vibes, preciseReferences, ct);

        if (error != null) { DebugLog($"[Inpaint] API error: {error}"); TxtStatus.Text = error; BtnGenerate.IsEnabled = true; return null; }
        if (imageBytes == null) { DebugLog("[Inpaint] API returned no image"); TxtStatus.Text = L("generate.error.empty_result"); BtnGenerate.IsEnabled = true; return null; }

        _pendingResultBytes = imageBytes;
        _pendingResultBitmap?.Dispose();

        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using var writer = new Windows.Storage.Streams.DataWriter(stream);
        writer.WriteBytes(imageBytes);
        await writer.StoreAsync();
        stream.Seek(0);
        _pendingResultBitmap = await CanvasBitmap.LoadAsync(device, stream, 96f);
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
        if (_cachedImageBase64 == null || _cachedMaskBase64 == null) return false;
        BtnGenerate.IsEnabled = false;
        _generateRequestRunning = true;
        UpdateBtnGenerateForApiKey();
        SetResultBarEnabled(false);
        TxtStatus.Text = L("generate.status.regenerating");
        var ip = _settings.Settings.InpaintParameters;
        int origSeed = ip.Seed;

        try
        {
            _generateCts?.Cancel();
            _generateCts = new CancellationTokenSource();
            var ct = _generateCts.Token;

            int actualSeed = (!forceRandomSeed && ip.Seed > 0) ? ip.Seed : Random.Shared.Next(1, int.MaxValue);
            ip.Seed = actualSeed;
            var wildcardContext = CreateWildcardContext(actualSeed, ip.Model);
            var (prompt, negPrompt) = GetPrompts(wildcardContext);
            _cachedPrompt = prompt;
            _cachedNegPrompt = negPrompt;

            var resultBitmap = await SendInpaintRequestAsync(
                _cachedImageBase64, _cachedMaskBase64,
                prompt, negPrompt, wildcardContext, ct);
            _lastUsedSeed = actualSeed;

            if (resultBitmap != null)
            {
                MaskCanvas.SetPreview(resultBitmap);
                _ = RefreshAnlasInfoAsync(forceRefresh: true);
                TxtStatus.Text = L("generate.status.regenerated");
                return true;
            }
        }
        catch (OperationCanceledException) { TxtStatus.Text = L("generate.status.cancelled"); }
        catch (Exception ex) { TxtStatus.Text = Lf("generate.status.regenerate_failed", ex.Message); }
        finally
        {
            _generateRequestRunning = false;
            UpdateBtnGenerateForApiKey();
            ip.Seed = origSeed;
            SetResultBarEnabled(true);
        }
        return false;
    }

    private async void OnRedoGenerate(object sender, RoutedEventArgs e)
    {
        if (_cachedImageBase64 == null || _cachedMaskBase64 == null) return;
        await RedoInpaintGenerateAsync();
    }

    private void OnDiscardResult(object sender, RoutedEventArgs e)
    {
        ExitPreviewMode();
        UpdateInpaintRedoButtonWarning();
        TxtStatus.Text = L("generate.status.discarded");
    }

    private void ExitPreviewMode()
    {
        MaskCanvas.ClearPreview();
        _pendingResultBitmap?.Dispose();
        _pendingResultBitmap = null;
        _pendingResultBytes = null;
        _cachedImageBase64 = null;
        _cachedMaskBase64 = null;
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
        UpdateInpaintRedoButtonWarning();
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
        if (device == null || _pendingResultBitmap == null) return;

        var doc = MaskCanvas.Document;
        var offset = doc.PixelAlignedImageOffset;
        int canvasW = MaskCanvas.CanvasW;
        int canvasH = MaskCanvas.CanvasH;

        if (doc.OriginalImage == null)
        {
            MaskCanvas.ClearPreview();
            doc.SetOriginalImage(_pendingResultBitmap);
            _lastGeneratedImageBytes = _pendingResultBytes;
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

            using var composite = new CanvasRenderTarget(device, compositeW, compositeH, 96f);
            using (var ds = composite.CreateDrawingSession())
            {
                ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                ds.DrawImage(doc.OriginalImage, -minX, -minY);
                ds.DrawImage(_pendingResultBitmap, canvasInOrigX - minX, canvasInOrigY - minY);
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

            var newOffset = new Vector2(offset.X + minX, offset.Y + minY);

            MaskCanvas.ClearPreview();
            doc.SetOriginalImage(newOriginal);
            doc.ImageOffset = newOffset;

            _pendingResultBitmap.Dispose();
            _pendingResultBitmap = null;
        }

        _pendingResultBytes = null;
        _cachedImageBase64 = null;
        _cachedMaskBase64 = null;
        doc.ClearMask();
        if (MaskCanvas.IsInPreviewMode) MaskCanvas.ClearPreview();
        MaskCanvas.UndoMgr.Clear();
        ResultActionBar.Visibility = Visibility.Collapsed;
        BtnGenerate.IsEnabled = true;
        MaskCanvas.RefreshCanvas();
    }
}
