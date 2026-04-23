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
    private enum SettingsHubSection
    {
        Usage,
        Network,
        Performance,
        Appearance,
        Language,
        Developer,
    }

    private async void OnUsageSettings(object sender, RoutedEventArgs e)
        => await ShowSettingsHubDialogAsync(SettingsHubSection.Usage);

    private async void OnPerformanceSettings(object sender, RoutedEventArgs e)
        => await ShowSettingsHubDialogAsync(SettingsHubSection.Performance);

    private void ApplyUsageSettings(
        bool weightHighlight,
        bool autoComplete,
        bool rememberPromptAndParameters,
        bool superDropEnabled,
        bool showGenerationResultBar,
        bool wildcardsEnabled,
        bool wildcardsRequireExplicitSyntax,
        string imageDeleteBehavior)
    {
        _settings.Settings.WeightHighlight = weightHighlight;
        _settings.Settings.AutoComplete = autoComplete;
        _settings.Settings.RememberPromptAndParameters = rememberPromptAndParameters;
        _settings.Settings.SuperDropEnabled = superDropEnabled;
        _settings.Settings.ShowGenerationResultBar = showGenerationResultBar;
        _settings.Settings.WildcardsEnabled = wildcardsEnabled;
        _settings.Settings.WildcardsRequireExplicitSyntax = wildcardsRequireExplicitSyntax;
        _settings.Settings.ImageDeleteBehavior = imageDeleteBehavior;

        UpdateFloatingResultBarsVisibility();
        if (!_settings.Settings.AutoComplete)
            CloseAutoComplete();

        if (_settings.Settings.RememberPromptAndParameters)
        {
            SaveCurrentPromptToBuffer();
            SyncUIToParams();
            SyncRememberedPromptAndParameterState();
        }
        else
        {
            ClearRememberedPromptState();
        }

        _settings.Save();
        ApplyDragDropModeSetting();
        UpdatePromptHighlights();
        TxtStatus.Text = L("settings.usage.saved");
    }

    private void ApplyPerformanceSettings(string devicePreference, bool unloadModelAfterInference)
    {
        var settings = OnnxPerformance;
        settings.DevicePreference = devicePreference;
        settings.UnloadModelAfterInference = unloadModelAfterInference;
        settings.Normalize();
        _settings.Save();
        TxtStatus.Text = L("settings.performance.saved");
    }

    private void ApplyDeveloperLogSetting(bool enabled)
    {
        _settings.Settings.DevLogEnabled = enabled;
        _settings.Save();
        TxtStatus.Text = enabled
            ? L("settings.dev.log_enabled_status")
            : L("settings.dev.log_disabled_status");
    }

    private void ApplyThemeModeSetting(string mode)
    {
        ApplyTheme(mode);
        SyncThemeMenuChecks(mode);
        _settings.Settings.ThemeMode = mode;
        _settings.Save();
        TxtStatus.Text = mode switch
        {
            "Light" => L("status.theme_light"),
            "Dark" => L("status.theme_dark"),
            _ => L("status.theme_system"),
        };
    }

    private void ApplyTransparencyModeSetting(string mode)
    {
        ApplyTransparency(mode);
        SyncTransparencyMenuChecks(mode);
        _settings.Settings.AppearanceTransparency = mode;
        _settings.Save();
        DebugLog($"[外观] 透明度已切换为 {mode}");
        TxtStatus.Text = mode switch
        {
            "Lesser" => L("status.transparency_lesser"),
            "Opaque" => L("status.transparency_opaque"),
            _ => L("status.transparency_standard"),
        };
    }

    private void ApplyLanguageCodeSetting(string languageCode)
    {
        _settings.Settings.LanguageCode = LocalizationService.NormalizeLanguageCode(languageCode);
        _loc.SetLanguage(_settings.Settings.LanguageCode);
        _settings.Save();
        ApplyLanguageSelectionChecks();
    }

    private async Task SaveNetworkSettingsAsync(
        string apiToken,
        bool streamGeneration,
        bool useProxy,
        string proxyPort,
        bool testConnection)
    {
        _settings.Settings.ApiToken = apiToken;
        _settings.Settings.StreamGeneration = streamGeneration;
        _settings.Settings.UseProxy = useProxy;
        _settings.Settings.ProxyPort = proxyPort;
        _settings.Save();
        UpdateBtnGenerateForApiKey();

        TxtStatus.Text = L("settings.network.testing");
        if (testConnection)
        {
            bool valid = !string.IsNullOrWhiteSpace(apiToken) && await ValidateSavedApiTokenAsync();
            if (string.IsNullOrWhiteSpace(apiToken))
                ClearAccountApiState(save: true);

            TxtStatus.Text = valid
                ? L("settings.network.test.success")
                : L("settings.network.invalid_api_or_network");
            return;
        }

        bool saveValid = await ValidateSavedApiTokenAsync();
        TxtStatus.Text = saveValid
            ? L("settings.network.saved")
            : L("settings.network.invalid_api_or_network");
    }

    private void AutoDetectTaggerModel()
    {
        var taggerSettings = _settings.Settings.ReverseTagger;
        if (!string.IsNullOrWhiteSpace(taggerSettings.ModelPath) && Directory.Exists(taggerSettings.ModelPath))
            return;

        var taggerDir = Path.Combine(ModelsDir, "tagger");
        if (!Directory.Exists(taggerDir)) return;

        string? found = FindValidTaggerDir(taggerDir);
        if (found == null) return;

        taggerSettings.ModelPath = found;
        _settings.Save();
        System.Diagnostics.Debug.WriteLine($"[ReverseTagger] Auto-detected tagger model: {found}");
    }

    private static string? FindValidTaggerDir(string searchRoot)
    {
        if (IsValidTaggerDirectory(searchRoot))
            return searchRoot;

        try
        {
            foreach (var subDir in Directory.GetDirectories(searchRoot))
            {
                if (IsValidTaggerDirectory(subDir))
                    return subDir;
            }
        }
        catch { }
        return null;
    }

    private static bool IsValidTaggerDirectory(string dir)
    {
        bool hasOnnx = Directory.GetFiles(dir, "*.onnx", SearchOption.TopDirectoryOnly).Length > 0;
        bool hasCsv = File.Exists(Path.Combine(dir, "selected_tags.csv"));
        return hasOnnx && hasCsv;
    }

    private async void OnReverseTaggerSettings(object sender, RoutedEventArgs e)
        => await ShowReverseTaggerSettingsDialogAsync();

    private async Task ShowReverseTaggerSettingsDialogAsync()
    {
        var settings = _settings.Settings.ReverseTagger;

        var pathBox = new TextBox
        {
            Text = settings.ModelPath ?? "",
            PlaceholderText = L("settings.reverse.model_path_placeholder"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 300,
        };

        var browseButton = new Button
        {
            Content = L("common.choose_folder"),
        };

        var addCharacterCheck = new CheckBox
        {
            Content = L("settings.reverse.add_character_tags"),
            IsChecked = settings.AddCharacterTags,
        };
        var addCopyrightCheck = new CheckBox
        {
            Content = L("settings.reverse.add_copyright_tags"),
            IsChecked = settings.AddCopyrightTags,
        };
        var replaceUnderscoreCheck = new CheckBox
        {
            Content = L("settings.reverse.replace_underscores"),
            IsChecked = settings.ReplaceUnderscoresWithSpaces,
        };
        var generalSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            StepFrequency = 0.01,
            SmallChange = 0.01,
            LargeChange = 0.05,
            Value = Math.Clamp(settings.GeneralThreshold, 0, 1),
            Width = 300,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var generalValue = new TextBlock
        {
            MinWidth = 52,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Text = generalSlider.Value.ToString("0.00"),
        };
        generalSlider.ValueChanged += (_, args) => generalValue.Text = args.NewValue.ToString("0.00");

        var characterSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            StepFrequency = 0.01,
            SmallChange = 0.01,
            LargeChange = 0.05,
            Value = Math.Clamp(settings.CharacterThreshold, 0, 1),
            Width = 300,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var characterValue = new TextBlock
        {
            MinWidth = 52,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Text = characterSlider.Value.ToString("0.00"),
        };
        characterSlider.ValueChanged += (_, args) => characterValue.Text = args.NewValue.ToString("0.00");

        browseButton.Click += async (_, _) =>
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
                pathBox.Text = folder.Path;
        };

        var pathPanel = new Grid { ColumnSpacing = 8 };
        pathPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(pathBox, 0);
        Grid.SetColumn(browseButton, 1);
        pathPanel.Children.Add(pathBox);
        pathPanel.Children.Add(browseButton);

        StackPanel BuildSliderRow(Slider slider, TextBlock valueText)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(slider);
            row.Children.Add(valueText);
            return row;
        }

        var panel = new StackPanel
        {
            Spacing = 12,
            MinWidth = 480,
            Padding = new Thickness(0, 0, 4, 0),
        };
        panel.Children.Add(new TextBlock { Text = L("settings.reverse.model_path") });
        panel.Children.Add(pathPanel);
        panel.Children.Add(new TextBlock
        {
            Text = L("settings.reverse.model_path_hint"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
        });
        var tagOptionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
        };
        tagOptionRow.Children.Add(addCharacterCheck);
        tagOptionRow.Children.Add(addCopyrightCheck);
        panel.Children.Add(tagOptionRow);
        panel.Children.Add(replaceUnderscoreCheck);
        panel.Children.Add(new TextBlock { Text = L("settings.reverse.general_threshold") });
        panel.Children.Add(BuildSliderRow(generalSlider, generalValue));
        panel.Children.Add(new TextBlock { Text = L("settings.reverse.character_threshold") });
        panel.Children.Add(BuildSliderRow(characterSlider, characterValue));

        var dialog = new ContentDialog
        {
            Title = L("settings.reverse.title"),
            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 520,
            },
            PrimaryButtonText = L("common.save"),
            CloseButtonText = L("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            string modelPath = pathBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(modelPath) && !Directory.Exists(modelPath))
            {
                args.Cancel = true;
                TxtStatus.Text = L("settings.reverse.model_path_not_found");
                return;
            }

            settings.ModelPath = modelPath;
            settings.AddCharacterTags = addCharacterCheck.IsChecked == true;
            settings.AddCopyrightTags = addCopyrightCheck.IsChecked == true;
            settings.ReplaceUnderscoresWithSpaces = replaceUnderscoreCheck.IsChecked == true;
            settings.GeneralThreshold = Math.Round(generalSlider.Value, 2);
            settings.CharacterThreshold = Math.Round(characterSlider.Value, 2);
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _settings.Save();
            TxtStatus.Text = L("settings.reverse.saved");
        }
    }

    private async void OnDevSettings(object sender, RoutedEventArgs e)
        => await ShowSettingsHubDialogAsync(SettingsHubSection.Developer);

    private async void OnQuotaSettings(object sender, RoutedEventArgs e)
    {
        bool isDark = ((FrameworkElement)this.Content).ActualTheme == ElementTheme.Dark;
        var hintBrush = new SolidColorBrush(isDark
            ? Windows.UI.Color.FromArgb(255, 178, 178, 178)
            : Windows.UI.Color.FromArgb(255, 92, 92, 92));
        var cardBackgroundBrush = new SolidColorBrush(isDark
            ? Windows.UI.Color.FromArgb(255, 44, 44, 44)
            : Windows.UI.Color.FromArgb(255, 250, 250, 250));
        var cardBorderBrush = new SolidColorBrush(isDark
            ? Windows.UI.Color.FromArgb(255, 84, 84, 84)
            : Windows.UI.Color.FromArgb(255, 214, 214, 214));
        var accountLabelBrush = new SolidColorBrush(isDark
            ? Windows.UI.Color.FromArgb(255, 194, 194, 194)
            : Windows.UI.Color.FromArgb(255, 100, 100, 100));

        NovelAiAccountInfo? latestAccountInfo = null;
        if (!string.IsNullOrWhiteSpace(_settings.Settings.ApiToken))
        {
            latestAccountInfo = await _naiService.GetAccountInfoAsync();
            if (latestAccountInfo != null)
            {
                _anlasBalance = latestAccountInfo.AnlasBalance;
                _isOpusSubscriber = latestAccountInfo.IsOpus;
                _hasActiveSubscription = latestAccountInfo.HasActiveSubscription;
                _settings.UpdateCachedAccountInfo(
                    latestAccountInfo.AnlasBalance,
                    latestAccountInfo.TierName,
                    latestAccountInfo.TierLevel,
                    latestAccountInfo.HasActiveSubscription,
                    latestAccountInfo.ExpiresAt);
                UpdateAnlasBalanceText();
            }
        }

        var cachedAccountInfo = _settings.CachedApiConfig;
        string notAvailable = L("settings.quota.account.not_available");

        static string? ReadDisplayString(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        string ResolveTierLabel()
        {
            string? tier = ReadDisplayString(latestAccountInfo?.TierName) ??
                           ReadDisplayString(cachedAccountInfo.SubscriptionTier);
            if (string.IsNullOrWhiteSpace(tier))
            {
                int? level = latestAccountInfo?.TierLevel ?? cachedAccountInfo.SubscriptionTierLevel;
                tier = level switch
                {
                    3 => "Opus",
                    2 => "Scroll",
                    1 => "Tablet",
                    0 => "Paper",
                    _ => null,
                };
            }

            if (string.IsNullOrWhiteSpace(tier))
                return notAvailable;

            bool isActive = latestAccountInfo?.HasActiveSubscription ??
                            cachedAccountInfo.SubscriptionActive == true;
            return isActive ? tier : $"{tier} ({L("settings.quota.account.inactive")})";
        }

        string ResolveExpiryLabel()
        {
            string? value = ReadDisplayString(latestAccountInfo?.ExpiresAt) ??
                            ReadDisplayString(cachedAccountInfo.SubscriptionExpiresAt);
            if (string.IsNullOrWhiteSpace(value))
                return notAvailable;

            return DateTimeOffset.TryParse(value, out var parsed)
                ? parsed.ToLocalTime().ToString("yyyy-MM-dd")
                : value;
        }

        StackPanel BuildRightInfoBlock(string label, string value)
        {
            var block = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            block.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = accountLabelBrush,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
            });
            block.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextAlignment = TextAlignment.Right,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 220,
            });
            return block;
        }

        int? accountAnlas = latestAccountInfo?.AnlasBalance ??
                            _anlasBalance ??
                            cachedAccountInfo.CachedAnlas;
        string accountAnlasText = accountAnlas.HasValue
            ? accountAnlas.Value.ToString("N0")
            : notAvailable;
        string accountTierText = ResolveTierLabel();
        string accountExpiryText = ResolveExpiryLabel();

        var accountGrid = new Grid { ColumnSpacing = 20 };
        accountGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        accountGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var anlasPanel = new StackPanel { Spacing = 2 };
        anlasPanel.Children.Add(new TextBlock
        {
            Text = L("settings.quota.account.current_anlas"),
            Foreground = accountLabelBrush,
            FontSize = 12,
        });
        anlasPanel.Children.Add(new TextBlock
        {
            Text = accountAnlasText,
            FontSize = 34,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            LineHeight = 38,
        });
        Grid.SetColumn(anlasPanel, 0);

        var rightInfoPanel = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        rightInfoPanel.Children.Add(BuildRightInfoBlock(L("settings.quota.account.subscription_tier"), accountTierText));
        rightInfoPanel.Children.Add(BuildRightInfoBlock(L("settings.quota.account.expires_at"), accountExpiryText));
        Grid.SetColumn(rightInfoPanel, 1);

        accountGrid.Children.Add(anlasPanel);
        accountGrid.Children.Add(rightInfoPanel);

        var accountPanel = new StackPanel { Spacing = 10 };
        accountPanel.Children.Add(CreateThemedCaption(L("settings.quota.account.title")));
        accountPanel.Children.Add(accountGrid);

        var accountCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = cardBorderBrush,
            Background = cardBackgroundBrush,
            Padding = new Thickness(12, 10, 12, 10),
            Child = accountPanel,
        };

        var masterSwitch = new ToggleSwitch
        {
            Header = L("settings.quota.asset_protection_mode"),
            IsOn = _settings.Settings.AccountAssetProtectionMode,
        };
        var masterHint = new TextBlock
        {
            Text = L("settings.quota.asset_protection_mode_hint"),
            FontSize = 12,
            Foreground = hintBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, -6, 0, 2),
        };

        var blockOversizedDimensionsCheck = new CheckBox
        {
            Content = L("settings.quota.block_oversized_dimensions"),
            IsChecked = _settings.Settings.AccountAssetProtectionBlockOversizedDimensions,
        };
        var blockOversizedStepsCheck = new CheckBox
        {
            Content = L("settings.quota.block_oversized_steps"),
            IsChecked = _settings.Settings.AccountAssetProtectionBlockOversizedSteps,
        };
        var disablePaidFeaturesCheck = new CheckBox
        {
            Content = L("settings.quota.disable_paid_features"),
            IsChecked = _settings.Settings.AccountAssetProtectionDisablePaidFeatures,
        };
        var disablePaidFeaturesHint = new TextBlock
        {
            Text = L("settings.quota.disable_paid_features_hint"),
            FontSize = 12,
            Foreground = hintBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(28, -8, 0, 0),
        };

        var detailsPanel = new StackPanel { Spacing = 8 };
        detailsPanel.Children.Add(CreateThemedCaption(L("settings.quota.section.title")));
        detailsPanel.Children.Add(blockOversizedDimensionsCheck);
        detailsPanel.Children.Add(blockOversizedStepsCheck);
        detailsPanel.Children.Add(disablePaidFeaturesCheck);
        detailsPanel.Children.Add(disablePaidFeaturesHint);
        void UpdateDetailState(bool enabled)
        {
            blockOversizedDimensionsCheck.IsEnabled = enabled;
            blockOversizedStepsCheck.IsEnabled = enabled;
            disablePaidFeaturesCheck.IsEnabled = enabled;
            disablePaidFeaturesHint.Opacity = enabled ? 1.0 : 0.6;
        }
        UpdateDetailState(masterSwitch.IsOn);
        masterSwitch.Toggled += (_, _) => UpdateDetailState(masterSwitch.IsOn);

        var detailCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = cardBorderBrush,
            Background = cardBackgroundBrush,
            Padding = new Thickness(12, 10, 12, 10),
            Child = detailsPanel,
        };

        var panel = new StackPanel
        {
            Spacing = 10,
            MinWidth = 420,
        };
        panel.Children.Add(accountCard);
        panel.Children.Add(masterSwitch);
        panel.Children.Add(masterHint);
        panel.Children.Add(detailCard);

        var dialog = new ContentDialog
        {
            Title = L("settings.quota.title"),
            Content = panel,
            PrimaryButtonText = L("common.save"),
            CloseButtonText = L("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            bool oldSizeLimitEnabled = IsAssetProtectionSizeLimitEnabled();
            bool oldStepLimitEnabled = IsAssetProtectionStepLimitEnabled();
            bool oldPaidLimitEnabled = IsAssetProtectionPaidFeatureLimitEnabled();

            _settings.Settings.AccountAssetProtectionMode = masterSwitch.IsOn;
            _settings.Settings.AccountAssetProtectionBlockOversizedDimensions = blockOversizedDimensionsCheck.IsChecked == true;
            _settings.Settings.AccountAssetProtectionBlockOversizedSteps = blockOversizedStepsCheck.IsChecked == true;
            _settings.Settings.AccountAssetProtectionDisablePaidFeatures = disablePaidFeaturesCheck.IsChecked == true;

            if (IsAssetProtectionStepLimitEnabled())
            {
                _settings.Settings.GenParameters.Steps = Math.Min(_settings.Settings.GenParameters.Steps, 28);
                _settings.Settings.InpaintParameters.Steps = Math.Min(_settings.Settings.InpaintParameters.Steps, 28);
                _settings.Settings.I2IDenoiseParameters.Steps = Math.Min(_settings.Settings.I2IDenoiseParameters.Steps, 28);
            }

            bool hasProtectionBehaviorChange =
                oldSizeLimitEnabled != IsAssetProtectionSizeLimitEnabled() ||
                oldStepLimitEnabled != IsAssetProtectionStepLimitEnabled() ||
                oldPaidLimitEnabled != IsAssetProtectionPaidFeatureLimitEnabled();

            _settings.Save();
            MaskCanvas.UseAssetProtectionCanvasSizing = IsAssetProtectionSizeLimitEnabled();

            if (hasProtectionBehaviorChange)
            {
                RefreshSizeComboBox();
                RefreshPromptModeUiForAccountModeChange();
            }

            TxtStatus.Text = L("settings.quota.saved");
        }
    }

    private async void OnNetworkSettings(object sender, RoutedEventArgs e)
        => await ShowSettingsHubDialogAsync(SettingsHubSection.Network);

    private async Task<bool> ValidateSavedApiTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.Settings.ApiToken))
        {
            ClearAccountApiState(save: true);
            return true;
        }

        _anlasRefreshRunning = true;
        _anlasInitialFetchDone = false;
        UpdateBtnGenerateForApiKey();

        NovelAiAccountInfo? accountInfo = null;
        try
        {
            accountInfo = await _naiService.GetAccountInfoAsync();
        }
        finally
        {
            _anlasRefreshRunning = false;
        }

        if (accountInfo == null)
        {
            ClearAccountApiState(save: true);
            return false;
        }

        ApplyAccountInfo(accountInfo, save: true);
        return true;
    }

    private void ClearAccountApiState(bool save)
    {
        _settings.Settings.ApiToken = null;
        _anlasBalance = null;
        _isOpusSubscriber = false;
        _hasActiveSubscription = false;
        _anlasInitialFetchDone = false;

        if (save)
            _settings.UpdateCachedAccountInfo(null, null, null, null, null);

        UpdateAnlasBalanceText();
        UpdateBtnGenerateForApiKey();
        UpdateGenerateButtonWarning();
        UpdateDynamicMenuStates();
    }

    private void ApplyAccountInfo(NovelAiAccountInfo accountInfo, bool save)
    {
        _anlasBalance = accountInfo.AnlasBalance;
        _isOpusSubscriber = accountInfo.IsOpus;
        _hasActiveSubscription = accountInfo.HasActiveSubscription;
        _anlasInitialFetchDone = true;

        if (save)
        {
            _settings.UpdateCachedAccountInfo(
                accountInfo.AnlasBalance,
                accountInfo.TierName,
                accountInfo.TierLevel,
                accountInfo.HasActiveSubscription,
                accountInfo.ExpiresAt);
        }

        UpdateAnlasBalanceText();
        UpdateBtnGenerateForApiKey();
        UpdateGenerateButtonWarning();
        UpdateDynamicMenuStates();
    }

    private void RefreshSizeComboBox()
    {
        int prevIdx = CboSize.SelectedIndex;
        CboSize.Items.Clear();
        foreach (var p in MaskCanvasControl.CanvasPresets)
            CboSize.Items.Add(CreateTextComboBoxItem(p.Label));
        CboSize.SelectedIndex = prevIdx >= 0 && prevIdx < CboSize.Items.Count ? prevIdx : 0;

        if (IsAdvancedWindowOpen)
        {
            _advCboSize.Items.Clear();
            foreach (var p in MaskCanvasControl.CanvasPresets)
                _advCboSize.Items.Add(CreateTextComboBoxItem(p.Label));
            _advCboSize.SelectedIndex = CboSize.SelectedIndex;
            UpdateAdvSizeControlMode();
            UpdateAdvSizeWarningVisuals();
            _advNbSteps.Maximum = IsAssetProtectionStepLimitEnabled() ? 28 : 50;
            if (_advNbSteps.Value > _advNbSteps.Maximum)
                _advNbSteps.Value = _advNbSteps.Maximum;
            UpdateAdvStepsWarning();
        }

        UpdateSizeControlMode();
        UpdateSizeWarningVisuals();
        UpdateGenerateButtonWarning();
    }

    private void RefreshPromptModeUiForAccountModeChange()
    {
        UpdateModelDependentUI();
        RecheckVibeTransferCacheState();
        RefreshVibeTransferPanel();
        RefreshPreciseReferencePanel();
        UpdateReferenceButtonAndPanelState();
        UpdateGenerateButtonWarning();
        UpdateDynamicMenuStates();
    }
}
