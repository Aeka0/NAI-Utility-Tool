using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NAITool.Services;

namespace NAITool;

public sealed partial class MainWindow
{
    private const double SettingsHubControlColumnWidth = 360;
    private const double SettingsHubLayerWidth = 760;

    private async Task ShowSettingsHubDialogAsync(SettingsHubSection initialSection)
    {
        var root = (FrameworkElement)this.Content;
        SettingsHubSection selectedSection = initialSection;

        var contentHost = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        var navigationView = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            CompactModeThresholdWidth = 0,
            ExpandedModeThresholdWidth = 0,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleButtonVisible = false,
            IsSettingsVisible = false,
            IsPaneOpen = true,
            OpenPaneLength = 240,
            CompactPaneLength = 48,
            AlwaysShowHeader = false,
            Content = contentHost,
            Width = 1080,
            RequestedTheme = root.RequestedTheme,
        };

        var usageItem = CreateSettingsHubNavItem(SettingsHubSection.Usage, L("settings.hub.usage.title"), "\uE713");
        var apiItem = CreateSettingsHubNavItem(SettingsHubSection.Api, L("settings.api.title"), "\uE8D7");
        var networkItem = CreateSettingsHubNavItem(SettingsHubSection.Network, L("settings.hub.network.title"), "\uE774");
        var localStorageItem = CreateSettingsHubNavItem(SettingsHubSection.LocalStorage, L("settings.hub.local_storage.title"), "\uEDA2");
        var performanceItem = CreateSettingsHubNavItem(SettingsHubSection.Performance, L("settings.hub.performance.title"), "\uE9D9");
        var appearanceItem = CreateSettingsHubNavItem(SettingsHubSection.Appearance, L("settings.hub.appearance.title"), "\uE790");
        var languageItem = CreateSettingsHubNavItem(SettingsHubSection.Language, L("settings.hub.language.title"), "\uF2B7");
        var developerItem = CreateSettingsHubNavItem(SettingsHubSection.Developer, L("settings.hub.developer.title"), "\uEC7A");

        navigationView.MenuItems.Add(usageItem);
        navigationView.MenuItems.Add(networkItem);
        navigationView.MenuItems.Add(apiItem);
        navigationView.MenuItems.Add(localStorageItem);
        navigationView.MenuItems.Add(performanceItem);
        navigationView.MenuItems.Add(appearanceItem);
        navigationView.MenuItems.Add(languageItem);
        navigationView.MenuItems.Add(developerItem);

        var sectionItems = new Dictionary<SettingsHubSection, NavigationViewItem>
        {
            [SettingsHubSection.Usage] = usageItem,
            [SettingsHubSection.Network] = networkItem,
            [SettingsHubSection.Api] = apiItem,
            [SettingsHubSection.LocalStorage] = localStorageItem,
            [SettingsHubSection.Performance] = performanceItem,
            [SettingsHubSection.Appearance] = appearanceItem,
            [SettingsHubSection.Language] = languageItem,
            [SettingsHubSection.Developer] = developerItem,
        };

        ContentDialog dialog = null!;

        UIElement BuildUsageSection()
        {
            var resetPromptHeightsButton = new Button
            {
                Content = L("settings.hub.usage.reset_prompt_heights.action"),
                MinWidth = 96,
            };
            resetPromptHeightsButton.Click += (_, _) => ResetPromptEditorHeights();

            return CreateSettingsHubPage(
                CreateSettingsHubLayer(
                    "\uEB50",
                    L("settings.hub.usage.weight_highlight"),
                    L("settings.hub.usage.weight_highlight.description"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.WeightHighlight, value =>
                    {
                        ApplyUsageSettings(
                            value,
                            _settings.Settings.AutoComplete,
                            _settings.Settings.RememberPromptAndParameters,
                            _settings.Settings.SuperDropEnabled,
                            _settings.Settings.ScrollHistoryToTopAfterGeneration,
                            _settings.Settings.WildcardsEnabled,
                            _settings.Settings.WildcardsRequireExplicitSyntax);
                    })),
                CreateSettingsHubLayer(
                    "\uE8FD",
                    L("settings.hub.usage.auto_complete"),
                    L("settings.hub.usage.auto_complete.description"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.AutoComplete, value =>
                    {
                        ApplyUsageSettings(
                            _settings.Settings.WeightHighlight,
                            value,
                            _settings.Settings.RememberPromptAndParameters,
                            _settings.Settings.SuperDropEnabled,
                            _settings.Settings.ScrollHistoryToTopAfterGeneration,
                            _settings.Settings.WildcardsEnabled,
                            _settings.Settings.WildcardsRequireExplicitSyntax);
                    })),
                CreateSettingsHubLayer(
                    "\uF168",
                    L("settings.hub.usage.reset_prompt_heights"),
                    L("settings.hub.usage.reset_prompt_heights.description"),
                    resetPromptHeightsButton),
                CreateSettingsHubLayer(
                    "\uE74C",
                    L("settings.hub.usage.wildcards_enabled"),
                    L("settings.hub.usage.wildcards_enabled.description"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.WildcardsEnabled, value =>
                    {
                        ApplyUsageSettings(
                            _settings.Settings.WeightHighlight,
                            _settings.Settings.AutoComplete,
                            _settings.Settings.RememberPromptAndParameters,
                            _settings.Settings.SuperDropEnabled,
                            _settings.Settings.ScrollHistoryToTopAfterGeneration,
                            value,
                            _settings.Settings.WildcardsRequireExplicitSyntax);
                    })),
                CreateSettingsHubLayer(
                    "\uE81E",
                    L("settings.hub.usage.superdrop"),
                    L("settings.hub.usage.superdrop.description"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.SuperDropEnabled, value =>
                    {
                        ApplyUsageSettings(
                            _settings.Settings.WeightHighlight,
                            _settings.Settings.AutoComplete,
                            _settings.Settings.RememberPromptAndParameters,
                            value,
                            _settings.Settings.ScrollHistoryToTopAfterGeneration,
                            _settings.Settings.WildcardsEnabled,
                            _settings.Settings.WildcardsRequireExplicitSyntax);
                    })),
                CreateSettingsHubLayer(
                    "\uE74A",
                    L("settings.hub.usage.scroll_history_top_after_generation"),
                    L("settings.hub.usage.scroll_history_top_after_generation.description"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.ScrollHistoryToTopAfterGeneration, value =>
                    {
                        ApplyUsageSettings(
                            _settings.Settings.WeightHighlight,
                            _settings.Settings.AutoComplete,
                            _settings.Settings.RememberPromptAndParameters,
                            _settings.Settings.SuperDropEnabled,
                            value,
                            _settings.Settings.WildcardsEnabled,
                            _settings.Settings.WildcardsRequireExplicitSyntax);
                    })),
                CreateSettingsHubLayer(
                    "\uE72E",
                    L("settings.hub.usage.new_image_delete_protection"),
                    L("settings.hub.usage.new_image_delete_protection.description"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.NewImageDeleteProtection, value =>
                    {
                        ApplyUsageSettings(
                            _settings.Settings.WeightHighlight,
                            _settings.Settings.AutoComplete,
                            _settings.Settings.RememberPromptAndParameters,
                            _settings.Settings.SuperDropEnabled,
                            _settings.Settings.ScrollHistoryToTopAfterGeneration,
                            _settings.Settings.WildcardsEnabled,
                            _settings.Settings.WildcardsRequireExplicitSyntax,
                            value);
                    })),
                CreateSettingsHubLayer(
                    "\uE75D",
                    L("settings.hub.usage.wildcards_explicit"),
                    L("settings.hub.usage.wildcards_explicit.description"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.WildcardsRequireExplicitSyntax, value =>
                    {
                        ApplyUsageSettings(
                            _settings.Settings.WeightHighlight,
                            _settings.Settings.AutoComplete,
                            _settings.Settings.RememberPromptAndParameters,
                            _settings.Settings.SuperDropEnabled,
                            _settings.Settings.ScrollHistoryToTopAfterGeneration,
                            _settings.Settings.WildcardsEnabled,
                            value);
                    })));
        }

        UIElement BuildNetworkSection()
        {
            var proxyToggle = CreateLocalizedToggleSwitch(_settings.Settings.UseProxy);
            var proxyPortBox = new TextBox
            {
                PlaceholderText = L("settings.hub.network.proxy_port_placeholder"),
                Text = _settings.Settings.ProxyPort,
                IsEnabled = _settings.Settings.UseProxy,
                Width = 200,
            };
            var proxyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            proxyRow.Children.Add(proxyPortBox);
            proxyRow.Children.Add(proxyToggle);
            proxyToggle.Toggled += (_, _) =>
            {
                proxyPortBox.IsEnabled = proxyToggle.IsOn;
                _settings.Settings.UseProxy = proxyToggle.IsOn;
                _settings.Save();
            };
            proxyPortBox.TextChanged += (_, _) => _settings.Settings.ProxyPort = proxyPortBox.Text;
            proxyPortBox.LostFocus += (_, _) => _settings.Save();
            return CreateSettingsHubPage(
                CreateSettingsHubLayer("\uE93E", L("settings.hub.network.stream_generation"),
                    L("settings.hub.network.stream_generation_hint"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.StreamGeneration, value =>
                    {
                        _settings.Settings.StreamGeneration = value;
                        _settings.Save();
                    })),
                CreateSettingsHubLayer("\uE705", L("settings.hub.network.use_proxy"),
                    L("settings.hub.network.proxy_hint"), proxyRow));
        }

        UIElement BuildLocalStorageSection()
        {
            return CreateSettingsHubPage(
                CreateSettingsHubLayer(
                    "\uE823",
                    L("settings.hub.local_storage.remember_prompt"),
                    L("settings.hub.local_storage.remember_prompt.description"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.RememberPromptAndParameters, value =>
                    {
                        ApplyUsageSettings(
                            _settings.Settings.WeightHighlight,
                            _settings.Settings.AutoComplete,
                            value,
                            _settings.Settings.SuperDropEnabled,
                            _settings.Settings.ScrollHistoryToTopAfterGeneration,
                            _settings.Settings.WildcardsEnabled,
                            _settings.Settings.WildcardsRequireExplicitSyntax);
                    })),
                CreateSettingsHubLayer(
                    "\uE74D",
                    L("settings.hub.local_storage.delete_behavior"),
                    L("settings.hub.local_storage.delete_behavior_hint"),
                    CreateSettingsHubComboBox(
                        new[]
                        {
                            new SettingsHubComboOption(L("settings.hub.local_storage.delete_behavior.recycle_bin"), "RecycleBin"),
                            new SettingsHubComboOption(L("settings.hub.local_storage.delete_behavior.permanent"), "PermanentDelete"),
                        },
                        _settings.Settings.ImageDeleteBehavior,
                        value =>
                        {
                            ApplyLocalStorageSettings(
                                value,
                                _settings.Settings.PrivacyMode,
                                _settings.Settings.StripSavedImageMetadata,
                                _settings.Settings.AutoCopyVibeOriginalsToWorkspace);
                        },
                        260)),
                CreateSettingsHubLayer(
                    "\uE708",
                    L("settings.hub.local_storage.privacy_mode"),
                    L("settings.hub.local_storage.privacy_mode.description"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.PrivacyMode, value =>
                    {
                        ApplyLocalStorageSettings(
                            _settings.Settings.ImageDeleteBehavior,
                            value,
                            _settings.Settings.StripSavedImageMetadata,
                            _settings.Settings.AutoCopyVibeOriginalsToWorkspace);
                    })),
                CreateSettingsHubLayer(
                    "\uED62",
                    L("settings.hub.local_storage.strip_metadata"),
                    L("settings.hub.local_storage.strip_metadata.description"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.StripSavedImageMetadata, value =>
                    {
                        ApplyLocalStorageSettings(
                            _settings.Settings.ImageDeleteBehavior,
                            _settings.Settings.PrivacyMode,
                            value,
                            _settings.Settings.AutoCopyVibeOriginalsToWorkspace);
                    })),
                CreateSettingsHubLayer(
                    "\uE8B7",
                    L("settings.hub.local_storage.auto_copy_vibe_originals"),
                    L("settings.hub.local_storage.auto_copy_vibe_originals.description"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.AutoCopyVibeOriginalsToWorkspace, value =>
                    {
                        ApplyLocalStorageSettings(
                            _settings.Settings.ImageDeleteBehavior,
                            _settings.Settings.PrivacyMode,
                            _settings.Settings.StripSavedImageMetadata,
                            value);
                    })));
        }

        UIElement BuildPerformanceSection()
        {
            string sectionDescription = L("settings.hub.performance.description");
            return CreateSettingsHubPage(
                CreateSettingsHubLayer(
                    "\uF211",
                    L("settings.hub.performance.device"),
                    sectionDescription,
                    CreateSettingsHubComboBox(
                        new[]
                        {
                            new SettingsHubComboOption(L("settings.hub.performance.device_gpu"), "Gpu"),
                            new SettingsHubComboOption(L("settings.hub.performance.device_cpu"), "Cpu"),
                        },
                        OnnxPerformance.PreferCpu ? "Cpu" : "Gpu",
                        value => ApplyPerformanceSettings(value, OnnxPerformance.UnloadModelAfterInference),
                        320)),
                CreateSettingsHubLayer(
                    "\uE950",
                    L("settings.hub.performance.post_effects_device"),
                    L("settings.hub.performance.post_effects_hint"),
                    CreateSettingsHubComboBox(
                        new[]
                        {
                            new SettingsHubComboOption(L("settings.hub.performance.post_effects_device_gpu"), "Gpu"),
                            new SettingsHubComboOption(L("settings.hub.performance.post_effects_device_cpu"), "Cpu"),
                        },
                        PreferCpuForPostEffects ? "Cpu" : "Gpu",
                        ApplyPostEffectsPerformanceSettings,
                        320)),
                CreateSettingsHubLayer(
                    "\uE7F8",
                    L("settings.hub.performance.unload_after_inference"),
                    L("settings.hub.performance.unload_after_inference_hint"),
                    CreateSettingsHubToggleSwitch(OnnxPerformance.UnloadModelAfterInference, value =>
                        ApplyPerformanceSettings(OnnxPerformance.DevicePreference, value))));
        }

        UIElement BuildAppearanceSection(Action refresh)
        {
            string sectionDescription = L("settings.hub.appearance.description");
            return CreateSettingsHubPage(
                CreateSettingsHubLayer(
                    "\uE706",
                    L("settings.hub.appearance.title"),
                    sectionDescription,
                    CreateSettingsHubComboBox(
                        new[]
                        {
                            new SettingsHubComboOption(L("settings.hub.appearance.theme.system"), "System"),
                            new SettingsHubComboOption(L("settings.hub.appearance.theme.light"), "Light"),
                            new SettingsHubComboOption(L("settings.hub.appearance.theme.dark"), "Dark"),
                        },
                        _settings.Settings.ThemeMode,
                        value =>
                        {
                            ApplyThemeModeSetting(value);
                            refresh();
                        },
                        220)),
                CreateSettingsHubLayer(
                    "\uE727",
                    L("settings.hub.appearance.transparency"),
                    L("settings.hub.appearance.transparency.description"),
                    CreateSettingsHubComboBox(
                        new[]
                        {
                            new SettingsHubComboOption(L("settings.hub.appearance.transparency.standard"), "Standard"),
                            new SettingsHubComboOption(L("settings.hub.appearance.transparency.lesser"), "Lesser"),
                            new SettingsHubComboOption(L("settings.hub.appearance.transparency.opaque"), "Opaque"),
                        },
                        _settings.Settings.AppearanceTransparency,
                        ApplyTransparencyModeSetting,
                        220)),
                CreateSettingsHubLayer(
                    "\uE916",
                    L("settings.hub.appearance.generation_waiting_animation"),
                    L("settings.hub.appearance.generation_waiting_animation.description"),
                    CreateSettingsHubToggleSwitch(
                        _settings.Settings.EnableGenerationWaitingAnimation,
                        ApplyGenerationWaitingAnimationSetting)));
        }

        UIElement BuildLanguageSection(Action refresh)
        {
            return CreateSettingsHubPage(
                CreateSettingsHubLayer(
                    "\uF2B7",
                    L("settings.hub.language.title"),
                    L("settings.hub.language.description"),
                    CreateSettingsHubComboBox(
                        LocalizationService.SupportedLanguages
                            .Select(language => new SettingsHubComboOption(_loc.GetLanguageDisplayName(language.Code), language.Code))
                            .ToArray(),
                        LocalizationService.NormalizeLanguageCode(_settings.Settings.LanguageCode),
                        value =>
                        {
                            ApplyLanguageCodeSetting(value);
                            refresh();
                        },
                        220)));
        }

        UIElement BuildDeveloperSection()
        {
            return CreateSettingsHubPage(
                CreateSettingsHubLayer(
                    "\uEC7A",
                    L("settings.hub.developer.log_enabled"),
                    L("settings.hub.developer.log_hint"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.DevLogEnabled, ApplyDeveloperLogSetting)));
        }

        UIElement BuildSectionContent(SettingsHubSection section, Action refresh) => section switch
        {
            SettingsHubSection.Network => BuildNetworkSection(),
            SettingsHubSection.Api => BuildApiSettingsPage(),
            SettingsHubSection.LocalStorage => BuildLocalStorageSection(),
            SettingsHubSection.Performance => BuildPerformanceSection(),
            SettingsHubSection.Appearance => BuildAppearanceSection(refresh),
            SettingsHubSection.Language => BuildLanguageSection(refresh),
            SettingsHubSection.Developer => BuildDeveloperSection(),
            _ => BuildUsageSection(),
        };

        void RefreshDialog()
        {
            if (dialog == null)
                return;

            dialog.Title = L("settings.hub.title");
            dialog.CloseButtonText = L("common.close");
            dialog.RequestedTheme = root.RequestedTheme;

            navigationView.RequestedTheme = root.RequestedTheme;
            navigationView.Language = UiLanguageTag;
            navigationView.FontFamily = UiTextFontFamily;

            SetSettingsHubNavItemLabel(usageItem, L("settings.hub.usage.title"));
            SetSettingsHubNavItemLabel(apiItem, L("settings.api.title"));
            SetSettingsHubNavItemLabel(networkItem, L("settings.hub.network.title"));
            SetSettingsHubNavItemLabel(localStorageItem, L("settings.hub.local_storage.title"));
            SetSettingsHubNavItemLabel(performanceItem, L("settings.hub.performance.title"));
            SetSettingsHubNavItemLabel(appearanceItem, L("settings.hub.appearance.title"));
            SetSettingsHubNavItemLabel(languageItem, L("settings.hub.language.title"));
            SetSettingsHubNavItemLabel(developerItem, L("settings.hub.developer.title"));

            contentHost.Content = BuildSectionContent(selectedSection, RefreshDialog);
            ApplyUiFontToVisualTree(navigationView);
        }

        navigationView.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItemContainer?.Tag is SettingsHubSection section)
            {
                selectedSection = section;
                RefreshDialog();
            }
        };

        dialog = new ContentDialog
        {
            Title = L("settings.hub.title"),
            Content = navigationView,
            CloseButtonText = L("common.close"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = root.RequestedTheme,
        };

        double xamlRootHeight = this.Content.XamlRoot?.Size.Height ?? 800;
        double dialogMaxHeight = Math.Clamp(xamlRootHeight - 60, 540, 880);
        navigationView.Height = Math.Clamp(dialogMaxHeight - 190, 420, 720);
        dialog.Resources["ContentDialogMaxWidth"] = 1280.0;
        dialog.Resources["ContentDialogMaxHeight"] = dialogMaxHeight;

        if (Application.Current.Resources.TryGetValue("DefaultButtonStyle", out var defaultButtonStyleObj)
            && defaultButtonStyleObj is Style defaultButtonStyle)
        {
            var closeButtonStyle = new Style(typeof(Button)) { BasedOn = defaultButtonStyle };
            closeButtonStyle.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right));
            closeButtonStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 120.0));
            closeButtonStyle.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 160.0));
            dialog.CloseButtonStyle = closeButtonStyle;
        }

        navigationView.SelectedItem = sectionItems[initialSection];
        RefreshDialog();
        await dialog.ShowAsync();
    }

    private NavigationViewItem CreateSettingsHubNavItem(SettingsHubSection section, string text, string glyph)
    {
        var item = new NavigationViewItem
        {
            Tag = section,
            Icon = new FontIcon
            {
                FontFamily = SymbolFontFamily,
                Glyph = glyph,
            },
        };
        SetSettingsHubNavItemLabel(item, text);
        return item;
    }

    private void SetSettingsHubNavItemLabel(NavigationViewItem item, string text)
    {
        item.Content = new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private UIElement CreateSettingsHubPage(params UIElement[] layers)
    {
        var panel = new Grid
        {
            Margin = new Thickness(20, 18, 20, 20),
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = SettingsHubLayerWidth,
        };

        for (int i = 0; i < layers.Length; i++)
        {
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (layers[i] is not FrameworkElement element)
                continue;

            element.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (i > 0)
                element.Margin = new Thickness(0, 10, 0, 0);

            Grid.SetRow(element, i);
            panel.Children.Add(element);
        }

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(0, 0, 0, 8),
        };
    }

    private UIElement CreateSettingsHubLayer(string glyph, string title, string description, FrameworkElement control, bool expanded = false)
    {
        bool isDark = IsSettingsHubDarkTheme();
        var backgroundBrush = new SolidColorBrush(isDark
            ? Windows.UI.Color.FromArgb(255, 42, 42, 42)
            : Windows.UI.Color.FromArgb(255, 250, 250, 250));
        var borderBrush = new SolidColorBrush(isDark
            ? Windows.UI.Color.FromArgb(255, 84, 84, 84)
            : Windows.UI.Color.FromArgb(255, 214, 214, 214));
        var iconBrush = new SolidColorBrush(isDark
            ? Windows.UI.Color.FromArgb(255, 232, 232, 232)
            : Windows.UI.Color.FromArgb(255, 52, 52, 52));
        var descriptionBrush = new SolidColorBrush(isDark
            ? Windows.UI.Color.FromArgb(255, 182, 182, 182)
            : Windows.UI.Color.FromArgb(255, 96, 96, 96));

        control.HorizontalAlignment = HorizontalAlignment.Right;
        control.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SettingsHubControlColumnWidth) });

        var iconHost = new FontIcon
        {
            FontFamily = SymbolFontFamily,
            Glyph = glyph,
            FontSize = 24,
            Foreground = iconBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };

        var controlHost = new Grid
        {
            Width = SettingsHubControlColumnWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        controlHost.Children.Add(control);

        var textPanel = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        textPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = descriptionBrush,
        });

        Grid.SetColumn(iconHost, 0);
        Grid.SetColumn(textPanel, 1);
        Grid.SetColumn(controlHost, 2);
        grid.Children.Add(iconHost);
        grid.Children.Add(textPanel);
        grid.Children.Add(controlHost);
        if (expanded)
        {
            grid.RowSpacing = 16;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions[2].Width = new GridLength(0);
            controlHost.Width = double.NaN;
            control.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(controlHost, 0);
            Grid.SetColumnSpan(controlHost, 3);
            Grid.SetRow(controlHost, 1);
        }

        return new Border
        {
            Width = SettingsHubLayerWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = borderBrush,
            Background = backgroundBrush,
            Padding = new Thickness(22, 14, 20, 14),
            Child = grid,
        };
    }

    private ToggleSwitch CreateSettingsHubToggleSwitch(bool initialValue, Action<bool> onChanged)
    {
        var toggle = CreateLocalizedToggleSwitch(initialValue);
        toggle.Toggled += (_, _) => onChanged(toggle.IsOn);
        return toggle;
    }

    private ComboBox CreateSettingsHubComboBox(
        IReadOnlyList<SettingsHubComboOption> options,
        string selectedTag,
        Action<string> onChanged,
        double width)
    {
        var comboBox = new ComboBox
        {
            Width = width,
        };

        foreach (var option in options)
        {
            comboBox.Items.Add(new ComboBoxItem
            {
                Content = option.Text,
                Tag = option.Tag,
            });
        }

        int selectedIndex = options
            .Select((option, index) => new { option.Tag, index })
            .FirstOrDefault(x => string.Equals(x.Tag, selectedTag, StringComparison.OrdinalIgnoreCase))
            ?.index ?? 0;
        comboBox.SelectedIndex = selectedIndex;

        comboBox.SelectionChanged += (_, _) =>
        {
            if (comboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                onChanged(tag);
        };

        return comboBox;
    }

    private bool IsSettingsHubDarkTheme()
    {
        if (this.Content is not FrameworkElement root)
            return false;

        return root.RequestedTheme == ElementTheme.Dark ||
               (root.RequestedTheme == ElementTheme.Default && root.ActualTheme == ElementTheme.Dark);
    }

    private sealed record SettingsHubComboOption(string Text, string Tag);
}
