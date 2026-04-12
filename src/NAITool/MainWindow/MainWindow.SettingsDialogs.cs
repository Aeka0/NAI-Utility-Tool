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
    private async void OnUsageSettings(object sender, RoutedEventArgs e)
    {
        var chkWeightHighlight = new CheckBox
        {
            Content = L("settings.usage.weight_highlight"),
            IsChecked = _settings.Settings.WeightHighlight,
        };
        var chkAutoComplete = new CheckBox
        {
            Content = L("settings.usage.auto_complete"),
            IsChecked = _settings.Settings.AutoComplete,
        };
        var chkRememberPromptAndParameters = new CheckBox
        {
            Content = L("settings.usage.remember_prompt"),
            IsChecked = _settings.Settings.RememberPromptAndParameters,
        };
        var chkSuperDrop = new CheckBox
        {
            Content = L("settings.usage.superdrop"),
            IsChecked = _settings.Settings.SuperDropEnabled,
        };
        var chkWildcardsEnabled = new CheckBox
        {
            Content = L("settings.usage.wildcards_enabled"),
            IsChecked = _settings.Settings.WildcardsEnabled,
        };
        var chkWildcardExplicitSyntax = new CheckBox
        {
            Content = L("settings.usage.wildcards_explicit"),
            IsChecked = _settings.Settings.WildcardsRequireExplicitSyntax,
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(chkWeightHighlight);
        panel.Children.Add(chkAutoComplete);
        panel.Children.Add(chkRememberPromptAndParameters);
        panel.Children.Add(chkSuperDrop);
        panel.Children.Add(chkWildcardsEnabled);
        panel.Children.Add(chkWildcardExplicitSyntax);

        var dialog = new ContentDialog
        {
            Title = L("settings.usage.title"),
            Content = panel,
            PrimaryButtonText = L("common.ok"),
            CloseButtonText = L("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _settings.Settings.WeightHighlight = chkWeightHighlight.IsChecked == true;
            _settings.Settings.AutoComplete = chkAutoComplete.IsChecked == true;
            _settings.Settings.RememberPromptAndParameters = chkRememberPromptAndParameters.IsChecked == true;
            _settings.Settings.SuperDropEnabled = chkSuperDrop.IsChecked == true;
            _settings.Settings.WildcardsEnabled = chkWildcardsEnabled.IsChecked == true;
            _settings.Settings.WildcardsRequireExplicitSyntax = chkWildcardExplicitSyntax.IsChecked == true;
            if (!_settings.Settings.AutoComplete) CloseAutoComplete();
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
        var unloadAfterInferenceCheck = new CheckBox
        {
            Content = L("settings.reverse.unload_after_inference"),
            IsChecked = settings.UnloadModelAfterInference,
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
        panel.Children.Add(unloadAfterInferenceCheck);
        panel.Children.Add(new TextBlock
        {
            Text = L("settings.reverse.unload_after_inference_hint"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            Margin = new Thickness(28, -8, 0, 0),
        });

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
            settings.UnloadModelAfterInference = unloadAfterInferenceCheck.IsChecked == true;
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _settings.Save();
            TxtStatus.Text = L("settings.reverse.saved");
        }
    }

    private async void OnDevSettings(object sender, RoutedEventArgs e)
    {
        var chkLog = new CheckBox
        {
            Content = L("settings.dev.log_enabled"),
            IsChecked = _settings.Settings.DevLogEnabled,
        };
        var hintText = new TextBlock
        {
            Text = L("settings.dev.log_hint"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
        };
        hintText.SetValue(TextBlock.ForegroundProperty,
            new SolidColorBrush(((FrameworkElement)this.Content).ActualTheme == ElementTheme.Dark
                ? Windows.UI.Color.FromArgb(255, 160, 160, 160)
                : Windows.UI.Color.FromArgb(255, 100, 100, 100)));

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(chkLog);
        panel.Children.Add(hintText);

        var dialog = new ContentDialog
        {
            Title = L("settings.dev.title"),
            Content = panel,
            PrimaryButtonText = L("common.ok"),
            CloseButtonText = L("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _settings.Settings.DevLogEnabled = chkLog.IsChecked == true;
            _settings.Save();
            TxtStatus.Text = _settings.Settings.DevLogEnabled
                ? L("settings.dev.log_enabled_status") : L("settings.dev.log_disabled_status");
        }
    }

    private async void OnNetworkSettings(object sender, RoutedEventArgs e)
    {
        var tokenBox = new PasswordBox
        {
            PlaceholderText = "Bearer Token",
            Password = _settings.Settings.ApiToken ?? "", Width = 360,
        };

        var assetProtectionCheck = new CheckBox { Content = L("settings.network.account_asset_protection_mode"), IsChecked = _settings.Settings.AccountAssetProtectionMode };
        var assetProtectionHint = new TextBlock
        {
            Text = L("settings.network.account_asset_protection_mode_hint"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(28, -8, 0, 0),
        };

        var proxyCheck = new CheckBox { Content = L("settings.network.use_proxy"), IsChecked = _settings.Settings.UseProxy };
        var proxyHint = new TextBlock
        {
            Text = L("settings.network.proxy_hint"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(28, -8, 0, 0),
        };
        var proxyPortBox = new TextBox { PlaceholderText = L("settings.network.proxy_port_placeholder"), Text = _settings.Settings.ProxyPort, Width = 120 };
        var streamGenerationCheck = new CheckBox
        {
            Content = L("settings.network.stream_generation"),
            IsChecked = _settings.Settings.StreamGeneration,
        };
        var streamGenerationHint = new TextBlock
        {
            Text = L("settings.network.stream_generation_hint"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(28, -8, 0, 0),
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = L("settings.network.api_token") });
        panel.Children.Add(tokenBox);
        panel.Children.Add(assetProtectionCheck);
        panel.Children.Add(assetProtectionHint);
        panel.Children.Add(streamGenerationCheck);
        panel.Children.Add(streamGenerationHint);
        panel.Children.Add(proxyCheck);
        panel.Children.Add(proxyHint);
        var pp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pp.Children.Add(new TextBlock { Text = L("settings.network.port"), VerticalAlignment = VerticalAlignment.Center });
        pp.Children.Add(proxyPortBox);
        panel.Children.Add(pp);

        var dialog = new ContentDialog
        {
            Title = L("settings.network.title"), Content = panel,
            PrimaryButtonText = L("common.save"), SecondaryButtonText = L("settings.network.test_connection"),
            CloseButtonText = L("common.cancel"), XamlRoot = this.Content.XamlRoot,
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
        {
            bool accountAssetProtectionMode = assetProtectionCheck.IsChecked == true;
            bool accountAssetProtectionModeChanged = _settings.Settings.AccountAssetProtectionMode != accountAssetProtectionMode;
            _settings.Settings.ApiToken = tokenBox.Password;
            _settings.Settings.AccountAssetProtectionMode = accountAssetProtectionMode;
            _settings.Settings.StreamGeneration = streamGenerationCheck.IsChecked == true;
            _settings.Settings.UseProxy = proxyCheck.IsChecked == true;
            _settings.Settings.ProxyPort = proxyPortBox.Text;
            _settings.Save();

            if (accountAssetProtectionModeChanged)
            {
                RefreshSizeComboBox();
                RefreshPromptModeUiForAccountModeChange();
            }
            UpdateBtnGenerateForApiKey();
            _ = RefreshAnlasInfoAsync();

            if (result == ContentDialogResult.Secondary)
            {
                TxtStatus.Text = L("settings.network.testing");
                var (success, msg) = await _naiService.TestConnectionAsync(tokenBox.Password);
                TxtStatus.Text = msg;
                if (success)
                    _ = RefreshAnlasInfoAsync(forceRefresh: true);
            }
            else TxtStatus.Text = L("settings.network.saved");
        }
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
            _advNbSteps.Maximum = _settings.Settings.AccountAssetProtectionMode ? 28 : 50;
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
