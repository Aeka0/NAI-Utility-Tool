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
        MaskCanvas.Brush.CurrentTool = tool;
        BtnBrush.IsChecked = tool == StrokeTool.Brush;
        BtnEraser.IsChecked = tool == StrokeTool.Eraser;
        BtnRect.IsChecked = tool == StrokeTool.Rectangle;
    }

    private void OnToolBrush(object sender, RoutedEventArgs e) => SetToolSelection(StrokeTool.Brush);
    private void OnToolEraser(object sender, RoutedEventArgs e) => SetToolSelection(StrokeTool.Eraser);
    private void OnToolRect(object sender, RoutedEventArgs e) => SetToolSelection(StrokeTool.Rectangle);

    private void OnBrushSizeChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (MaskCanvas == null) return;
        MaskCanvas.Brush.BrushSize = (float)e.NewValue;
        if (TxtBrushSize != null) TxtBrushSize.Text = $"{(int)e.NewValue}";
    }

    private void OnTogglePreviewMask(object sender, RoutedEventArgs e)
    {
        MaskCanvas.PreviewMaskOnly = ChkPreviewMask.IsChecked == true;
    }

    // ═══════════════════════════════════════════════════════════
    //  键盘快捷键
    // ═══════════════════════════════════════════════════════════

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

        if (FocusManager.GetFocusedElement(this.Content.XamlRoot) is TextBox or PasswordBox)
            return;

        if (_currentMode == AppMode.I2I)
        {
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

        char row = up ? 'T' : down ? 'B' : 'C';
        char col = left ? 'L' : right ? 'R' : 'C';

        var tag = $"{row}{col}";
        MaskCanvas.AlignImage(tag);
        TxtStatus.Text = L("image.aligned");
        return true;
    }

    private void OnPromptPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
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
