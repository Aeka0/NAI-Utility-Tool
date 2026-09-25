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
    private void SetToolSelection(StrokeTool tool)
    {
        if (_i2iEditMode != I2IEditMode.Inpaint) return;
        MaskCanvas.CancelMaskMove();
        if (MaskCanvas.IsActivelyDrawing) return;
        MaskCanvas.Brush.CurrentTool = tool;
        BtnBrush.IsChecked = tool == StrokeTool.Brush;
        BtnEraser.IsChecked = tool == StrokeTool.Eraser;
        BtnRect.IsChecked = tool == StrokeTool.Rectangle;
        BtnMoveMask.IsChecked = tool == StrokeTool.MoveMask;
        MaskCanvas.RefreshToolCursor();
    }

    private void OnToolBrush(object sender, RoutedEventArgs e) => SetToolSelection(StrokeTool.Brush);
    private void OnToolEraser(object sender, RoutedEventArgs e) => SetToolSelection(StrokeTool.Eraser);
    private void OnToolRect(object sender, RoutedEventArgs e) => SetToolSelection(StrokeTool.Rectangle);
    private void OnToolMoveMask(object sender, RoutedEventArgs e) => SetToolSelection(StrokeTool.MoveMask);

    private void OnBrushSizeChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (MaskCanvas == null) return;
        MaskCanvas.Brush.BrushSize = (float)e.NewValue;
        MaskCanvas.RefreshToolCursor();
        if (TxtBrushSize != null) TxtBrushSize.Text = $"{(int)e.NewValue}";
    }

    // XAML assigns slider defaults during InitializeComponent; these must not overwrite loaded settings.
    private bool _updatingImageRequestParameters = true;

    private NAIParameters ImageRequestParameters => _i2iEditMode == I2IEditMode.Inpaint
        ? _settings.Settings.InpaintParameters : _settings.Settings.I2IDenoiseParameters;

    private void OnDenoiseStrengthChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingImageRequestParameters || MaskCanvas == null) return;
        if (_i2iEditMode == I2IEditMode.Inpaint)
            ImageRequestParameters.InpaintStrength = Math.Round(e.NewValue, 2);
        else
            ImageRequestParameters.DenoiseStrength = Math.Round(e.NewValue, 2);
        if (TxtDenoiseStrength != null) TxtDenoiseStrength.Text = e.NewValue.ToString("0.00", CultureInfo.InvariantCulture);
        UpdateGenerateButtonWarning();
        UpdateBtnGenerateForApiKey();
        UpdateQuotaSummaryFlyoutContent();
    }

    private void OnDenoiseNoiseChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingImageRequestParameters || MaskCanvas == null) return;
        if (_i2iEditMode == I2IEditMode.Inpaint)
            ImageRequestParameters.InpaintNoise = Math.Round(e.NewValue, 2);
        else
            ImageRequestParameters.DenoiseNoise = Math.Round(e.NewValue, 2);
        if (TxtDenoiseNoise != null) TxtDenoiseNoise.Text = e.NewValue.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private void OnI2IEditModeSwitch(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, BtnI2IInpaintMode) && BtnI2IInpaintMode.IsChecked == true)
            SwitchI2IEditMode(I2IEditMode.Inpaint);
        else if (ReferenceEquals(sender, BtnI2IDenoiseMode) && BtnI2IDenoiseMode.IsChecked == true)
            SwitchI2IEditMode(I2IEditMode.Denoise);
        else if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton tb)
            tb.IsChecked = true;
    }

    private void SwitchI2IEditMode(I2IEditMode mode)
    {
        if (_i2iEditMode == mode)
        {
            UpdateI2IEditModeUI();
            return;
        }

        if (_currentMode == AppMode.I2I)
            SyncUIToParams();

        _i2iEditMode = mode;

        if (_currentMode == AppMode.I2I)
        {
            PopulateModelList();
            SyncParamsToUI();
            UpdateDynamicMenuStates();
            UpdateSizeWarningVisuals();
            UpdateGenerateButtonWarning();
        }

        UpdateI2IEditModeUI();
    }

    private void UpdateI2IEditModeUI()
    {
        if (BtnI2IInpaintMode == null || BtnI2IDenoiseMode == null || MaskCanvas == null) return;

        bool isInpaint = _i2iEditMode == I2IEditMode.Inpaint;
        BtnI2IInpaintMode.IsChecked = isInpaint;
        BtnI2IDenoiseMode.IsChecked = !isInpaint;

        PanelI2IInpaintTools.Visibility = isInpaint ? Visibility.Visible : Visibility.Collapsed;

        MaskCanvas.IsMaskEditingEnabled = isInpaint;
        MaskCanvas.IsMaskOverlayVisible = isInpaint;
        if (!isInpaint)
            MaskCanvas.PreviewMaskOnly = false;
        else
            MaskCanvas.PreviewMaskOnly = ChkPreviewMask.IsChecked == true;
        MaskCanvas.RefreshCanvas();

        UpdateImageRequestParameterControls();
        UpdateReferenceButtonAndPanelState();
    }

    private void UpdateImageRequestParameterControls()
    {
        if (SliderDenoiseStrength == null || SliderDenoiseNoise == null) return;
        bool inpaint = _i2iEditMode == I2IEditMode.Inpaint;
        var p = ImageRequestParameters;
        bool supported = !inpaint || IsV4PlusModelKey(p.Model);
        bool wasUpdating = _updatingImageRequestParameters;
        _updatingImageRequestParameters = true;
        try
        {
            SliderDenoiseStrength.IsEnabled = supported;
            SliderDenoiseNoise.IsEnabled = supported;
            SliderDenoiseStrength.Value = supported
                ? Math.Clamp(inpaint ? p.InpaintStrength : p.DenoiseStrength, 0, 1) : 1;
            SliderDenoiseNoise.Value = supported
                ? Math.Clamp(inpaint ? p.InpaintNoise : p.DenoiseNoise, 0, 1) : 0;
            TxtDenoiseStrength.Text = SliderDenoiseStrength.Value.ToString("0.00", CultureInfo.InvariantCulture);
            TxtDenoiseNoise.Text = SliderDenoiseNoise.Value.ToString("0.00", CultureInfo.InvariantCulture);
        }
        finally
        {
            _updatingImageRequestParameters = wasUpdating;
        }
    }

    private void OnTogglePreviewMask(object sender, RoutedEventArgs e)
    {
        MaskCanvas.PreviewMaskOnly = ChkPreviewMask.IsChecked == true;
    }

    // ═══════════════════════════════════════════════════════════
    //  键盘快捷键
    // ═══════════════════════════════════════════════════════════

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(this.Content.XamlRoot) is not TextBox
            and not RichEditBox
            and not PasswordBox)
        {
            return;
        }

        if (IsWithinTextInput(e.OriginalSource as DependencyObject))
            return;

        KeyboardShortcutFocusSink.IsTabStop = true;
        if (!KeyboardShortcutFocusSink.Focus(FocusState.Programmatic))
            KeyboardShortcutFocusSink.IsTabStop = false;
    }

    private static bool IsWithinTextInput(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is TextBox or RichEditBox or PasswordBox or NumberBox or AutoSuggestBox)
                return true;

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void OnGlobalKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Control);
            if (ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                OnGenerate(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
        }

        if (FocusManager.GetFocusedElement(this.Content.XamlRoot) is TextBox or RichEditBox or PasswordBox)
            return;

        if (e.Key == Windows.System.VirtualKey.Delete &&
            _currentMode == AppMode.ImageGeneration &&
            _currentGenImageBytes != null)
        {
            OnDeleteGenResult(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.F5 &&
            (_currentMode == AppMode.I2I || _currentMode == AppMode.Upscale || _currentMode == AppMode.Effects))
        {
            _ = ReloadCurrentWorkspaceImageAsync();
            e.Handled = true;
            return;
        }

        if (_currentMode == AppMode.ImageGeneration && TryNavigateHistory(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (_currentMode == AppMode.I2I && _i2iEditMode == I2IEditMode.Inpaint)
        {
            if (e.Key == Windows.System.VirtualKey.Escape && MaskCanvas.CancelMaskMove())
            {
                e.Handled = true;
                return;
            }
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Control);
            bool ctrlDown = ctrlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (ctrlDown && e.Key == (Windows.System.VirtualKey)187)
            {
                OnExpandMask(this, new RoutedEventArgs());
                e.Handled = true; return;
            }
            if (ctrlDown && e.Key == (Windows.System.VirtualKey)189)
            {
                OnShrinkMask(this, new RoutedEventArgs());
                e.Handled = true; return;
            }

            var aKeyState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.A);
            bool aKeyDown = aKeyState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (aKeyDown && !MaskCanvas.IsInPreviewMode && TryAlignByArrowKey(e.Key))
            {
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Windows.System.VirtualKey.B:
                    SetToolSelection(StrokeTool.Brush); e.Handled = true; break;
                case Windows.System.VirtualKey.E:
                    SetToolSelection(StrokeTool.Eraser); e.Handled = true; break;
                case Windows.System.VirtualKey.R:
                    SetToolSelection(StrokeTool.Rectangle); e.Handled = true; break;
                case Windows.System.VirtualKey.M:
                    SetToolSelection(StrokeTool.MoveMask); e.Handled = true; break;
            }
        }
    }

    private bool TryAlignByArrowKey(Windows.System.VirtualKey key)
    {
        bool IsDown(Windows.System.VirtualKey k) =>
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (key == Windows.System.VirtualKey.NumberPad0)
        {
            if (!MaskCanvas.CanMoveImage)
                return true;

            MaskCanvas.AlignImage("CC");
            TxtStatus.Text = L("image.aligned_center");
            return true;
        }

        bool up = key == Windows.System.VirtualKey.Up || IsDown(Windows.System.VirtualKey.Up);
        bool down = key == Windows.System.VirtualKey.Down || IsDown(Windows.System.VirtualKey.Down);
        bool left = key == Windows.System.VirtualKey.Left || IsDown(Windows.System.VirtualKey.Left);
        bool right = key == Windows.System.VirtualKey.Right || IsDown(Windows.System.VirtualKey.Right);

        if (!up && !down && !left && !right) return false;
        if (up && down) down = false;
        if (left && right) right = false;

        if (!MaskCanvas.CanMoveImage)
            return true;

        char row = up ? 'T' : down ? 'B' : 'C';
        char col = left ? 'L' : right ? 'R' : 'C';

        var tag = $"{row}{col}";
        MaskCanvas.AlignImage(tag);
        TxtStatus.Text = L("image.aligned");
        return true;
    }

    private bool TryNavigateHistory(Windows.System.VirtualKey key)
    {
        if (key != Windows.System.VirtualKey.Up && key != Windows.System.VirtualKey.Down)
            return false;

        if (_historyFiles.Count == 0 || _currentGenImagePath == null)
            return false;
        int currentIndex = _historyFiles.IndexOf(_currentGenImagePath);
        if (currentIndex < 0) return false;
        int targetIndex = currentIndex + (key == Windows.System.VirtualKey.Up ? -1 : 1);
        if (targetIndex < 0 || targetIndex >= _historyFiles.Count) return true;

        var targetPath = _historyFiles[targetIndex];
        _ = ShowHistoryImageAsync(targetPath);
        int row = _historyRows.FindRow(targetPath);
        if (row >= 0)
            HistoryListView.ScrollIntoView(_historyRows.GetRow(row), ScrollIntoViewAlignment.Leading);

        return true;
    }

    private void OnPromptPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is PromptTextBox promptTextBox && TryHandlePromptWeightShortcut(promptTextBox, e.Key))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Control);
            if (ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                OnGenerate(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
        }

        if (!AutoCompletePopup.IsOpen) return;
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Down:
                    if (AutoCompleteList.Items.Count > 0)
                    {
                        int next = AutoCompleteList.SelectedIndex + 1;
                        if (next >= AutoCompleteList.Items.Count) next = 0;
                        AutoCompleteList.SelectedIndex = next;
                        AutoCompleteList.ScrollIntoView(AutoCompleteList.SelectedItem);
                    }
                    e.Handled = true;
                break;
                case Windows.System.VirtualKey.Up:
                    if (AutoCompleteList.Items.Count > 0)
                    {
                        int prev = AutoCompleteList.SelectedIndex - 1;
                        if (prev < 0) prev = AutoCompleteList.Items.Count - 1;
                        AutoCompleteList.SelectedIndex = prev;
                        AutoCompleteList.ScrollIntoView(AutoCompleteList.SelectedItem);
                    }
                    e.Handled = true;
                break;
                case Windows.System.VirtualKey.Tab:
                    if (AutoCompleteList.SelectedItem is AutoCompleteItem tabSel)
                        InsertAutoCompleteTag(tabSel.InsertText);
                else if (AutoCompleteList.Items.Count > 0 &&
                         AutoCompleteList.Items[0] is AutoCompleteItem tabFirst)
                    InsertAutoCompleteTag(tabFirst.InsertText);
                    e.Handled = true;
                break;
                case Windows.System.VirtualKey.Enter:
                    if (AutoCompleteList.SelectedIndex >= 0 &&
                        AutoCompleteList.SelectedItem is AutoCompleteItem enterSel)
                        InsertAutoCompleteTag(enterSel.InsertText);
                else if (AutoCompleteList.Items.Count > 0 &&
                         AutoCompleteList.Items[0] is AutoCompleteItem enterFirst)
                    InsertAutoCompleteTag(enterFirst.InsertText);
                else
                    CloseAutoComplete();
                e.Handled = true;
                    break;
                case Windows.System.VirtualKey.Escape:
                    CloseAutoComplete();
                    e.Handled = true;
                break;
            }
        }

    private void OnPromptKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Control);
            if (ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                OnGenerate(sender, new RoutedEventArgs());
                e.Handled = true;
            }
        }
    }
}
