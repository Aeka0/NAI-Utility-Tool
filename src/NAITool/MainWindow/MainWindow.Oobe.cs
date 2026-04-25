using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using NAITool.Services;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace NAITool;

public sealed partial class MainWindow
{
    private const int OobePageCount = 5;
    private const int OobeSlideOutMilliseconds = 90;
    private const int OobeSlideInMilliseconds = 180;
    private const double OobeSlideOffset = 76;
    private const double OobeDialogContentWidth = 900;
    private const double OobeDialogContentHeight = 548;
    private const double OobeDialogChromeWidth = 960;
    private const double OobeVisualColumnWidth = 306;
    private static readonly Dictionary<string, BitmapImage> OobeImageCache = new();

    private void QueueStartupOobe()
    {
        if (!_showOobeOnStartup)
            return;

        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(250);
            await ShowOobeDialogAsync(isStartup: true);
        });
    }

    private async void OnQuickTour(object sender, RoutedEventArgs e)
        => await ShowOobeDialogAsync(isStartup: false);

    private static async Task LoadEmbeddedOobeImageAsync(Image target, string imageName)
    {
        if (OobeImageCache.TryGetValue(imageName, out var cachedImage))
        {
            target.Source = cachedImage;
            return;
        }

        string resourceName = $"NAITool.oobe.{imageName}";
        await using var stream = typeof(MainWindow)
            .Assembly
            .GetManifestResourceStream(resourceName);
        if (stream == null)
            return;

        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer);
            bytes = buffer.ToArray();
        }

        using var memory = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(memory.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        memory.Seek(0);

        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(memory);
        OobeImageCache[imageName] = bitmap;
        target.Source = bitmap;
    }

    private async Task ShowOobeDialogAsync(bool isStartup)
    {
        if (_oobeDialogOpen || this.Content?.XamlRoot == null)
            return;

        _oobeDialogOpen = true;
        try
        {
            var root = (FrameworkElement)this.Content;
            int pageIndex = 0;
            bool rebuilding = false;
            bool transitionInProgress = false;
            Task pendingNavigation = Task.CompletedTask;
            string selectedLanguageCode = LocalizationService.NormalizeLanguageCode(_settings.Settings.LanguageCode);
            string detectedLanguageDisplayName = _loc.GetLanguageDisplayName(selectedLanguageCode);

            var pageSlideTransform = new TranslateTransform();
            string apiTokenValue = _settings.Settings.ApiToken ?? "";
            string reversePathValue = _settings.Settings.ReverseTagger.ModelPath ?? "";
            bool assetProtectionModeValue = _settings.Settings.AccountAssetProtectionMode;
            CancellationTokenSource? apiTokenTestCts = null;
            int apiTokenTestSerial = 0;

            var pageHost = new ContentControl
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                RenderTransform = pageSlideTransform,
            };

            ContentDialog dialog = null!;
            Button backButton = null!;
            Button nextButton = null!;

            string GetDialogDisplayTitle()
                => isStartup ? L("oobe.dialog.title") : L("oobe.dialog.quick_tour_title");

            bool isDarkTheme() => root.ActualTheme == ElementTheme.Dark;

            Windows.UI.Color AccentColor()
            {
                if (Application.Current.Resources.TryGetValue("SystemAccentColor", out var accentObj) &&
                    accentObj is Windows.UI.Color accent)
                {
                    return accent;
                }

                return Windows.UI.Color.FromArgb(255, 76, 116, 221);
            }

            SolidColorBrush Solid(byte a, byte r, byte g, byte b)
                => new(Windows.UI.Color.FromArgb(a, r, g, b));

            SolidColorBrush TextSecondaryBrush() => isDarkTheme()
                ? Solid(255, 203, 207, 214)
                : Solid(255, 82, 87, 96);

            SolidColorBrush TextTertiaryBrush() => isDarkTheme()
                ? Solid(255, 154, 160, 171)
                : Solid(255, 112, 118, 130);

            SolidColorBrush SubtleBorderBrush() => isDarkTheme()
                ? Solid(70, 255, 255, 255)
                : Solid(64, 30, 41, 59);

            SolidColorBrush SubtleSurfaceBrush() => isDarkTheme()
                ? Solid(34, 255, 255, 255)
                : Solid(170, 255, 255, 255);

            SolidColorBrush SuccessBrush() => isDarkTheme()
                ? Solid(255, 116, 211, 151)
                : Solid(255, 16, 124, 64);

            SolidColorBrush ErrorBrush() => isDarkTheme()
                ? Solid(255, 255, 135, 135)
                : Solid(255, 184, 40, 40);

            LinearGradientBrush CreateVisualPanelBrush()
            {
                var stops = isDarkTheme()
                    ? new GradientStopCollection
                    {
                        new GradientStop { Color = Windows.UI.Color.FromArgb(255, 31, 38, 56), Offset = 0.0 },
                        new GradientStop { Color = Windows.UI.Color.FromArgb(255, 54, 44, 72), Offset = 0.55 },
                        new GradientStop { Color = Windows.UI.Color.FromArgb(255, 28, 53, 62), Offset = 1.0 },
                    }
                    : new GradientStopCollection
                    {
                        new GradientStop { Color = Windows.UI.Color.FromArgb(255, 245, 249, 255), Offset = 0.0 },
                        new GradientStop { Color = Windows.UI.Color.FromArgb(255, 239, 242, 252), Offset = 0.48 },
                        new GradientStop { Color = Windows.UI.Color.FromArgb(255, 235, 249, 246), Offset = 1.0 },
                    };

                return new LinearGradientBrush(stops, 0.0)
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 1),
                };
            }

            UIElement CreateTitle(string text, string glyph)
            {
                var panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };

                var icon = new TextBlock
                {
                    Text = glyph,
                    FontFamily = SymbolFontFamily,
                    FontSize = 18,
                    Foreground = new SolidColorBrush(AccentColor()),
                    Margin = new Thickness(-1, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var title = new TextBlock
                {
                    Text = text,
                    FontSize = 28,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 470,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                panel.Children.Add(icon);
                panel.Children.Add(title);
                return panel;
            }

            TextBlock CreateBodyText(string text) => new()
            {
                Text = text,
                FontSize = 14,
                LineHeight = 21,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TextSecondaryBrush(),
            };

            UIElement CreateRaisedLayer(params UIElement[] children)
            {
                var panel = new StackPanel
                {
                    Spacing = 10,
                };
                foreach (var child in children)
                    panel.Children.Add(child);

                return new Border
                {
                    Background = SubtleSurfaceBrush(),
                    BorderBrush = SubtleBorderBrush(),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14, 12, 14, 12),
                    Child = panel,
                };
            }

            UIElement CreateExternalLinkRow(string label, string url)
            {
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 2,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                row.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = 14,
                    Foreground = TextSecondaryBrush(),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                row.Children.Add(new HyperlinkButton
                {
                    NavigateUri = new Uri(url),
                    Padding = new Thickness(0),
                    MinWidth = 0,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Content = new TextBlock
                    {
                        Text = url,
                        FontSize = 14,
                        TextDecorations = Windows.UI.Text.TextDecorations.Underline,
                        Foreground = new SolidColorBrush(AccentColor()),
                    },
                });
                return row;
            }

            UIElement CreatePageLayout(string imageName, params UIElement[] contentChildren)
            {
                var pageGrid = new Grid { RowSpacing = 18 };
                pageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                pageGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                pageGrid.Children.Add(CreateStepHeader());

                var twoCol = new Grid { ColumnSpacing = 28 };
                twoCol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(OobeVisualColumnWidth) });
                twoCol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var image = new Image
                {
                    Stretch = Stretch.Uniform,
                    MaxHeight = 330,
                    MaxWidth = 254,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                _ = LoadEmbeddedOobeImageAsync(image, imageName);

                var visualGrid = new Grid();
                visualGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                visualGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                visualGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var brandRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                brandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                brandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                brandRow.Children.Add(new TextBlock
                {
                    Text = "NAI Utility Tool",
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = TextTertiaryBrush(),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var stepBadge = new Border
                {
                    Background = SubtleSurfaceBrush(),
                    BorderBrush = SubtleBorderBrush(),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(10, 4, 10, 4),
                    Child = new TextBlock
                    {
                        Text = Lf("oobe.step", pageIndex + 1, OobePageCount),
                        FontSize = 12,
                        Foreground = TextSecondaryBrush(),
                    },
                };
                Grid.SetColumn(stepBadge, 1);
                brandRow.Children.Add(stepBadge);

                var imageFrame = new Grid { VerticalAlignment = VerticalAlignment.Stretch };
                imageFrame.Children.Add(image);

                var visualCaption = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Thickness(0, 14, 0, 0),
                    VerticalAlignment = VerticalAlignment.Bottom,
                };
                visualCaption.Children.Add(new FontIcon
                {
                    FontFamily = SymbolFontFamily,
                    Glyph = "\uE946",
                    FontSize = 13,
                    Foreground = TextTertiaryBrush(),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                visualCaption.Children.Add(new TextBlock
                {
                    Text = GetDialogDisplayTitle(),
                    FontSize = 12,
                    Foreground = TextTertiaryBrush(),
                    TextWrapping = TextWrapping.NoWrap,
                    VerticalAlignment = VerticalAlignment.Center,
                });

                Grid.SetRow(brandRow, 0);
                Grid.SetRow(imageFrame, 1);
                Grid.SetRow(visualCaption, 2);
                visualGrid.Children.Add(brandRow);
                visualGrid.Children.Add(imageFrame);
                visualGrid.Children.Add(visualCaption);

                var imageHost = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(18),
                    Background = CreateVisualPanelBrush(),
                    BorderBrush = SubtleBorderBrush(),
                    BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Child = visualGrid,
                };
                Grid.SetColumn(imageHost, 0);
                twoCol.Children.Add(imageHost);

                var contentPanel = new StackPanel
                {
                    Spacing = 16,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(0, 16, 6, 16),
                };
                foreach (var child in contentChildren)
                    contentPanel.Children.Add(child);
                Grid.SetColumn(contentPanel, 1);
                twoCol.Children.Add(contentPanel);

                Grid.SetRow(twoCol, 1);
                pageGrid.Children.Add(twoCol);
                ApplyUiFontToVisualTree(pageGrid);
                pageGrid.Language = UiLanguageTag;
                return pageGrid;
            }

            Grid CreateStepHeader()
            {
                var grid = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 0, 0, 2) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var steps = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 7,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var accent = AccentColor();
                for (int i = 0; i < OobePageCount; i++)
                {
                    steps.Children.Add(new Border
                    {
                        Width = i == pageIndex ? 34 : 9,
                        Height = 9,
                        CornerRadius = new CornerRadius(5),
                        Background = i <= pageIndex
                            ? new SolidColorBrush(Windows.UI.Color.FromArgb(i == pageIndex ? (byte)255 : (byte)132, accent.R, accent.G, accent.B))
                            : (isDarkTheme() ? Solid(70, 255, 255, 255) : Solid(70, 50, 61, 78)),
                    });
                }

                var label = new TextBlock
                {
                    Text = Lf("oobe.step", pageIndex + 1, OobePageCount),
                    FontSize = 12,
                    Foreground = TextTertiaryBrush(),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(steps, 0);
                Grid.SetColumn(label, 1);
                grid.Children.Add(steps);
                grid.Children.Add(label);
                return grid;
            }

            UIElement BuildLanguagePage()
            {
                var languageBox = new ComboBox
                {
                    Width = 260,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };

                foreach (var language in LocalizationService.SupportedLanguages)
                {
                    languageBox.Items.Add(new ComboBoxItem
                    {
                        Content = _loc.GetLanguageDisplayName(language.Code),
                        Tag = language.Code,
                    });
                }

                int selectedIndex = LocalizationService.SupportedLanguages
                    .Select((language, index) => new { language.Code, index })
                    .FirstOrDefault(x => string.Equals(x.Code, selectedLanguageCode, StringComparison.OrdinalIgnoreCase))
                    ?.index ?? 0;
                languageBox.SelectedIndex = selectedIndex;
                languageBox.SelectionChanged += (_, _) =>
                {
                    if (rebuilding || languageBox.SelectedItem is not ComboBoxItem item || item.Tag is not string code)
                        return;

                    selectedLanguageCode = LocalizationService.NormalizeLanguageCode(code);
                    _settings.Settings.LanguageCode = selectedLanguageCode;
                    _loc.SetLanguage(selectedLanguageCode);
                    ApplyLanguageSelectionChecks();
                    RefreshDialog();
                };

                return CreatePageLayout("MaidAekaLang.png",
                    CreateTitle(L("oobe.language.title"), "\uF2B7"),
                    CreateBodyText(Lf(
                        "oobe.language.description",
                        detectedLanguageDisplayName)),
                    languageBox);
            }

            UIElement BuildWelcomePage()
            {
                var versionText = new TextBlock
                {
                    Text = Lf("oobe.welcome.version", GetAppVersionText()),
                    FontSize = 14,
                    Opacity = 0.76,
                };

                return CreatePageLayout("MaidAeka.png",
                    CreateTitle(L("oobe.welcome.title"), "\uE734"),
                    versionText,
                    CreateExternalLinkRow(
                        L("oobe.welcome.github_label"),
                        "https://github.com/Aeka0/NAI-Utility-Tool"));
            }

            UIElement BuildApiPage()
            {
                async Task ScheduleApiTokenTestAsync(TextBlock statusBlock, int delayMilliseconds)
                {
                    int serial = ++apiTokenTestSerial;
                    apiTokenTestCts?.Cancel();
                    apiTokenTestCts?.Dispose();
                    apiTokenTestCts = new CancellationTokenSource();
                    var ct = apiTokenTestCts.Token;

                    string tokenToTest = apiTokenValue.Trim();
                    if (string.IsNullOrWhiteSpace(tokenToTest))
                    {
                        statusBlock.Text = L("oobe.api.test.empty");
                        statusBlock.Foreground = TextTertiaryBrush();
                        return;
                    }

                    statusBlock.Text = L("oobe.api.test.testing");
                    statusBlock.Foreground = TextTertiaryBrush();

                    try
                    {
                        if (delayMilliseconds > 0)
                            await Task.Delay(delayMilliseconds, ct);

                        NovelAiAccountInfo? accountInfo = null;
                        _anlasRefreshRunning = true;
                        _anlasInitialFetchDone = false;
                        try
                        {
                            accountInfo = await _naiService.GetAccountInfoAsync(tokenToTest, ct);
                        }
                        finally
                        {
                            _anlasRefreshRunning = false;
                        }

                        if (ct.IsCancellationRequested || serial != apiTokenTestSerial)
                            return;

                        if (accountInfo != null)
                            ApplyAccountInfo(accountInfo, save: false);

                        statusBlock.Text = accountInfo != null
                            ? L("oobe.api.test.valid")
                            : L("oobe.api.test.invalid");
                        statusBlock.Foreground = accountInfo != null ? SuccessBrush() : ErrorBrush();
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                var tokenBox = new PasswordBox
                {
                    Password = apiTokenValue,
                    PlaceholderText = "Bearer Token",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                var tokenStatus = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(apiTokenValue) ? L("oobe.api.test.empty") : "",
                    FontSize = 12,
                    MinHeight = 18,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = TextTertiaryBrush(),
                };
                tokenBox.PasswordChanged += (_, _) =>
                {
                    apiTokenValue = tokenBox.Password;
                    _ = ScheduleApiTokenTestAsync(tokenStatus, 1000);
                };
                tokenBox.LostFocus += (_, _) => _ = ScheduleApiTokenTestAsync(tokenStatus, 0);

                var assetProtectionToggle = CreateLocalizedToggleSwitch(assetProtectionModeValue, 120);
                assetProtectionToggle.Header = L("oobe.asset_protection.title");
                assetProtectionToggle.HorizontalAlignment = HorizontalAlignment.Left;
                assetProtectionToggle.Toggled += (_, _) => assetProtectionModeValue = assetProtectionToggle.IsOn;

                var helpButton = new HyperlinkButton
                {
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Content = new TextBlock
                    {
                        Text = L("oobe.api.find_key"),
                        FontSize = 14,
                        TextDecorations = Windows.UI.Text.TextDecorations.Underline,
                        Foreground = new SolidColorBrush(AccentColor()),
                    },
                };
                FlyoutBase.SetAttachedFlyout(helpButton, new Flyout
                {
                    Content = new TextBlock
                    {
                        Text = L("oobe.api.find_key_steps"),
                        TextWrapping = TextWrapping.Wrap,
                        Width = 360,
                    },
                    Placement = FlyoutPlacementMode.Bottom,
                });
                helpButton.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(helpButton);

                return CreatePageLayout("MaidAekaAPI.png",
                    CreateTitle(L("oobe.api.title"), "\uE8D7"),
                    CreateRaisedLayer(
                        tokenBox,
                        tokenStatus),
                    CreateRaisedLayer(
                        assetProtectionToggle,
                        CreateBodyText(L("oobe.asset_protection.description"))),
                    helpButton,
                    CreateBodyText(L("oobe.api.skip_hint")));
            }

            UIElement BuildReversePage()
            {
                var pathBox = new TextBox
                {
                    Text = reversePathValue,
                    PlaceholderText = L("oobe.reverse.path_placeholder"),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                pathBox.TextChanged += (_, _) => reversePathValue = pathBox.Text;

                var browseButton = new Button { Content = L("common.choose_folder") };
                browseButton.Click += async (_, _) =>
                {
                    var picker = new FolderPicker();
                    picker.FileTypeFilter.Add("*");
                    InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                    var folder = await picker.PickSingleFolderAsync();
                    if (folder != null)
                        pathBox.Text = folder.Path;
                };

                var pathRow = new Grid { ColumnSpacing = 8 };
                pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(pathBox, 0);
                Grid.SetColumn(browseButton, 1);
                pathRow.Children.Add(pathBox);
                pathRow.Children.Add(browseButton);

                return CreatePageLayout("MaidAekaModel.png",
                    CreateTitle(L("oobe.reverse.title"), "\uE8EC"),
                    CreateBodyText(L("oobe.reverse.description")),
                    pathRow,
                    CreateBodyText(L("oobe.reverse.skip_hint")),
                    CreateExternalLinkRow(
                        L("oobe.reverse.model_link_label"),
                        "https://huggingface.co/deepghs/pixai-tagger-v0.9-onnx"));
            }

            UIElement BuildDonePage()
            {
                return CreatePageLayout("MaidAekaHappy.png",
                    CreateTitle(L("oobe.done.title"), "\uE8E1"),
                    CreateBodyText(L("oobe.done.description")));
            }

            UIElement BuildCurrentPage() => pageIndex switch
            {
                0 => BuildLanguagePage(),
                1 => BuildWelcomePage(),
                2 => BuildApiPage(),
                3 => BuildReversePage(),
                _ => BuildDonePage(),
            };

            void UpdateNavigationButtonState()
            {
                nextButton.IsEnabled = !transitionInProgress;
                backButton.IsEnabled = !transitionInProgress && pageIndex > 0;
            }

            void UpdateDialogChrome()
            {
                if (dialog == null)
                    return;

                dialog.Title = null;
                nextButton.Content = pageIndex == OobePageCount - 1 ? L("oobe.finish") : L("oobe.next");
                backButton.Content = L("oobe.back");
                backButton.Visibility = pageIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
                UpdateNavigationButtonState();
                dialog.RequestedTheme = root.RequestedTheme;
            }

            void RefreshDialog()
            {
                if (dialog == null)
                    return;

                rebuilding = true;
                UpdateDialogChrome();
                pageHost.Content = BuildCurrentPage();
                pageHost.Opacity = 1;
                pageSlideTransform.X = 0;
                rebuilding = false;
            }

            bool ValidateCurrentPage()
            {
                if (pageIndex != 3)
                    return true;

                string modelPath = reversePathValue.Trim();
                if (string.IsNullOrWhiteSpace(modelPath) || Directory.Exists(modelPath))
                    return true;

                TxtStatus.Text = L("settings.reverse.model_path_not_found");
                return false;
            }

            async Task SaveOobeSettingsAsync()
            {
                _settings.Settings.LanguageCode = selectedLanguageCode;
                _settings.Settings.ApiToken = apiTokenValue.Trim();
                _settings.Settings.ReverseTagger.ModelPath = reversePathValue.Trim();
                bool oldSizeLimitEnabled = IsAssetProtectionSizeLimitEnabled();
                bool oldStepLimitEnabled = IsAssetProtectionStepLimitEnabled();
                bool oldPaidLimitEnabled = IsAssetProtectionPaidFeatureLimitEnabled();
                _settings.Settings.AccountAssetProtectionMode = assetProtectionModeValue;
                _settings.Save();

                UpdateBtnGenerateForApiKey();
                UpdateGenerateButtonWarning();
                UpdateDynamicMenuStates();
                ApplyLanguageSelectionChecks();
                bool hasProtectionBehaviorChange =
                    oldSizeLimitEnabled != IsAssetProtectionSizeLimitEnabled() ||
                    oldStepLimitEnabled != IsAssetProtectionStepLimitEnabled() ||
                    oldPaidLimitEnabled != IsAssetProtectionPaidFeatureLimitEnabled();

                MaskCanvas.UseAssetProtectionCanvasSizing = IsAssetProtectionSizeLimitEnabled();
                if (hasProtectionBehaviorChange)
                {
                    RefreshSizeComboBox();
                    RefreshPromptModeUiForAccountModeChange();
                }

                if (string.IsNullOrWhiteSpace(_settings.Settings.ApiToken))
                {
                    ClearAccountApiState(save: true);
                }
                else
                {
                    TxtStatus.Text = L("settings.network.testing");
                    await ValidateSavedApiTokenAsync();
                }

                TxtStatus.Text = L("oobe.status.completed");
            }

            DoubleAnimation CreatePageAnimation(DependencyObject target, string property, double from, double to, int milliseconds)
            {
                var animation = new DoubleAnimation
                {
                    From = from,
                    To = to,
                    Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                    EnableDependentAnimation = true,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(animation, target);
                Storyboard.SetTargetProperty(animation, property);
                return animation;
            }

            async Task RunPageAnimationAsync(double fromX, double toX, double fromOpacity, double toOpacity, int milliseconds)
            {
                pageSlideTransform.X = fromX;
                pageHost.Opacity = fromOpacity;

                var storyboard = new Storyboard();
                storyboard.Children.Add(CreatePageAnimation(pageSlideTransform, "X", fromX, toX, milliseconds));
                storyboard.Children.Add(CreatePageAnimation(pageHost, "Opacity", fromOpacity, toOpacity, milliseconds));

                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnCompleted(object? sender, object e) => completed.TrySetResult();

                storyboard.Completed += OnCompleted;
                storyboard.Begin();
                await Task.WhenAny(completed.Task, Task.Delay(milliseconds + 140));
                storyboard.Completed -= OnCompleted;
                storyboard.Stop();

                pageSlideTransform.X = toX;
                pageHost.Opacity = toOpacity;
            }

            async Task MoveOobePageAsync(int delta)
            {
                if (transitionInProgress)
                    return;

                int targetPageIndex = pageIndex + delta;
                if (targetPageIndex < 0 || targetPageIndex >= OobePageCount)
                    return;

                if (delta > 0 && !ValidateCurrentPage())
                    return;

                transitionInProgress = true;
                UpdateNavigationButtonState();

                double outgoingX = delta > 0 ? -OobeSlideOffset : OobeSlideOffset;
                double incomingX = -outgoingX;

                try
                {
                    await RunPageAnimationAsync(0, outgoingX, 1, 0, OobeSlideOutMilliseconds);

                    rebuilding = true;
                    pageIndex = targetPageIndex;
                    UpdateDialogChrome();
                    pageHost.Content = BuildCurrentPage();
                    rebuilding = false;

                    await RunPageAnimationAsync(incomingX, 0, 0, 1, OobeSlideInMilliseconds);
                }
                finally
                {
                    rebuilding = false;
                    transitionInProgress = false;
                    pageSlideTransform.X = 0;
                    pageHost.Opacity = 1;
                    UpdateNavigationButtonState();
                }
            }

            var contentRoot = new Grid
            {
                Width = OobeDialogContentWidth,
                Height = OobeDialogContentHeight,
                RowSpacing = 16,
                Padding = new Thickness(4, 0, 4, 2),
            };
            contentRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            contentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var footer = new Grid
            {
                ColumnSpacing = 10,
                Padding = new Thickness(0, 12, 0, 0),
            };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            backButton = new Button
            {
                MinWidth = 108,
                Height = 36,
            };
            nextButton = new Button
            {
                MinWidth = 128,
                Height = 36,
            };
            if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var styleObj) && styleObj is Style accentStyle)
                nextButton.Style = accentStyle;

            Grid.SetColumn(backButton, 0);
            Grid.SetColumn(nextButton, 2);
            footer.Children.Add(backButton);
            footer.Children.Add(nextButton);

            var footerShell = new Border
            {
                BorderBrush = SubtleBorderBrush(),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = footer,
            };

            Grid.SetRow(pageHost, 0);
            Grid.SetRow(footerShell, 1);
            contentRoot.Children.Add(pageHost);
            contentRoot.Children.Add(footerShell);
            ApplyUiFontToVisualTree(contentRoot);
            contentRoot.Language = UiLanguageTag;

            dialog = new ContentDialog
            {
                Title = null,
                Content = contentRoot,
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = root.RequestedTheme,
            };
            dialog.Resources["ContentDialogMinWidth"] = OobeDialogChromeWidth;
            dialog.Resources["ContentDialogMaxWidth"] = OobeDialogChromeWidth;
            dialog.Resources["ContentDialogMaxHeight"] = 680.0;

            nextButton.Click += async (_, _) =>
            {
                if (transitionInProgress || !pendingNavigation.IsCompleted)
                    return;

                if (pageIndex < OobePageCount - 1)
                {
                    pendingNavigation = MoveOobePageAsync(1);
                    await pendingNavigation;
                    return;
                }

                if (!ValidateCurrentPage())
                    return;

                transitionInProgress = true;
                UpdateNavigationButtonState();
                try
                {
                    await SaveOobeSettingsAsync();
                    dialog.Hide();
                }
                finally
                {
                    transitionInProgress = false;
                    UpdateNavigationButtonState();
                }
            };

            backButton.Click += async (_, _) =>
            {
                if (transitionInProgress || !pendingNavigation.IsCompleted)
                    return;
                pendingNavigation = MoveOobePageAsync(-1);
                await pendingNavigation;
            };

            RefreshDialog();
            await dialog.ShowAsync();
            apiTokenTestCts?.Cancel();
            apiTokenTestCts?.Dispose();
        }
        finally
        {
            _oobeDialogOpen = false;
        }
    }

    private static string GetAppVersionText()
    {
        return "1.0.0";
    }
}
