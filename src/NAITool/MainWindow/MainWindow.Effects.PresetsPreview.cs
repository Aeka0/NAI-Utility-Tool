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
    private static bool HasEffectsPresets()
    {
        EnsureDefaultFxPresets();
        if (!Directory.Exists(FxPresetsDir)) return false;
        return Directory.EnumerateFiles(FxPresetsDir, "*.json").Any();
    }

    private static void EnsureDefaultFxPresets()
    {
        try
        {
            if (!Directory.Exists(DefaultFxPresetsDir)) return;

            Directory.CreateDirectory(FxPresetsDir);
            foreach (string sourcePath in Directory.EnumerateFiles(DefaultFxPresetsDir, "*.json"))
            {
                string targetPath = Path.Combine(FxPresetsDir, Path.GetFileName(sourcePath));
                if (!File.Exists(targetPath))
                    File.Copy(sourcePath, targetPath, overwrite: false);
            }
        }
        catch
        {
            // 默认预设复制失败时静默跳过，不影响主流程。
        }
    }

    private static string SanitizePresetFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name.Trim())
            sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString().Trim();
    }

    private static EffectEntry RehydratePresetEffect(EffectEntry x) => new()
    {
        Type = x.Type,
        Value1 = x.Value1,
        Value2 = x.Value2,
        Value3 = x.Value3,
        Value4 = x.Value4,
        Value5 = x.Value5,
        Value6 = x.Value6,
        TextValue = x.TextValue ?? "",
    };

    private async void OnAddEffectsPreset(object sender, RoutedEventArgs e)
    {
        if (_effects.Count == 0)
        {
            TxtStatus.Text = L("post.preset.none_to_save");
            return;
        }

        var nameBox = new TextBox
        {
            PlaceholderText = L("post.preset.enter_name_hint"),
            Text = Lf("post.preset.default_name", DateTime.Now.ToString("MMdd_HHmm")),
            MinWidth = 260,
        };

        var dialog = new ContentDialog
        {
            Title = L("menu.edit.add_preset"),
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = L("post.preset.enter_name") },
                    nameBox,
                },
            },
            PrimaryButtonText = L("common.save"),
            CloseButtonText = L("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        string presetName = (nameBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(presetName))
        {
            TxtStatus.Text = L("post.preset.name_required");
            return;
        }

        string fileName = SanitizePresetFileName(presetName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            TxtStatus.Text = L("post.preset.name_invalid");
            return;
        }

        var payload = new EffectsPresetFile
        {
            Name = presetName,
            SavedAt = DateTime.Now,
            Effects = _effects.Select(CloneEffect).ToList(),
        };

        try
        {
            Directory.CreateDirectory(FxPresetsDir);
            string path = Path.Combine(FxPresetsDir, $"{fileName}.json");
            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
            TxtStatus.Text = Lf("post.preset.saved", presetName);
            UpdateDynamicMenuStates();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("post.preset.save_failed", ex.Message);
        }
    }

    private async void OnUseEffectsPreset(object sender, RoutedEventArgs e)
    {
        EnsureDefaultFxPresets();
        if (!Directory.Exists(FxPresetsDir))
        {
            TxtStatus.Text = L("post.preset.none_available");
            return;
        }

        var files = Directory.EnumerateFiles(FxPresetsDir, "*.json")
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .ToList();
        if (files.Count == 0)
        {
            TxtStatus.Text = L("post.preset.none_available");
            return;
        }

        var fileToDisplay = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var presetCombo = new ComboBox { MinWidth = 320, FontFamily = UiTextFontFamily };
        foreach (var file in files)
        {
            string label = Path.GetFileNameWithoutExtension(file);
            try
            {
                var parsed = JsonSerializer.Deserialize<EffectsPresetFile>(await File.ReadAllTextAsync(file));
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.Name))
                    label = parsed.Name;
            }
            catch { }

            string uniqueLabel = label;
            int suffix = 2;
            while (fileToDisplay.ContainsKey(uniqueLabel))
            {
                uniqueLabel = $"{label} ({suffix})";
                suffix++;
            }
            fileToDisplay[uniqueLabel] = file;
            presetCombo.Items.Add(CreateTextComboBoxItem(uniqueLabel));
        }
        presetCombo.SelectedIndex = 0;
        ApplyMenuTypography(presetCombo);

        var dialog = new ContentDialog
        {
            Title = L("menu.edit.use_preset"),
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = L("post.preset.use_hint") },
                    presetCombo,
                },
            },
            PrimaryButtonText = L("button.apply"),
            CloseButtonText = L("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        string? selectedName = GetSelectedComboText(presetCombo);
        if (selectedName == null || !fileToDisplay.TryGetValue(selectedName, out var selectedFile))
            return;

        try
        {
            var parsed = JsonSerializer.Deserialize<EffectsPresetFile>(await File.ReadAllTextAsync(selectedFile));
            var loadedEffects = parsed?.Effects ?? new List<EffectEntry>();
            if (loadedEffects.Count == 0)
            {
                TxtStatus.Text = L("post.preset.empty");
                return;
            }

            PushEffectsUndoState();
            _effects.Clear();
            foreach (var fx in loadedEffects.Take(10))
                _effects.Add(RehydratePresetEffect(fx));

            _selectedEffectId = _effects.Count > 0 ? _effects[0].Id : null;
            RefreshEffectsPanel();
            RefreshEffectsOverlay();
            QueueEffectsPreviewRefresh(immediate: true);
            UpdateDynamicMenuStates();
            UpdateFileMenuState();
            TxtStatus.Text = Lf("post.preset.applied", selectedName);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("post.preset.load_failed", ex.Message);
        }
    }

    private void OnClearAllEffects(object sender, RoutedEventArgs e)
    {
        if (_effects.Count == 0) return;
        PushEffectsUndoState();
        _effects.Clear();
        _selectedEffectId = null;
        RefreshEffectsPanel();
        QueueEffectsPreviewRefresh();
        UpdateDynamicMenuStates();
        UpdateFileMenuState();
        RefreshEffectsOverlay();
        TxtStatus.Text = L("post.status.effects_cleared");
    }

    private async void OnApplyEffects(object sender, RoutedEventArgs e)
    {
        if (_effectsImageBytes == null || _effects.Count == 0) return;
        PushEffectsUndoState();

        var bytes = await GetEffectsSaveBytesAsync();
        if (bytes == null)
        {
            TxtStatus.Text = L("post.status.no_image_to_apply");
            return;
        }

        _effectsImageBytes = bytes;
        _effectsPreviewImageBytes = bytes;
        ReplaceEffectsSourceBitmap(bytes);
        _effectsPreviewVersion++;
        _effects.Clear();
        _selectedEffectId = null;
        RefreshEffectsPanel();
        await ShowEffectsPreviewAsync(bytes, fitToScreen: false);
        UpdateDynamicMenuStates();
        UpdateFileMenuState();
        TxtStatus.Text = L("post.status.effects_applied");
    }

    private async Task LoadEffectsImageAsync(string filePath)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            await LoadEffectsImageFromBytesAsync(bytes, filePath);
            TxtStatus.Text = Lf("post.status.loaded_source", Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("common.load_failed", ex.Message);
        }
    }

    private Task LoadEffectsImageFromBytesAsync(byte[] bytes, string? filePath = null)
    {
        PushEffectsUndoState();
        _effectsImageBytes = bytes;
        _effectsPreviewImageBytes = bytes;
        ReplaceEffectsSourceBitmap(bytes);
        _effectsImagePath = filePath;
        MarkEffectsWorkspaceClean();
        if (_currentMode == AppMode.Effects)
            ReplaceEditMenu();
        UpdateFileMenuState();
        QueueEffectsPreviewRefresh(fitToScreen: true);
        return Task.CompletedTask;
    }

    private async Task ReloadEffectsImageAsync(string filePath)
    {
        try
        {
            bool wasDirty = _effectsWorkspaceDirty;
            var bytes = await File.ReadAllBytesAsync(filePath);
            using var bitmap = SKBitmap.Decode(bytes);
            int width = bitmap?.Width ?? 0;
            int height = bitmap?.Height ?? 0;
            await LoadEffectsImageFromBytesAsync(bytes, filePath);
            _effectsWorkspaceDirty = wasDirty;
            TxtStatus.Text = Lf("image.reload.loaded", Path.GetFileName(filePath), width, height);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("common.load_failed", ex.Message);
        }
    }

    private void ReplaceEffectsSourceBitmap(byte[]? bytes)
    {
        _effectsSourceBitmap?.Dispose();
        _effectsSourceBitmap = null;
        if (bytes == null || bytes.Length == 0)
            return;

        using var decoded = SKBitmap.Decode(bytes);
        if (decoded == null)
            return;

        _effectsSourceBitmap = decoded.Copy();
    }

    private async Task SendBytesToEffectsAsync(byte[] bytes, string? sourcePath = null)
    {
        SwitchMode(AppMode.Effects);
        await LoadEffectsImageFromBytesAsync(bytes, sourcePath);
        TxtStatus.Text = sourcePath != null
            ? Lf("post.status.sent_to_post_with_name", Path.GetFileName(sourcePath))
            : L("post.status.sent_to_post");
    }

    private void QueueEffectsPreviewRefresh(bool fitToScreen = false, bool immediate = false)
    {
        _effectsPreviewQueuedFit |= fitToScreen;
        if (_effectsPreviewTimer == null)
        {
            _ = RenderQueuedEffectsPreview();
            return;
        }

        _effectsPreviewTimer.Stop();
        _effectsPreviewTimer.Interval = TimeSpan.FromMilliseconds(immediate ? 1 : 60);
        _effectsPreviewTimer.Start();
    }

    private async Task RenderQueuedEffectsPreview()
    {
        int version = ++_effectsPreviewVersion;
        bool fitToScreen = _effectsPreviewQueuedFit;
        _effectsPreviewQueuedFit = false;
        var sourceBytes = _effectsImageBytes;
        var sourceBitmap = _effectsSourceBitmap;

        if (sourceBytes == null)
        {
            _effectsPreviewImageBytes = null;
            ReplaceEffectsSourceBitmap(null);
            EffectsPreviewImage.Source = null;
            EffectsImagePlaceholder.Visibility = Visibility.Visible;
            UpdateDynamicMenuStates();
            return;
        }

        var snapshot = _effects
            .Select(CloneEffect)
            .ToList();

        try
        {
            if (snapshot.Count == 0)
            {
                _effectsPreviewImageBytes = sourceBytes;
                await ShowEffectsPreviewAsync(sourceBytes, fitToScreen);
                UpdateDynamicMenuStates();
                return;
            }

            using var previewBitmap = await Task.Run(() => RenderEffectsPreview(sourceBitmap, sourceBytes, snapshot));

            if (version != _effectsPreviewVersion) return;

            _effectsPreviewImageBytes = null;
            await ShowEffectsPreviewBitmapAsync(previewBitmap, fitToScreen);
            UpdateDynamicMenuStates();
        }
        catch (Exception ex)
        {
            if (version != _effectsPreviewVersion) return;
            DebugLog($"[Effects] Preview failed: {ex}");
            TxtStatus.Text = Lf("post.error.preview_failed", ex.Message);
        }
    }

    private async Task<byte[]?> GetEffectsSaveBytesAsync()
    {
        var sourceBytes = _effectsImageBytes;
        if (sourceBytes == null) return null;
        if (_effects.Count == 0) return sourceBytes;

        var snapshot = _effects
            .Select(CloneEffect)
            .ToList();
        return await Task.Run(() => RenderEffects(sourceBytes, snapshot));
    }

    private async Task ShowEffectsPreviewBitmapAsync(SKBitmap bitmap, bool fitToScreen)
    {
        var writeable = new WriteableBitmap(bitmap.Width, bitmap.Height);
        byte[] buffer = new byte[bitmap.ByteCount];
        Marshal.Copy(bitmap.GetPixels(), buffer, 0, buffer.Length);
        using (var stream = writeable.PixelBuffer.AsStream())
        {
            stream.Seek(0, SeekOrigin.Begin);
            await stream.WriteAsync(buffer, 0, buffer.Length);
            stream.SetLength(buffer.Length);
        }

        EffectsPreviewImage.Source = writeable;
        EffectsPreviewContent.Width = writeable.PixelWidth;
        EffectsPreviewContent.Height = writeable.PixelHeight;
        EffectsOverlayCanvas.Width = writeable.PixelWidth;
        EffectsOverlayCanvas.Height = writeable.PixelHeight;
        EffectsImagePlaceholder.Visibility = Visibility.Collapsed;
        RefreshEffectsOverlay();

        if (fitToScreen)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => FitEffectsPreviewToScreen());
        }
    }

    private async Task ShowEffectsPreviewAsync(byte[] bytes, bool fitToScreen)
    {
        var bitmapImage = new BitmapImage();
        using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using var writer = new Windows.Storage.Streams.DataWriter(ms);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        writer.DetachStream();
        ms.Seek(0);
        await bitmapImage.SetSourceAsync(ms);
        EffectsPreviewImage.Source = bitmapImage;
        EffectsPreviewContent.Width = bitmapImage.PixelWidth;
        EffectsPreviewContent.Height = bitmapImage.PixelHeight;
        EffectsOverlayCanvas.Width = bitmapImage.PixelWidth;
        EffectsOverlayCanvas.Height = bitmapImage.PixelHeight;
        EffectsImagePlaceholder.Visibility = Visibility.Collapsed;
        RefreshEffectsOverlay();

        if (fitToScreen)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => FitEffectsPreviewToScreen());
        }
    }

    private void FitEffectsPreviewToScreen()
    {
        if (EffectsPreviewImage.Source is not BitmapSource bmp) return;
        double imgW = bmp.PixelWidth;
        double imgH = bmp.PixelHeight;
        if (imgW <= 0 || imgH <= 0) return;

        double viewW = EffectsImageScroller.ViewportWidth;
        double viewH = EffectsImageScroller.ViewportHeight;
        if (viewW <= 0 || viewH <= 0) return;

        float zoom = (float)Math.Min(viewW / imgW, viewH / imgH);
        zoom = Math.Min(zoom, 1.0f);
        EffectsImageScroller.ChangeView(0, 0, zoom);
    }

    private void CenterEffectsPreview()
    {
        if (EffectsPreviewImage.Source is not BitmapSource bmp) return;
        double contentW = bmp.PixelWidth * EffectsImageScroller.ZoomFactor;
        double contentH = bmp.PixelHeight * EffectsImageScroller.ZoomFactor;
        double offsetX = Math.Max(0, (contentW - EffectsImageScroller.ViewportWidth) / 2);
        double offsetY = Math.Max(0, (contentH - EffectsImageScroller.ViewportHeight) / 2);
        EffectsImageScroller.ChangeView(offsetX, offsetY, null);
    }

    private void RefreshEffectsOverlay()
    {
        if (EffectsOverlayCanvas == null) return;

        EffectsOverlayCanvas.Children.Clear();
        var effect = GetSelectedEffect();
        if (effect == null || !IsRegionEffect(effect.Type) || EffectsPreviewImage.Source is not BitmapSource bmp)
        {
            EffectsOverlayCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        EffectsOverlayCanvas.Visibility = Visibility.Visible;
        GetEffectRegionValues(effect, out double centerX, out double centerY, out double widthPct, out double heightPct);
        GetEffectRect(bmp.PixelWidth, bmp.PixelHeight, centerX, centerY, widthPct, heightPct,
            out int left, out int top, out int right, out int bottom);

        var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = Math.Max(1, right - left),
            Height = Math.Max(1, bottom - top),
            Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 215)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0, 120, 215)),
            RadiusX = 4,
            RadiusY = 4,
            Tag = "region",
        };
        Canvas.SetLeft(rect, left);
        Canvas.SetTop(rect, top);
        EffectsOverlayCanvas.Children.Add(rect);

        var handle = new Border
        {
            Width = 12,
            Height = 12,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 215)),
            CornerRadius = new CornerRadius(2),
            Tag = "resize",
        };
        Canvas.SetLeft(handle, right - 6);
        Canvas.SetTop(handle, bottom - 6);
        EffectsOverlayCanvas.Children.Add(handle);
    }
}
