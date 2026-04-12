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
    private async void OnMaskCanvasImageFileLoaded(string filePath)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            var meta = await Task.Run(() => ImageMetadataService.ReadFromBytes(bytes));
            if (meta != null && (meta.IsNaiParsed || meta.IsSdFormat))
                ApplyMetadataToI2I(meta, Path.GetFileName(filePath));
        }
        catch { }
    }

    private void OnInspectDragOver(object sender, DragEventArgs e)
    {
        TryAcceptImageFileDrag(e);
    }

    private async void OnInspectDrop(object sender, DragEventArgs e)
    {
        if (IsSuperDropEnabled || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        var file = await GetFirstDroppedImageFileAsync(e, includeBmp: false);
        if (file != null)
        {
            await LoadInspectImageAsync(file.Path);
            return;
        }

        TxtStatus.Text = L("common.unsupported_file_format_inspect");
    }

    private void OnEffectsDragOver(object sender, DragEventArgs e)
    {
        TryAcceptImageFileDrag(e);
    }

    private async void OnEffectsDrop(object sender, DragEventArgs e)
    {
        if (IsSuperDropEnabled || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        var file = await GetFirstDroppedImageFileAsync(e, includeBmp: true);
        if (file != null)
        {
            await LoadEffectsImageAsync(file.Path);
            return;
        }

        TxtStatus.Text = L("common.unsupported_file_format_post");
    }

    private void OnEffectsOverlayPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var effect = GetSelectedEffect();
        if (effect == null || !IsRegionEffect(effect.Type) || EffectsPreviewImage.Source is not BitmapImage bmp)
            return;

        var pos = e.GetCurrentPoint(EffectsOverlayCanvas).Position;
        GetEffectRegionValues(effect, out double centerX, out double centerY, out double widthPct, out double heightPct);
        GetEffectRect(bmp.PixelWidth, bmp.PixelHeight, centerX, centerY, widthPct, heightPct,
            out int left, out int top, out int right, out int bottom);

        bool inResize = Math.Abs(pos.X - right) <= 12 && Math.Abs(pos.Y - bottom) <= 12;
        bool inRect = pos.X >= left && pos.X <= right && pos.Y >= top && pos.Y <= bottom;
        if (!inResize && !inRect)
        {
            OnPreviewDragStart(sender, e);
            return;
        }

        PushEffectsUndoState();
        _effectsRegionDragging = inRect && !inResize;
        _effectsRegionResizing = inResize;
        _effectsRegionDragStart = pos;
        _effectsRegionStartCenterX = centerX;
        _effectsRegionStartCenterY = centerY;
        _effectsRegionStartWidth = widthPct;
        _effectsRegionStartHeight = heightPct;
        EffectsOverlayCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnEffectsOverlayPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_effectsRegionDragging && !_effectsRegionResizing)
        {
            OnPreviewDragMove(sender, e);
            return;
        }
        if (EffectsPreviewImage.Source is not BitmapImage bmp) return;

        var effect = GetSelectedEffect();
        if (effect == null) return;

        var pos = e.GetCurrentPoint(EffectsOverlayCanvas).Position;
        double dxPct = (pos.X - _effectsRegionDragStart.X) / Math.Max(1, bmp.PixelWidth) * 100.0;
        double dyPct = (pos.Y - _effectsRegionDragStart.Y) / Math.Max(1, bmp.PixelHeight) * 100.0;

        double centerX = _effectsRegionStartCenterX;
        double centerY = _effectsRegionStartCenterY;
        double widthPct = _effectsRegionStartWidth;
        double heightPct = _effectsRegionStartHeight;

        if (_effectsRegionDragging)
        {
            centerX = Math.Clamp(_effectsRegionStartCenterX + dxPct, 0, 100);
            centerY = Math.Clamp(_effectsRegionStartCenterY + dyPct, 0, 100);
        }
        else if (_effectsRegionResizing)
        {
            widthPct = Math.Clamp(_effectsRegionStartWidth + dxPct * 2.0, 1, 100);
            heightPct = Math.Clamp(_effectsRegionStartHeight + dyPct * 2.0, 1, 100);
        }

        SetEffectRegionValues(effect, centerX, centerY, widthPct, heightPct);
        RefreshEffectsOverlay();
        QueueEffectsPreviewRefresh();
        UpdateFileMenuState();
        e.Handled = true;
    }

    private void OnEffectsOverlayPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_effectsRegionDragging && !_effectsRegionResizing)
        {
            OnPreviewDragEnd(sender, e);
            return;
        }
        _effectsRegionDragging = false;
        _effectsRegionResizing = false;
        EffectsOverlayCanvas.ReleasePointerCapture(e.Pointer);
        RefreshEffectsPanel();
        RefreshEffectsOverlay();
        QueueEffectsPreviewRefresh(immediate: true);
        e.Handled = true;
    }
}
