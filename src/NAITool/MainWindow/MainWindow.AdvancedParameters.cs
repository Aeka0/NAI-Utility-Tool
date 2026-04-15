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
    private void OnPresetResolutionSelected(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is (int w, int h))
        {
            _isUpdatingMaxSize = true;
            try
            {
                _customWidth = w;
                _customHeight = h;
                NbMaxWidth.Value = w;
                NbMaxHeight.Value = h;
                int idx = Array.FindIndex(MaskCanvasControl.CanvasPresets, p => p.W == w && p.H == h);
                if (idx >= 0) CboSize.SelectedIndex = idx;
                if (IsAdvancedWindowOpen)
                {
                    _advNbMaxWidth.Value = w;
                    _advNbMaxHeight.Value = h;
                    if (_advCboSize != null && idx >= 0) _advCboSize.SelectedIndex = idx;
                }
                if (_currentMode == AppMode.I2I &&
                    (MaskCanvas.CanvasW != _customWidth || MaskCanvas.CanvasH != _customHeight))
                {
                    MaskCanvas.InitializeCanvas(_customWidth, _customHeight);
            MaskCanvas.FitToScreen();
                }
                TxtStatus.Text = Lf("size.preset_applied", w, h);
                UpdateSizeWarningVisuals();
            }
            finally
            {
                _isUpdatingMaxSize = false;
            }
        }
    }

    private void OnSwapSizeDimensions(object sender, RoutedEventArgs e)
    {
        ApplyMaxSizeInput(_customHeight, _customWidth, fromAdvancedPanel: false, changedBox: NbMaxWidth);
        TxtStatus.Text = Lf("size.swapped", _customWidth, _customHeight);
    }

    private void OnAdvSwapSizeDimensions(object sender, RoutedEventArgs e)
    {
        ApplyMaxSizeInput(_customHeight, _customWidth, fromAdvancedPanel: true, changedBox: _advNbMaxWidth);
        TxtStatus.Text = Lf("size.swapped", _customWidth, _customHeight);
    }

    private static int SnapToMultipleOf64(double rawValue)
    {
        var snapped = (int)Math.Round(rawValue / 64d) * 64;
        return Math.Max(64, snapped);
    }

    private void OnMaxSizeValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isUpdatingMaxSize) return;
        if (NbMaxWidth == null || NbMaxHeight == null) return;
        if (double.IsNaN(NbMaxWidth.Value) || double.IsNaN(NbMaxHeight.Value)) return;
        ApplyMaxSizeInput((int)NbMaxWidth.Value, (int)NbMaxHeight.Value, fromAdvancedPanel: false, changedBox: sender);
    }

    private void OnAdvMaxSizeValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isUpdatingMaxSize || !IsAdvancedWindowOpen) return;
        ApplyMaxSizeInput((int)_advNbMaxWidth.Value, (int)_advNbMaxHeight.Value, fromAdvancedPanel: true, changedBox: sender);
    }

    private void ApplyMaxSizeInput(int width, int height, bool fromAdvancedPanel, NumberBox? changedBox = null)
    {
        _isUpdatingMaxSize = true;
        try
        {
            _customWidth = SnapToMultipleOf64(width);
            _customHeight = SnapToMultipleOf64(height);

            if (IsAssetProtectionSizeLimitEnabled())
                AutoAdjustNonMaxSize(changedBox, fromAdvancedPanel);

            NbMaxWidth.Value = _customWidth;
            NbMaxHeight.Value = _customHeight;
            if (IsAdvancedWindowOpen)
            {
                _advNbMaxWidth.Value = _customWidth;
                _advNbMaxHeight.Value = _customHeight;
            }

            if (_currentMode == AppMode.I2I &&
                (MaskCanvas.CanvasW != _customWidth || MaskCanvas.CanvasH != _customHeight))
            {
                MaskCanvas.InitializeCanvas(_customWidth, _customHeight);
                MaskCanvas.FitToScreen();
                TxtStatus.Text = Lf("size.canvas_resized", _customWidth, _customHeight);
            }
            else if (fromAdvancedPanel)
            {
                TxtStatus.Text = Lf("size.updated", _customWidth, _customHeight);
            }

            UpdateSizeWarningVisuals();
        }
        finally
        {
            _isUpdatingMaxSize = false;
        }
    }

    private void AutoAdjustNonMaxSize(NumberBox? changedBox, bool fromAdvancedPanel)
    {
        const long maxPixels = 1024L * 1024;
        if ((long)_customWidth * _customHeight <= maxPixels) return;

        bool userChangedWidth = changedBox == NbMaxWidth ||
                                (fromAdvancedPanel && changedBox == _advNbMaxWidth);

        if (userChangedWidth)
        {
            while ((long)_customWidth * _customHeight > maxPixels && _customHeight > 64)
                _customHeight -= 64;
        }
        else
        {
            while ((long)_customWidth * _customHeight > maxPixels && _customWidth > 64)
                _customWidth -= 64;
        }
    }

    private (int W, int H) GetSelectedSize()
    {
        return (_customWidth, _customHeight);
    }

    // ═══════════════════════════════════════════════════════════
    //  高级参数独立窗口
    // ═══════════════════════════════════════════════════════════
}
