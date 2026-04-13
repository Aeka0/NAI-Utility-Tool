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
    //  缩略图（重绘模式）
    // ═══════════════════════════════════════════════════════════

    private void SetupThumbnailTimer()
    {
        ThumbnailCanvas.Paused = false;
    }

    private void QueueThumbnailRender() { }

    private void OnThumbnailCanvasDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        try
        {
            float width = (float)sender.Size.Width;
            float height = (float)sender.Size.Height;
            if (width <= 0 || height <= 0) return;
            MaskCanvas.RenderThumbnail(args.DrawingSession, width, height);
        }
        catch { }
    }

    private void OnThumbnailPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(ThumbnailContainer);
        if (pt.Properties.IsLeftButtonPressed)
        {
            _thumbDragging = true;
            ThumbnailContainer.CapturePointer(e.Pointer);
            _thumbDragStart = new Vector2((float)pt.Position.X, (float)pt.Position.Y);
            MaskCanvas.BeginMoveImage();
            e.Handled = true;
        }
    }

    private void OnThumbnailPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_thumbDragging) return;
        var pt = e.GetCurrentPoint(ThumbnailContainer);
        var cur = new Vector2((float)pt.Position.X, (float)pt.Position.Y);
        var delta = cur - _thumbDragStart;
        _thumbDragStart = cur;

        float ctrlW = (float)ThumbnailContainer.ActualWidth;
        float ctrlH = (float)ThumbnailContainer.ActualHeight;
        float scale = MaskCanvas.GetThumbnailScale(ctrlW, ctrlH);
        if (scale > 0)
            MaskCanvas.MoveImage(delta / scale);
        e.Handled = true;
    }

    private void OnThumbnailPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_thumbDragging)
        {
            _thumbDragging = false;
            ThumbnailContainer.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  提示词标签页
    // ═══════════════════════════════════════════════════════════

    private void OnPromptTabSwitch(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, TabPositive) && TabPositive.IsChecked == true)
        {
            SaveCurrentPromptToBuffer();
            _isPositiveTab = true;
            TabNegative.IsChecked = false;
            LoadPromptFromBuffer();
            UpdateSplitVisibility();
        }
        else if (ReferenceEquals(sender, TabNegative) && TabNegative.IsChecked == true)
        {
            SaveCurrentPromptToBuffer();
            _isPositiveTab = false;
            TabPositive.IsChecked = false;
            LoadPromptFromBuffer();
            UpdateSplitVisibility();
        }
        else
        {
            if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton tb)
                tb.IsChecked = true;
        }
        UpdatePromptHighlights();
    }

    private void OnSplitPromptToggle(object sender, RoutedEventArgs e)
    {
        _isSplitPrompt = BtnSplitPrompt.IsChecked == true;

        if (_isSplitPrompt)
        {
            string curStyle = _currentMode == AppMode.ImageGeneration
                ? _genStylePrompt : _i2iStylePrompt;
            TxtStylePrompt.Text = curStyle;
        TxtPrompt.PlaceholderText = L("prompt.enter_positive");
        }
        else
        {
            string merged = MergeStyleAndMain(TxtStylePrompt.Text, TxtPrompt.Text);
            TxtPrompt.Text = merged;
            TxtStylePrompt.Text = "";
            if (_currentMode == AppMode.ImageGeneration) _genStylePrompt = "";
            else _i2iStylePrompt = "";
        }

        UpdateSplitVisibility();
        UpdatePromptHighlights();
        SyncRememberedPromptAndParameterState();
    }

    private void UpdateSplitVisibility()
    {
        bool showSplit = _isSplitPrompt && _isPositiveTab;
        StylePromptGrid.Visibility = showSplit ? Visibility.Visible : Visibility.Collapsed;
        BtnSplitPrompt.Visibility = _isPositiveTab ? Visibility.Visible : Visibility.Collapsed;
        UpdatePromptTabText();
    }

    private void OnPromptTabRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePromptTabText();
    }

    private static string MergeStyleAndMain(string style, string main)
    {
        style = style?.Trim() ?? "";
        main = main?.Trim() ?? "";
        if (style.Length > 0 && main.Length > 0) return style + ", " + main;
        return style.Length > 0 ? style : main;
    }

    private void LoadPromptFromBuffer()
    {
        if (_currentMode == AppMode.ImageGeneration)
        {
            TxtPrompt.Text = _isPositiveTab ? _genPositivePrompt : _genNegativePrompt;
            if (_isPositiveTab && _isSplitPrompt) TxtStylePrompt.Text = _genStylePrompt;
        }
        else
        {
            TxtPrompt.Text = _isPositiveTab ? _i2iPositivePrompt : _i2iNegativePrompt;
            if (_isPositiveTab && _isSplitPrompt) TxtStylePrompt.Text = _i2iStylePrompt;
        }
        BtnSplitPrompt.IsChecked = _isSplitPrompt;
        TxtPrompt.PlaceholderText = _isPositiveTab ? L("prompt.enter_positive") : L("prompt.enter_negative");
        _promptBufferLoaded = true;
    }

    private void SaveCurrentPromptToBuffer()
    {
        if (_currentMode == AppMode.ImageGeneration)
        {
            if (_isPositiveTab)
            {
                _genPositivePrompt = TxtPrompt.Text;
                if (_isSplitPrompt) _genStylePrompt = TxtStylePrompt.Text;
            }
            else _genNegativePrompt = TxtPrompt.Text;
            SaveAllCharacterPrompts();
        }
        else
        {
            if (_isPositiveTab)
            {
                _i2iPositivePrompt = TxtPrompt.Text;
                if (_isSplitPrompt) _i2iStylePrompt = TxtStylePrompt.Text;
            }
            else _i2iNegativePrompt = TxtPrompt.Text;
            SaveAllCharacterPrompts();
        }

        SyncRememberedPromptAndParameterState();
    }

    private (string Positive, string Negative) GetPrompts(WildcardExpandContext? wildcardContext = null)
    {
        SaveCurrentPromptToBuffer();
        string genStyle = _currentMode == AppMode.ImageGeneration ? _genStylePrompt : _i2iStylePrompt;
        string genPos = _currentMode == AppMode.ImageGeneration ? _genPositivePrompt : _i2iPositivePrompt;
        string neg = _currentMode == AppMode.ImageGeneration ? _genNegativePrompt : _i2iNegativePrompt;
        string positiveRaw = MergeStyleAndMain(genStyle, genPos);
        if (wildcardContext == null)
            return (ExpandPromptShortcuts(positiveRaw), ExpandPromptShortcuts(neg));

        string positive = ExpandPromptFeatures(positiveRaw, wildcardContext);
        string negative = ExpandPromptFeatures(neg, wildcardContext, isNegativeText: true);
        return (positive, negative);
    }

    private void LoadPromptShortcuts()
    {
        _promptShortcuts.Clear();
        try
        {
            if (!File.Exists(PromptShortcutsFilePath))
                return;
            var json = File.ReadAllText(PromptShortcutsFilePath);
            var items = JsonSerializer.Deserialize<List<PromptShortcutEntry>>(json) ?? new();
            _promptShortcuts.AddRange(items
                .Where(x => !string.IsNullOrWhiteSpace(x.Shortcut) && !string.IsNullOrWhiteSpace(x.Prompt))
                .Select(x => new PromptShortcutEntry
                {
                    Shortcut = x.Shortcut.Trim(),
                    Prompt = x.Prompt.Trim(),
                }));
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("prompt_shortcuts.load_failed", ex.Message);
        }
    }

    private void SavePromptShortcuts(IEnumerable<PromptShortcutEntry> items)
    {
        var normalized = items
            .Where(x => !string.IsNullOrWhiteSpace(x.Shortcut) && !string.IsNullOrWhiteSpace(x.Prompt))
            .GroupBy(x => x.Shortcut.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new PromptShortcutEntry
            {
                Shortcut = g.First().Shortcut.Trim(),
                Prompt = g.First().Prompt.Trim(),
            })
            .OrderBy(x => x.Shortcut, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(PromptShortcutsFilePath)!);
        File.WriteAllText(PromptShortcutsFilePath, JsonSerializer.Serialize(normalized, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));

        _promptShortcuts.Clear();
        _promptShortcuts.AddRange(normalized);
    }

    private string ExpandPromptShortcuts(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _promptShortcuts.Count == 0)
            return text;

        var shortcutMap = _promptShortcuts
            .Where(x => !string.IsNullOrWhiteSpace(x.Shortcut) && !string.IsNullOrWhiteSpace(x.Prompt))
            .GroupBy(x => x.Shortcut.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Prompt.Trim(), StringComparer.OrdinalIgnoreCase);

        if (shortcutMap.Count == 0)
            return text;

        var parts = Regex.Split(text, @"[,\r\n]+");
        for (int i = 0; i < parts.Length; i++)
        {
            string token = parts[i].Trim();
            if (shortcutMap.TryGetValue(token, out var fullPrompt))
                parts[i] = fullPrompt;
            else
                parts[i] = token;
        }
        return string.Join(", ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    // ═══════════════════════════════════════════════════════════
    //  角色提示词管理
    // ═══════════════════════════════════════════════════════════
}
