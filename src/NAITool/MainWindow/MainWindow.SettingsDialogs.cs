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
        Api,
        LocalStorage,
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
        bool scrollHistoryToTopAfterGeneration,
        bool wildcardsEnabled,
        bool wildcardsRequireExplicitSyntax,
        bool? newImageDeleteProtection = null)
    {
        _settings.Settings.WeightHighlight = weightHighlight;
        _settings.Settings.AutoComplete = autoComplete;
        _settings.Settings.RememberPromptAndParameters = rememberPromptAndParameters;
        _settings.Settings.SuperDropEnabled = superDropEnabled;
        _settings.Settings.ScrollHistoryToTopAfterGeneration = scrollHistoryToTopAfterGeneration;
        _settings.Settings.WildcardsEnabled = wildcardsEnabled;
        _settings.Settings.WildcardsRequireExplicitSyntax = wildcardsRequireExplicitSyntax;
        _settings.Settings.NewImageDeleteProtection =
            newImageDeleteProtection ?? _settings.Settings.NewImageDeleteProtection;

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

    private void ApplyLocalStorageSettings(
        string imageDeleteBehavior,
        bool privacyMode,
        bool stripSavedImageMetadata,
        bool autoCopyVibeOriginalsToWorkspace)
    {
        _settings.Settings.ImageDeleteBehavior = imageDeleteBehavior;
        _settings.Settings.PrivacyMode = privacyMode;
        _settings.Settings.StripSavedImageMetadata = stripSavedImageMetadata;
        _settings.Settings.AutoCopyVibeOriginalsToWorkspace = autoCopyVibeOriginalsToWorkspace;
        _settings.Save();
        TxtStatus.Text = L("settings.local_storage.saved");
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

    private void ApplyPostEffectsPerformanceSettings(string devicePreference)
    {
        var settings = PostEffectsPerformance;
        settings.DevicePreference = devicePreference;
        settings.Normalize();
        _effectsGpuFallbackNotified = false;
        _settings.Save();
        QueueEffectsPreviewRefresh(immediate: true);
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

    private void ApplyGenerationWaitingAnimationSetting(bool enabled)
    {
        _settings.Settings.EnableGenerationWaitingAnimation = enabled;
        _settings.Save();

        if (_generateRequestRunning)
        {
            if (enabled)
                StartGenerationPreviewPulse();
            else
                StopGenerationPreviewPulse();
        }

        TxtStatus.Text = enabled
            ? L("status.generation_waiting_animation_on")
            : L("status.generation_waiting_animation_off");
    }

    private void ApplyLanguageCodeSetting(string languageCode)
    {
        _settings.Settings.LanguageCode = LocalizationService.NormalizeLanguageCode(languageCode);
        _loc.SetLanguage(_settings.Settings.LanguageCode);
        _settings.Save();
        ApplyLanguageSelectionChecks();
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
        return ReverseTaggerModelCatalog.HasUsableModelDirectory(dir);
    }

    private async void OnReverseTaggerSettings(object sender, RoutedEventArgs e)
        => await ShowReverseTaggerSettingsDialogAsync();

    private string FormatReverseDownloadStatus(ModelDownloadProgress progress)
    {
        if (progress.IsCompleted)
            return L("settings.reverse.download_complete");

        int fileIndex = Math.Max(progress.FileIndex, 1);
        int totalFiles = Math.Max(progress.TotalFiles, 1);
        double percent = Math.Clamp(progress.ProgressPercent, 0, 100);
        return Lf("settings.reverse.download_progress", percent, fileIndex, totalFiles);
    }

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
        var addRatingCheck = new CheckBox
        {
            Content = L("settings.reverse.add_rating_tags"),
            IsChecked = settings.AddRatingTags,
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

        var downloadButton = new Button
        {
            Content = L("settings.reverse.download_button"),
        };
        var downloadProgressBar = new ProgressBar
        {
            Width = 100,
            Height = 4,
            IsIndeterminate = false,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };

        downloadButton.Click += async (_, _) =>
        {
            downloadButton.IsEnabled = false;
            downloadProgressBar.Visibility = Visibility.Visible;
            downloadProgressBar.IsIndeterminate = true;
            TxtStatus.Text = L("settings.reverse.download_preparing");

            try
            {
                string dlPath = await ModelDownloadService.DownloadModelAsync(
                    onProgress: p =>
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            TxtStatus.Text = FormatReverseDownloadStatus(p);
                            downloadProgressBar.IsIndeterminate = false;
                            downloadProgressBar.Value = p.IsCompleted
                                ? 100
                                : Math.Clamp(p.ProgressPercent, 0, 100);
                        });
                    });

                pathBox.Text = dlPath;
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    TxtStatus.Text = Lf("settings.reverse.download_failed", ex.Message);
                    downloadButton.IsEnabled = true;
                });
            }
            finally
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    downloadProgressBar.Visibility = Visibility.Collapsed;
                });
            }
        };

        var pathPanel = new Grid { ColumnSpacing = 8 };
        pathPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(pathBox, 0);
        Grid.SetColumn(browseButton, 1);
        Grid.SetColumn(downloadButton, 2);
        pathPanel.Children.Add(pathBox);
        pathPanel.Children.Add(browseButton);
        pathPanel.Children.Add(downloadButton);

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
        panel.Children.Add(downloadProgressBar);
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
        tagOptionRow.Children.Add(addRatingCheck);
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
            settings.AddRatingTags = addRatingCheck.IsChecked == true;
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
        using var dialogLifetimeCts = new CancellationTokenSource();

        var accounts = _settings.Accounts.ToArray();
        ApiAccount? selectedAccount = accounts.FirstOrDefault();
        var cachedAccountInfo = selectedAccount?.Info;
        var accountSelector = new NumberBox
        {
            Minimum = 1, Maximum = Math.Max(1, accounts.Length), Value = 1,
            SmallChange = 1, LargeChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter { FractionDigits = 0 },
            IsEnabled = accounts.Length > 0, Width = 120,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(accountSelector, L("settings.api.select_account"));
        var recoveryText = new TextBlock { FontSize = 12, Foreground = hintBrush, TextWrapping = TextWrapping.Wrap };
        CancellationTokenSource? selectionCts = null;
        int refreshVersion = 0;
        string notAvailable = L("settings.quota.account.not_available");

        static string? ReadDisplayString(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        string ResolveTierLabel()
        {
            string? tier = ReadDisplayString(latestAccountInfo?.TierName) ??
                           ReadDisplayString(cachedAccountInfo?.TierName);
            if (string.IsNullOrWhiteSpace(tier))
            {
                int? level = latestAccountInfo?.TierLevel ?? cachedAccountInfo?.TierLevel;
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
                            cachedAccountInfo?.HasActiveSubscription == true;
            return isActive ? tier : $"{tier} ({L("settings.quota.account.inactive")})";
        }

        string ResolveExpiryLabel()
        {
            string? value = ReadDisplayString(latestAccountInfo?.ExpiresAt) ??
                            ReadDisplayString(cachedAccountInfo?.ExpiresAt);
            if (string.IsNullOrWhiteSpace(value))
                return notAvailable;

            return DateTimeOffset.TryParse(value, out var parsed)
                ? parsed.ToLocalTime().ToString("yyyy-MM-dd")
                : value;
        }

        StackPanel BuildAccountInfoBlock(
            string label,
            string value,
            bool isPrimary,
            out TextBlock valueBlock)
        {
            var block = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            block.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = accountLabelBrush,
                FontSize = 12,
                TextWrapping = TextWrapping.WrapWholeWords,
            });
            valueBlock = new TextBlock
            {
                Text = value,
                FontSize = isPrimary ? 36 : 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                LineHeight = isPrimary ? 40 : 20,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            block.Children.Add(valueBlock);
            return block;
        }

        string ResolveAnlasLabel()
        {
            int? accountAnlas = latestAccountInfo?.AnlasBalance ??
                                cachedAccountInfo?.AnlasBalance;
            return accountAnlas.HasValue
                ? accountAnlas.Value.ToString("N0")
                : notAvailable;
        }

        string ResolveV5UsageLabel()
        {
            int? usagePercent = latestAccountInfo?.V5UsagePercent ??
                                cachedAccountInfo?.V5UsagePercent;
            return usagePercent.HasValue
                ? $"{Math.Max(usagePercent.Value, 0)}%"
                : notAvailable;
        }

        string accountAnlasText = ResolveAnlasLabel();
        string accountV5UsageText = ResolveV5UsageLabel();
        string accountTierText = ResolveTierLabel();
        string accountExpiryText = ResolveExpiryLabel();

        var primaryInfoGrid = new Grid { ColumnSpacing = 24 };
        primaryInfoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        primaryInfoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var anlasPanel = BuildAccountInfoBlock(
            L("settings.quota.account.current_anlas"),
            accountAnlasText,
            isPrimary: true,
            out var accountAnlasTextBlock);
        Grid.SetColumn(anlasPanel, 0);
        primaryInfoGrid.Children.Add(anlasPanel);

        var v5UsagePanel = BuildAccountInfoBlock(
            L("settings.quota.account.v5_usage"),
            accountV5UsageText,
            isPrimary: true,
            out var accountV5UsageTextBlock);
        Grid.SetColumn(v5UsagePanel, 1);
        primaryInfoGrid.Children.Add(v5UsagePanel);

        var secondaryInfoGrid = new Grid { ColumnSpacing = 24 };
        secondaryInfoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        secondaryInfoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var tierPanel = BuildAccountInfoBlock(
            L("settings.quota.account.subscription_tier"),
            accountTierText,
            isPrimary: false,
            out var accountTierTextBlock);
        Grid.SetColumn(tierPanel, 0);
        secondaryInfoGrid.Children.Add(tierPanel);

        var expiryPanel = BuildAccountInfoBlock(
            L("settings.quota.account.expires_at"),
            accountExpiryText,
            isPrimary: false,
            out var accountExpiryTextBlock);
        Grid.SetColumn(expiryPanel, 1);
        secondaryInfoGrid.Children.Add(expiryPanel);

        var secondaryInfoBorder = new Border
        {
            BorderBrush = cardBorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 10, 0, 0),
            Child = secondaryInfoGrid,
        };

        void RefreshAccountTextBlocks()
        {
            accountAnlasTextBlock.Text = ResolveAnlasLabel();
            accountV5UsageTextBlock.Text = ResolveV5UsageLabel();
            accountTierTextBlock.Text = ResolveTierLabel();
            accountExpiryTextBlock.Text = ResolveExpiryLabel();
            var info = latestAccountInfo ?? cachedAccountInfo;
            recoveryText.Text = info?.V5UsagePercent is int percent
                ? percent > 100 ? L("settings.quota.summary.v5_overage")
                    : percent == 100 ? L("settings.quota.summary.v5_full")
                    : info.V5UsageTimeUntilNextPercentSeconds is int seconds
                        ? FormatV5UsageRecoveryTime(percent, seconds) : ""
                : "";
        }

        var refreshRotateTransform = new RotateTransform();
        var refreshIcon = new FontIcon
        {
            FontFamily = SymbolFontFamily,
            Glyph = "\uE72C",
            FontSize = 12,
            Foreground = accountLabelBrush,
            RenderTransform = refreshRotateTransform,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var transparentColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        var refreshButton = new Button
        {
            Width = 16,
            Height = 16,
            MinWidth = 16,
            Padding = new Thickness(0),
            Margin = new Thickness(3, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(transparentColor),
            BorderBrush = new SolidColorBrush(transparentColor),
            BorderThickness = new Thickness(0),
            Content = refreshIcon,
        };
        var transparentBrush = new SolidColorBrush(transparentColor);
        refreshButton.Resources["ButtonBackground"] = transparentBrush;
        refreshButton.Resources["ButtonBackgroundPointerOver"] = transparentBrush;
        refreshButton.Resources["ButtonBackgroundPressed"] = transparentBrush;
        refreshButton.Resources["ButtonBackgroundDisabled"] = transparentBrush;
        refreshButton.Resources["ButtonBorderBrush"] = transparentBrush;
        refreshButton.Resources["ButtonBorderBrushPointerOver"] = transparentBrush;
        refreshButton.Resources["ButtonBorderBrushPressed"] = transparentBrush;
        refreshButton.Resources["ButtonBorderBrushDisabled"] = transparentBrush;
        ToolTipService.SetToolTip(refreshButton, L("dialog.vibe_manager.action.refresh"));
        var refreshSpinTimer = DispatcherQueue.CreateTimer();
        refreshSpinTimer.Interval = TimeSpan.FromMilliseconds(16);
        refreshSpinTimer.Tick += (_, _) => refreshRotateTransform.Angle = (refreshRotateTransform.Angle + 18) % 360;
        async Task RefreshAccountInfoAsync(bool showProgress)
        {
            if (selectedAccount == null || string.IsNullOrWhiteSpace(selectedAccount.Token) ||
                dialogLifetimeCts.IsCancellationRequested) return;
            int version = ++refreshVersion;
            var account = selectedAccount;
            selectionCts?.Cancel();
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(dialogLifetimeCts.Token);
            selectionCts = requestCts;
            refreshButton.IsEnabled = false;
            if (showProgress) refreshSpinTimer.Start();
            try
            {
                var info = await _naiService.RefreshAccountAsync(account, requestCts.Token);
                if (version != refreshVersion || requestCts.IsCancellationRequested) return;
                latestAccountInfo = info;
                cachedAccountInfo = account.Info;
                _settings.Save();
                UpdateAccountUi();
                RefreshAccountTextBlocks();
                if (info == null) recoveryText.Text = L("settings.network.invalid_api_or_network");
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (version == refreshVersion)
                {
                    selectionCts = null;
                    refreshSpinTimer.Stop();
                    refreshRotateTransform.Angle = 0;
                    refreshButton.IsEnabled = true;
                }
            }
        }

        bool normalizingIndex = false;
        accountSelector.ValueChanged += async (_, _) =>
        {
            if (normalizingIndex) return;
            double value = double.IsFinite(accountSelector.Value) ? accountSelector.Value : 1;
            int index = (int)Math.Clamp(Math.Round(value), 1, Math.Max(1, accounts.Length));
            normalizingIndex = true;
            accountSelector.Value = index;
            normalizingIndex = false;
            selectionCts?.Cancel();
            selectedAccount = accounts.ElementAtOrDefault(index - 1);
            latestAccountInfo = null;
            cachedAccountInfo = selectedAccount?.Info;
            RefreshAccountTextBlocks();
            await RefreshAccountInfoAsync(showProgress: true);
        };
        RefreshAccountTextBlocks();

        refreshButton.Click += async (_, _) => await RefreshAccountInfoAsync(showProgress: true);

        var accountPanel = new StackPanel { Spacing = 10 };
        var accountHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
        };
        accountHeader.Children.Add(CreateThemedCaption(L("settings.quota.account.title")));
        accountHeader.Children.Add(refreshButton);
        accountPanel.Children.Add(accountHeader);
        accountPanel.Children.Add(primaryInfoGrid);
        accountPanel.Children.Add(recoveryText);
        accountPanel.Children.Add(secondaryInfoBorder);

        var accountCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = cardBorderBrush,
            Background = cardBackgroundBrush,
            Padding = new Thickness(12, 10, 12, 10),
            Child = accountPanel,
        };

        var masterSwitch = CreateLocalizedToggleSwitch(_settings.Settings.AccountAssetProtectionMode);
        masterSwitch.Header = L("settings.quota.asset_protection_mode");
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

        var titleRow = new Grid { Width = panel.MinWidth, ColumnSpacing = 16 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(new TextBlock
        {
            Text = L("settings.quota.title"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        var accountPicker = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        accountPicker.Children.Add(new TextBlock
        {
            Text = L("settings.api.select_account"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        });
        accountPicker.Children.Add(accountSelector);
        Grid.SetColumn(accountPicker, 1);
        titleRow.Children.Add(accountPicker);
        // WinUI's title presenter is left-aligned; follow the actual content width to keep the picker at the right edge.
        panel.SizeChanged += (_, args) => titleRow.Width = args.NewSize.Width;

        var dialog = new ContentDialog
        {
            Title = titleRow,
            Content = panel,
            PrimaryButtonText = L("common.save"),
            CloseButtonText = L("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };

        dialog.Closed += (_, _) => dialogLifetimeCts.Cancel();
        var dialogResultOperation = dialog.ShowAsync();
        Task initialAccountRefreshTask = RefreshAccountInfoAsync(showProgress: false);

        ContentDialogResult dialogResult = await dialogResultOperation;
        await initialAccountRefreshTask;

        if (dialogResult == ContentDialogResult.Primary)
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

    private async void NotifyApiTokenDecryptFailed()
    {
        TxtStatus.Text = L("settings.network.token_decrypt_failed");
        UpdateAccountUi();

        var dialog = new ContentDialog
        {
            Title = L("settings.network.token_decrypt_failed_title"),
            Content = new TextBlock
            {
                Text = L("settings.network.token_decrypt_failed"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            },
            PrimaryButtonText = L("common.ok"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };

        await dialog.ShowAsync();
    }

    private async void OnNetworkSettings(object sender, RoutedEventArgs e)
        => await ShowSettingsHubDialogAsync(SettingsHubSection.Network);

    private async void OnApiSettings(object sender, RoutedEventArgs e)
        => await ShowSettingsHubDialogAsync(SettingsHubSection.Api);

    private async Task<bool> ValidateSavedApiTokenAsync()
    {
        if (!AppSettings.IsValidApiBaseUrl(_settings.Settings.ApiBaseUrl)) return false;
        await RefreshAnlasInfoAsync(forceRefresh: true);
        return _settings.Accounts.Any(a => a.Token.Length > 0 &&
            (_settings.Settings.UsesCustomApiBaseUrl || a.IsTokenValid == true));
    }

    private void ClearAccountApiState(bool save)
    {
        _settings.SetApiTokens([]);
        ApplyCachedAccountInfo();
        _anlasInitialFetchDone = false;
        if (save) _settings.Save();
        UpdateAccountUi();
    }

    private void UpdateAccountUi()
    {
        ApplyCachedAccountInfo();
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
