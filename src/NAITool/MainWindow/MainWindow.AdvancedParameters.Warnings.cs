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
    private static readonly string[] AccentButtonResourceKeys =
    [
        "AccentButtonBackground",
        "AccentButtonBackgroundPointerOver",
        "AccentButtonBackgroundPressed",
        "AccentButtonForeground",
        "AccentButtonForegroundPointerOver",
        "AccentButtonForegroundPressed",
    ];

    private static void RefreshButtonStyle(Button button)
    {
        var style = button.Style;
        button.Style = null;
        button.Style = style;
    }

    private enum SizeWarningLevel
    {
        None,
        Yellow,
        Red,
    }

    private void UpdateSizeControlMode()
    {
        CboSize.Visibility = Visibility.Collapsed;
        MaxSizePanel.Visibility = Visibility.Visible;
    }

    private void UpdateAdvSizeControlMode()
    {
        if (!IsAdvancedWindowOpen) return;
        _advCboSize.Visibility = Visibility.Collapsed;
        _advMaxSizePanel.Visibility = Visibility.Visible;
    }

    private SizeWarningLevel GetSizeWarningLevel()
    {
        long pixels = (long)_customWidth * _customHeight;
        if (IsAssetProtectionSizeLimitEnabled())
        {
            if (pixels > 1024L * 1024) return SizeWarningLevel.Red;
            return SizeWarningLevel.None;
        }

        if (pixels > 2048L * 2048) return SizeWarningLevel.Red;
        if (pixels > 1024L * 1024) return SizeWarningLevel.Yellow;
        return SizeWarningLevel.None;
    }

    private void ApplyWarningStyle(NumberBox box, SizeWarningLevel level)
    {
        if (level == SizeWarningLevel.None)
        {
            box.ClearValue(Control.BackgroundProperty);
            return;
        }

        bool isDark = ((FrameworkElement)this.Content).ActualTheme == ElementTheme.Dark;
        Windows.UI.Color color = level switch
        {
            SizeWarningLevel.Red => isDark
                ? Windows.UI.Color.FromArgb(200, 180, 60, 60)
                : Windows.UI.Color.FromArgb(200, 255, 210, 210),
            _ => isDark
                ? Windows.UI.Color.FromArgb(200, 120, 110, 40)
                : Windows.UI.Color.FromArgb(200, 255, 245, 190),
        };
        box.Background = new SolidColorBrush(color);
    }

    private void UpdateSizeWarningVisuals()
    {
        var level = GetSizeWarningLevel();
        ApplyWarningStyle(NbMaxWidth, level);
        ApplyWarningStyle(NbMaxHeight, level);
        UpdateAdvSizeWarningVisuals();
        UpdateGenerateButtonWarning();
    }

    private void UpdateAdvSizeWarningVisuals()
    {
        if (!IsAdvancedWindowOpen) return;
        var level = GetSizeWarningLevel();
        ApplyWarningStyle(_advNbMaxWidth, level);
        ApplyWarningStyle(_advNbMaxHeight, level);
    }

    // ═══════════════════════════════════════════════════════════
    //  NumberBox 清除按钮隐藏 & 辅助方法
    // ═══════════════════════════════════════════════════════════

    private static void SuppressNumberBoxClearButton(NumberBox nb)
    {
        nb.Loaded += (_, _) => nb.DispatcherQueue?.TryEnqueue(() =>
        {
            var textBox = FindDescendant<TextBox>(nb);
            if (textBox == null) return;

            int childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(textBox);
            if (childCount == 0)
            {
                textBox.Loaded += (_, _) => DisableTextBoxClearButton(textBox);
                return;
            }
            DisableTextBoxClearButton(textBox);
        });
    }

    private static void SuppressNumberBoxInitialSelection(NumberBox nb)
    {
        nb.DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var textBox = FindDescendant<TextBox>(nb);
            if (textBox == null) return;
            textBox.SelectionStart = textBox.Text?.Length ?? 0;
            textBox.Select(textBox.SelectionStart, 0);
        });
    }

    private static void DisableTextBoxClearButton(TextBox textBox)
    {
        int childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(textBox);
        if (childCount == 0) return;

        if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(textBox, 0) is not FrameworkElement templateRoot)
            return;

        foreach (var group in VisualStateManager.GetVisualStateGroups(templateRoot))
        {
            if (group.Name != "ButtonStates") continue;
            foreach (var state in group.States)
            {
                if (state.Name == "ButtonVisible")
                {
                    state.Storyboard = null;
                    state.Setters.Clear();
                    return;
                }
            }
        }
    }

    private static T? FindDescendant<T>(DependencyObject parent, string? name = null) where T : DependencyObject
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t && (name == null || (t is FrameworkElement fe && fe.Name == name)))
                return t;
            var result = FindDescendant<T>(child, name);
            if (result != null) return result;
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════
    //  生成按钮警告状态
    // ═══════════════════════════════════════════════════════════

    private bool HasAnyWarning()
    {
        if (GetSizeWarningLevel() != SizeWarningLevel.None) return true;
        if (CurrentRequestUsesAnlas()) return true;
        if (!IsAssetProtectionStepLimitEnabled())
        {
            int steps = IsAdvancedWindowOpen ? (int)_advNbSteps.Value : CurrentParams.Steps;
            if (steps > 28) return true;
        }
        return false;
    }

    private StackPanel CreateAnlasActionButtonContent(string actionText, int anlasCost)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon
        {
            FontFamily = SymbolFontFamily,
            Glyph = "\uF159",
            FontSize = 16,
        });
        content.Children.Add(new TextBlock
        {
            Text = anlasCost.ToString(),
            Opacity = 0.9,
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = actionText,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return content;
    }

    private StackPanel CreateSymbolActionButtonContent(Symbol symbol, string actionText)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new SymbolIcon { Symbol = symbol });
        content.Children.Add(new TextBlock
        {
            Text = actionText,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return content;
    }

    private void ApplyGoldAccentButtonStyle(Button button)
    {
        if (button == null) return;

        bool isLight = GetResolvedTheme() != ElementTheme.Dark;
        const byte darkGoldR = 0xB2, darkGoldG = 0x8E, darkGoldB = 0x36;
        const byte lightGoldR = 0xF6, lightGoldG = 0xD2, lightGoldB = 0x7E;

        static LinearGradientBrush CreateDiagonalGold(byte r0, byte g0, byte b0, byte r1, byte g1, byte b1)
        {
            var stops = new GradientStopCollection
            {
                new GradientStop { Color = Windows.UI.Color.FromArgb(255, r0, g0, b0), Offset = 0.0 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(255, r1, g1, b1), Offset = 1.0 },
            };
            var brush = new LinearGradientBrush(stops, 0.0)
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
            };
            return brush;
        }

        static byte Brighten(byte value, double amount) =>
            (byte)Math.Clamp((int)Math.Round(value + (255 - value) * amount), 0, 255);

        byte baseStartR = isLight ? Brighten(darkGoldR, 0.24) : darkGoldR;
        byte baseStartG = isLight ? Brighten(darkGoldG, 0.24) : darkGoldG;
        byte baseStartB = isLight ? Brighten(darkGoldB, 0.24) : darkGoldB;
        byte baseEndR = isLight ? Brighten(lightGoldR, 0.18) : lightGoldR;
        byte baseEndG = isLight ? Brighten(lightGoldG, 0.18) : lightGoldG;
        byte baseEndB = isLight ? Brighten(lightGoldB, 0.18) : lightGoldB;

        button.Resources["AccentButtonBackground"] = CreateDiagonalGold(baseStartR, baseStartG, baseStartB, baseEndR, baseEndG, baseEndB);
        button.Resources["AccentButtonBackgroundPointerOver"] = CreateDiagonalGold(
            Brighten(baseStartR, isLight ? 0.16 : 0.08), Brighten(baseStartG, isLight ? 0.16 : 0.08), Brighten(baseStartB, isLight ? 0.16 : 0.08),
            Brighten(baseEndR, isLight ? 0.12 : 0.06), Brighten(baseEndG, isLight ? 0.12 : 0.06), Brighten(baseEndB, isLight ? 0.12 : 0.06));
        button.Resources["AccentButtonBackgroundPressed"] = CreateDiagonalGold(
            (byte)(baseStartR * 0.88), (byte)(baseStartG * 0.88), (byte)(baseStartB * 0.88),
            (byte)(baseEndR * 0.88), (byte)(baseEndG * 0.88), (byte)(baseEndB * 0.88));

        var fgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 28, 20));
        button.Resources["AccentButtonForeground"] = fgBrush;
        button.Resources["AccentButtonForegroundPointerOver"] = fgBrush;
        button.Resources["AccentButtonForegroundPressed"] = fgBrush;

        RefreshButtonStyle(button);
    }

    private static void ClearGoldAccentButtonStyle(Button button)
    {
        if (button == null) return;

        foreach (var key in AccentButtonResourceKeys)
            button.Resources.Remove(key);

        RefreshButtonStyle(button);
    }

    private void UpdateGenerateButtonWarning()
    {
        if (IsAnyGenerateLoopRunning() || BtnGenerate == null || this.Content == null) return;
        UpdateBtnGenerateForApiKey();
        bool warn = EstimateCurrentRequestAnlasCost() > 0;

        if (warn)
            ApplyGoldAccentButtonStyle(BtnGenerate);
        else
            ClearGoldAccentButtonStyle(BtnGenerate);

        UpdateI2IRedoButtonWarning();
    }

    private void UpdateI2IRedoButtonWarning()
    {
        if (BtnRedoGenerate == null) return;

        bool warn = _currentMode == AppMode.I2I &&
                    MaskCanvas.IsInPreviewMode &&
                    EstimateCurrentRequestAnlasCost() > 0;

        if (warn)
        {
            BtnRedoGenerate.Content = CreateAnlasActionButtonContent(L("button.regenerate"), EstimateCurrentRequestAnlasCost());
            ApplyGoldAccentButtonStyle(BtnRedoGenerate);
        }
        else
        {
            BtnRedoGenerate.Content = CreateSymbolActionButtonContent(Symbol.Refresh, L("button.regenerate"));
            ClearGoldAccentButtonStyle(BtnRedoGenerate);
        }
    }

    private void UpdateBtnGenerateForApiKey()
    {
        if (IsAnyGenerateLoopRunning()) return;
        BtnGenerate.IsEnabled = !_generateRequestRunning;
        bool hasKey = !string.IsNullOrEmpty(_settings.Settings.ApiToken);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (hasKey && _anlasRefreshRunning && !_anlasInitialFetchDone)
        {
            content.Children.Add(new SymbolIcon(Symbol.Sync));
            content.Children.Add(new TextBlock { Text = L("status.syncing_account") });
            BtnGenerate.Content = content;
            BtnGenerate.IsEnabled = false;
            return;
        }

        if (!IsAnyGenerateLoopRunning())
            BtnGenerate.IsEnabled = !_generateRequestRunning;

        if (hasKey)
        {
            int estimatedAnlas = EstimateCurrentRequestAnlasCost();
            if (estimatedAnlas > 0)
            {
                content.Children.Add(new FontIcon
                {
                    FontFamily = SymbolFontFamily,
                    Glyph = "\uF159",
                    FontSize = 16,
                });
                content.Children.Add(new TextBlock
                {
                    Text = estimatedAnlas.ToString(),
                    Opacity = 0.9,
                });
            }
            else
            {
                content.Children.Add(new SymbolIcon(Symbol.Send));
            }
            content.Children.Add(new TextBlock { Text = L("button.send") });
        }
        else
        {
            content.Children.Add(new SymbolIcon(Symbol.Globe));
            content.Children.Add(new TextBlock { Text = L("button.setup_api") });
        }
        BtnGenerate.Content = content;
    }

    // ═══════════════════════════════════════════════════════════
    //  自动化生成
    // ═══════════════════════════════════════════════════════════
}
