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
    private bool IsSuperDropEnabled => _settings.Settings.SuperDropEnabled;

    private void ApplyDragDropModeSetting()
    {
        bool useLegacyDrop = !IsSuperDropEnabled;

        RootGrid.AllowDrop = IsSuperDropEnabled;
        GenPreviewArea.AllowDrop = useLegacyDrop;
        InspectPreviewArea.AllowDrop = useLegacyDrop;
        EffectsPreviewArea.AllowDrop = useLegacyDrop;
        UpscalePreviewArea.AllowDrop = useLegacyDrop;
        MaskCanvas.AllowDrop = useLegacyDrop;
        MaskCanvas.IsImageFileDropEnabled = useLegacyDrop;

        if (!IsSuperDropEnabled)
            HideSuperDropOverlay();
    }

    private bool TryAcceptImageFileDrag(DragEventArgs e)
    {
        if (IsSuperDropEnabled || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return false;

        e.AcceptedOperation = DataPackageOperation.Copy;
        return true;
    }

    private async Task<StorageFile?> GetFirstDroppedImageFileAsync(DragEventArgs e, bool includeBmp)
    {
        if (IsSuperDropEnabled || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return null;

        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is StorageFile file && IsSupportedDroppedImageFile(file, includeBmp))
                return file;
        }

        return null;
    }

    private static bool IsSupportedDroppedImageFile(StorageFile file, bool includeBmp)
    {
        string ext = file.FileType;
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               (includeBmp && ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase));
    }

    private void ShowSuperDropOverlay()
    {
        if (!IsSuperDropEnabled)
            return;

        if (_superDropOverlayVisible)
            return;

        _superDropOverlayVisible = true;
        SuperDropOverlay.Visibility = Visibility.Visible;
        SuperDropOverlay.Opacity = 0;
        SuperDropCardsHost.Opacity = 0;
        SuperDropCardsScale.ScaleX = 0.96;
        SuperDropCardsScale.ScaleY = 0.96;
        AnimateDouble(SuperDropOverlay, "Opacity", 1, 180);
        AnimateDouble(SuperDropCardsHost, "Opacity", 1, 220);
        AnimateDouble(SuperDropCardsScale, "ScaleX", 1, 220);
        AnimateDouble(SuperDropCardsScale, "ScaleY", 1, 220);
    }

    private void HideSuperDropOverlay()
    {
        if (SuperDropOverlay == null || !_superDropOverlayVisible)
            return;

        _superDropOverlayVisible = false;
        ResetSuperDropCardHighlights();

        var storyboard = new Storyboard();
        storyboard.Children.Add(CreateDoubleAnimation(SuperDropOverlay, "Opacity", 0, 120));
        storyboard.Children.Add(CreateDoubleAnimation(SuperDropCardsHost, "Opacity", 0, 100));
        storyboard.Children.Add(CreateDoubleAnimation(SuperDropCardsScale, "ScaleX", 0.98, 120));
        storyboard.Children.Add(CreateDoubleAnimation(SuperDropCardsScale, "ScaleY", 0.98, 120));
        storyboard.Completed += (_, _) =>
        {
            if (!_superDropOverlayVisible)
                SuperDropOverlay.Visibility = Visibility.Collapsed;
        };
        storyboard.Begin();
    }

    private static DoubleAnimation CreateDoubleAnimation(DependencyObject target, string property, double to, int milliseconds)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    private static void AnimateDouble(DependencyObject target, string property, double to, int milliseconds)
    {
        var storyboard = new Storyboard();
        storyboard.Children.Add(CreateDoubleAnimation(target, property, to, milliseconds));
        storyboard.Begin();
    }

    private void AnimateSuperDropCardHover(Border card, bool isHovering)
    {
        if (card.Child is not Grid grid || grid.Children.Count == 0 || grid.Children[0] is not Border highlight)
            return;

        AnimateDouble(highlight, "Opacity", isHovering ? 0.22 : 0, 130);
        card.BorderThickness = isHovering ? new Thickness(2) : new Thickness(1);
    }

    private void ResetSuperDropCardHighlights()
    {
        foreach (var card in EnumerateSuperDropCards())
            AnimateSuperDropCardHover(card, false);
    }

    private IEnumerable<Border> EnumerateSuperDropCards()
    {
        if (SuperDropCardsHost == null)
            yield break;

        var stack = new Stack<DependencyObject>();
        stack.Push(SuperDropCardsHost);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(current);
            for (int i = 0; i < count; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(current, i);
                if (child is Border { Tag: string })
                    yield return (Border)child;
                stack.Push(child);
            }
        }
    }

    private void ScheduleSuperDropDragCancelCheck()
    {
        int version = _superDropDragVersion;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(140);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_superDropDragVersion == version && _superDropOverlayVisible)
                HideSuperDropOverlay();
        };
        timer.Start();
    }

    private void OnSuperDropCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border card)
            AnimateSuperDropCardHover(card, true);
    }

    private void OnSuperDropCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border card)
            AnimateSuperDropCardHover(card, false);
    }

    private void OnSuperDropCardDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border card)
            AnimateSuperDropCardHover(card, false);

        var p = e.GetPosition(RootGrid);
        if (p.X < 0 || p.Y < 0 || p.X > RootGrid.ActualWidth || p.Y > RootGrid.ActualHeight)
            HideSuperDropOverlay();
        else
            ScheduleSuperDropDragCancelCheck();

        e.Handled = true;
    }

    private bool TryAcceptSuperDropDrag(DragEventArgs e)
    {
        if (!IsSuperDropEnabled || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return false;

        _superDropDragVersion++;
        ShowSuperDropOverlay();
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.Handled = true;
        return true;
    }

    private void OnSuperDropDragEnter(object sender, DragEventArgs e)
    {
        TryAcceptSuperDropDrag(e);
    }

    private void OnSuperDropDragOver(object sender, DragEventArgs e)
    {
        TryAcceptSuperDropDrag(e);
    }

    private void OnSuperDropDragLeave(object sender, DragEventArgs e)
    {
        if (!IsSuperDropEnabled || SuperDropOverlay.Visibility != Visibility.Visible)
            return;

        var p = e.GetPosition(RootGrid);
        if (p.X < 0 || p.Y < 0 || p.X > RootGrid.ActualWidth || p.Y > RootGrid.ActualHeight)
            HideSuperDropOverlay();
        else
            ScheduleSuperDropDragCancelCheck();
    }

    private void OnSuperDropCardDragOver(object sender, DragEventArgs e)
    {
        if (TryAcceptSuperDropDrag(e) && sender is Border card)
        {
            ResetSuperDropCardHighlights();
            AnimateSuperDropCardHover(card, true);
        }
    }

    private void OnSuperDropRootDrop(object sender, DragEventArgs e)
    {
        if (!IsSuperDropEnabled)
            return;

        HideSuperDropOverlay();
        e.Handled = true;
    }

    private async void OnSuperDropCardDrop(object sender, DragEventArgs e)
    {
        if (!IsSuperDropEnabled)
            return;

        HideSuperDropOverlay();
        e.Handled = true;

        if (sender is not FrameworkElement { Tag: string actionText } ||
            !Enum.TryParse(actionText, out SuperDropAction action))
            return;

        var file = await GetFirstSuperDropFileAsync(e);
        if (file == null)
        {
            TxtStatus.Text = L("superdrop.unsupported_file");
            return;
        }
        if (!IsSupportedSuperDropFile(file, action))
        {
            TxtStatus.Text = L("superdrop.unsupported_file");
            return;
        }

        await ExecuteSuperDropActionAsync(action, file);
    }

    private async Task<StorageFile?> GetFirstSuperDropFileAsync(DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return null;

        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is StorageFile file)
                return file;
        }

        return null;
    }

    private static bool IsSupportedSuperDropFile(StorageFile file, SuperDropAction action)
    {
        string ext = file.FileType;
        if ((action == SuperDropAction.GenerateVibe || action == SuperDropAction.I2IVibe) &&
            ext.Equals(".naiv4vibe", StringComparison.OrdinalIgnoreCase))
            return true;

        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ExecuteSuperDropActionAsync(SuperDropAction action, StorageFile file)
    {
        try
        {
            switch (action)
            {
                case SuperDropAction.GeneratePrompt:
                    SwitchMode(AppMode.ImageGeneration);
                    await ApplySuperDropGenerationPromptAsync(file);
                    break;
                case SuperDropAction.GenerateVibe:
                    SwitchMode(AppMode.ImageGeneration);
                    await AddDroppedVibeTransferAsync(file);
                    break;
                case SuperDropAction.GeneratePrecise:
                    SwitchMode(AppMode.ImageGeneration);
                    await AddDroppedPreciseReferenceAsync(file);
                    break;
                case SuperDropAction.I2IPrompt:
                    SwitchMode(AppMode.I2I);
                    await MaskCanvas.LoadImageAsync(file);
                    await ApplySuperDropI2IPromptAsync(file);
                    break;
                case SuperDropAction.I2IVibe:
                    SwitchMode(AppMode.I2I);
                    await AddDroppedVibeTransferAsync(file);
                    break;
                case SuperDropAction.I2IPrecise:
                    SwitchMode(AppMode.I2I);
                    await AddDroppedPreciseReferenceAsync(file);
                    break;
                case SuperDropAction.Upscale:
                    SwitchMode(AppMode.Upscale);
                    await LoadUpscaleImageAsync(file.Path);
                    break;
                case SuperDropAction.Effects:
                    SwitchMode(AppMode.Effects);
                    await LoadEffectsImageAsync(file.Path);
                    break;
                case SuperDropAction.Inspect:
                    SwitchMode(AppMode.Inspect);
                    await LoadInspectImageAsync(file.Path);
                    break;
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("superdrop.failed", ex.Message);
        }
    }
}
