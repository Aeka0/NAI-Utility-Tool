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
    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem item && item.Tag is string mode)
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
    }

    private void ApplyTheme(string mode)
    {
        if (this.Content is FrameworkElement root)
        {
            root.RequestedTheme = mode switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => GetSystemTheme(),
            };
            UpdateTitleBarColors(root.RequestedTheme == ElementTheme.Dark ||
                (root.RequestedTheme == ElementTheme.Default && root.ActualTheme == ElementTheme.Dark));
            UpdateSizeWarningVisuals();

            if (_advParamsWindow?.Content is FrameworkElement advRoot)
                advRoot.RequestedTheme = root.RequestedTheme;
            if (_advParamsWindow != null)
                ApplyWindowChrome(_advParamsWindow,
                    root.RequestedTheme == ElementTheme.Dark ||
                    (root.RequestedTheme == ElementTheme.Default && root.ActualTheme == ElementTheme.Dark),
                    _advTitleBarGrid, _advRootPanel);

            RefreshEffectsPanel();
            RefreshEffectsOverlay();
        }
    }

    private void UpdateTitleBarColors(bool isDark)
    {
        ApplyWindowChrome(this, isDark, null, null);
    }

    private static ElementTheme GetSystemTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v)
                return v == 1 ? ElementTheme.Light : ElementTheme.Dark;
        }
        catch { }
        return ElementTheme.Default;
    }

    private async void OnHelpOverview(object sender, RoutedEventArgs e)
    {
        var pages = new (string Title, string Body, string Glyph)[]
        {
            (
                L("help.overview.image.title"),
                L("help.overview.image.body"),
                "\uE768"
            ),
            (
                L("help.overview.i2i.title"),
                L("help.overview.i2i.body"),
                "\uE70F"
            ),
            (
                L("help.overview.tools.title"),
                L("help.overview.tools.body"),
                "\uEDFB"
            ),
            (
                L("help.overview.view.title"),
                L("help.overview.view.body"),
                "\uE7C3"
            ),
            (
                L("help.overview.wildcards.title"),
                L("help.overview.wildcards.body"),
                "\uE74C"
            ),
            (
                L("help.overview.shortcuts.title"),
                L("help.overview.shortcuts.body"),
                "\uE765"
            ),
        };

        var pageTitleText = new TextBlock
        {
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        var pageBodyText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 24,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var contentPanel = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(20, 18, 20, 18),
        };
        contentPanel.Children.Add(pageTitleText);
        contentPanel.Children.Add(pageBodyText);

        var contentScrollViewer = new ScrollViewer
        {
            Content = contentPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        void ShowOverviewPage(int index)
        {
            var page = pages[index];
            pageTitleText.Text = page.Title;
            pageBodyText.Text = page.Body;
            contentScrollViewer.ChangeView(null, 0, null, true);
        }

        var nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleButtonVisible = false,
            IsBackEnabled = false,
            IsSettingsVisible = false,
            AlwaysShowHeader = false,
            SelectionFollowsFocus = NavigationViewSelectionFollowsFocus.Disabled,
            CompactModeThresholdWidth = 0,
            ExpandedModeThresholdWidth = 0,
            OpenPaneLength = 190,
            Width = 760,
            Height = 460,
            Content = contentScrollViewer,
        };

        for (int i = 0; i < pages.Length; i++)
        {
            nav.MenuItems.Add(new NavigationViewItem
            {
                Content = pages[i].Title,
                Tag = i,
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = pages[i].Glyph }
            });
        }

        nav.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItemContainer?.Tag is int index)
                ShowOverviewPage(index);
        };

        if (nav.MenuItems[0] is NavigationViewItem firstItem)
            nav.SelectedItem = firstItem;
        ShowOverviewPage(0);

        var dialog = new ContentDialog
        {
            Title = L("help.overview.dialog_title"),
            Content = nav,
            CloseButtonText = L("common.close"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 860.0;

        await dialog.ShowAsync();
    }

    private async void OnHelpHighlights(object sender, RoutedEventArgs e)
    {
        var pages = new (string Title, string Body)[]
        {
            (
                L("help.highlights.automation.title"),
                L("help.highlights.automation.body")
            ),
            (
                L("help.highlights.prompt_generator.title"),
                L("help.highlights.prompt_generator.body")
            ),
            (
                L("help.highlights.prompt_normalization.title"),
                L("help.highlights.prompt_normalization.body")
            ),
            (
                L("help.highlights.random_style.title"),
                L("help.highlights.random_style.body")
            ),
            (
                L("help.highlights.quick_prompts.title"),
                L("help.highlights.quick_prompts.body")
            ),
            (
                L("help.highlights.wildcards.title"),
                L("help.highlights.wildcards.body")
            ),
            (
                L("help.highlights.weight_converter.title"),
                L("help.highlights.weight_converter.body")
            ),
            (
                L("help.highlights.actions.title"),
                L("help.highlights.actions.body")
            ),
            (
                L("help.highlights.large_inpaint.title"),
                L("help.highlights.large_inpaint.body")
            ),
            (
                L("help.highlights.upscale.title"),
                L("help.highlights.upscale.body")
            ),
            (
                L("help.highlights.post.title"),
                L("help.highlights.post.body")
            ),
            (
                L("help.highlights.inspect.title"),
                L("help.highlights.inspect.body")
            ),
            (
                L("help.highlights.reverse.title"),
                L("help.highlights.reverse.body")
            ),
        };

        var pageIndexText = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.7,
        };
        var pageTitleText = new TextBlock
        {
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        var pageBodyText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var contentPanel = new StackPanel
        {
            Spacing = 10,
            Padding = new Thickness(18, 12, 24, 16),
        };
        contentPanel.Children.Add(pageIndexText);
        contentPanel.Children.Add(pageTitleText);
        contentPanel.Children.Add(pageBodyText);

        var contentScrollViewer = new ScrollViewer
        {
            Content = contentPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        void ShowPage(int index)
        {
            var page = pages[index];
            pageIndexText.Text = Lf("help.highlights.index", index + 1, pages.Length);
            pageTitleText.Text = page.Title;
            pageBodyText.Text = page.Body;
            contentScrollViewer.ChangeView(null, 0, null, true);
        }

        var nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleButtonVisible = false,
            IsBackEnabled = false,
            IsSettingsVisible = false,
            AlwaysShowHeader = false,
            SelectionFollowsFocus = NavigationViewSelectionFollowsFocus.Disabled,
            CompactModeThresholdWidth = 0,
            ExpandedModeThresholdWidth = 0,
            OpenPaneLength = 190,
            Width = 760,
            Height = 460,
            Content = contentScrollViewer,
        };

        for (int i = 0; i < pages.Length; i++)
        {
            nav.MenuItems.Add(new NavigationViewItem
            {
                Content = pages[i].Title,
                Tag = i,
            });
        }

        nav.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItemContainer?.Tag is int index)
                ShowPage(index);
        };

        if (nav.MenuItems[0] is NavigationViewItem firstItem)
            nav.SelectedItem = firstItem;
        ShowPage(0);

        var dialog = new ContentDialog
        {
            Title = L("help.highlights.dialog_title"),
            Content = nav,
            CloseButtonText = L("common.close"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 860.0;

        await dialog.ShowAsync();
    }

    private async void OnHelpUsefulLinks(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel
        {
            Spacing = 10,
            MinWidth = 560,
        };

        panel.Children.Add(new TextBlock
        {
            Text = L("help.links.description"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        });

        var links = new (string Name, string Url, string Description)[]
        {
            (L("help.links.novelai.name"), "https://novelai.net/", L("help.links.novelai.description")),
            (L("help.links.danbooru.name"), "https://danbooru.donmai.us/", L("help.links.danbooru.description")),
            (L("help.links.aistudio.name"), "https://aistudio.google.com/", L("help.links.aistudio.description")),
            (L("help.links.github.name"), "https://github.com/", L("help.links.github.description")),
            (L("help.links.huggingface.name"), "https://huggingface.co/", L("help.links.huggingface.description")),
        };

        foreach (var link in links)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textPanel = new StackPanel { Spacing = 2 };
            textPanel.Children.Add(new TextBlock
            {
                Text = link.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            textPanel.Children.Add(new TextBlock
            {
                Text = $"{link.Description}\n{link.Url}",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
            });

            var openBtn = new Button
            {
                Content = L("common.open"),
                MinWidth = 76,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = link.Url,
            };
            openBtn.Click += (_, _) => OpenExternalUrl(link.Url);

            Grid.SetColumn(textPanel, 0);
            Grid.SetColumn(openBtn, 1);
            row.Children.Add(textPanel);
            row.Children.Add(openBtn);
            panel.Children.Add(row);
        }

        var dialog = new ContentDialog
        {
            Title = L("help.links.dialog_title"),
            Content = panel,
            CloseButtonText = L("common.close"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 700.0;

        await dialog.ShowAsync();
    }

    private void OpenExternalUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("help.links.open_failed", ex.Message);
        }
    }

    private async void OnAbout(object sender, RoutedEventArgs e)
    {
        var aboutPanel = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(4, 8, 4, 8),
        };

        aboutPanel.Children.Add(new TextBlock
        {
            Text = "NAI Utility Tool",
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold
        });
        aboutPanel.Children.Add(new TextBlock
        {
            Text = L("about.version"),
            Opacity = 0.7
        });
        aboutPanel.Children.Add(new TextBlock
        {
            Text = L("about.description"),
            TextWrapping = TextWrapping.Wrap
        });

        bool aboutIsDark = ((FrameworkElement)this.Content).ActualTheme == ElementTheme.Dark;
        var cardBg = new SolidColorBrush(aboutIsDark
            ? Windows.UI.Color.FromArgb(28, 255, 255, 255)
            : Windows.UI.Color.FromArgb(16, 0, 0, 0));
        var cardBorder = new SolidColorBrush(aboutIsDark
            ? Windows.UI.Color.FromArgb(20, 255, 255, 255)
            : Windows.UI.Color.FromArgb(14, 0, 0, 0));
        var accentDim = new SolidColorBrush(aboutIsDark
            ? Windows.UI.Color.FromArgb(40, 96, 165, 250)
            : Windows.UI.Color.FromArgb(35, 59, 130, 246));

        var thanksItems = new (string Name, string Description, string Glyph)[]
        {
            ("Aeka", L("about.thanks.aeka"), "\uE77B"),
            ("Xianyun", L("about.thanks.xianyun"), "\uE77B"),
            ("CloverIris", L("about.thanks.cloveriris"), "\uE77B"),
            ("柊咲桜", L("about.thanks.hiiragisaki_sakura"), "\uE77B"),
            ("五月不蓝", L("about.thanks.wuyue_bulan"), "\uE77B"),
            ("Dominik Reh", L("about.thanks.dominik_reh"), "\uE8D2"),
            ("SmilingWolf", L("about.thanks.smilingwolf"), "\uE8BA"),
            ("pixai-labs", L("about.thanks.pixai_labs"), "\uE8BA"),
            ("DeepGHS", L("about.thanks.deepghs"), "\uE835"),
            ("xinntao", L("about.thanks.xinntao"), "\uE740"),
            ("EinAeffchen", L("about.thanks.einaeffchen"), "\uE74C"),
            (L("about.name.qinglong"), L("about.thanks.qinglong"), "\uE74C"),
            ("jiarandiana0307", L("about.thanks.jiarandiana0307"), "\uF404"),
            ("NovelAI", L("about.thanks.novelai"), "\uE8BA"),
        };

        var thanksHeader = new TextBlock
        {
            Text = L("about.thanks.header"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
            Margin = new Thickness(4, 8, 4, 4),
        };

        const int rowsPerCol = 2;
        int colCount = (thanksItems.Length + rowsPerCol - 1) / rowsPerCol;
        var columnsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Padding = new Thickness(4, 0, 24, 0),
        };

        for (int col = 0; col < colCount; col++)
        {
            var column = new StackPanel { Spacing = 16 };

            for (int row = 0; row < rowsPerCol; row++)
            {
                int idx = col * rowsPerCol + row;
                if (idx >= thanksItems.Length) break;
                var item = thanksItems[idx];

                var iconBorder = new Border
                {
                    Width = 44, Height = 44,
                    CornerRadius = new CornerRadius(22),
                    Background = accentDim,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                iconBorder.Child = new FontIcon
                {
                    FontFamily = SymbolFontFamily, Glyph = item.Glyph,
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var textCol = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                textCol.Children.Add(new TextBlock
                {
                    Text = item.Name,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize = 15,
                });
                textCol.Children.Add(new TextBlock
                {
                    Text = item.Description,
                    Opacity = 0.65,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 310,
                });

                var cardInner = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                };
                cardInner.Children.Add(iconBorder);
                cardInner.Children.Add(textCol);

                var card = new Border
                {
                    Background = cardBg,
                    BorderBrush = cardBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(18, 16, 20, 16),
                    MinWidth = 340,
                    MinHeight = 108,
                };
                card.Child = cardInner;
                column.Children.Add(card);
            }

            columnsPanel.Children.Add(column);
        }

        var thanksCardScroller = new ScrollViewer
        {
            Content = columnsPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
        };

        var thanksPanel = new StackPanel { Spacing = 8 };
        thanksPanel.Children.Add(thanksHeader);
        thanksPanel.Children.Add(thanksCardScroller);

        var aboutScrollViewer = new ScrollViewer
        {
            Content = aboutPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 380,
        };
        var thanksScrollViewer = new ScrollViewer
        {
            Content = thanksPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = 380,
        };

        var contentHost = new ContentControl
        {
            Height = 380,
            Content = aboutScrollViewer,
        };

        var selectorBar = new SelectorBar
        {
            Margin = new Thickness(0, 0, 0, 8),
        };
        selectorBar.Items.Add(new SelectorBarItem { Text = L("about.tab.description") });
        selectorBar.Items.Add(new SelectorBarItem { Text = L("about.tab.thanks") });
        selectorBar.SelectionChanged += (_, _) =>
        {
            int selectedIndex = selectorBar.Items.IndexOf(selectorBar.SelectedItem);
            contentHost.Content = selectedIndex == 1 ? thanksScrollViewer : aboutScrollViewer;
        };
        selectorBar.SelectedItem = selectorBar.Items[0];

        var panel = new StackPanel
        {
            Spacing = 8,
            Width = 680,
        };
        panel.Children.Add(selectorBar);
        panel.Children.Add(contentHost);

        var dialog = new ContentDialog
        {
            Title = L("about.dialog_title"),
            Content = panel,
            PrimaryButtonText = L("common.ok"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1040.0;
        await dialog.ShowAsync();
    }

    private void SyncThemeMenuChecks(string mode)
    {
        MenuThemeSystem.IsChecked = mode == "System";
        MenuThemeLight.IsChecked = mode == "Light";
        MenuThemeDark.IsChecked = mode == "Dark";
    }

    // ═══════════════════════════════════════════════════════════
    //  工具栏（重绘模式）
}
