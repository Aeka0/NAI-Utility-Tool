using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NAITool.Services;

namespace NAITool;

public sealed partial class MainWindow
{
    private sealed record EnhanceOptions(
        double Strength,
        double Noise,
        bool UseMaxUpscale,
        int OutputWidth,
        int OutputHeight);

    private async Task<EnhanceOptions?> ShowGenEnhanceSettingsDialogAsync(int sourceWidth, int sourceHeight)
    {
        var settings = _settings.Settings;
        string model = settings.GenParameters.Model;
        var availableAmounts = EnhanceRules.GetAvailableAmounts(sourceWidth, sourceHeight);
        if (availableAmounts.Count == 0)
        {
            TxtStatus.Text = L("enhance.size_limit");
            return null;
        }
        bool maxAvailable = EnhanceRules.CanUseMax(model, sourceWidth, sourceHeight);
        double selectedAmount = EnhanceRules.GetPreferredAmount(availableAmounts, settings.EnhanceUpscaleAmount);
        int magnitude = Math.Clamp(settings.EnhanceMagnitude, 1, 5);
        var magnitudeValues = EnhanceRules.GetMagnitudeValues(magnitude);

        var upscaleCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (double amount in availableAmounts)
        {
            var dims = EnhanceRules.GetOutputDimensions(sourceWidth, sourceHeight, amount);
            upscaleCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{amount:0.0}×  ({dims.Width} × {dims.Height})",
                Tag = amount,
            });
        }
        if (maxAvailable)
        {
            var dims = EnhanceRules.GetOutputDimensions(sourceWidth, sourceHeight, 1.0, useMax: true);
            upscaleCombo.Items.Add(new ComboBoxItem
            {
                Content = Lf("enhance.max", dims.Width, dims.Height),
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
        magnitudePanel.Children.Add(new TextBlock { Text = L("enhance.magnitude") });
        magnitudePanel.Children.Add(magnitudeGrid);

        var strengthBox = new NumberBox
        {
            Header = L("references.precise.strength"),
            Minimum = 0.01,
            Maximum = 0.99,
            SmallChange = 0.01,
            Value = settings.EnhanceShowIndividualSettings ? settings.EnhanceStrength : magnitudeValues.Strength,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var noiseBox = new NumberBox
        {
            Header = L("post.effect.noise"),
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
            Header = L("enhance.individual"),
            OnContent = L("common.on"),
            OffContent = L("enhance.use_magnitude"),
            IsOn = settings.EnhanceShowIndividualSettings,
        };
        magnitudePanel.Visibility = individualToggle.IsOn ? Visibility.Collapsed : Visibility.Visible;
        individualGrid.Visibility = individualToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;

        ContentDialog? dialog = null;
        Button? confirmationButton = null;
        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.76,
            FontSize = 12,
        };
        void RefreshSummary()
        {
            bool valid = !individualToggle.IsOn ||
                double.IsFinite(strengthBox.Value) && double.IsFinite(noiseBox.Value);
            if (dialog != null) dialog.IsPrimaryButtonEnabled = valid;
            if (!valid)
            {
                summary.Text = L("enhance.invalid_values");
                return;
            }
            double amount = upscaleCombo.SelectedItem is ComboBoxItem item && item.Tag is double taggedAmount
                ? taggedAmount
                : 1.0;
            bool useMax = upscaleCombo.SelectedItem is ComboBoxItem maxItem &&
                maxItem.Tag is string maxTag && maxTag == "max";
            int currentMagnitude = (int)Math.Round(magnitudeSlider.Value);
            var mapped = EnhanceRules.GetMagnitudeValues(currentMagnitude);
            double strength = individualToggle.IsOn ? Math.Clamp(strengthBox.Value, 0.01, 0.99) : mapped.Strength;
            double noise = individualToggle.IsOn ? Math.Clamp(noiseBox.Value, 0, 0.99) : mapped.Noise;
            var dims = EnhanceRules.GetOutputDimensions(sourceWidth, sourceHeight, amount, useMax);
            int cost = EstimateGenEnhanceAnlasCost(dims.Width, dims.Height, strength, noise);
            summary.Text = Lf("enhance.summary", dims.Width, dims.Height, strength, noise);
            bool overCostLimit = !settings.UsesCustomApiBaseUrl &&
                NovelAiAnlasCalculator.PaidBaseCost(model, dims.Width, dims.Height,
                    settings.GenParameters.Steps, sm: false, strength: strength) > NovelAiAnlasCalculator.MaxBaseCost;
            if (overCostLimit)
                summary.Text += "\n" + Lf("api.error.generation_cost_limit", NovelAiAnlasCalculator.MaxBaseCost);
            if (dialog != null)
            {
                dialog.IsPrimaryButtonEnabled = !overCostLimit;
                dialog.PrimaryButtonText = cost > 0 ? $"{cost} Anlas · {L("enhance.start")}" : L("enhance.start");
            }
            if (confirmationButton != null)
            {
                confirmationButton.Content = cost > 0
                    ? CreateAnlasActionButtonContent(L("enhance.start"), cost)
                    : L("enhance.start");
                if (cost > 0) ApplyGoldAccentButtonStyle(confirmationButton);
                else ClearGoldAccentButtonStyle(confirmationButton);
            }
        }

        magnitudeSlider.ValueChanged += (_, _) =>
        {
            int currentMagnitude = (int)Math.Round(magnitudeSlider.Value);
            magnitudeValueText.Text = currentMagnitude.ToString(CultureInfo.InvariantCulture);
            if (!individualToggle.IsOn)
            {
                var mapped = EnhanceRules.GetMagnitudeValues(currentMagnitude);
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
                var mapped = EnhanceRules.GetMagnitudeValues((int)Math.Round(magnitudeSlider.Value));
                strengthBox.Value = mapped.Strength;
                noiseBox.Value = mapped.Noise;
            }
            RefreshSummary();
        };
        upscaleCombo.SelectionChanged += (_, _) => RefreshSummary();
        strengthBox.ValueChanged += (_, _) => RefreshSummary();
        noiseBox.ValueChanged += (_, _) => RefreshSummary();

        var panel = new StackPanel { Spacing = 12, Width = 430 };
        panel.Children.Add(new TextBlock { Text = L("enhance.upscale_amount") });
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

        dialog = new ContentDialog
        {
            Title = L("enhance.title"),
            Content = panel,
            PrimaryButtonText = L("enhance.start"),
            CloseButtonText = L("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        dialog.Resources["ContentDialogMaxWidth"] = 520.0;
        dialog.Opened += (_, _) =>
        {
            confirmationButton = FindDescendant<Button>(dialog, "PrimaryButton");
            RefreshSummary();
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            RefreshSummary();
            args.Cancel = !dialog.IsPrimaryButtonEnabled;
        };
        RefreshSummary();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            TxtStatus.Text = L("generate.enhance.cancelled");
            return null;
        }
        if (individualToggle.IsOn && (!double.IsFinite(strengthBox.Value) || !double.IsFinite(noiseBox.Value)))
            return null;

        selectedAmount = upscaleCombo.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag is double amountTag
            ? amountTag
            : selectedAmount;
        bool selectedMax = upscaleCombo.SelectedItem is ComboBoxItem finalItem &&
            finalItem.Tag is string finalTag && finalTag == "max";
        magnitude = (int)Math.Round(magnitudeSlider.Value);
        var finalMapped = EnhanceRules.GetMagnitudeValues(magnitude);
        double finalStrength = individualToggle.IsOn ? Math.Clamp(strengthBox.Value, 0.01, 0.99) : finalMapped.Strength;
        double finalNoise = individualToggle.IsOn ? Math.Clamp(noiseBox.Value, 0, 0.99) : finalMapped.Noise;
        var output = EnhanceRules.GetOutputDimensions(sourceWidth, sourceHeight, selectedAmount, selectedMax);

        settings.EnhanceMagnitude = magnitude;
        settings.EnhanceShowIndividualSettings = individualToggle.IsOn;
        settings.EnhanceStrength = finalStrength;
        settings.EnhanceNoise = finalNoise;
        settings.EnhanceUpscaleAmount = selectedAmount;
        settings.EnhanceUseMaxUpscale = selectedMax;
        return new EnhanceOptions(finalStrength, finalNoise, selectedMax, output.Width, output.Height);
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
        if (!TryValidateCharacterCount(out string characterError))
        {
            TxtStatus.Text = characterError;
            return false;
        }
        var options = await ShowGenEnhanceSettingsDialogAsync(width, height);
        if (options == null)
            return false;
        if (!await ValidateGenEnhanceSizeAsync(options.OutputWidth, options.OutputHeight))
            return false;

        _settings.Save();
        SetGenResultBarRequested(false);
        return await DoGenEnhanceAsync(imageBytes, imagePath, options, forceRandomSeed);
    }

    private async Task<bool> ValidateGenEnhanceSizeAsync(int width, int height)
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

        return true;
    }

    private async Task<bool> DoGenEnhanceAsync(byte[] sourceImageBytes, string? sourceImagePath,
        EnhanceOptions options, bool forceRandomSeed = false)
    {
        if (_generateRequestRunning) return false;
        if (!TryGetImageDimensions(sourceImageBytes, out int sourceWidth, out int sourceHeight))
        { TxtStatus.Text = L("generate.error.empty_result"); return false; }
        int width = options.OutputWidth;
        int height = options.OutputHeight;
        int requestWidth = options.UseMaxUpscale ? sourceWidth : width;
        int requestHeight = options.UseMaxUpscale ? sourceHeight : height;

        BtnGenerate.IsEnabled = false;
        SetGenerationRequestRunning(true);
        UpdateBtnGenerateForApiKey();
        UpdateGenEnhanceButtonWarning();
        TxtStatus.Text = L("generate.status.generating");

        _currentGenImageBytes = sourceImageBytes;
        _currentGenImagePath = sourceImagePath;

        var enhanceParams = CreateGenEnhanceParameters(_settings.Settings.GenParameters, options.Strength, options.Noise);
        string requestedSeed = "0";
        string? pendingHistoryId = null;

        try
        {
            _generateCts?.Cancel();
            _generateCts = new CancellationTokenSource();
            var ct = _generateCts.Token;
            SaveCurrentPromptToBuffer();

            await ShowGenPreviewAsync(sourceImageBytes, sourceWidth, sourceHeight);
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
                    options.UseMaxUpscale ? "gen-enhance-max" : "gen-enhance",
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
                    _ = ShowGenPreviewAsync(bytes, width, height);
                })
                : null;

            if (!_settings.Settings.PrivacyMode)
                pendingHistoryId = AddPendingHistoryItem();
            DebugLog($"[Enhance] Start | Request={requestWidth}x{requestHeight} | Expected={width}x{height} | Model={enhanceParams.Model} | Seed={actualSeed} | Strength={options.Strength:0.00} | Noise={options.Noise:0.00} | Max={options.UseMaxUpscale}");
            var (imageBytes, error) = await _naiService.ImageToImageAsync(
                imageBase64,
                requestWidth, requestHeight,
                prompt, negPrompt, chars, vibes, preciseReferences, progress, ct,
                parametersOverride: enhanceParams,
                isEnhance: true,
                upscaledEnhance: options.UseMaxUpscale);
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

            if (TryGetImageDimensions(imageBytes, out int actualWidth, out int actualHeight))
            {
                width = actualWidth;
                height = actualHeight;
            }
            await ShowGenPreviewAsync(imageBytes, width, height);
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
            SetGenerationRequestRunning(false);
            UpdateBtnGenerateForApiKey();
            UpdateGenEnhanceButtonWarning();
        }
    }

    private static NAIParameters CreateGenEnhanceParameters(NAIParameters source, double strength, double noise) => new()
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

}
