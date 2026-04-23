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
    private const double SettingsHubLayerWidth = 800;

    private async Task ShowSettingsHubDialogAsync(SettingsHubSection initialSection)
    {
        var root = (FrameworkElement)this.Content;
        SettingsHubSection selectedSection = initialSection;
        bool isRefreshing = false;

        var contentHost = new ContentControl();
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
            Height = 680,
            RequestedTheme = root.RequestedTheme,
        };

        var usageItem = CreateSettingsHubNavItem(SettingsHubSection.Usage, L("settings.usage.title"), "\uE713");
        var networkItem = CreateSettingsHubNavItem(SettingsHubSection.Network, L("settings.network.title"), "\uE774");
        var performanceItem = CreateSettingsHubNavItem(SettingsHubSection.Performance, L("settings.performance.title"), "\uE9D9");
        var appearanceItem = CreateSettingsHubNavItem(SettingsHubSection.Appearance, L("menu.settings.appearance"), "\uE790");
        var languageItem = CreateSettingsHubNavItem(SettingsHubSection.Language, L("menu.settings.language"), "\uF2B7");
        var developerItem = CreateSettingsHubNavItem(SettingsHubSection.Developer, L("settings.dev.title"), "\uEC7A");

        navigationView.MenuItems.Add(usageItem);
        navigationView.MenuItems.Add(networkItem);
        navigationView.MenuItems.Add(performanceItem);
        navigationView.MenuItems.Add(appearanceItem);
        navigationView.MenuItems.Add(languageItem);
        navigationView.MenuItems.Add(developerItem);

        var sectionItems = new Dictionary<SettingsHubSection, NavigationViewItem>
        {
            [SettingsHubSection.Usage] = usageItem,
            [SettingsHubSection.Network] = networkItem,
            [SettingsHubSection.Performance] = performanceItem,
            [SettingsHubSection.Appearance] = appearanceItem,
            [SettingsHubSection.Language] = languageItem,
            [SettingsHubSection.Developer] = developerItem,
        };

        ContentDialog dialog = null!;

        UIElement BuildUsageSection()
        {
            return CreateSettingsHubPage(
                CreateSettingsHubLayer(
                    "\uE8D2",
                    L("settings.usage.weight_highlight"),
                    L("settings.hub.usage.weight_highlight.description"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.WeightHighlight, value =>
                    {
                        ApplyUsageSettings(
                            value,
                            _settings.Settings.AutoComplete,
                            _settings.Settings.RememberPromptAndParameters,
                            _settings.Settings.SuperDropEnabled,
                            _settings.Settings.ShowGenerationResultBar,
                            _settings.Settings.WildcardsEnabled,
                            _settings.Settings.WildcardsRequireExplicitSyntax,
                            _settings.Settings.ImageDeleteBehavior);
                    })),
                CreateSettingsHubLayer(
                    "\uE8A9",
                    L("settings.usage.auto_complete"),
                    L("settings.hub.usage.auto_complete.description"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.AutoComplete, value =>
                    {
                        ApplyUsageSettings(
                            _settings.Settings.WeightHighlight,
                            value,
                            _settings.Settings.RememberPromptAndParameters,
                            _settings.Settings.SuperDropEnabled,
                            _settings.Settings.ShowGenerationResultBar,
                            _settings.Settings.WildcardsEnabled,
                            _settings.Settings.WildcardsRequireExplicitSyntax,
                            _settings.Settings.ImageDeleteBehavior);
                    })),
                CreateSettingsHubLayer(
                    "\uE823",
                    L("settings.usage.remember_prompt"),
                    L("settings.hub.usage.remember_prompt.description"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.RememberPromptAndParameters, value =>
                    {
                        ApplyUsageSettings(
                            _settings.Settings.WeightHighlight,
                            _settings.Settings.AutoComplete,
                            value,
                            _settings.Settings.SuperDropEnabled,
                            _settings.Settings.ShowGenerationResultBar,
                            _settings.Settings.WildcardsEnabled,
                            _settings.Settings.WildcardsRequireExplicitSyntax,
                            _settings.Settings.ImageDeleteBehavior);
                    })),
                CreateSettingsHubLayer(
                    "\uE7C3",
                    L("settings.usage.superdrop"),
                    L("settings.hub.usage.superdrop.description"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.SuperDropEnabled, value =>
                    {
                        ApplyUsageSettings(
                            _settings.Settings.WeightHighlight,
                            _settings.Settings.AutoComplete,
                            _settings.Settings.RememberPromptAndParameters,
                            value,
                            _settings.Settings.ShowGenerationResultBar,
                            _settings.Settings.WildcardsEnabled,
                            _settings.Settings.WildcardsRequireExplicitSyntax,
                            _settings.Settings.ImageDeleteBehavior);
                    })),
                CreateSettingsHubLayer(
                    "\uE7F4",
                    L("settings.usage.show_generation_result_bar"),
                    L("settings.hub.usage.show_generation_result_bar.description"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.ShowGenerationResultBar, value =>
                    {
                        ApplyUsageSettings(
                            _settings.Settings.WeightHighlight,
                            _settings.Settings.AutoComplete,
                            _settings.Settings.RememberPromptAndParameters,
                            _settings.Settings.SuperDropEnabled,
                            value,
                            _settings.Settings.WildcardsEnabled,
                            _settings.Settings.WildcardsRequireExplicitSyntax,
                            _settings.Settings.ImageDeleteBehavior);
                    })),
                CreateSettingsHubLayer(
                    "\uE74C",
                    L("settings.usage.wildcards_enabled"),
                    L("settings.hub.usage.wildcards_enabled.description"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.WildcardsEnabled, value =>
                    {
                        ApplyUsageSettings(
                            _settings.Settings.WeightHighlight,
                            _settings.Settings.AutoComplete,
                            _settings.Settings.RememberPromptAndParameters,
                            _settings.Settings.SuperDropEnabled,
                            _settings.Settings.ShowGenerationResultBar,
                            value,
                            _settings.Settings.WildcardsRequireExplicitSyntax,
                            _settings.Settings.ImageDeleteBehavior);
                    })),
                CreateSettingsHubLayer(
                    "\uE943",
                    L("settings.usage.wildcards_explicit"),
                    L("settings.hub.usage.wildcards_explicit.description"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.WildcardsRequireExplicitSyntax, value =>
                    {
                        ApplyUsageSettings(
                            _settings.Settings.WeightHighlight,
                            _settings.Settings.AutoComplete,
                            _settings.Settings.RememberPromptAndParameters,
                            _settings.Settings.SuperDropEnabled,
                            _settings.Settings.ShowGenerationResultBar,
                            _settings.Settings.WildcardsEnabled,
                            value,
                            _settings.Settings.ImageDeleteBehavior);
                    })),
                CreateSettingsHubLayer(
                    "\uE74D",
                    L("settings.usage.delete_behavior"),
                    L("settings.usage.delete_behavior_hint"),
                    CreateSettingsHubComboBox(
                        new[]
                        {
                            new SettingsHubComboOption(L("settings.usage.delete_behavior.recycle_bin"), "RecycleBin"),
                            new SettingsHubComboOption(L("settings.usage.delete_behavior.permanent"), "PermanentDelete"),
                        },
                        _settings.Settings.ImageDeleteBehavior,
                        value =>
                        {
                            ApplyUsageSettings(
                                _settings.Settings.WeightHighlight,
                                _settings.Settings.AutoComplete,
                                _settings.Settings.RememberPromptAndParameters,
                                _settings.Settings.SuperDropEnabled,
                                _settings.Settings.ShowGenerationResultBar,
                                _settings.Settings.WildcardsEnabled,
                                _settings.Settings.WildcardsRequireExplicitSyntax,
                                value);
                        },
                        260)));
        }

        UIElement BuildNetworkSection()
        {
            var tokenBox = new PasswordBox
            {
                PlaceholderText = "Bearer Token",
                Password = _settings.Settings.ApiToken ?? "",
                Width = 320,
            };
            var saveButton = new Button
            {
                Content = L("common.save"),
                MinWidth = 110,
            };
            var testButton = new Button
            {
                Content = L("settings.network.test_connection"),
                MinWidth = 110,
            };

            async Task RunNetworkActionAsync(bool testConnection)
            {
                saveButton.IsEnabled = false;
                testButton.IsEnabled = false;
                try
                {
                    await SaveNetworkSettingsAsync(
                        tokenBox.Password.Trim(),
                        _settings.Settings.StreamGeneration,
                        _settings.Settings.UseProxy,
                        _settings.Settings.ProxyPort,
                        testConnection);
                }
                finally
                {
                    saveButton.IsEnabled = true;
                    testButton.IsEnabled = true;
                }
            }

            saveButton.Click += async (_, _) => await RunNetworkActionAsync(false);
            testButton.Click += async (_, _) => await RunNetworkActionAsync(true);

            var tokenActions = new StackPanel
            {
                Spacing = 10,
                Width = 340,
            };
            tokenActions.Children.Add(tokenBox);
            tokenActions.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    saveButton,
                    testButton,
                },
            });

            var proxyToggle = new ToggleSwitch
            {
                IsOn = _settings.Settings.UseProxy,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var proxyPortBox = new TextBox
            {
                PlaceholderText = L("settings.network.proxy_port_placeholder"),
                Text = _settings.Settings.ProxyPort,
                Width = 140,
                IsEnabled = _settings.Settings.UseProxy,
            };
            proxyToggle.Toggled += (_, _) =>
            {
                if (isRefreshing)
                    return;

                proxyPortBox.IsEnabled = proxyToggle.IsOn;
                _settings.Settings.UseProxy = proxyToggle.IsOn;
                _settings.Save();
            };
            proxyPortBox.LostFocus += (_, _) =>
            {
                if (isRefreshing)
                    return;

                _settings.Settings.ProxyPort = proxyPortBox.Text;
                _settings.Save();
            };

            var proxyControl = new StackPanel
            {
                Spacing = 8,
                Width = 220,
            };
            proxyControl.Children.Add(proxyToggle);
            proxyControl.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = L("settings.network.port"),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    proxyPortBox,
                },
            });

            return CreateSettingsHubPage(
                CreateSettingsHubLayer(
                    "\uE774",
                    L("settings.network.api_token"),
                    L("settings.hub.network.description"),
                    tokenActions),
                CreateSettingsHubLayer(
                    "\uE895",
                    L("settings.network.stream_generation"),
                    L("settings.network.stream_generation_hint"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.StreamGeneration, value =>
                    {
                        _settings.Settings.StreamGeneration = value;
                        _settings.Save();
                    })),
                CreateSettingsHubLayer(
                    "\uE1D9",
                    L("settings.network.use_proxy"),
                    L("settings.network.proxy_hint"),
                    proxyControl));
        }

        UIElement BuildPerformanceSection()
        {
            string sectionDescription = L("settings.hub.performance.description");
            return CreateSettingsHubPage(
                CreateSettingsHubLayer(
                    "\uE9D9",
                    L("settings.performance.device"),
                    sectionDescription,
                    CreateSettingsHubComboBox(
                        new[]
                        {
                            new SettingsHubComboOption(L("settings.performance.device_gpu"), "Gpu"),
                            new SettingsHubComboOption(L("settings.performance.device_cpu"), "Cpu"),
                        },
                        OnnxPerformance.PreferCpu ? "Cpu" : "Gpu",
                        value => ApplyPerformanceSettings(value, OnnxPerformance.UnloadModelAfterInference),
                        320)),
                CreateSettingsHubLayer(
                    "\uE7F8",
                    L("settings.performance.unload_after_inference"),
                    L("settings.performance.unload_after_inference_hint"),
                    CreateSettingsHubToggleSwitch(OnnxPerformance.UnloadModelAfterInference, value =>
                        ApplyPerformanceSettings(OnnxPerformance.DevicePreference, value))));
        }

        UIElement BuildAppearanceSection(Action refresh)
        {
            string sectionDescription = L("settings.hub.appearance.description");
            return CreateSettingsHubPage(
                CreateSettingsHubLayer(
                    "\uE706",
                    L("menu.settings.appearance"),
                    sectionDescription,
                    CreateSettingsHubComboBox(
                        new[]
                        {
                            new SettingsHubComboOption(L("menu.settings.theme.system"), "System"),
                            new SettingsHubComboOption(L("menu.settings.theme.light"), "Light"),
                            new SettingsHubComboOption(L("menu.settings.theme.dark"), "Dark"),
                        },
                        _settings.Settings.ThemeMode,
                        value =>
                        {
                            ApplyThemeModeSetting(value);
                            refresh();
                        },
                        220)),
                CreateSettingsHubLayer(
                    "\uE7F5",
                    L("menu.settings.transparency.standard"),
                    sectionDescription,
                    CreateSettingsHubComboBox(
                        new[]
                        {
                            new SettingsHubComboOption(L("menu.settings.transparency.standard"), "Standard"),
                            new SettingsHubComboOption(L("menu.settings.transparency.lesser"), "Lesser"),
                            new SettingsHubComboOption(L("menu.settings.transparency.opaque"), "Opaque"),
                        },
                        _settings.Settings.AppearanceTransparency,
                        ApplyTransparencyModeSetting,
                        220)));
        }

        UIElement BuildLanguageSection(Action refresh)
        {
            return CreateSettingsHubPage(
                CreateSettingsHubLayer(
                    "\uF2B7",
                    L("menu.settings.language"),
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
                    L("settings.dev.log_enabled"),
                    L("settings.dev.log_hint"),
                    CreateSettingsHubToggleSwitch(_settings.Settings.DevLogEnabled, ApplyDeveloperLogSetting)));
        }

        UIElement BuildSectionContent(SettingsHubSection section, Action refresh) => section switch
        {
            SettingsHubSection.Network => BuildNetworkSection(),
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

            isRefreshing = true;
            try
            {
                dialog.Title = L("menu.settings");
                dialog.CloseButtonText = L("common.close");
                dialog.RequestedTheme = root.RequestedTheme;

                navigationView.RequestedTheme = root.RequestedTheme;
                navigationView.Language = UiLanguageTag;
                navigationView.FontFamily = UiTextFontFamily;

                SetSettingsHubNavItemLabel(usageItem, L("settings.usage.title"));
                SetSettingsHubNavItemLabel(networkItem, L("settings.network.title"));
                SetSettingsHubNavItemLabel(performanceItem, L("settings.performance.title"));
                SetSettingsHubNavItemLabel(appearanceItem, L("menu.settings.appearance"));
                SetSettingsHubNavItemLabel(languageItem, L("menu.settings.language"));
                SetSettingsHubNavItemLabel(developerItem, L("settings.dev.title"));

                contentHost.Content = BuildSectionContent(selectedSection, RefreshDialog);
                ApplyUiFontToVisualTree(navigationView);
            }
            finally
            {
                isRefreshing = false;
            }
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
            Title = L("menu.settings"),
            Content = navigationView,
            CloseButtonText = L("common.close"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = root.RequestedTheme,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1280.0;

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
        };
    }

    private UIElement CreateSettingsHubLayer(string glyph, string title, string description, FrameworkElement control)
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

        return new Border
        {
            Width = SettingsHubLayerWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = borderBrush,
            Background = backgroundBrush,
            Padding = new Thickness(14, 12, 14, 12),
            Child = grid,
        };
    }

    private ToggleSwitch CreateSettingsHubToggleSwitch(bool initialValue, Action<bool> onChanged)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = initialValue,
            MinWidth = 84,
        };
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
