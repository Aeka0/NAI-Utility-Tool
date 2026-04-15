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
    //  生成（根据模式分流）
    // ═══════════════════════════════════════════════════════════

    private async void OnGenerate(object sender, RoutedEventArgs e)
    {
        if (_autoGenRunning) { StopAutoGeneration(); return; }
        if (_continuousGenRunning) { StopContinuousGeneration(); return; }

        if (string.IsNullOrEmpty(_settings.Settings.ApiToken))
        {
            OnNetworkSettings(sender, e);
            return;
        }

        SyncPromptGenerationInputsToState();

        if (GetSizeWarningLevel() == SizeWarningLevel.Red)
        {
            long limit = _settings.Settings.AccountAssetProtectionMode ? 1024L : 2048L;
            TxtStatus.Text = Lf("generate.error.size_limit_exceeded", limit);
            return;
        }

        _settings.Save();

        await ExecuteCurrentGenerationAsync();
    }

    private void SyncPromptGenerationInputsToState()
    {
        SaveCurrentPromptToBuffer();
        SyncUIToParams();
        if (IsAdvancedWindowOpen)
            SaveAdvancedPanelToSettings();
    }

    private Task<bool> ExecuteCurrentGenerationAsync(bool forceRandomSeed = false) =>
        _currentMode == AppMode.ImageGeneration
            ? DoImageGenerationAsync(forceRandomSeed)
            : DoInpaintGenerateAsync(forceRandomSeed);

    // ═══════════════════════════════════════════════════════════
    //  生图模式生成
    // ═══════════════════════════════════════════════════════════

    private async Task<bool> DoImageGenerationAsync(bool forceRandomSeed = false)
    {
        var autoContext = _autoGenRunning ? _automationRunContext : null;
        var (w, h) = autoContext?.CurrentSizeOverride ?? GetSelectedSize();
        bool keepGenerateButtonInteractive = _autoGenRunning || _continuousGenRunning;
        if (!keepGenerateButtonInteractive) BtnGenerate.IsEnabled = false;
        _generateRequestRunning = true;
        UpdateBtnGenerateForApiKey();
        TxtStatus.Text = L("generate.status.generating");
        var p = _settings.Settings.GenParameters;
        int restoreSeed = p.Seed;

        try
        {
            _generateCts?.Cancel();
            _generateCts = new CancellationTokenSource();
            var ct = _generateCts.Token;
            SaveCurrentPromptToBuffer();
            if (!TryValidateReferenceRequest(out string referenceError))
            {
                TxtStatus.Text = referenceError;
                return false;
            }

            int actualSeed;
            string prompt;
            string negPrompt;
            List<CharacterPromptInfo>? chars;
            List<VibeTransferInfo>? vibes;
            List<PreciseReferenceInfo>? preciseReferences;
            while (true)
            {
                actualSeed = (!forceRandomSeed && p.Seed > 0) ? p.Seed : Random.Shared.Next(1, int.MaxValue);
                p.Seed = actualSeed;

                var wildcardContext = CreateWildcardContext(actualSeed, p.Model);
                string automationPrompt = autoContext?.CurrentPromptOverride ?? _genPositivePrompt;
                string positiveRaw = MergeStyleAndMain(_genStylePrompt, automationPrompt);
                string negativeRaw = _genNegativePrompt;
                if (_autoGenRunning && _activeAutomationSettings?.Randomization.RandomizeStyleTags == true)
                {
                    string? stylePrefix = BuildRandomStylePrefixForRequest();
                    if (string.IsNullOrWhiteSpace(stylePrefix))
                        return false;
                    positiveRaw = MergeStyleAndMain(_genStylePrompt, MergeStyleAndMain(stylePrefix, automationPrompt));
                }

                prompt = ExpandPromptFeatures(positiveRaw, wildcardContext);
                negPrompt = ExpandPromptFeatures(negativeRaw, wildcardContext, isNegativeText: true);
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    TxtStatus.Text = L("generate.error.prompt_required");
                    return false;
                }

                if (_genCharacters.Count > 0) ApplyCharCountPrefixStrip();
                chars = (_genCharacters.Count > 0 && !IsCurrentModelV3()) ? GetCharacterData(wildcardContext) : null;
                if (autoContext?.CurrentVibeOverride == null && _genVibeTransfers.Count > 0 && _genPreciseReferences.Count == 0)
                {
                    string? encodeError = await EnsureVibesEncodedAsync(p.Model, ct);
                    if (encodeError != null) { TxtStatus.Text = encodeError; return false; }
                }

                vibes = autoContext?.CurrentVibeOverride ?? GetVibeTransferData();
                preciseReferences = GetPreciseReferenceData();
                var signature = BuildImageGenerationRequestSignature(
                    p, w, h, actualSeed, prompt, negPrompt, chars, vibes, preciseReferences);
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
                break;
            }

            DebugLog($"[Generate] Start | {w}x{h} | Model={p.Model} | Seed={actualSeed}");
            IProgress<byte[]>? progress = _settings.Settings.StreamGeneration
                ? new Progress<byte[]>(bytes =>
                {
                    _currentGenImageBytes = bytes;
                    _ = ShowGenPreviewAsync(bytes, w, h);
                })
                : null;
            var (imageBytes, error) = await _naiService.GenerateAsync(
                w, h, prompt, negPrompt,
                chars, vibes, preciseReferences, progress, ct);
            _lastUsedSeed = actualSeed;

            if (error != null) { DebugLog($"[Generate] API error: {error}"); TxtStatus.Text = error; return false; }
            if (imageBytes == null) { DebugLog("[Generate] API returned no image"); TxtStatus.Text = L("generate.error.empty_result"); return false; }

            byte[] finalBytes = imageBytes;
            string? originalSavedPath = await SaveToOutputAsync(imageBytes);
            string? finalSavedPath = originalSavedPath;
            string postSummary = "";

            if (_autoGenRunning && _activeAutomationSettings?.Effects.Enabled == true)
            {
                var postResult = await RunAutomationEffectsProcessAsync(imageBytes, _activeAutomationSettings.Effects, ct);
                finalBytes = postResult.Bytes;
                finalSavedPath = await SaveToOutputAsync(finalBytes, "auto");
                postSummary = postResult.Summary;
            }

            _currentGenImageBytes = finalBytes;
            _currentGenImagePath = finalSavedPath;

            await ShowGenPreviewAsync(finalBytes, w, h);

            if (finalSavedPath != null)
                AddHistoryItem(finalSavedPath);

            if (!_autoGenRunning && _settings.Settings.ShowGenerationResultBar)
            {
                GenResultBarTranslate.X = 0;
                GenResultBarTranslate.Y = 0;
                GenResultBar.Visibility = Visibility.Visible;
            }
            _ = RefreshAnlasInfoAsync(forceRefresh: true);
            UpdateDynamicMenuStates();
            DebugLog($"[Generate] Completed | Seed={actualSeed} | Saved={finalSavedPath}");
            TxtStatus.Text = string.IsNullOrWhiteSpace(postSummary)
                ? Lf("generate.status.completed_saved", finalSavedPath)
                : Lf("generate.status.completed_post_saved", postSummary, finalSavedPath);
            return true;
        }
        catch (OperationCanceledException) { DebugLog("[Generate] Cancelled"); TxtStatus.Text = L("generate.status.cancelled"); return false; }
        catch (Exception ex) { DebugLog($"[Generate] Failed: {ex}"); TxtStatus.Text = Lf("generate.status.failed", ex.Message); return false; }
        finally
        {
            _generateRequestRunning = false;
            UpdateBtnGenerateForApiKey();
            p.Seed = restoreSeed;
        }
    }

    private async Task ShowGenPreviewAsync(byte[] imageBytes, int targetW = 0, int targetH = 0)
    {
        var bitmapImage = new BitmapImage();
        using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using var writer = new Windows.Storage.Streams.DataWriter(ms);
        writer.WriteBytes(imageBytes);
        await writer.StoreAsync();
        writer.DetachStream();
        ms.Seek(0);
        await bitmapImage.SetSourceAsync(ms);
        GenPreviewImage.Source = bitmapImage;
        GenPreviewImage.Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform;
        if (targetW > 0 && targetH > 0)
        {
            GenPreviewImage.Width = targetW;
            GenPreviewImage.Height = targetH;
        }
        else if (bitmapImage.PixelWidth > 0 && bitmapImage.PixelHeight > 0)
        {
            GenPreviewImage.Width = bitmapImage.PixelWidth;
            GenPreviewImage.Height = bitmapImage.PixelHeight;
        }
        GenPlaceholder.Visibility = Visibility.Collapsed;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => FitGenPreviewToScreen());
    }

    private void FitGenPreviewToScreen()
    {
        if (GenPreviewImage.Source is not BitmapImage bmp) return;
        double imgW = GenPreviewImage.Width > 0 && !double.IsNaN(GenPreviewImage.Width) ? GenPreviewImage.Width : bmp.PixelWidth;
        double imgH = GenPreviewImage.Height > 0 && !double.IsNaN(GenPreviewImage.Height) ? GenPreviewImage.Height : bmp.PixelHeight;
        if (imgW <= 0 || imgH <= 0) return;

        double viewW = GenImageScroller.ViewportWidth;
        double viewH = GenImageScroller.ViewportHeight;
        if (viewW <= 0 || viewH <= 0) return;

        float zoom = (float)Math.Min(viewW / imgW, viewH / imgH);
        zoom = Math.Min(zoom, 1.0f);
        GenImageScroller.ChangeView(0, 0, zoom);
    }

    private static async Task<string?> SaveToOutputAsync(byte[] imageBytes, string prefix = "gen")
    {
        var dateDir = Path.Combine(OutputBaseDir, DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dateDir);
        var fileName = $"{prefix}_{DateTime.Now:HHmmss_fff}.png";
        var filePath = Path.Combine(dateDir, fileName);
        await File.WriteAllBytesAsync(filePath, imageBytes);
        return filePath;
    }

    // ═══ 生图模式浮动操作窗 ═══

    private void OnSendToI2I(object sender, RoutedEventArgs e)
    {
        if (_currentGenImageBytes == null)
        { TxtStatus.Text = L("generate.error.no_result_to_send"); return; }

        GenResultBar.Visibility = Visibility.Collapsed;
        SendImageToI2I(_currentGenImageBytes, _currentGenImagePath);
    }

    private async void OnSendToEffectsFromGen(object sender, RoutedEventArgs e)
    {
        if (_currentGenImageBytes == null)
        {
            TxtStatus.Text = L("generate.error.no_result_to_send");
            return;
        }

        GenResultBar.Visibility = Visibility.Collapsed;
        await SendBytesToEffectsAsync(_currentGenImageBytes, _currentGenImagePath);
    }

    private async void OnSendToEffectsFromI2I(object sender, RoutedEventArgs e)
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
                TxtStatus.Text = L("post.error.no_image_to_send");
                return;
            }

            await SendBytesToEffectsAsync(bytesToSend, MaskCanvas.LoadedFilePath);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("post.error.send_failed", ex.Message);
        }
    }

    private async void OnGenSendToInspect(object sender, RoutedEventArgs e)
    {
        if (_currentGenImageBytes == null)
        { TxtStatus.Text = L("generate.error.no_result_to_send"); return; }
        GenResultBar.Visibility = Visibility.Collapsed;
        SwitchMode(AppMode.Inspect);
        await LoadInspectImageFromBytesAsync(_currentGenImageBytes, _currentGenImagePath != null ? Path.GetFileName(_currentGenImagePath) : null);
    }

    private async void OnDeleteGenResult(object sender, RoutedEventArgs e)
    {
        GenResultBar.Visibility = Visibility.Collapsed;

        string? deletedPath = _currentGenImagePath;
        if (!string.IsNullOrEmpty(deletedPath) && File.Exists(deletedPath))
        {
            try
            {
                int idx = _historyFiles.IndexOf(deletedPath);
                File.Delete(deletedPath);
                var delDateStr = GetDateFromFilePath(deletedPath);
                if (delDateStr != null && _historyByDate.ContainsKey(delDateStr))
                {
                    _historyByDate[delDateStr].Remove(deletedPath);
                    if (_historyByDate[delDateStr].Count == 0)
                    {
                        _historyByDate.Remove(delDateStr);
                        _historyAvailableDates.Remove(delDateStr);
                        _historyAvailableDateSet.Remove(delDateStr);
                    }
                }
                _historyFiles.Remove(deletedPath);
                _historyLoadedCount = Math.Min(_historyLoadedCount, _historyFiles.Count);
                RefreshHistoryPanel();

                string? nextPath = null;
                if (idx >= 0 && _historyFiles.Count > 0)
                    nextPath = _historyFiles[Math.Min(idx, _historyFiles.Count - 1)];

                if (nextPath != null)
                {
                    await ShowHistoryImageAsync(nextPath);
                    TxtStatus.Text = L("common.deleted");
                    return;
                }
                TxtStatus.Text = L("history.generated_result_deleted");
            }
            catch (Exception ex) { TxtStatus.Text = Lf("common.delete_failed", ex.Message); }
        }

        _currentGenImageBytes = null;
        _currentGenImagePath = null;
        GenPreviewImage.Source = null;
        GenPlaceholder.Visibility = Visibility.Visible;
        UpdateDynamicMenuStates();
    }

    private void CopyImageToClipboard(byte[] imageBytes)
    {
        try
        {
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var writer = new Windows.Storage.Streams.DataWriter(stream);
            writer.WriteBytes(imageBytes);
            _ = writer.StoreAsync().AsTask().ContinueWith(_ =>
            {
                stream.Seek(0);
                DispatcherQueue.TryEnqueue(() =>
                {
                    var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream));
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                    TxtStatus.Text = L("image.copied_to_clipboard");
                });
            });
        }
        catch (Exception ex) { TxtStatus.Text = Lf("common.copy_failed", ex.Message); }
    }

    private async void OnHistoryCopyImage(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string filePath)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(filePath);
                CopyImageToClipboard(bytes);
            }
            catch (Exception ex) { TxtStatus.Text = Lf("common.copy_failed", ex.Message); }
        }
    }

    private void OnCloseGenResultBar(object sender, RoutedEventArgs e)
    {
        GenResultBar.Visibility = Visibility.Collapsed;
    }

    private void OnGenResultBarDrag(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        GenResultBarTranslate.X += e.Delta.Translation.X;
        GenResultBarTranslate.Y += e.Delta.Translation.Y;
    }

    // ═══════════════════════════════════════════════════════════
    //  生图预览区右键菜单 & 拖放
    // ═══════════════════════════════════════════════════════════

    private void SetupGenPreviewContextMenu()
    {
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            bool hasImage = _currentGenImageBytes != null;

            var copyItem = new MenuFlyoutItem
            {
                Text = L("common.copy"),
                Icon = new SymbolIcon(Symbol.Copy),
                IsEnabled = hasImage,
            };
            copyItem.Click += (_, _) =>
            {
                if (_currentGenImageBytes != null)
                    CopyImageToClipboard(_currentGenImageBytes);
            };
            flyout.Items.Add(copyItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var readerItem = new MenuFlyoutItem
            {
                Text = L("action.send_to_inspect"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEE6F" },
                IsEnabled = hasImage,
            };
            readerItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes == null) return;
                SwitchMode(AppMode.Inspect);
                await LoadInspectImageFromBytesAsync(_currentGenImageBytes,
                    _currentGenImagePath != null ? Path.GetFileName(_currentGenImagePath) : null);
            };
            flyout.Items.Add(readerItem);

            var postItem = new MenuFlyoutItem
            {
                Text = L("action.send_to_post"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEB3C" },
                IsEnabled = hasImage,
            };
            postItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes == null) return;
                await SendBytesToEffectsAsync(_currentGenImageBytes, _currentGenImagePath);
            };
            flyout.Items.Add(postItem);

            var i2iItem = new MenuFlyoutItem
            {
                Text = L("action.send_to_i2i"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEDFB" },
                IsEnabled = hasImage,
            };
            i2iItem.Click += (_, _) =>
            {
                if (_currentGenImageBytes != null) SendImageToI2I(_currentGenImageBytes, _currentGenImagePath);
            };
            flyout.Items.Add(i2iItem);

            var upscaleItem = new MenuFlyoutItem
            {
                Text = L("action.send_to_upscale"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uECE9" },
                IsEnabled = hasImage,
            };
            upscaleItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes == null) return;
                await SendBytesToUpscaleAsync(_currentGenImageBytes, _currentGenImagePath);
            };
            flyout.Items.Add(upscaleItem);

            if (!string.IsNullOrEmpty(_currentGenImagePath))
            {
                var folderItem = new MenuFlyoutItem
                {
                    Text = L("action.open_containing_folder"),
                    Icon = new SymbolIcon(Symbol.OpenLocal),
                };
                folderItem.Click += (_, _) =>
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(_currentGenImagePath);
                        if (dir != null) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_currentGenImagePath}\"");
                    }
                    catch { }
                };
                flyout.Items.Add(folderItem);
            }

            flyout.Items.Add(new MenuFlyoutSeparator());

            var useParamsItem = new MenuFlyoutItem
            {
                Text = L("action.use_parameters"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8B6" },
                IsEnabled = hasImage,
            };
            useParamsItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes != null)
                    await ApplyDroppedImageMetadata(_currentGenImageBytes, L("image.preview_label"));
            };
            flyout.Items.Add(useParamsItem);

            var useParamsNoSeedItem = new MenuFlyoutItem
            {
                Text = L("action.use_parameters_no_seed"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8B5" },
                IsEnabled = hasImage,
            };
            useParamsNoSeedItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes != null)
                    await ApplyDroppedImageMetadata(_currentGenImageBytes, L("image.preview_label"), skipSeed: true);
            };
            flyout.Items.Add(useParamsNoSeedItem);
            foreach (var item in flyout.Items)
                ApplyMenuTypography(item);
        };
        GenPreviewArea.ContextFlyout = flyout;
    }

    private void OnGenPreviewDragOver(object sender, DragEventArgs e)
    {
        TryAcceptImageFileDrag(e);
    }

    private async void OnGenPreviewDrop(object sender, DragEventArgs e)
    {
        var file = await GetFirstDroppedImageFileAsync(e, includeBmp: false);
        if (file == null)
            return;

        var bytes = await File.ReadAllBytesAsync(file.Path);
        await ApplyDroppedImageMetadata(bytes, file.Name);
    }

    private async Task ApplyDroppedImageMetadata(byte[] bytes, string fileName, bool skipSeed = false)
    {
        var meta = await Task.Run(() => ImageMetadataService.ReadFromBytes(bytes));
        if (meta == null || !meta.IsNaiParsed)
        {
            TxtStatus.Text = Lf("metadata.drop_no_nai_data", fileName);
            return;
        }

        bool accountAssetProtectionMode = _settings.Settings.AccountAssetProtectionMode;
        var skipped = new List<string>();

        string positivePrompt = meta.PositivePrompt;
        bool strippedQuality = false;
        if (positivePrompt.Contains(QualityTagBlock))
        {
            positivePrompt = positivePrompt.Replace(QualityTagBlock, "");
            positivePrompt = System.Text.RegularExpressions.Regex.Replace(positivePrompt, @",\s*,", ",");
            positivePrompt = positivePrompt.Trim(' ', ',');
            strippedQuality = true;
        }

        _genPositivePrompt = positivePrompt;
        _genNegativePrompt = meta.NegativePrompt;
        _genStylePrompt = "";

        var p = _settings.Settings.GenParameters;

        if (strippedQuality)
        {
            p.QualityToggle = true;
            if (IsAdvancedWindowOpen) _advCboQuality.SelectedIndex = 0;
        }

        if (meta.Steps > 0)
        {
            if (accountAssetProtectionMode && meta.Steps > 28)
                skipped.Add(Lf("metadata.skipped.steps", meta.Steps));
            else
                p.Steps = meta.Steps;
        }
        if (!skipSeed && meta.Seed > 0 && meta.Seed <= int.MaxValue) p.Seed = (int)meta.Seed;
        if (meta.Scale > 0) p.Scale = meta.Scale;
        p.CfgRescale = meta.CfgRescale;
        if (!string.IsNullOrEmpty(meta.Sampler)) p.Sampler = meta.Sampler;
        if (!string.IsNullOrEmpty(meta.NoiseSchedule)) p.Schedule = meta.NoiseSchedule;
        p.Variety = meta.SmDyn || meta.Sm;

        if (meta.Width > 0 && meta.Height > 0)
        {
            if (accountAssetProtectionMode && (long)meta.Width * meta.Height > 1024L * 1024)
                skipped.Add(Lf("metadata.skipped.size", meta.Width, meta.Height));
            else
            {
                _customWidth = meta.Width;
                _customHeight = meta.Height;
            }
        }

        SetSizeInputsSilently(_customWidth, _customHeight);
        if (!skipSeed) NbSeed.Value = p.Seed;
        ChkVariety.IsChecked = p.Variety;

        if (meta.CharacterPrompts.Count > 0)
            SetGenCharactersFromMetadata(meta);
        else
            _genCharacters.Clear();
        ApplyReferenceDataFromMetadata(meta);
        RefreshCharacterPanel();

        LoadPromptFromBuffer();
        UpdateSplitVisibility();
        UpdateSizeWarningVisuals();

        if (IsAdvancedWindowOpen) SyncSidebarToAdvanced();

        var notes = new List<string>();
        if (strippedQuality) notes.Add(L("metadata.note.quality_extracted"));
        if (skipSeed) notes.Add(L("metadata.note.seed_skipped"));
        if (skipped.Count > 0) notes.Add(Lf("metadata.note.skipped", string.Join(", ", skipped)));
        AppendReferenceImportNotes(meta, notes);

        TxtStatus.Text = notes.Count > 0
            ? Lf("metadata.applied_with_notes", fileName, string.Join("; ", notes))
            : Lf("metadata.applied", fileName);
    }

    // ═══════════════════════════════════════════════════════════
    //  发送到重绘
    // ═══════════════════════════════════════════════════════════
}
