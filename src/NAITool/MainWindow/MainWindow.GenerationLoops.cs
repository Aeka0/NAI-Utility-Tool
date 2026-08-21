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
    private const double ContinuousGenerationFlyoutContentWidth = 260;

    private bool IsAnyGenerateLoopRunning() => _autoGenRunning || _continuousGenRunning;

    private void SetupGenerateButtonContextFlyout()
    {
        var title = new TextBlock
        {
            Text = L("generate.continuous.title"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            MaxWidth = ContinuousGenerationFlyoutContentWidth,
            TextWrapping = TextWrapping.Wrap,
        };
        var countRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        var hintText = new TextBlock
        {
            Text = L("generate.continuous.hint"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = ContinuousGenerationFlyoutContentWidth,
            Opacity = 0.72,
            FontSize = 12,
        };

        var buttons = new List<Button>();
        Style? normalButtonStyle = null;
        Style? accentButtonStyle = Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accentStyleObj)
            ? accentStyleObj as Style
            : null;
        Flyout? flyout = null;
        for (int i = 1; i <= 6; i++)
        {
            int count = i;
            var button = new Button
            {
                Content = count.ToString(),
                MinWidth = 34,
                Padding = new Thickness(10, 4, 10, 4),
                Tag = count,
            };
            normalButtonStyle ??= button.Style;
            button.Click += (_, _) =>
            {
                flyout?.Hide();
                StartContinuousGeneration(count);
            };
            buttons.Add(button);
            countRow.Children.Add(button);
        }

        var panel = new StackPanel
        {
            Width = ContinuousGenerationFlyoutContentWidth,
            Spacing = 10,
            Children =
            {
                title,
                countRow,
                hintText,
            },
        };

        flyout = new Flyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top,
            Content = new Border
            {
                Padding = new Thickness(14, 12, 14, 12),
                Child = panel,
            },
        };
        flyout.Opening += (_, _) =>
        {
            bool canStart = !_autoGenRunning &&
                            !_continuousGenRunning &&
                            _currentMode == AppMode.ImageGeneration &&
                            !string.IsNullOrWhiteSpace(_settings.Settings.ApiToken);
            foreach (var button in buttons)
                button.IsEnabled = canStart;
            hintText.Text = canStart
                ? L("generate.continuous.hint")
                : L("generate.continuous.unavailable");

            bool useAnlasStyle = canStart && EstimateCurrentRequestAnlasCost() > 0;
            foreach (var button in buttons)
            {
                if (useAnlasStyle && accentButtonStyle != null)
                {
                    button.Style = accentButtonStyle;
                    ApplyGoldAccentButtonStyle(button);
                }
                else
                {
                    ClearGoldAccentButtonStyle(button);
                    button.Style = normalButtonStyle;
                }
            }
        };
        BtnGenerate.ContextFlyout = flyout;
    }

    private void StartContinuousGeneration(int count)
    {
        if (count <= 0 || _currentMode != AppMode.ImageGeneration || IsAnyGenerateLoopRunning())
            return;

        _ = RunContinuousGenerationAsync(count);
    }

    private void StopContinuousGeneration()
    {
        if (_continuousStopRequested)
            return;
        _continuousStopRequested = true;
        TxtStatus.Text = _generateRequestRunning
            ? L("generate.loop.waiting_current_request")
            : L("generate.continuous.stopping");
        _continuousGenCts?.Cancel();
        UpdateAutoGenUI();
    }

    private async Task RefreshAnlasInfoAsync(bool forceRefresh = false)
    {
        if (TxtAnlasBalance == null || TxtV5UsagePercent == null)
            return;

        if (string.IsNullOrWhiteSpace(_settings.Settings.ApiToken))
        {
            _anlasBalance = null;
            _v5UsagePercent = null;
            _isOpusSubscriber = false;
            _hasActiveSubscription = false;
            _anlasInitialFetchDone = false;
            UpdateAnlasBalanceText();
            UpdateBtnGenerateForApiKey();
            UpdateGenerateButtonWarning();
            UpdateDynamicMenuStates();
            return;
        }

        if (_anlasInitialFetchDone && !forceRefresh)
        {
            UpdateAnlasBalanceText();
            return;
        }

        if (_anlasRefreshRunning)
        {
            _anlasRefreshPending |= forceRefresh;
            return;
        }

        do
        {
            _anlasRefreshPending = false;
            _anlasRefreshRunning = true;
            UpdateBtnGenerateForApiKey();
            try
            {
                var accountInfo = await _naiService.GetAccountInfoAsync();
                if (accountInfo != null)
                {
                    _anlasBalance = accountInfo.AnlasBalance;
                    _v5UsagePercent = accountInfo.V5UsagePercent;
                    _isOpusSubscriber = accountInfo.IsOpus;
                    _hasActiveSubscription = accountInfo.HasActiveSubscription;
                    _anlasInitialFetchDone = true;
                    _settings.UpdateCachedAccountInfo(
                        accountInfo.AnlasBalance,
                        accountInfo.V5UsagePercent,
                        accountInfo.TierName,
                        accountInfo.TierLevel,
                        accountInfo.HasActiveSubscription,
                        accountInfo.ExpiresAt);
                }
            }
            finally
            {
                _anlasRefreshRunning = false;
                UpdateAnlasBalanceText();
                UpdateBtnGenerateForApiKey();
                UpdateGenerateButtonWarning();
                UpdateDynamicMenuStates();
            }
        }
        while (_anlasRefreshPending);
    }

    private void ApplyCachedAccountInfo()
    {
        var cached = _settings.CachedApiConfig;
        if (cached.CachedAnlas.HasValue)
            _anlasBalance = cached.CachedAnlas;
        if (cached.CachedV5UsagePercent.HasValue)
            _v5UsagePercent = Math.Clamp(cached.CachedV5UsagePercent.Value, 0, 100);
        if (cached.SubscriptionTierLevel.HasValue)
            _isOpusSubscriber = cached.SubscriptionTierLevel.Value >= 3;
        if (cached.SubscriptionActive.HasValue)
            _hasActiveSubscription = cached.SubscriptionActive.Value;
    }

    private void UpdateAnlasBalanceText()
    {
        if (TxtAnlasBalance == null || TxtV5UsagePercent == null || AnlasBalanceButton == null)
            return;

        bool visible = IsPromptMode(_currentMode) &&
                       !string.IsNullOrWhiteSpace(_settings.Settings.ApiToken);
        AnlasBalanceButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
            return;

        TxtAnlasBalance.Text = _anlasBalance?.ToString("N0") ?? "--";
        TxtV5UsagePercent.Text = _v5UsagePercent.HasValue
            ? $"{Math.Clamp(_v5UsagePercent.Value, 0, 100)}%"
            : "--%";
        ToolTipService.SetToolTip(AnlasBalanceButton, L("menu.settings.quota"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            AnlasBalanceButton,
            L("menu.settings.quota"));
        UpdateQuotaSummaryFlyoutContent();
    }

    private void OnQuotaSummaryFlyout(object sender, RoutedEventArgs e)
    {
        if (AnlasBalanceButton == null)
            return;

        EnsureQuotaSummaryFlyout();
        UpdateQuotaSummaryFlyoutContent();
        _quotaSummaryFlyout!.ShowAt(AnlasBalanceButton);
    }

    private void EnsureQuotaSummaryFlyout()
    {
        if (_quotaSummaryFlyout != null)
            return;

        StackPanel CreateQuotaMeter(
            string label,
            string glyph,
            out TextBlock labelText,
            out TextBlock valueText,
            out ProgressBar progressBar,
            out TextBlock estimateText)
        {
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            header.Children.Add(new FontIcon
            {
                FontFamily = SymbolFontFamily,
                Glyph = glyph,
                FontSize = 14,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });

            labelText = new TextBlock
            {
                Text = label,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(labelText, 1);
            header.Children.Add(labelText);

            valueText = new TextBlock
            {
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(valueText, 2);
            header.Children.Add(valueText);

            progressBar = new ProgressBar
            {
                Height = 4,
                MinHeight = 4,
                MaxHeight = 4,
                IsIndeterminate = false,
            };
            estimateText = new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };

            return new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    header,
                    progressBar,
                    estimateText,
                },
            };
        }

        var content = new StackPanel
        {
            Width = 244,
            Spacing = 14,
        };
        content.Children.Add(CreateQuotaMeter(
            L("settings.quota.account.current_anlas"),
            "\uF159",
            out _quotaSummaryAnlasLabel,
            out _quotaSummaryAnlasText,
            out _quotaSummaryAnlasProgress,
            out _quotaSummaryAnlasEstimateText));
        var v5UsageMeter = CreateQuotaMeter(
            L("settings.quota.account.v5_usage"),
            "\uF156",
            out _quotaSummaryV5UsageLabel,
            out _quotaSummaryV5UsageText,
            out _quotaSummaryV5UsageProgress,
            out _quotaSummaryV5UsageEstimateText);
        _quotaSummaryV5UsageRecoveryText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        v5UsageMeter.Children.Add(_quotaSummaryV5UsageRecoveryText);
        content.Children.Add(v5UsageMeter);

        var manageButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 6,
                Children =
                {
                    new FontIcon
                    {
                        FontFamily = SymbolFontFamily,
                        Glyph = "\uF159",
                        FontSize = 14,
                    },
                    new TextBlock { Text = L("settings.quota.title") },
                },
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 2, 0, 0),
        };
        manageButton.Click += (_, args) =>
        {
            _quotaSummaryFlyout?.Hide();
            OnQuotaSettings(AnlasBalanceButton, args);
        };
        content.Children.Add(manageButton);

        _quotaSummaryFlyout = new Flyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = true,
            Content = new Border
            {
                Padding = new Thickness(14, 12, 14, 12),
                Child = content,
            },
            FlyoutPresenterStyle = CreateQuotaSummaryFlyoutPresenterStyle(),
        };
        _quotaSummaryFlyout.Opened += (_, _) =>
        {
            UpdateQuotaSummaryFlyoutContent();
            _ = RefreshAnlasInfoAsync(forceRefresh: true);
        };
    }

    private void UpdateQuotaSummaryFlyoutContent()
    {
        if (_quotaSummaryAnlasLabel == null ||
            _quotaSummaryAnlasText == null ||
            _quotaSummaryAnlasProgress == null ||
            _quotaSummaryAnlasEstimateText == null ||
            _quotaSummaryV5UsageLabel == null ||
            _quotaSummaryV5UsageText == null ||
            _quotaSummaryV5UsageProgress == null ||
            _quotaSummaryV5UsageEstimateText == null ||
            _quotaSummaryV5UsageRecoveryText == null)
            return;

        int? anlas = _anlasBalance ?? _settings.CachedApiConfig.CachedAnlas;
        int? v5Usage = _v5UsagePercent ?? _settings.CachedApiConfig.CachedV5UsagePercent;
        bool isDark = this.Content is FrameworkElement root && root.ActualTheme == ElementTheme.Dark;
        var summaryTextBrush = new SolidColorBrush(isDark
            ? Windows.UI.Color.FromArgb(255, 245, 245, 245)
            : Windows.UI.Color.FromArgb(255, 32, 32, 32));

        _quotaSummaryAnlasLabel.Text = L("settings.quota.account.current_anlas");
        _quotaSummaryAnlasText.Text = anlas?.ToString("N0") ?? "--";
        _quotaSummaryAnlasProgress.Maximum = 10_000;
        _quotaSummaryAnlasProgress.Value = anlas.HasValue
            ? Math.Clamp(anlas.Value, 0, 10_000)
            : 0;
        _quotaSummaryAnlasProgress.Opacity = anlas.HasValue ? 1 : 0.45;
        int currentAnlasCost = EstimateCurrentRequestAnlasCost();
        string anlasImageCount = currentAnlasCost == 0
            ? "∞"
            : anlas.HasValue
                ? Math.Max(0, anlas.Value / currentAnlasCost).ToString("N0")
                : "--";
        _quotaSummaryAnlasEstimateText.Text = Lf("settings.quota.summary.anlas_estimate", anlasImageCount);

        _quotaSummaryV5UsageLabel.Text = L("settings.quota.account.v5_usage");
        _quotaSummaryV5UsageText.Text = v5Usage.HasValue
            ? $"{Math.Clamp(v5Usage.Value, 0, 100)}%"
            : "--%";
        _quotaSummaryV5UsageProgress.Maximum = 100;
        _quotaSummaryV5UsageProgress.Value = v5Usage.HasValue
            ? Math.Clamp(v5Usage.Value, 0, 100)
            : 0;
        _quotaSummaryV5UsageProgress.Opacity = v5Usage.HasValue ? 1 : 0.45;
        string v5ImageCount = v5Usage.HasValue
            ? Math.Floor(1_730d * Math.Clamp(v5Usage.Value, 0, 100) / 100).ToString("N0")
            : "--";
        _quotaSummaryV5UsageEstimateText.Text = Lf("settings.quota.summary.v5_estimate", v5ImageCount);
        _quotaSummaryV5UsageRecoveryText.Text = v5Usage.HasValue && v5Usage.Value >= 100
            ? L("settings.quota.summary.v5_full")
            : v5Usage.HasValue
                ? FormatV5UsageRecoveryTime(v5Usage.Value)
                : Lf("settings.quota.summary.v5_recovery", "--", "--");

        _quotaSummaryAnlasLabel.Foreground = summaryTextBrush;
        _quotaSummaryAnlasText.Foreground = summaryTextBrush;
        _quotaSummaryAnlasEstimateText.Foreground = summaryTextBrush;
        _quotaSummaryV5UsageLabel.Foreground = summaryTextBrush;
        _quotaSummaryV5UsageText.Foreground = summaryTextBrush;
        _quotaSummaryV5UsageEstimateText.Foreground = summaryTextBrush;
        _quotaSummaryV5UsageRecoveryText.Foreground = summaryTextBrush;
    }

    private string FormatV5UsageRecoveryTime(int usagePercent)
    {
        int remainingPercentagePoints = 100 - Math.Clamp(usagePercent, 0, 100);
        int remainingMinutes = (int)Math.Ceiling(remainingPercentagePoints * 24d * 60d / 11d);
        int hours = remainingMinutes / 60;
        int minutes = remainingMinutes % 60;
        return Lf("settings.quota.summary.v5_recovery", hours, minutes);
    }

    private static Style CreateQuotaSummaryFlyoutPresenterStyle()
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        return style;
    }
}
