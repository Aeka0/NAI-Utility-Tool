using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using NAITool.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NAITool;

public sealed partial class MainWindow
{
    private const int OobePageCount = 5;
    private const int OobeSlideOutMilliseconds = 90;
    private const int OobeSlideInMilliseconds = 180;
    private const double OobeSlideOffset = 76;

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
            string selectedLanguageCode = LocalizationService.NormalizeLanguageCode(_settings.Settings.LanguageCode);

            var pageSlideTransform = new TranslateTransform();
            var apiTokenBox = new PasswordBox
            {
                Password = _settings.Settings.ApiToken ?? "",
                PlaceholderText = "Bearer Token",
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var reversePathBox = new TextBox
            {
                Text = _settings.Settings.ReverseTagger.ModelPath ?? "",
                PlaceholderText = L("oobe.reverse.path_placeholder"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var pageHost = new ContentControl
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                RenderTransform = pageSlideTransform,
            };

            ContentDialog dialog = null!;
            Button backButton = null!;
            Button nextButton = null!;

            TextBlock CreateTitle(string text) => new()
            {
                Text = text,
                FontSize = 24,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            };

            TextBlock CreateBodyText(string text) => new()
            {
                Text = text,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(root.ActualTheme == ElementTheme.Dark
                    ? Windows.UI.Color.FromArgb(255, 196, 196, 196)
                    : Windows.UI.Color.FromArgb(255, 92, 92, 92)),
            };

            StackPanel CreatePage(params UIElement[] children)
            {
                var panel = new StackPanel
                {
                    Spacing = 14,
                    Padding = new Thickness(4, 2, 4, 2),
                    Width = 620,
                };

                foreach (var child in children)
                    panel.Children.Add(child);

                ApplyUiFontToVisualTree(panel);
                panel.Language = UiLanguageTag;
                return panel;
            }

            Grid CreateStepHeader()
            {
                var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 0, 0, 4) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var progress = new ProgressBar
                {
                    Minimum = 1,
                    Maximum = OobePageCount,
                    Value = pageIndex + 1,
                    Height = 4,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var label = new TextBlock
                {
                    Text = Lf("oobe.step", pageIndex + 1, OobePageCount),
                    FontSize = 12,
                    Opacity = 0.72,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(progress, 0);
                Grid.SetColumn(label, 1);
                grid.Children.Add(progress);
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

                return CreatePage(
                    CreateStepHeader(),
                    CreateTitle(L("oobe.language.title")),
                    CreateBodyText(Lf(
                        "oobe.language.description",
                        _loc.GetLanguageDisplayName(selectedLanguageCode))),
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

                var imagePath = Path.Combine(AppRootDir, "assets", "img", "MaidAeka.png");
                var image = new Image
                {
                    Width = 300,
                    Height = 300,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 0),
                };
                if (File.Exists(imagePath))
                    image.Source = new BitmapImage(new Uri(imagePath));

                return CreatePage(
                    CreateStepHeader(),
                    CreateTitle(L("oobe.welcome.title")),
                    versionText,
                    image);
            }

            UIElement BuildApiPage()
            {
                var helpButton = new Button
                {
                    Content = L("oobe.api.find_key"),
                    HorizontalAlignment = HorizontalAlignment.Left,
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

                return CreatePage(
                    CreateStepHeader(),
                    CreateTitle(L("oobe.api.title")),
                    CreateBodyText(L("oobe.api.description")),
                    apiTokenBox,
                    helpButton,
                    CreateBodyText(L("oobe.api.skip_hint")));
            }

            UIElement BuildReversePage()
            {
                var browseButton = new Button { Content = L("common.choose_folder") };
                browseButton.Click += async (_, _) =>
                {
                    var picker = new FolderPicker();
                    picker.FileTypeFilter.Add("*");
                    InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                    var folder = await picker.PickSingleFolderAsync();
                    if (folder != null)
                        reversePathBox.Text = folder.Path;
                };

                var pathRow = new Grid { ColumnSpacing = 8 };
                pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(reversePathBox, 0);
                Grid.SetColumn(browseButton, 1);
                pathRow.Children.Add(reversePathBox);
                pathRow.Children.Add(browseButton);

                return CreatePage(
                    CreateStepHeader(),
                    CreateTitle(L("oobe.reverse.title")),
                    CreateBodyText(L("oobe.reverse.description")),
                    pathRow,
                    CreateBodyText(L("oobe.reverse.skip_hint")));
            }

            UIElement BuildDonePage()
            {
                var icon = new FontIcon
                {
                    FontFamily = SymbolFontFamily,
                    Glyph = "\uE930",
                    FontSize = 44,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 2, 0, 2),
                };

                return CreatePage(
                    CreateStepHeader(),
                    icon,
                    CreateTitle(L("oobe.done.title")),
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

                dialog.Title = isStartup ? L("oobe.dialog.title") : L("oobe.dialog.quick_tour_title");
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

                string modelPath = reversePathBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(modelPath) || Directory.Exists(modelPath))
                    return true;

                TxtStatus.Text = L("settings.reverse.model_path_not_found");
                return false;
            }

            void SaveOobeSettings()
            {
                _settings.Settings.LanguageCode = selectedLanguageCode;
                _settings.Settings.ApiToken = apiTokenBox.Password.Trim();
                _settings.Settings.ReverseTagger.ModelPath = reversePathBox.Text.Trim();
                _settings.Save();

                UpdateBtnGenerateForApiKey();
                UpdateGenerateButtonWarning();
                UpdateDynamicMenuStates();
                ApplyLanguageSelectionChecks();
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

            var contentRoot = new Grid { RowSpacing = 18 };
            contentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var footer = new Grid { ColumnSpacing = 8 };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            backButton = new Button
            {
                MinWidth = 96,
            };
            nextButton = new Button
            {
                MinWidth = 96,
            };
            if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var styleObj) && styleObj is Style accentStyle)
                nextButton.Style = accentStyle;

            Grid.SetColumn(backButton, 0);
            Grid.SetColumn(nextButton, 2);
            footer.Children.Add(backButton);
            footer.Children.Add(nextButton);

            Grid.SetRow(pageHost, 0);
            Grid.SetRow(footer, 1);
            contentRoot.Children.Add(pageHost);
            contentRoot.Children.Add(footer);
            ApplyUiFontToVisualTree(contentRoot);
            contentRoot.Language = UiLanguageTag;

            dialog = new ContentDialog
            {
                Title = L("oobe.dialog.title"),
                Content = contentRoot,
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = root.RequestedTheme,
            };
            dialog.Resources["ContentDialogMaxWidth"] = 780.0;
            dialog.Resources["ContentDialogMaxHeight"] = 720.0;

            nextButton.Click += async (_, _) =>
            {
                if (pageIndex < OobePageCount - 1)
                {
                    await MoveOobePageAsync(1);
                    return;
                }

                if (transitionInProgress)
                    return;

                if (!ValidateCurrentPage())
                    return;

                transitionInProgress = true;
                UpdateNavigationButtonState();
                try
                {
                    SaveOobeSettings();
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
                await MoveOobePageAsync(-1);
            };

            RefreshDialog();
            await dialog.ShowAsync();
        }
        finally
        {
            _oobeDialogOpen = false;
        }
    }

    private static string GetAppVersionText()
    {
        string? informationalVersion = typeof(MainWindow)
            .Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
            return informationalVersion.Split('+')[0];

        return typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }
}
