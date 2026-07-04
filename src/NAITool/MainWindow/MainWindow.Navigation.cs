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
    private bool _workspaceModeButtonHovering;
    private bool _workspaceModeFlyoutOpen;
    private const double WorkspaceModeAccentIdleHeight = 16;
    private const double WorkspaceModeAccentActiveHeight = 22;

    // ═══════════════════════════════════════════════════════════
    //  模式切换
    // ═══════════════════════════════════════════════════════════

    private void OnWorkspaceModeMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button item ||
            item.Tag is not string modeName ||
            !Enum.TryParse(modeName, out AppMode target))
            return;

        if (target == _currentMode)
        {
            UpdateWorkspaceModeButton();
            WorkspaceModeFlyout.Hide();
            return;
        }

        SwitchMode(target);
        WorkspaceModeFlyout.Hide();
    }

    private void SwitchMode(AppMode mode)
    {
        if (_continuousGenRunning) StopContinuousGeneration();

        if (IsPromptMode(_currentMode) && _promptBufferLoaded)
        {
        SaveCurrentPromptToBuffer();
        SyncUIToParams();
        }
        _currentMode = mode;
        if (IsPromptMode(mode))
        SyncParamsToUI();

        if (IsPromptMode(mode))
        {
        LoadPromptFromBuffer();
        UpdateSplitVisibility();
        RefreshCharacterPanel();
        }

        bool isGen = mode == AppMode.ImageGeneration;
        bool isI2I = mode == AppMode.I2I;
        bool isUpscale = mode == AppMode.Upscale;
        bool isPost = mode == AppMode.Effects;
        bool isReader = mode == AppMode.Inspect;

        GenPreviewArea.Visibility = isGen ? Visibility.Visible : Visibility.Collapsed;
        MaskCanvas.Visibility = isI2I ? Visibility.Visible : Visibility.Collapsed;
        UpscalePreviewArea.Visibility = isUpscale ? Visibility.Visible : Visibility.Collapsed;
        EffectsPreviewArea.Visibility = isPost ? Visibility.Visible : Visibility.Collapsed;
        InspectPreviewArea.Visibility = isReader ? Visibility.Visible : Visibility.Collapsed;

        PanelLeftMain.Visibility = (isReader || isPost || isUpscale) ? Visibility.Collapsed : Visibility.Visible;
        PanelLeftEffects.Visibility = isPost ? Visibility.Visible : Visibility.Collapsed;
        PanelLeftUpscale.Visibility = isUpscale ? Visibility.Visible : Visibility.Collapsed;
        PanelLeftInspect.Visibility = isReader ? Visibility.Visible : Visibility.Collapsed;

        PanelHistory.Visibility = isGen ? Visibility.Visible : Visibility.Collapsed;
        PanelI2ITools.Visibility = isI2I ? Visibility.Visible : Visibility.Collapsed;
        CharacterPanel.Visibility = (isGen || isI2I) ? Visibility.Visible : Visibility.Collapsed;
        UpdateI2IEditModeUI();

        UpdateFileMenuState();
        MenuSaveStripped.Visibility = (isReader || isGen) ? Visibility.Visible : Visibility.Collapsed;
        MenuExportCanvasMask.Visibility = _currentMode == AppMode.I2I ? Visibility.Visible : Visibility.Collapsed;

        UpdateWorkspaceModeButton();

        if (IsPromptMode(mode)) PopulateModelList();
        if (isUpscale) PopulateUpscaleModelList();
        ReplaceEditMenu();
        ReplaceToolMenu();
        if (IsPromptMode(mode))
        {
            UpdateSizeControlMode();
            UpdateAdvSizeControlMode();
        }
        UpdateSizeWarningVisuals();
        UpdateAnlasBalanceText();
        UpdateFloatingResultBarsVisibility();
        _ = RefreshAnlasInfoAsync();
    }

    private static bool IsPromptMode(AppMode mode) =>
        mode == AppMode.ImageGeneration || mode == AppMode.I2I;

    private string GetModeLabel(AppMode mode) => mode switch
    {
        AppMode.ImageGeneration => L("mode.generate"),
        AppMode.I2I => L("mode.i2i"),
        AppMode.Upscale => L("mode.upscale"),
        AppMode.Effects => L("mode.post"),
        AppMode.Inspect => L("mode.inspect"),
        _ => mode.ToString(),
    };

    private static string GetModeIconGlyph(AppMode mode) => mode switch
    {
        AppMode.ImageGeneration => "\uE91B",
        AppMode.I2I => "\uEDFB",
        AppMode.Upscale => "\uECE9",
        AppMode.Effects => "\uEB3C",
        AppMode.Inspect => "\uE71E",
        _ => "\uE91B",
    };

    private void UpdateWorkspaceModeButton()
    {
        if (WorkspaceModeText == null)
            return;

        string currentLabel = GetModeLabel(_currentMode);
        WorkspaceModeIcon.Glyph = GetModeIconGlyph(_currentMode);
        WorkspaceModeText.Text = currentLabel;
        ToolTipService.SetToolTip(WorkspaceModeButton, $"{L("workspace.switcher")}: {currentLabel}");

        WorkspaceModeGenerateIcon.Glyph = GetModeIconGlyph(AppMode.ImageGeneration);
        WorkspaceModeI2IIcon.Glyph = GetModeIconGlyph(AppMode.I2I);
        WorkspaceModeUpscaleIcon.Glyph = GetModeIconGlyph(AppMode.Upscale);
        WorkspaceModeEffectsIcon.Glyph = GetModeIconGlyph(AppMode.Effects);
        WorkspaceModeInspectIcon.Glyph = GetModeIconGlyph(AppMode.Inspect);

        UpdateWorkspaceModeCardState(
            WorkspaceModeGenerateCard,
            WorkspaceModeGenerateCheck,
            _currentMode == AppMode.ImageGeneration);
        UpdateWorkspaceModeCardState(
            WorkspaceModeI2ICard,
            WorkspaceModeI2ICheck,
            _currentMode == AppMode.I2I);
        UpdateWorkspaceModeCardState(
            WorkspaceModeUpscaleCard,
            WorkspaceModeUpscaleCheck,
            _currentMode == AppMode.Upscale);
        UpdateWorkspaceModeCardState(
            WorkspaceModeEffectsCard,
            WorkspaceModeEffectsCheck,
            _currentMode == AppMode.Effects);
        UpdateWorkspaceModeCardState(
            WorkspaceModeInspectCard,
            WorkspaceModeInspectCheck,
            _currentMode == AppMode.Inspect);
    }

    private void UpdateWorkspaceModeCardState(Border card, FontIcon check, bool selected)
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        card.BorderBrush = selected
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 215))
            : new SolidColorBrush(isDark
                ? Windows.UI.Color.FromArgb(70, 255, 255, 255)
                : Windows.UI.Color.FromArgb(60, 0, 0, 0));
        card.Background = selected
            ? new SolidColorBrush(isDark
                ? Windows.UI.Color.FromArgb(45, 255, 255, 255)
                : Windows.UI.Color.FromArgb(36, 0, 0, 0))
            : new SolidColorBrush(isDark
                ? Windows.UI.Color.FromArgb(12, 255, 255, 255)
                : Windows.UI.Color.FromArgb(6, 0, 0, 0));
        check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnWorkspaceModeCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        AnimateWorkspaceModeCardHover(sender, 1);
    }

    private void OnWorkspaceModeCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        AnimateWorkspaceModeCardHover(sender, 0);
    }

    private void OnWorkspaceModeCardPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ResetWorkspaceModeCardHoverIfPointerOutside(sender);
    }

    private void OnWorkspaceModeCardPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        ResetWorkspaceModeCardHoverIfPointerOutside(sender);
    }

    private static void ResetWorkspaceModeCardHoverIfPointerOutside(object sender)
    {
        if (sender is Button button && !button.IsPointerOver)
            AnimateWorkspaceModeCardHover(button, 0);
    }

    private void OnWorkspaceModeButtonPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _workspaceModeButtonHovering = true;
        UpdateWorkspaceModeAccentState();
    }

    private void OnWorkspaceModeButtonPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _workspaceModeButtonHovering = false;
        UpdateWorkspaceModeAccentState();
    }

    private void OnWorkspaceModeFlyoutOpened(object sender, object e)
    {
        _workspaceModeFlyoutOpen = true;
        UpdateWorkspaceModeAccentState();
    }

    private void OnWorkspaceModeFlyoutClosed(object sender, object e)
    {
        _workspaceModeFlyoutOpen = false;
        UpdateWorkspaceModeAccentState();
    }

    private void UpdateWorkspaceModeAccentState()
    {
        if (WorkspaceModeAccent == null)
            return;

        double targetHeight = _workspaceModeButtonHovering || _workspaceModeFlyoutOpen
            ? WorkspaceModeAccentActiveHeight
            : WorkspaceModeAccentIdleHeight;

        if (Math.Abs(WorkspaceModeAccent.Height - targetHeight) < 0.1)
            return;

        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            To = targetHeight,
            Duration = TimeSpan.FromMilliseconds(140),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, WorkspaceModeAccent);
        Storyboard.SetTargetProperty(animation, nameof(FrameworkElement.Height));
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private static void AnimateWorkspaceModeCardHover(object sender, double targetOpacity)
    {
        Border? card = sender switch
        {
            Border border => border,
            Button { Content: Border border } => border,
            _ => null,
        };

        if (card?.Child is not Grid grid)
            return;

        Border? hoverLayer = grid.Children.OfType<Border>()
            .FirstOrDefault(child => child.Name.EndsWith("Hover", StringComparison.Ordinal));
        if (hoverLayer == null)
            return;

        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(120),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, hoverLayer);
        Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void UpdateWorkspaceModeButtonTitleBarInset()
    {
        if (WorkspaceModeButton == null)
            return;

        double rightInset = AppWindow?.TitleBar?.RightInset ?? 138;
        WorkspaceModeButton.Margin = new Thickness(0, 0, rightInset, 0);
    }

    private void SetGenResultBarRequested(bool requested, bool resetPosition = false)
    {
        _genResultBarRequested = requested;
        if (requested)
        {
            if (resetPosition)
            {
                GenResultBarTranslate.X = 0;
                GenResultBarTranslate.Y = 0;
            }

            UpdateGenEnhanceButtonWarning();
        }

        UpdateFloatingResultBarsVisibility();
    }

    private void ShowI2IResultBar(bool resetPosition = false)
    {
        if (resetPosition)
        {
            ResultBarTranslate.X = 0;
            ResultBarTranslate.Y = 0;
        }

        if (MaskCanvas.IsInPreviewMode)
            UpdateI2IRedoButtonWarning();

        UpdateFloatingResultBarsVisibility();
    }

    private void UpdateFloatingResultBarsVisibility()
    {
        bool showGenResultBar =
            (_genResultBarRequested ||
             (_genResultBarPinned && _currentGenImageBytes != null)) &&
            _currentMode == AppMode.ImageGeneration &&
            !_autoGenRunning &&
            _settings.Settings.ShowGenerationResultBar &&
            _currentGenImageBytes != null;
        GenResultBar.Visibility = showGenResultBar ? Visibility.Visible : Visibility.Collapsed;

        BtnShowGenResultBar.Visibility =
            (!showGenResultBar &&
             _currentMode == AppMode.ImageGeneration &&
             _currentGenImageBytes != null)
            ? Visibility.Visible : Visibility.Collapsed;

        bool showI2IResultBar =
            _currentMode == AppMode.I2I &&
            MaskCanvas.IsInPreviewMode;
        ResultActionBar.Visibility = showI2IResultBar ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnShowGenResultBar(object sender, RoutedEventArgs e)
    {
        _genResultBarPinned = true;
        BtnPinGenResult.IsChecked = true;
        var icon = BtnPinGenResult.Content as FontIcon;
        if (icon != null)
            icon.Glyph = "";
        UpdateFloatingResultBarsVisibility();
    }

    private void OnPinGenResult(object sender, RoutedEventArgs e)
    {
        _genResultBarPinned = BtnPinGenResult.IsChecked == true;
        if (!_genResultBarPinned)
            _genResultBarRequested = true;
        var icon = BtnPinGenResult.Content as FontIcon;
        if (icon != null)
            icon.Glyph = _genResultBarPinned ? "" : "";
        UpdateFloatingResultBarsVisibility();
    }

    private void OnLeftSidebarResizeStart(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement handle) return;
        _leftSidebarResizing = true;
        _leftSidebarDragStartX = e.GetCurrentPoint(MainContentGrid).Position.X;
        _leftSidebarStartWidth = MainContentGrid.ColumnDefinitions[0].ActualWidth;
        handle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnLeftSidebarResizeMove(object sender, PointerRoutedEventArgs e)
    {
        if (!_leftSidebarResizing) return;

        double currentX = e.GetCurrentPoint(MainContentGrid).Position.X;
        double newWidth = _leftSidebarStartWidth + (currentX - _leftSidebarDragStartX);
        newWidth = Math.Clamp(newWidth, 283, 720);
        MainContentGrid.ColumnDefinitions[0].Width = new GridLength(newWidth);
        UpdatePromptTabText();
        e.Handled = true;
    }

    private void OnLeftSidebarResizeEnd(object sender, PointerRoutedEventArgs e)
    {
        if (!_leftSidebarResizing) return;
        _leftSidebarResizing = false;
        if (sender is UIElement handle)
            handle.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnLeftSidebarHandlePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Panel panel)
            panel.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, 0x80, 0x80, 0x80));
    }

    private void OnLeftSidebarHandlePointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Panel panel && !_leftSidebarResizing)
            panel.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
    }
}
