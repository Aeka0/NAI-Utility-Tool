using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAITool.Controls;
using NAITool.Models;
using NAITool.Services;

namespace NAITool;

public sealed partial class MainWindow
{
    private async Task ShowAutomationDialogAsync()
    {
        var workingSettings = GetAutomationSettings().Clone();
        workingSettings.Normalize();

        var focusSink = new Button
        {
            Width = 1,
            Height = 1,
            Opacity = 0,
            IsTabStop = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var txtPresetName = new TextBox
        {
            Header = L("automation.preset_name_header"),
            PlaceholderText = L("automation.preset_name_placeholder"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var presetCombo = new ComboBox
        {
            Header = L("automation.saved_presets"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        ApplyMenuTypography(presetCombo);
        var presetSummary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            IsTextSelectionEnabled = true,
        };
        var presetSummaryCard = new Border
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.08 },
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Child = presetSummary,
        };

        var btnLoadPreset = new Button { Content = L("automation.load"), VerticalAlignment = VerticalAlignment.Bottom };
        var btnSavePreset = new Button { Content = L("automation.save_as"), VerticalAlignment = VerticalAlignment.Bottom };
        var btnOverwritePreset = new Button { Content = L("automation.overwrite"), VerticalAlignment = VerticalAlignment.Bottom };

        var nbMinDelay = CreateAutomationDecimalBox(L("automation.min_delay"), workingSettings.Generation.MinDelaySeconds);
        var nbMaxDelay = CreateAutomationDecimalBox(L("automation.max_delay"), workingSettings.Generation.MaxDelaySeconds);
        var nbRequestCount = new NumberBox
        {
            Minimum = 0,
            Maximum = 100000,
            Value = workingSettings.Generation.RequestLimit,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var errorRetryBoxes = AutomationErrorHandlingOptions.SupportedStatusCodes.ToDictionary(
            statusCode => statusCode,
            statusCode => CreateAutomationRetryLimitBox(workingSettings.ErrorHandling.GetRetryLimit(statusCode)));

        var chkRandomSize = CreateAutomationToggleSwitch(workingSettings.Randomization.RandomizeSize);
        var chkRandomVibe = CreateAutomationToggleSwitch(workingSettings.Randomization.RandomizeVibeFiles);
        var chkRandomStyle = CreateAutomationToggleSwitch(workingSettings.Randomization.RandomizeStyleTags);
        var chkRandomPrompt = CreateAutomationToggleSwitch(workingSettings.Randomization.RandomizePrompt);
        var sizePresetChecks = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
        var sizeGrid = new Grid { ColumnSpacing = 8, RowSpacing = 0 };
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int sizeIdx = 0;
        foreach (var preset in MaskCanvasControl.CanvasPresets)
        {
            string key = FormatAutomationSizePreset(preset.W, preset.H);
            var box = new CheckBox
            {
                Content = preset.Label,
                Tag = key,
                IsChecked = workingSettings.Randomization.SizePresets.Contains(key, StringComparer.OrdinalIgnoreCase),
                Padding = new Thickness(6, 0, 0, 0),
                MinHeight = 32,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            sizePresetChecks[key] = box;
            int row = sizeIdx / 2;
            int col = sizeIdx % 2;
            while (sizeGrid.RowDefinitions.Count <= row)
                sizeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(box, row);
            Grid.SetColumn(box, col);
            sizeGrid.Children.Add(box);
            sizeIdx++;
        }

        var btnSelectSizes = new Button { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        var randomSizeControl = new Grid
        {
            ColumnSpacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        randomSizeControl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        randomSizeControl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(btnSelectSizes, 0);
        Grid.SetColumn(chkRandomSize, 1);
        randomSizeControl.Children.Add(btnSelectSizes);
        randomSizeControl.Children.Add(chkRandomSize);

        void UpdateSizeButtonText()
        {
            int selected = sizePresetChecks.Count(x => x.Value.IsChecked == true);
            btnSelectSizes.Content = Lf("automation.size_pool_button", selected, sizePresetChecks.Count);
        }

        foreach (var chk in sizePresetChecks.Values)
            chk.Click += (_, _) => UpdateSizeButtonText();
        UpdateSizeButtonText();

        btnSelectSizes.Flyout = new Flyout
        {
            Content = new ScrollViewer
            {
                MaxHeight = 380,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel
                {
                    Padding = new Thickness(4),
                    Children = { sizeGrid }
                }
            },
        };

        var chkEnableUpscale = CreateAutomationToggleSwitch(workingSettings.Effects.UpscaleEnabled);
        var chkEnableFx = CreateAutomationToggleSwitch(workingSettings.Effects.FxEnabled);
        var cboUpscaleModel = new ComboBox { Header = L("automation.upscale_model"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var upscaleModels = UpscaleService.ScanModels(Path.Combine(ModelsDir, "upscaler"));
        foreach (var model in upscaleModels)
            cboUpscaleModel.Items.Add(CreateTextComboBoxItem(model.DisplayName));
        ApplyMenuTypography(cboUpscaleModel);

        var cboUpscaleScale = new ComboBox { Header = L("automation.upscale_scale"), HorizontalAlignment = HorizontalAlignment.Stretch };
        cboUpscaleScale.Items.Add(new ComboBoxItem { Content = "2x", Tag = 2 });
        cboUpscaleScale.Items.Add(new ComboBoxItem { Content = "3x", Tag = 3 });
        cboUpscaleScale.Items.Add(new ComboBoxItem { Content = "4x", Tag = 4 });
        ApplyMenuTypography(cboUpscaleScale);

        var cboEffectsPreset = new ComboBox { Header = L("automation.post_preset"), HorizontalAlignment = HorizontalAlignment.Stretch };
        PopulateAutomationEffectsPresetCombo(cboEffectsPreset);
        ApplyMenuTypography(cboEffectsPreset);

        void ApplySettingsToControls(AutomationSettings settings)
        {
            settings.Normalize();
            workingSettings = settings.Clone();

            txtPresetName.Text = settings.SelectedPresetName ?? "";
            SetComboNumberBoxValue(nbMinDelay, settings.Generation.MinDelaySeconds);
            SetComboNumberBoxValue(nbMaxDelay, settings.Generation.MaxDelaySeconds);
            nbRequestCount.Value = settings.Generation.RequestLimit;
            foreach (var pair in errorRetryBoxes)
                pair.Value.Value = settings.ErrorHandling.GetRetryLimit(pair.Key);

            chkRandomSize.IsOn = settings.Randomization.RandomizeSize;
            chkRandomVibe.IsOn = settings.Randomization.RandomizeVibeFiles;
            chkRandomStyle.IsOn = settings.Randomization.RandomizeStyleTags;
            chkRandomPrompt.IsOn = settings.Randomization.RandomizePrompt;
            foreach (var pair in sizePresetChecks)
                pair.Value.IsChecked = settings.Randomization.SizePresets.Contains(pair.Key, StringComparer.OrdinalIgnoreCase);

            chkEnableUpscale.IsOn = settings.Effects.UpscaleEnabled;
            chkEnableFx.IsOn = settings.Effects.FxEnabled;
            SelectComboText(cboUpscaleModel, settings.Effects.UpscaleModel);
            SelectComboTag(cboUpscaleScale, settings.Effects.UpscaleScale);
            SelectComboText(cboEffectsPreset, settings.Effects.FxPresetName);

            RefreshPresetSummary();
            UpdateRandomSizePanelState();
            UpdateEffectsPanelState();
        }

        AutomationSettings CollectSettingsFromControls()
        {
            var sizePresets = sizePresetChecks
                .Where(x => x.Value.IsChecked == true && x.Value.Tag is string)
                .Select(x => (string)x.Value.Tag)
                .ToList();

            if (chkRandomSize.IsOn && sizePresets.Count == 0)
            {
                var (w, h) = GetSelectedSize();
                sizePresets.Add(FormatAutomationSizePreset(w, h));
            }

            var errorHandling = new AutomationErrorHandlingOptions();
            foreach (var pair in errorRetryBoxes)
                errorHandling.SetRetryLimit(pair.Key, ReadAutomationInteger(pair.Value, -1, 100000));

            var collected = new AutomationSettings
            {
                SelectedPresetName = GetSelectedComboText(presetCombo) ?? "",
                Generation = new AutomationGenerationOptions
                {
                    MinDelaySeconds = Math.Max(0.5, nbMinDelay.Value),
                    MaxDelaySeconds = Math.Max(nbMaxDelay.Value, nbMinDelay.Value),
                    RequestLimit = ReadAutomationInteger(nbRequestCount, 0, 100000),
                },
                ErrorHandling = errorHandling,
                Randomization = new AutomationRandomizationOptions
                {
                    RandomizeSize = chkRandomSize.IsOn,
                    SizePresets = sizePresets,
                    RandomizeVibeFiles = chkRandomVibe.IsOn,
                    RandomizeStyleTags = chkRandomStyle.IsOn,
                    RandomizePrompt = chkRandomPrompt.IsOn,
                },
                Effects = new AutomationEffectsOptions
                {
                    UpscaleEnabled = chkEnableUpscale.IsEnabled &&
                                     chkEnableUpscale.IsOn &&
                                     !string.IsNullOrWhiteSpace(GetSelectedComboText(cboUpscaleModel)),
                    UpscaleModel = GetSelectedComboText(cboUpscaleModel) ?? "",
                    UpscaleScale = GetSelectedComboTagInt(cboUpscaleScale, workingSettings.Effects.UpscaleScale),
                    FxEnabled = chkEnableFx.IsOn,
                    FxPresetName = GetSelectedComboText(cboEffectsPreset) ?? "",
                },
            };
            collected.Normalize();
            return collected;
        }

        void RefreshPresetSummary()
        {
            presetSummary.Text = GetAutomationPresetSummary(CollectSettingsFromControls());
        }

        void RefreshPresetCombo(string? targetName = null)
        {
            string? currentName = targetName ?? GetSelectedComboText(presetCombo) ?? workingSettings.SelectedPresetName;
            presetCombo.Items.Clear();
            foreach (var preset in _automationPresetService.ListPresets())
                presetCombo.Items.Add(CreateTextComboBoxItem(preset.Name));

            if (!string.IsNullOrWhiteSpace(currentName))
                SelectComboText(presetCombo, currentName);

            if (presetCombo.SelectedIndex < 0 && presetCombo.Items.Count > 0)
                presetCombo.SelectedIndex = 0;
        }

        void UpdateRandomSizePanelState()
        {
            btnSelectSizes.IsEnabled = chkRandomSize.IsOn;
        }

        void UpdateEffectsPanelState()
        {
            bool hasUpscaleModels = upscaleModels.Count > 0;
            chkEnableUpscale.IsEnabled = hasUpscaleModels;
            if (!hasUpscaleModels)
                chkEnableUpscale.IsOn = false;
            bool upscaleOn = chkEnableUpscale.IsOn;
            cboUpscaleModel.IsEnabled = upscaleOn && hasUpscaleModels;
            cboUpscaleScale.IsEnabled = upscaleOn;

            cboEffectsPreset.IsEnabled = chkEnableFx.IsOn;
        }

        async Task LoadSelectedPresetAsync()
        {
            string? name = GetSelectedComboText(presetCombo);
            if (string.IsNullOrWhiteSpace(name))
            {
                TxtStatus.Text = L("automation.status.no_preset_to_load");
                return;
            }

            try
            {
                var preset = await _automationPresetService.LoadPresetAsync(name);
                if (preset?.Settings == null)
                {
                    TxtStatus.Text = L("automation.status.load_failed");
                    return;
                }

                preset.Settings.SelectedPresetName = preset.Name;
                ApplySettingsToControls(preset.Settings);
                SelectComboText(presetCombo, preset.Name);
                txtPresetName.Text = preset.Name;
                TxtStatus.Text = Lf("automation.status.loaded", preset.Name);
            }
            catch (Exception ex)
            {
                TxtStatus.Text = Lf("automation.status.load_failed_with_reason", ex.Message);
            }
        }

        async Task SaveNewPresetAsync()
        {
            string presetName = (txtPresetName.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(presetName))
            {
                TxtStatus.Text = L("automation.preset_name_required");
                return;
            }

            try
            {
                var snapshot = CollectSettingsFromControls();
                snapshot.SelectedPresetName = presetName;
                await _automationPresetService.SavePresetAsync(presetName, snapshot);
                RefreshPresetCombo(presetName);
                SelectComboText(presetCombo, presetName);
                RefreshPresetSummary();
                TxtStatus.Text = Lf("automation.status.saved", presetName);
            }
            catch (Exception ex)
            {
                TxtStatus.Text = Lf("automation.status.save_failed", ex.Message);
            }
        }

        async Task OverwritePresetAsync()
        {
            string? selectedName = GetSelectedComboText(presetCombo);
            if (string.IsNullOrWhiteSpace(selectedName))
            {
                TxtStatus.Text = L("automation.status.select_preset_to_overwrite");
                return;
            }

            try
            {
                var snapshot = CollectSettingsFromControls();
                snapshot.SelectedPresetName = selectedName;
                await _automationPresetService.SavePresetAsync(selectedName, snapshot);
                txtPresetName.Text = selectedName;
                RefreshPresetCombo(selectedName);
                RefreshPresetSummary();
                TxtStatus.Text = Lf("automation.status.overwritten", selectedName);
            }
            catch (Exception ex)
            {
                TxtStatus.Text = Lf("automation.status.overwrite_failed", ex.Message);
            }
        }

        btnLoadPreset.Click += async (_, _) => await LoadSelectedPresetAsync();
        btnSavePreset.Click += async (_, _) => await SaveNewPresetAsync();
        btnOverwritePreset.Click += async (_, _) => await OverwritePresetAsync();
        presetCombo.SelectionChanged += (_, _) =>
        {
            if (GetSelectedComboText(presetCombo) is string selected && !string.IsNullOrWhiteSpace(selected))
                txtPresetName.Text = selected;
        };

        chkRandomSize.Toggled += (_, _) => UpdateRandomSizePanelState();
        chkEnableUpscale.Toggled += (_, _) => UpdateEffectsPanelState();
        chkEnableFx.Toggled += (_, _) => UpdateEffectsPanelState();

        var presetPage = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                focusSink,
                new Grid
                {
                    ColumnSpacing = 6,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto },
                        new ColumnDefinition { Width = GridLength.Auto },
                    },
                    Children =
                    {
                        SetGridColumn(txtPresetName, 0),
                        SetGridColumn(btnSavePreset, 1),
                        SetGridColumn(btnOverwritePreset, 2),
                    }
                },
                new Grid
                {
                    ColumnSpacing = 6,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto },
                    },
                    Children =
                    {
                        SetGridColumn(presetCombo, 0),
                        SetGridColumn(btnLoadPreset, 1),
                    }
                },
                presetSummaryCard,
                new TextBlock { Text = L("automation.preset_summary_note"), TextWrapping = TextWrapping.Wrap, Opacity = 0.6, FontSize = 12 }
            }
        };

        var generationPage = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                CreateAutomationSection(
                    L("automation.section.request_pacing.title"),
                    L("automation.section.request_pacing.description"),
                    CreateAutomationTwoColumnRow(nbMinDelay, nbMaxDelay)),
                CreateAutomationSection(
                    L("automation.section.run_limits.title"),
                    L("automation.section.run_limits.description"),
                    CreateAutomationLabeledField(L("automation.request_count"), nbRequestCount, 0)),
            }
        };

        var errorHandlingFields = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        for (int i = 0; i < AutomationErrorHandlingOptions.SupportedStatusCodes.Count; i += 2)
        {
            int leftCode = AutomationErrorHandlingOptions.SupportedStatusCodes[i];
            var left = CreateAutomationLabeledField(
                Lf("automation.error_handling.status_code", leftCode),
                errorRetryBoxes[leftCode],
                0);

            if (i + 1 < AutomationErrorHandlingOptions.SupportedStatusCodes.Count)
            {
                int rightCode = AutomationErrorHandlingOptions.SupportedStatusCodes[i + 1];
                var right = CreateAutomationLabeledField(
                    Lf("automation.error_handling.status_code", rightCode),
                    errorRetryBoxes[rightCode],
                    0);
                errorHandlingFields.Children.Add(CreateAutomationTwoColumnRow(left, right));
            }
            else
            {
                errorHandlingFields.Children.Add(left);
            }
        }

        var errorHandlingPage = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                CreateAutomationSection(
                    L("automation.section.error_handling.title"),
                    L("automation.section.error_handling.description"),
                    errorHandlingFields),
                new TextBlock
                {
                    Text = L("automation.error_handling.note"),
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.65,
                    FontSize = 12,
                },
            }
        };

        var randomizationPage = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                CreateAutomationSettingRow(
                    L("automation.random_size"),
                    L("automation.random_size_description"),
                    randomSizeControl),
                CreateAutomationSettingRow(
                    L("automation.random_vibe"),
                    L("automation.random_vibe_description"),
                    chkRandomVibe),
                CreateAutomationSettingRow(
                    L("automation.random_style"),
                    L("automation.random_style_description"),
                    chkRandomStyle),
                CreateAutomationSettingRow(
                    L("automation.random_prompt"),
                    L("automation.random_prompt_description"),
                    chkRandomPrompt),
            }
        };

        cboEffectsPreset.HorizontalAlignment = HorizontalAlignment.Stretch;

        var postProcessPage = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                CreateAutomationSettingRow(
                    L("automation.enable_auto_upscale"),
                    L("automation.enable_auto_upscale_description"),
                    chkEnableUpscale),
                CreateAutomationSection(
                    L("automation.section.upscale_settings.title"),
                    L("automation.section.upscale_settings.description"),
                    CreateAutomationTwoColumnRow(cboUpscaleModel, cboUpscaleScale)),
                new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 6, 0, 6),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                    Opacity = 0.25,
                },
                CreateAutomationSettingRow(
                    L("automation.enable_post_preset"),
                    L("automation.enable_post_preset_description"),
                    chkEnableFx),
                CreateAutomationSection(
                    L("automation.section.filter_preset.title"),
                    L("automation.section.filter_preset.description"),
                    cboEffectsPreset),
            }
        };

        var contentHost = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        FrameworkElement WrapPage(FrameworkElement content)
        {
            content.HorizontalAlignment = HorizontalAlignment.Stretch;
            content.Margin = new Thickness(0);
            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new Grid
                {
                    Padding = new Thickness(16, 14, 12, 12),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Children = { content }
                }
            };
        }

        var pages = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal)
        {
            ["preset"] = WrapPage(presetPage),
            ["generation"] = WrapPage(generationPage),
            ["errorHandling"] = WrapPage(errorHandlingPage),
            ["randomization"] = WrapPage(randomizationPage),
            ["post"] = WrapPage(postProcessPage),
        };

        void ShowPage(string key)
        {
            if (pages.TryGetValue(key, out var page))
                contentHost.Content = page;
        }

        var nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Top,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleButtonVisible = false,
            IsBackEnabled = false,
            IsSettingsVisible = false,
            AlwaysShowHeader = false,
            SelectionFollowsFocus = NavigationViewSelectionFollowsFocus.Enabled,
            CompactModeThresholdWidth = 0,
            ExpandedModeThresholdWidth = 0,
            Content = contentHost,
            Width = 540,
            Height = 430,
        };
        nav.MenuItems.Add(new NavigationViewItem { Content = L("automation.tab.preset"), Tag = "preset" });
        nav.MenuItems.Add(new NavigationViewItem { Content = L("automation.tab.generation"), Tag = "generation" });
        nav.MenuItems.Add(new NavigationViewItem { Content = L("automation.tab.error_handling"), Tag = "errorHandling" });
        nav.MenuItems.Add(new NavigationViewItem { Content = L("automation.tab.randomization"), Tag = "randomization" });
        nav.MenuItems.Add(new NavigationViewItem { Content = L("automation.tab.post"), Tag = "post" });
        nav.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItemContainer?.Tag is string key)
                ShowPage(key);
        };
        if (nav.MenuItems[0] is NavigationViewItem firstItem)
            nav.SelectedItem = firstItem;
        ShowPage("preset");

        RefreshPresetCombo(workingSettings.SelectedPresetName);
        ApplySettingsToControls(workingSettings);

        var dialog = new ContentDialog
        {
            Title = L("automation.dialog.title"),
            Content = nav,
            PrimaryButtonText = L("automation.dialog.run"),
            CloseButtonText = L("automation.dialog.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };
        dialog.Resources["ContentDialogMaxWidth"] = (double)620;

        dialog.Opened += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                focusSink.Focus(FocusState.Programmatic);
                SuppressNumberBoxInitialSelection(nbMinDelay);
                SuppressNumberBoxInitialSelection(nbMaxDelay);
                SuppressNumberBoxInitialSelection(nbRequestCount);
                foreach (var box in errorRetryBoxes.Values)
                    SuppressNumberBoxInitialSelection(box);
            });
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var collected = CollectSettingsFromControls();
        _settings.Settings.Automation = collected;
        _settings.Settings.AutoGenRandomStylePrefix = collected.Randomization.RandomizeStyleTags;
        _settings.Save();
        _ = RunAutoGenerationAsync();
    }

    private static NumberBox CreateAutomationDecimalBox(string header, double value) => new()
    {
        Header = header,
        Minimum = 0.5,
        Maximum = 3600,
        Value = value,
        SmallChange = 0.5,
        LargeChange = 5,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter
        {
            FractionDigits = 1,
            IntegerDigits = 1,
        },
    };

    private static NumberBox CreateAutomationRetryLimitBox(int value) => new()
    {
        Minimum = -1,
        Maximum = 100000,
        Value = value,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private static int ReadAutomationInteger(NumberBox box, int minimum, int maximum)
    {
        double value = box.Value;
        if (double.IsNaN(value) || double.IsInfinity(value))
            return minimum;
        return Math.Clamp((int)Math.Round(value), minimum, maximum);
    }

    private static ToggleSwitch CreateAutomationToggleSwitch(bool isOn) => new()
    {
        IsOn = isOn,
        OnContent = "",
        OffContent = "",
        MinWidth = 56,
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    private static StackPanel CreateAutomationLabeledField(string label, FrameworkElement control, double minHeaderHeight = 0)
    {
        control.HorizontalAlignment = HorizontalAlignment.Stretch;

        return new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    TextWrapping = TextWrapping.Wrap,
                    MinHeight = minHeaderHeight,
                    VerticalAlignment = VerticalAlignment.Bottom,
                },
                control
            }
        };
    }

    private static Grid CreateAutomationTwoColumnRow(FrameworkElement left, FrameworkElement right)
    {
        left.HorizontalAlignment = HorizontalAlignment.Stretch;
        right.HorizontalAlignment = HorizontalAlignment.Stretch;
        var grid = new Grid { ColumnSpacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private static Border CreateAutomationSection(string title, string description, FrameworkElement content)
    {
        content.HorizontalAlignment = HorizontalAlignment.Stretch;
        var stack = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                },
                new TextBlock
                {
                    Text = description,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.6,
                    FontSize = 12,
                },
                content
            }
        };

        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.06 },
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Child = stack,
        };
    }

    private static Border CreateAutomationSettingRow(string title, string description, FrameworkElement control)
    {
        control.VerticalAlignment = VerticalAlignment.Center;

        var textPanel = new StackPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                },
                new TextBlock
                {
                    Text = description,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.6,
                    FontSize = 12,
                }
            }
        };

        var grid = new Grid
        {
            ColumnSpacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(textPanel, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(textPanel);
        grid.Children.Add(control);

        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.04 },
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Child = grid,
        };
    }

    private static T SetGridColumn<T>(T element, int column) where T : FrameworkElement
    {
        Grid.SetColumn(element, column);
        return element;
    }

    private static void SetComboNumberBoxValue(NumberBox box, double value)
    {
        box.Value = value;
    }

    private void SelectComboText(ComboBox combo, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    private static void SelectComboTag(ComboBox combo, int tagValue)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                item.Tag is int value &&
                value == tagValue)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    private static int GetSelectedComboTagInt(ComboBox combo, int fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item)
        {
            if (item.Tag is int intValue)
                return intValue;
            if (item.Tag is string strValue && int.TryParse(strValue, out int parsed))
                return parsed;
        }
        return fallback;
    }

    private static string FormatAutomationSizePreset(int width, int height) => $"{width}x{height}";

    private static bool TryParseAutomationSizePreset(string? value, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string normalized = value.Replace("×", "x", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", "", StringComparison.Ordinal);
        string[] parts = normalized.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;

        return int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height)
            && width > 0 && height > 0;
    }

    private string GetAutomationPresetSummary(AutomationSettings settings)
    {
        settings.Normalize();
        var gen = settings.Generation;
        var errorHandling = settings.ErrorHandling;
        var rand = settings.Randomization;
        var post = settings.Effects;

        string reqLabel = gen.RequestLimit > 0 ? $"{gen.RequestLimit}" : "\u221e";
        string errorHandlingLabel = string.Join(", ",
            AutomationErrorHandlingOptions.SupportedStatusCodes.Select(
                statusCode => $"{statusCode}={FormatAutomationRetryLimit(errorHandling.GetRetryLimit(statusCode))}"));

        string sizeLabel = rand.RandomizeSize ? Lf("automation.summary.size_count", rand.SizePresets.Count) : L("common.off");
        string vibeLabel = rand.RandomizeVibeFiles ? L("common.on") : L("common.off");
        string styleLabel = rand.RandomizeStyleTags ? L("common.on") : L("common.off");
        string promptLabel = rand.RandomizePrompt ? L("common.on") : L("common.off");

        string upscaleLabel = post.UpscaleEnabled && !string.IsNullOrWhiteSpace(post.UpscaleModel)
            ? $"{post.UpscaleModel} {post.UpscaleScale}x"
            : L("common.off");
        string fxLabel = post.FxEnabled && !string.IsNullOrWhiteSpace(post.FxPresetName)
            ? post.FxPresetName
            : L("common.off");

        return Lf("automation.summary.line1", gen.MinDelaySeconds, gen.MaxDelaySeconds, reqLabel) + "\n" +
               Lf("automation.summary.line_error_handling", errorHandlingLabel) + "\n" +
               Lf("automation.summary.line2", sizeLabel, vibeLabel, styleLabel, promptLabel) + "\n" +
               Lf("automation.summary.line3", upscaleLabel, fxLabel);
    }

    private static string FormatAutomationRetryLimit(int retryLimit) =>
        retryLimit < 0 ? "\u221e" : retryLimit.ToString();
}
