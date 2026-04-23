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
    private async void OnAutoGenSettings(object sender, RoutedEventArgs e)
    {
        if (_autoGenRunning)
        {
            StopAutoGeneration();
            return;
        }

        await ShowAutomationDialogAsync();
    }

    private void SyncImageGenerationInputsForAutoGeneration()
    {
        if (_currentMode != AppMode.ImageGeneration)
            return;

        SaveCurrentPromptToBuffer();
        SyncUIToParams();
        if (IsAdvancedWindowOpen)
            SaveAdvancedPanelToSettings();
    }

    private async Task RunAutoGenerationAsync()
    {
        SyncImageGenerationInputsForAutoGeneration();
        _settings.Save();

        var automationSettings = GetAutomationSettings().Clone();
        automationSettings.Normalize();
        int requestLimit = automationSettings.Generation.RequestLimit;

        _autoGenRunning = true;
        _autoGenCts = new CancellationTokenSource();
        _autoGenRemaining = requestLimit > 0 ? requestLimit : 0;
        _activeAutomationSettings = automationSettings;
        _automationRunContext = CreateAutomationRunContext(automationSettings);
        UpdateAutoGenUI();
        int requestCount = 0;
        int? consecutiveErrorStatusCode = null;
        int consecutiveRetriesForCurrentError = 0;

        try
        {
            while (!_autoGenCts.IsCancellationRequested)
            {
                SyncImageGenerationInputsForAutoGeneration();
                if (_automationRunContext != null)
                    PrepareAutomationIteration(_automationRunContext);
                _settings.Save();

                bool success = await DoImageGenerationAsync();
                requestCount++;
                _autoGenRemaining = requestLimit > 0
                    ? Math.Max(0, requestLimit - requestCount)
                    : 0;
                UpdateAutoGenUI();

                if (success)
                {
                    consecutiveErrorStatusCode = null;
                    consecutiveRetriesForCurrentError = 0;
                }
                else
                {
                    int? statusCode = _lastGenerationFailureStatusCode;
                    int retryLimit = automationSettings.ErrorHandling.GetRetryLimit(statusCode);

                    if (retryLimit == 0)
                    {
                        TxtStatus.Text = statusCode.HasValue
                            ? Lf("automation.status.stopped_error_retry", statusCode.Value, retryLimit)
                            : L("automation.status.stopped_unhandled_error");
                        break;
                    }

                    if (consecutiveErrorStatusCode != statusCode)
                    {
                        consecutiveErrorStatusCode = statusCode;
                        consecutiveRetriesForCurrentError = 0;
                    }

                    if (retryLimit > 0 && consecutiveRetriesForCurrentError >= retryLimit)
                    {
                        TxtStatus.Text = statusCode.HasValue
                            ? Lf("automation.status.stopped_error_retry", statusCode.Value, retryLimit)
                            : L("automation.status.stopped_unhandled_error");
                        break;
                    }

                    consecutiveRetriesForCurrentError++;
                }

                if (requestLimit > 0 && requestCount >= requestLimit)
                {
                    TxtStatus.Text = Lf("generate.continuous.stopped_requests", requestCount);
                    break;
                }

                if (_autoGenCts.IsCancellationRequested) break;

                var delay = automationSettings.Generation.MinDelaySeconds +
                            Random.Shared.NextDouble() *
                            (automationSettings.Generation.MaxDelaySeconds - automationSettings.Generation.MinDelaySeconds);
                TxtStatus.Text = Lf("automation.status.waiting", delay);

                try { await Task.Delay(TimeSpan.FromSeconds(delay), _autoGenCts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            _autoGenRunning = false;
            _autoGenCts = null;
            _autoGenRemaining = 0;
            _activeAutomationSettings = null;
            _automationRunContext = null;
            UpdateAutoGenUI();
            if (!TxtStatus.Text.StartsWith(L("automation.status.stopped_prefix"), StringComparison.CurrentCulture))
                TxtStatus.Text = L("automation.status.stopped");
        }
    }

    private async Task RunContinuousGenerationAsync(int totalCount)
    {
        _continuousGenRunning = true;
        _continuousGenCts = new CancellationTokenSource();
        _continuousGenRemaining = totalCount;
        _continuousStopRequested = false;
        UpdateAutoGenUI();
        int completedCount = 0;

        try
        {
            SyncUIToParams();
            if (IsAdvancedWindowOpen) SaveAdvancedPanelToSettings();
            _settings.Save();

            for (int i = 0; i < totalCount; i++)
            {
                if (_continuousGenCts.IsCancellationRequested)
                    break;

                _continuousGenRemaining = totalCount - i;
                UpdateAutoGenUI();
                TxtStatus.Text = Lf("generate.continuous.progress", i + 1, totalCount);

                bool success = await ExecuteCurrentGenerationAsync(forceRandomSeed: true);
                if (!success)
                {
                    TxtStatus.Text = Lf("generate.continuous.stopped_failed_request", i + 1);
                    break;
                }

                completedCount++;
            }
        }
        finally
        {
            bool wasCancelled = _continuousGenCts?.IsCancellationRequested == true;
            _continuousGenRunning = false;
            _continuousGenCts = null;
            _continuousGenRemaining = 0;
            _continuousStopRequested = false;
            UpdateAutoGenUI();

            if (wasCancelled)
                TxtStatus.Text = L("generate.continuous.stopped");
            else if (completedCount == totalCount)
                TxtStatus.Text = Lf("generate.continuous.completed", completedCount);
        }
    }

    private void StopAutoGeneration()
    {
        _autoGenCts?.Cancel();
    }

    private void UpdateAutoGenUI()
    {
        if (_autoGenRunning)
        {
            string text = _autoGenRemaining > 0
                ? Lf("automation.button.stop_with_remaining", _autoGenRemaining)
                : L("automation.button.stop");
            ApplyGenerateStopButtonContent(text);
        }
        else if (_continuousGenRunning)
        {
            string text = _continuousStopRequested
                ? L("common.stopping")
                : (_continuousGenRemaining > 0
                    ? Lf("generate.continuous.stop_button_with_remaining", _continuousGenRemaining)
                    : L("generate.continuous.stop_button"));
            ApplyGenerateStopButtonContent(text);
        }
        else
        {
            foreach (var key in AccentButtonResourceKeys)
                BtnGenerate.Resources.Remove(key);
            RefreshButtonStyle(BtnGenerate);
            UpdateBtnGenerateForApiKey();
            UpdateGenerateButtonWarning();
        }
    }

    private void ApplyGenerateStopButtonContent(string text)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon
        {
            FontFamily = SymbolFontFamily, Glyph = "\uE71A", FontSize = 16,
        });
        content.Children.Add(new TextBlock { Text = text });
        BtnGenerate.Content = content;
        BtnGenerate.Resources["AccentButtonBackground"] = new SolidColorBrush(
            Windows.UI.Color.FromArgb(255, 196, 43, 28));
        BtnGenerate.Resources["AccentButtonBackgroundPointerOver"] = new SolidColorBrush(
            Windows.UI.Color.FromArgb(255, 220, 60, 45));
        BtnGenerate.Resources["AccentButtonBackgroundPressed"] = new SolidColorBrush(
            Windows.UI.Color.FromArgb(255, 170, 35, 22));
        RefreshButtonStyle(BtnGenerate);
    }
}
