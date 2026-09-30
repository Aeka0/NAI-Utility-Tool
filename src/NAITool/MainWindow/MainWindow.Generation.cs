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
            long limit = IsAssetProtectionSizeLimitEnabled() ? 1024L : 2048L;
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
        int allowedConcurrency = _settings.IsGenerationTokenRotationEnabled ? 2 : 1;
        if (_activeGenerationCount >= allowedConcurrency)
        {
            TxtStatus.Text = allowedConcurrency == 2
                ? "账号 A 和 B 都在生成中；请等待其中一个完成。"
                : "账号 A 正在生成中。";
            return false;
        }
        if (!await _generationSlots.WaitAsync(0))
        {
            TxtStatus.Text = "账号 A 和 B 都在生成中；请等待其中一个完成。";
            return false;
        }
        _lastGenerationFailureStatusCode = null;
        var autoContext = _autoGenRunning ? _automationRunContext : null;
        var (w, h) = autoContext?.CurrentSizeOverride ?? GetSelectedSize();
        bool keepGenerateButtonInteractive = _autoGenRunning || _continuousGenRunning;
        if (!keepGenerateButtonInteractive) BtnGenerate.IsEnabled = _activeGenerationCount < 1;
        SetGenerationRequestRunning(true);
        UpdateBtnGenerateForApiKey();
        TxtStatus.Text = L("generate.status.generating");
        var p = _settings.Settings.GenParameters.Clone();
        string restoreSeed = p.Seed;
        string? pendingHistoryId = null;
        string? selectedAccountLabel = null;
        bool requestCompleted = false;

        try
        {
            _generateCts = new CancellationTokenSource();
            var ct = _generateCts.Token;
            SaveCurrentPromptToBuffer();
            if (!TryValidateReferenceRequest(out string referenceError))
            {
                TxtStatus.Text = referenceError;
                return false;
            }

            string actualSeed;
            string prompt;
            string negPrompt;
            List<CharacterPromptInfo>? chars;
            List<VibeTransferInfo>? vibes;
            List<PreciseReferenceInfo>? preciseReferences;
            while (true)
            {
                actualSeed = SeedValue.Resolve(p.Seed, forceRandomSeed);
                p.Seed = actualSeed;

                var wildcardContext = CreateWildcardContext(actualSeed, p.Model);
                string automationPrompt = autoContext?.CurrentPromptOverride ?? _genPositivePrompt;
                string positiveRaw = MergeStyleAndMain(_genStylePrompt, automationPrompt);
                string negativeRaw = _genNegativePrompt;
                if (_autoGenRunning && _activeAutomationSettings?.Randomization.RandomizeStyleTags == true)
                {
                    var styleOptions = new RandomStyleOptions(
                        _activeAutomationSettings.Randomization.StyleTagCount,
                        _activeAutomationSettings.Randomization.StyleMinCount,
                        _activeAutomationSettings.Randomization.StyleUseWeight);
                    string? stylePrefix = BuildRandomStylePrefixForRequest(styleOptions);
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

                if (CurrentCharacterEntries.Count > 0) ApplyCharCountPrefixStrip();
                chars = (CurrentCharacterEntries.Count > 0 && !IsCurrentModelV3()) ? GetCharacterData(wildcardContext) : null;
                if (autoContext?.CurrentVibeOverride == null &&
                    ActiveVibeTransferCount() > 0 &&
                    ActivePreciseReferenceCount() == 0)
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
                    restoreSeed = "0";
                    forceRandomSeed = true;
                    continue;
                }

                RememberLastGenerationRequest(signature);
                break;
            }

            if (!_settings.Settings.PrivacyMode)
                pendingHistoryId = AddPendingHistoryItem();
            DebugLog($"[Generate] Start | {w}x{h} | Model={p.Model} | Seed={actualSeed}");
            IProgress<byte[]>? progress = _settings.Settings.StreamGeneration && !_settings.IsGenerationTokenRotationEnabled
                ? new Progress<byte[]>(bytes =>
                {
                    _currentGenImageBytes = bytes;
                    _ = ShowGenPreviewAsync(bytes, w, h);
                })
                : null;
            var (imageBytes, error) = await _naiService.GenerateAsync(
                w, h, prompt, negPrompt,
                chars, vibes, preciseReferences, progress, ct,
                parametersOverride: p,
                accountSelected: label =>
                {
                    selectedAccountLabel = label;
                    OnGenerationAccountStarted(label);
                });
            _lastUsedSeed = actualSeed;

            if (error != null)
            {
                if (pendingHistoryId != null)
                    RemovePendingHistoryItem(pendingHistoryId);
                _lastGenerationFailureStatusCode = _naiService.LastGenerationErrorStatusCode;
                DebugLog($"[Generate] API error: {error}");
                TxtStatus.Text = error;
                return false;
            }
            if (imageBytes == null)
            {
                if (pendingHistoryId != null)
                    RemovePendingHistoryItem(pendingHistoryId);
                DebugLog("[Generate] API returned no image");
                TxtStatus.Text = L("generate.error.empty_result");
                return false;
            }

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
            ArmNewImageDeleteProtection(finalSavedPath);

            await ShowGenPreviewAsync(finalBytes, w, h);

            if (finalSavedPath != null)
            {
                if (pendingHistoryId != null)
                    ResolvePendingHistoryItem(pendingHistoryId, finalSavedPath);
                else
                    AddHistoryItem(finalSavedPath);
            }
            else if (pendingHistoryId != null)
            {
                RemovePendingHistoryItem(pendingHistoryId);
            }

            if (!_autoGenRunning)
                SetGenResultBarRequested(true, resetPosition: true);
            _ = RefreshAnlasInfoAsync(forceRefresh: true);
            UpdateDynamicMenuStates();
            DebugLog($"[Generate] Completed | Seed={actualSeed} | Saved={finalSavedPath}");
            TxtStatus.Text = _settings.Settings.PrivacyMode
                ? L("generate.status.completed_unsaved_privacy")
                : string.IsNullOrWhiteSpace(postSummary)
                ? Lf("generate.status.completed_saved", finalSavedPath)
                : Lf("generate.status.completed_post_saved", postSummary, finalSavedPath);
            requestCompleted = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            if (pendingHistoryId != null)
                RemovePendingHistoryItem(pendingHistoryId);
            DebugLog("[Generate] Cancelled");
            TxtStatus.Text = L("generate.status.cancelled");
            return false;
        }
        catch (Exception ex)
        {
            if (pendingHistoryId != null)
                RemovePendingHistoryItem(pendingHistoryId);
            DebugLog($"[Generate] Failed: {ex}");
            TxtStatus.Text = Lf("generate.status.failed", ex.Message);
            return false;
        }
        finally
        {
            OnGenerationAccountFinished(selectedAccountLabel, requestCompleted);
            SetGenerationRequestRunning(false);
            _generationSlots.Release();
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

    private async Task<string?> SaveToOutputAsync(byte[] imageBytes, string prefix = "gen")
    {
        if (_settings.Settings.PrivacyMode)
            return null;

        var dateDir = Path.Combine(OutputBaseDir, DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dateDir);
        var fileName = $"{prefix}_{DateTime.Now:HHmmss_fff}.png";
        var filePath = Path.Combine(dateDir, fileName);
        var bytesToSave = await PrepareImageBytesForSaveAsync(imageBytes, stripMetadata: false);
        await File.WriteAllBytesAsync(filePath, bytesToSave);
        return filePath;
    }

    // ═══ 生图模式浮动操作窗 ═══

    private const long MaxEnhancePixels = 3L * 1024 * 1024;

    private sealed record EnhanceOptions(
        int Magnitude,
        bool ShowIndividualSettings,
        double Strength,
        double Noise,
        double UpscaleAmount,
        bool UseMaxUpscale,
        int OutputWidth,
        int OutputHeight);

    private static (double Strength, double Noise) GetEnhanceMagnitudeValues(int magnitude) =>
        Math.Clamp(magnitude, 1, 5) switch
        {
            1 => (0.2, 0.0),
            2 => (0.4, 0.0),
            3 => (0.5, 0.0),
            4 => (0.6, 0.0),
            _ => (0.7, 0.1),
        };

    private static bool CanUseMaxEnhance(string model, int width, int height) =>
        IsV5ModelKey(model) &&
        width > 0 && height > 0 &&
        (long)width * height < MaxEnhancePixels * 0.8;

    private static (int Width, int Height) GetEnhanceOutputDimensions(
        int width, int height, double amount, bool useMax = false)
    {
        if (useMax)
        {
            double scale = Math.Min(2, Math.Sqrt((double)MaxEnhancePixels / ((long)width * height)));
            return ((int)Math.Floor(width * scale), (int)Math.Floor(height * scale));
        }
        if (amount <= 1.001)
            return (width, height);
        return ((int)Math.Floor(width * amount), (int)Math.Floor(height * amount));
    }

    private async Task<EnhanceOptions?> ShowGenEnhanceSettingsDialogAsync(int sourceWidth, int sourceHeight)
    {
        var settings = _settings.Settings;
        string model = settings.GenParameters.Model;
        var availableAmounts = new List<double> { 1.0 };
        bool standardPortrait = sourceWidth == 832 && sourceHeight == 1216 ||
            sourceWidth == 1216 && sourceHeight == 832;
        foreach (double amount in standardPortrait ? new[] { 1.5 } : new[] { 1.5, 2.0 })
        {
            var dims = GetEnhanceOutputDimensions(sourceWidth, sourceHeight, amount);
            if ((long)dims.Width * dims.Height <= MaxEnhancePixels &&
                (standardPortrait || dims.Width % 64 == 0 && dims.Height % 64 == 0))
                availableAmounts.Add(amount);
        }
        bool maxAvailable = CanUseMaxEnhance(model, sourceWidth, sourceHeight);

        double selectedAmount = availableAmounts
            .OrderBy(value => Math.Abs(value - settings.EnhanceUpscaleAmount))
            .First();
        int magnitude = Math.Clamp(settings.EnhanceMagnitude, 1, 5);
        var magnitudeValues = GetEnhanceMagnitudeValues(magnitude);

        var upscaleCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (double amount in availableAmounts)
        {
            var dims = GetEnhanceOutputDimensions(sourceWidth, sourceHeight, amount);
            upscaleCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{amount:0.0}×  ({dims.Width} × {dims.Height})",
                Tag = amount,
            });
        }
        if (maxAvailable)
        {
            var dims = GetEnhanceOutputDimensions(sourceWidth, sourceHeight, 1.0, useMax: true);
            upscaleCombo.Items.Add(new ComboBoxItem
            {
                Content = $"最高  ({dims.Width} × {dims.Height})",
                Tag = "max",
            });
        }
        upscaleCombo.SelectedIndex = settings.EnhanceUseMaxUpscale && maxAvailable
            ? upscaleCombo.Items.Count - 1
            : availableAmounts.IndexOf(selectedAmount);

        var magnitudeValueText = new TextBlock
        {
            Text = magnitude.ToString(CultureInfo.InvariantCulture),
            MinWidth = 28,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var magnitudeSlider = new Slider
        {
            Minimum = 1,
            Maximum = 5,
            Value = magnitude,
            StepFrequency = 1,
            SmallChange = 1,
            LargeChange = 1,
        };
        var magnitudeGrid = new Grid { ColumnSpacing = 10 };
        magnitudeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        magnitudeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        magnitudeGrid.Children.Add(magnitudeSlider);
        Grid.SetColumn(magnitudeValueText, 1);
        magnitudeGrid.Children.Add(magnitudeValueText);
        var magnitudePanel = new StackPanel { Spacing = 4 };
        magnitudePanel.Children.Add(new TextBlock { Text = "Magnitude（增强幅度）" });
        magnitudePanel.Children.Add(magnitudeGrid);

        var strengthBox = new NumberBox
        {
            Header = "Strength",
            Minimum = 0.01,
            Maximum = 0.99,
            SmallChange = 0.01,
            Value = settings.EnhanceShowIndividualSettings ? settings.EnhanceStrength : magnitudeValues.Strength,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var noiseBox = new NumberBox
        {
            Header = "Noise",
            Minimum = 0,
            Maximum = 0.99,
            SmallChange = 0.01,
            Value = settings.EnhanceShowIndividualSettings ? settings.EnhanceNoise : magnitudeValues.Noise,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var individualGrid = new Grid { ColumnSpacing = 10 };
        individualGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        individualGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        individualGrid.Children.Add(strengthBox);
        Grid.SetColumn(noiseBox, 1);
        individualGrid.Children.Add(noiseBox);

        var individualToggle = new ToggleSwitch
        {
            Header = "显示单独设置",
            OnContent = "已展开",
            OffContent = "使用 Magnitude",
            IsOn = settings.EnhanceShowIndividualSettings,
        };
        magnitudePanel.Visibility = individualToggle.IsOn ? Visibility.Collapsed : Visibility.Visible;
        individualGrid.Visibility = individualToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;

        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.76,
            FontSize = 12,
        };
        void RefreshSummary()
        {
            double amount = upscaleCombo.SelectedItem is ComboBoxItem item && item.Tag is double taggedAmount
                ? taggedAmount
                : 1.0;
            bool useMax = upscaleCombo.SelectedItem is ComboBoxItem maxItem &&
                maxItem.Tag is string maxTag && maxTag == "max";
            int currentMagnitude = (int)Math.Round(magnitudeSlider.Value);
            var mapped = GetEnhanceMagnitudeValues(currentMagnitude);
            double strength = individualToggle.IsOn ? Math.Clamp(strengthBox.Value, 0.01, 0.99) : mapped.Strength;
            double noise = individualToggle.IsOn ? Math.Clamp(noiseBox.Value, 0, 0.99) : mapped.Noise;
            var dims = GetEnhanceOutputDimensions(sourceWidth, sourceHeight, amount, useMax);
            int cost = EstimateGenEnhanceAnlasCost(dims.Width, dims.Height, strength, noise);
            summary.Text = $"输出 {dims.Width} × {dims.Height}  ·  Strength {strength:0.00}  ·  Noise {noise:0.00}" +
                           (cost > 0 ? $"  ·  预计 {cost:N0} Anlas" : "  ·  当前参数预计不消耗 Anlas");
        }

        magnitudeSlider.ValueChanged += (_, _) =>
        {
            int currentMagnitude = (int)Math.Round(magnitudeSlider.Value);
            magnitudeValueText.Text = currentMagnitude.ToString(CultureInfo.InvariantCulture);
            if (!individualToggle.IsOn)
            {
                var mapped = GetEnhanceMagnitudeValues(currentMagnitude);
                strengthBox.Value = mapped.Strength;
                noiseBox.Value = mapped.Noise;
            }
            RefreshSummary();
        };
        individualToggle.Toggled += (_, _) =>
        {
            magnitudePanel.Visibility = individualToggle.IsOn ? Visibility.Collapsed : Visibility.Visible;
            individualGrid.Visibility = individualToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
            if (!individualToggle.IsOn)
            {
                var mapped = GetEnhanceMagnitudeValues((int)Math.Round(magnitudeSlider.Value));
                strengthBox.Value = mapped.Strength;
                noiseBox.Value = mapped.Noise;
            }
            RefreshSummary();
        };
        upscaleCombo.SelectionChanged += (_, _) => RefreshSummary();
        strengthBox.ValueChanged += (_, _) => RefreshSummary();
        noiseBox.ValueChanged += (_, _) => RefreshSummary();

        var panel = new StackPanel { Spacing = 12, Width = 430 };
        panel.Children.Add(new TextBlock { Text = "Upscale Amount（增强倍率）" });
        panel.Children.Add(upscaleCombo);
        panel.Children.Add(individualToggle);
        panel.Children.Add(magnitudePanel);
        panel.Children.Add(individualGrid);
        panel.Children.Add(new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Child = summary,
        });
        RefreshSummary();

        var dialog = new ContentDialog
        {
            Title = "增强设置",
            Content = panel,
            PrimaryButtonText = "开始增强",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        dialog.Resources["ContentDialogMaxWidth"] = 520.0;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        selectedAmount = upscaleCombo.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag is double amountTag
            ? amountTag
            : selectedAmount;
        bool selectedMax = upscaleCombo.SelectedItem is ComboBoxItem finalItem &&
            finalItem.Tag is string finalTag && finalTag == "max";
        magnitude = (int)Math.Round(magnitudeSlider.Value);
        var finalMapped = GetEnhanceMagnitudeValues(magnitude);
        double finalStrength = individualToggle.IsOn ? Math.Clamp(strengthBox.Value, 0.01, 0.99) : finalMapped.Strength;
        double finalNoise = individualToggle.IsOn ? Math.Clamp(noiseBox.Value, 0, 0.99) : finalMapped.Noise;
        var output = GetEnhanceOutputDimensions(sourceWidth, sourceHeight, selectedAmount, selectedMax);

        settings.EnhanceMagnitude = magnitude;
        settings.EnhanceShowIndividualSettings = individualToggle.IsOn;
        settings.EnhanceStrength = finalStrength;
        settings.EnhanceNoise = finalNoise;
        settings.EnhanceUpscaleAmount = selectedAmount;
        settings.EnhanceUseMaxUpscale = selectedMax;
        return new EnhanceOptions(magnitude, individualToggle.IsOn, finalStrength, finalNoise,
            selectedAmount, selectedMax, output.Width, output.Height);
    }

    private async void OnEnhanceGenResult(object sender, RoutedEventArgs e)
    {
        if (_currentGenImageBytes == null)
        { TxtStatus.Text = L("generate.error.no_result_to_send"); return; }

        await BeginGenEnhanceAsync(_currentGenImageBytes, _currentGenImagePath);
    }

    private async Task<bool> BeginGenEnhanceAsync(byte[] imageBytes, string? imagePath, bool forceRandomSeed = false)
    {
        if (_generateRequestRunning)
            return false;

        if (string.IsNullOrEmpty(_settings.Settings.ApiToken))
        {
            OnNetworkSettings(this, new RoutedEventArgs());
            return false;
        }

        if (!TryGetImageDimensions(imageBytes, out int width, out int height))
        {
            TxtStatus.Text = L("generate.error.empty_result");
            return false;
        }

        SyncPromptGenerationInputsToState();
        var options = await ShowGenEnhanceSettingsDialogAsync(width, height);
        if (options == null)
            return false;
        if (!await ConfirmGenEnhanceSizeAsync(options.OutputWidth, options.OutputHeight))
            return false;

        _settings.Save();
        SetGenResultBarRequested(false);
        return await DoGenEnhanceAsync(imageBytes, imagePath, options, forceRandomSeed);
    }

    private async Task<bool> ConfirmGenEnhanceSizeAsync(int width, int height)
    {
        if ((long)width * height <= 1024L * 1024)
            return true;

        if (IsAssetProtectionSizeLimitEnabled())
        {
            var blockedDialog = new ContentDialog
            {
                Title = L("dialog.notify.title"),
                Content = new TextBlock
                {
                    Text = L("generate.enhance.asset_protection_oversized_blocked"),
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = L("common.ok"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
            };
            blockedDialog.Resources["ContentDialogMaxWidth"] = 520.0;
            await blockedDialog.ShowAsync();
            TxtStatus.Text = L("generate.enhance.asset_protection_oversized_blocked");
            return false;
        }

        int anlasCost = EstimateGenEnhanceAnlasCost(width, height);
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = anlasCost > 0
                ? Lf("generate.enhance.oversized_confirm_message_with_cost", anlasCost)
                : L("generate.enhance.oversized_confirm_message"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = Lf("generate.enhance.oversized_confirm_size", width, height),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
            FontSize = 12,
        });

        var dialog = new ContentDialog
        {
            Title = L("dialog.notify.title"),
            Content = panel,
            PrimaryButtonText = L("common.yes"),
            CloseButtonText = L("common.no"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        ApplyGoldAccentResources(dialog.Resources);
        dialog.Resources["ContentDialogMaxWidth"] = 520.0;

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            return true;

        TxtStatus.Text = L("generate.enhance.cancelled");
        return false;
    }

    private async Task<bool> DoGenEnhanceAsync(
        byte[] sourceImageBytes,
        string? sourceImagePath,
        EnhanceOptions options,
        bool forceRandomSeed = false)
    {
        if (!TryGetImageDimensions(sourceImageBytes, out int sourceWidth, out int sourceHeight))
        { TxtStatus.Text = L("generate.error.empty_result"); return false; }
        int outputWidth = options.OutputWidth;
        int outputHeight = options.OutputHeight;
        int requestWidth = options.UseMaxUpscale ? sourceWidth : outputWidth;
        int requestHeight = options.UseMaxUpscale ? sourceHeight : outputHeight;

        BtnGenerate.IsEnabled = false;
        SetGenerationRequestRunning(true);
        UpdateBtnGenerateForApiKey();
        UpdateGenEnhanceButtonWarning();
        TxtStatus.Text = L("generate.status.generating");

        _currentGenImageBytes = sourceImageBytes;
        _currentGenImagePath = sourceImagePath;
        await ShowGenPreviewAsync(sourceImageBytes, sourceWidth, sourceHeight);

        var enhanceParams = CreateGenEnhanceParameters(
            _settings.Settings.GenParameters,
            options.Strength,
            options.Noise);
        string requestedSeed = "0";
        string? pendingHistoryId = null;
        string? selectedAccountLabel = null;
        bool requestCompleted = false;

        try
        {
            _generateCts?.Cancel();
            _generateCts = new CancellationTokenSource();
            var ct = _generateCts.Token;
            SaveCurrentPromptToBuffer();

            string imageBase64 = await Task.Run(() => NovelAIService.PrepareEnhancedImageBase64(
                sourceImageBytes, requestWidth, requestHeight), ct);
            string actualSeed;
            string prompt;
            string negPrompt;
            List<CharacterPromptInfo>? chars;
            List<VibeTransferInfo>? vibes;
            List<PreciseReferenceInfo>? preciseReferences;

            while (true)
            {
                actualSeed = SeedValue.Resolve(requestedSeed, forceRandomSeed);
                enhanceParams.Seed = actualSeed;

                var wildcardContext = CreateWildcardContext(actualSeed, enhanceParams.Model);
                (prompt, negPrompt) = GetPrompts(wildcardContext);
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    TxtStatus.Text = L("generate.error.prompt_required");
                    return false;
                }

                if (CurrentCharacterEntries.Count > 0)
                    ApplyCharCountPrefixStrip();
                if (ActiveVibeTransferCount() > 0 && ActivePreciseReferenceCount() == 0)
                {
                    string? encodeError = await EnsureVibesEncodedAsync(enhanceParams.Model, ct);
                    if (encodeError != null) { TxtStatus.Text = encodeError; return false; }
                }

                chars = (CurrentCharacterEntries.Count > 0 && !IsV3ModelKey(enhanceParams.Model)) ? GetCharacterData(wildcardContext) : null;
                vibes = GetVibeTransferData();
                preciseReferences = GetPreciseReferenceData();

                var signature = BuildI2IGenerationRequestSignature(
                    "gen-enhance",
                    enhanceParams,
                    requestWidth,
                    requestHeight,
                    actualSeed,
                    prompt,
                    negPrompt,
                    chars,
                    vibes,
                    preciseReferences,
                    imageBase64,
                    null,
                    Vector2.Zero);
                var duplicateDecision = await CheckDuplicateGenerationRequestAsync(signature, requestedSeed);
                if (duplicateDecision == DuplicateGenerationDecision.Cancel)
                    return false;
                if (duplicateDecision == DuplicateGenerationDecision.ProceedWithRandomSeed)
                {
                    requestedSeed = "0";
                    forceRandomSeed = true;
                    continue;
                }

                RememberLastGenerationRequest(signature);
                break;
            }

            IProgress<byte[]>? progress = _settings.Settings.StreamGeneration
                ? new Progress<byte[]>(bytes =>
                {
                    _currentGenImageBytes = bytes;
                    _ = ShowGenPreviewAsync(bytes, outputWidth, outputHeight);
                })
                : null;

            if (!_settings.Settings.PrivacyMode)
                pendingHistoryId = AddPendingHistoryItem();
            DebugLog($"[Enhance] Start | Request={requestWidth}x{requestHeight} | Expected={outputWidth}x{outputHeight} | Model={enhanceParams.Model} | Seed={actualSeed} | Strength={options.Strength:0.00} | Noise={options.Noise:0.00} | Amount={(options.UseMaxUpscale ? "Max" : $"{options.UpscaleAmount:0.0}x")}");
            var (imageBytes, error) = await _naiService.ImageToImageAsync(
                imageBase64,
                requestWidth, requestHeight,
                prompt, negPrompt, chars, vibes, preciseReferences, progress, ct,
                parametersOverride: enhanceParams,
                accountSelected: label =>
                {
                    selectedAccountLabel = label;
                    OnGenerationAccountStarted(label);
                },
                upscaledEnhance: options.UseMaxUpscale,
                isEnhance: true);
            _lastUsedSeed = actualSeed;

            if (error != null)
            {
                if (pendingHistoryId != null)
                    RemovePendingHistoryItem(pendingHistoryId);
                DebugLog($"[Enhance] API error: {error}");
                TxtStatus.Text = error;
                return false;
            }
            if (imageBytes == null)
            {
                if (pendingHistoryId != null)
                    RemovePendingHistoryItem(pendingHistoryId);
                DebugLog("[Enhance] API returned no image");
                TxtStatus.Text = L("generate.error.empty_result");
                return false;
            }

            string? savedPath = await SaveToOutputAsync(imageBytes, "enhance");
            _currentGenImageBytes = imageBytes;
            _currentGenImagePath = savedPath;
            ArmNewImageDeleteProtection(savedPath);

            int displayWidth = outputWidth;
            int displayHeight = outputHeight;
            if (TryGetImageDimensions(imageBytes, out int actualWidth, out int actualHeight))
            {
                displayWidth = actualWidth;
                displayHeight = actualHeight;
            }
            await ShowGenPreviewAsync(imageBytes, displayWidth, displayHeight);
            if (savedPath != null)
            {
                if (pendingHistoryId != null)
                    ResolvePendingHistoryItem(pendingHistoryId, savedPath);
                else
                    AddHistoryItem(savedPath);
            }
            else if (pendingHistoryId != null)
            {
                RemovePendingHistoryItem(pendingHistoryId);
            }

            SetGenResultBarRequested(true, resetPosition: true);

            _ = RefreshAnlasInfoAsync(forceRefresh: true);
            UpdateDynamicMenuStates();
            DebugLog($"[Enhance] Completed | Seed={actualSeed} | Saved={savedPath}");
            TxtStatus.Text = _settings.Settings.PrivacyMode
                ? L("generate.status.completed_unsaved_privacy")
                : Lf("generate.status.completed_saved", savedPath);
            requestCompleted = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            if (pendingHistoryId != null)
                RemovePendingHistoryItem(pendingHistoryId);
            DebugLog("[Enhance] Cancelled");
            TxtStatus.Text = L("generate.status.cancelled");
            return false;
        }
        catch (Exception ex)
        {
            if (pendingHistoryId != null)
                RemovePendingHistoryItem(pendingHistoryId);
            DebugLog($"[Enhance] Failed: {ex}");
            TxtStatus.Text = Lf("generate.status.failed", ex.Message);
            return false;
        }
        finally
        {
            OnGenerationAccountFinished(selectedAccountLabel, requestCompleted);
            SetGenerationRequestRunning(false);
            UpdateBtnGenerateForApiKey();
            UpdateGenEnhanceButtonWarning();
        }
    }

    private static NAIParameters CreateGenEnhanceParameters(
        NAIParameters source,
        double strength,
        double noise) => new()
    {
        Model = source.Model,
        Sampler = source.Sampler,
        Schedule = source.Schedule,
        Scale = source.Scale,
        CfgRescale = source.CfgRescale,
        TagHintTransparentBackground = source.TagHintTransparentBackground,
        StraightAlpha = source.StraightAlpha,
        Sm = source.Sm,
        Variety = source.Variety,
        QualityToggle = source.QualityToggle,
        Steps = source.Steps,
        Seed = source.Seed,
        UcPreset = source.UcPreset,
        DenoiseStrength = Math.Clamp(strength, 0.01, 0.99),
        DenoiseNoise = Math.Clamp(noise, 0, 0.99),
    };

    private void OnSendToI2I(object sender, RoutedEventArgs e)
    {
        if (_currentGenImageBytes == null)
        { TxtStatus.Text = L("generate.error.no_result_to_send"); return; }

        SetGenResultBarRequested(false);
        SendImageToI2I(_currentGenImageBytes, _currentGenImagePath);
    }

    private async void OnSendToEffectsFromGen(object sender, RoutedEventArgs e)
    {
        if (_currentGenImageBytes == null)
        {
            TxtStatus.Text = L("generate.error.no_result_to_send");
            return;
        }

        SetGenResultBarRequested(false);
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
        SetGenResultBarRequested(false);
        SwitchMode(AppMode.Inspect);
        await LoadInspectImageFromBytesAsync(_currentGenImageBytes, _currentGenImagePath != null ? Path.GetFileName(_currentGenImagePath) : null);
    }

    private async void OnDeleteGenResult(object sender, RoutedEventArgs e)
    {
        if (TryBlockNewImageDeletion(_currentGenImagePath, isCurrentPreviewAction: true))
            return;

        if (!_genResultBarResident)
            SetGenResultBarRequested(false);

        string? deletedPath = _currentGenImagePath;
        if (!string.IsNullOrEmpty(deletedPath) && File.Exists(deletedPath))
        {
            try
            {
                int idx = _historyFiles.IndexOf(deletedPath);
                if (!TryDeleteImageFileWithConfiguredBehavior(deletedPath))
                    return;

                RemoveHistoryFile(deletedPath);
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

        ClearCurrentGenPreview();
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
        _genResultBarAutoOpenEnabled = false;
        _genResultBarResident = false;
        SetGenResultBarRequested(false);
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

            var enhanceItem = new MenuFlyoutItem
            {
                Text = L("button.enhance"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE771" },
                IsEnabled = hasImage && !_generateRequestRunning,
            };
            enhanceItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes != null)
                    await BeginGenEnhanceAsync(_currentGenImageBytes, _currentGenImagePath);
            };
            flyout.Items.Add(enhanceItem);

            var saveAsItem = new MenuFlyoutItem
            {
                Text = L("menu.file.save_as"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE792" },
                IsEnabled = hasImage,
            };
            saveAsItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes != null)
                    await SaveImageBytesAsAsync(_currentGenImageBytes, stripMetadata: false, _currentGenImagePath);
            };
            flyout.Items.Add(saveAsItem);

            var saveAsStrippedItem = new MenuFlyoutItem
            {
                Text = L("menu.file.save_as_stripped"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE792" },
                IsEnabled = hasImage,
            };
            saveAsStrippedItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes != null)
                    await SaveImageBytesAsAsync(_currentGenImageBytes, stripMetadata: true, _currentGenImagePath);
            };
            flyout.Items.Add(saveAsStrippedItem);

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

            var useSeedItem = new MenuFlyoutItem
            {
                Text = L("action.use_seed"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uF0B9" },
                IsEnabled = hasImage,
            };
            useSeedItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes != null)
                    await ApplyImageSeedToGenerationAsync(_currentGenImageBytes, L("image.preview_label"));
            };
            flyout.Items.Add(useSeedItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var deleteItem = new MenuFlyoutItem
            {
                Text = L("common.delete"),
                Icon = new SymbolIcon(Symbol.Delete),
                IsEnabled = hasImage,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28)),
            };
            deleteItem.Click += OnDeleteGenResult;
            flyout.Items.Add(deleteItem);

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
        await ApplyDroppedImageMetadata(bytes, file.Name, promptForOptions: true);
    }

    private async Task ApplyDroppedImageMetadata(byte[] bytes, string fileName,
        bool skipSeed = false, bool promptForOptions = false)
    {
        var meta = await Task.Run(() => ImageMetadataService.ReadFromBytes(bytes));
        if (meta == null || !meta.IsNaiParsed)
        {
            TxtStatus.Text = Lf("metadata.drop_no_nai_data", fileName);
            return;
        }

        if (promptForOptions)
        {
            var selection = await ShowMetadataImportDialogAsync(meta);
            if (selection != null) ApplyMetadataToGeneration(meta, selection);
            return;
        }

        bool blockOversizedSteps = IsAssetProtectionStepLimitEnabled();
        bool blockOversizedDimensions = IsAssetProtectionSizeLimitEnabled();
        var skipped = new List<string>();
        var notes = new List<string>();
        var p = _settings.Settings.GenParameters;
        ApplyImportedImageModel(meta, p, GenerationModels);

        ApplyGenerationModelFromMetadata(meta);

        var presetMatch = ExtractImportedPromptPresetMatch(meta.PositivePrompt, meta.NegativePrompt, p.Model);
        string positivePrompt = presetMatch.PositivePrompt;
        string negativePrompt = presetMatch.NegativePrompt;

        _genPositivePrompt = positivePrompt;
        _genNegativePrompt = negativePrompt;
        _genStylePrompt = "";

        p.QualityToggle = presetMatch.QualityMatched;
        p.UcPreset = presetMatch.UcPresetMatched ?? 2;

        if (meta.Steps > 0)
        {
            if (blockOversizedSteps && meta.Steps > 28)
                skipped.Add(Lf("metadata.skipped.steps", meta.Steps));
            else
                p.Steps = meta.Steps;
        }
        if (!skipSeed && !string.IsNullOrEmpty(meta.Seed)) p.Seed = meta.Seed;
        if (meta.Scale > 0) p.Scale = meta.Scale;
        p.CfgRescale = meta.CfgRescale;
        if (meta.TagHintTransparentBackground.HasValue) p.TagHintTransparentBackground = meta.TagHintTransparentBackground.Value;
        if (meta.StraightAlpha.HasValue) p.StraightAlpha = meta.StraightAlpha.Value;
        if (!string.IsNullOrEmpty(meta.Sampler)) p.Sampler = meta.Sampler;
        if (!string.IsNullOrEmpty(meta.NoiseSchedule)) p.Schedule = meta.NoiseSchedule;
        p.Variety = meta.SmDyn || meta.Sm;

        if (meta.Width > 0 && meta.Height > 0)
        {
            if (blockOversizedDimensions && (long)meta.Width * meta.Height > 1024L * 1024)
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

        if (presetMatch.QualityMatched) notes.Add(L("metadata.note.quality_extracted"));
        if (presetMatch.UcPresetMatched.HasValue)
            notes.Add(Lf("metadata.note.negative_quality_extracted", GetUcPresetDisplayName(presetMatch.UcPresetMatched.Value)));
        if (skipSeed) notes.Add(L("metadata.note.seed_skipped"));
        if (skipped.Count > 0) notes.Add(Lf("metadata.note.skipped", string.Join(", ", skipped)));
        AppendReferenceImportNotes(meta, notes);

        TxtStatus.Text = notes.Count > 0
            ? Lf("metadata.applied_with_notes", fileName, string.Join("; ", notes))
            : Lf("metadata.applied", fileName);
    }

    private async Task ApplyImageSeedToGenerationAsync(byte[] bytes, string fileName)
    {
        var meta = await Task.Run(() => ImageMetadataService.ReadFromBytes(bytes));
        if (meta == null || string.IsNullOrEmpty(meta.Seed))
        {
            TxtStatus.Text = Lf("metadata.no_usable_seed", fileName);
            return;
        }

        string seed = meta.Seed;
        _settings.Settings.GenParameters.Seed = seed;
        NbSeed.Value = seed;
        if (IsAdvancedWindowOpen) SyncSidebarToAdvanced();
        UpdateSeedRandomizeButtonStyle();

        TxtStatus.Text = Lf("metadata.seed_applied", fileName, seed);
    }

    // ═══════════════════════════════════════════════════════════
    //  发送到重绘
    // ═══════════════════════════════════════════════════════════
}
