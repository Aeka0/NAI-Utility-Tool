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
        if (TxtAnlasBalance == null)
            return;

        if (string.IsNullOrWhiteSpace(_settings.Settings.ApiToken))
        {
            _anlasBalance = null;
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
            return;

        _anlasRefreshRunning = true;
        UpdateBtnGenerateForApiKey();
        try
        {
            var accountInfo = await _naiService.GetAccountInfoAsync();
            _anlasBalance = accountInfo?.AnlasBalance;
            _isOpusSubscriber = accountInfo?.IsOpus == true;
            _hasActiveSubscription = accountInfo?.HasActiveSubscription == true;
            _anlasInitialFetchDone = true;

            if (accountInfo != null)
            {
                _settings.UpdateCachedAccountInfo(
                    accountInfo.AnlasBalance,
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

    private void ApplyCachedAccountInfo()
    {
        var cached = _settings.CachedApiConfig;
        if (cached.CachedAnlas.HasValue)
            _anlasBalance = cached.CachedAnlas;
        if (cached.SubscriptionTierLevel.HasValue)
            _isOpusSubscriber = cached.SubscriptionTierLevel.Value >= 3;
        if (cached.SubscriptionActive.HasValue)
            _hasActiveSubscription = cached.SubscriptionActive.Value;
    }

    private void UpdateAnlasBalanceText()
    {
        if (TxtAnlasBalance == null)
            return;

        bool visible = IsPromptMode(_currentMode) &&
                       !string.IsNullOrWhiteSpace(_settings.Settings.ApiToken);
        TxtAnlasBalance.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
            return;

        TxtAnlasBalance.Text = _anlasBalance.HasValue
            ? $"Anlas: {_anlasBalance.Value:N0}"
            : "Anlas: --";
    }
}
