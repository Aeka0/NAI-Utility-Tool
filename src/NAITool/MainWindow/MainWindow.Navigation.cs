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
    // ═══════════════════════════════════════════════════════════
    //  模式切换
    // ═══════════════════════════════════════════════════════════

    private void OnModeTabSwitch(object sender, RoutedEventArgs e)
    {
        AppMode? target = null;
        if (ReferenceEquals(sender, TabGenerate) && TabGenerate.IsChecked == true)
            target = AppMode.ImageGeneration;
        else if (ReferenceEquals(sender, TabI2I) && TabI2I.IsChecked == true)
            target = AppMode.I2I;
        else if (ReferenceEquals(sender, TabUpscale) && TabUpscale.IsChecked == true)
            target = AppMode.Upscale;
        else if (ReferenceEquals(sender, TabEffects) && TabEffects.IsChecked == true)
            target = AppMode.Effects;
        else if (ReferenceEquals(sender, TabInspect) && TabInspect.IsChecked == true)
            target = AppMode.Inspect;

        if (target.HasValue)
            SwitchMode(target.Value);
        else if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton tb)
                tb.IsChecked = true;
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

        TabGenerate.IsChecked = isGen;
        TabI2I.IsChecked = isI2I;
        TabUpscale.IsChecked = isUpscale;
        TabEffects.IsChecked = isPost;
        TabInspect.IsChecked = isReader;

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
            _genResultBarRequested &&
            _currentMode == AppMode.ImageGeneration &&
            !_autoGenRunning &&
            _settings.Settings.ShowGenerationResultBar &&
            _currentGenImageBytes != null;
        GenResultBar.Visibility = showGenResultBar ? Visibility.Visible : Visibility.Collapsed;

        bool showI2IResultBar =
            _currentMode == AppMode.I2I &&
            MaskCanvas.IsInPreviewMode;
        ResultActionBar.Visibility = showI2IResultBar ? Visibility.Visible : Visibility.Collapsed;
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
