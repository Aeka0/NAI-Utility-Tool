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
    private async void OnHistorySendToInspect(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string filePath)
        {
            SwitchMode(AppMode.Inspect);
            await LoadInspectImageAsync(filePath);
        }
    }

    private async void OnHistorySendToEffects(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string filePath)
        {
            SwitchMode(AppMode.Effects);
            await LoadEffectsImageAsync(filePath);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  历史记录
    // ═══════════════════════════════════════════════════════════

    private async void LoadHistoryAsync(bool preserveSelection = false)
    {
        var requestedDate = preserveSelection ? _selectedHistoryDate : null;

        await Task.Run(() =>
        {
            lock (_historyFiles)
            {
                _historyByDate.Clear();
                _historyAvailableDates.Clear();
                _historyAvailableDateSet.Clear();
                _historyFiles.Clear();
            }

            if (!Directory.Exists(OutputBaseDir)) return;

            var dateDirs = Directory.GetDirectories(OutputBaseDir)
                .Select(d => new DirectoryInfo(d))
                .Where(d => DateTime.TryParseExact(d.Name, "yyyy-MM-dd", null,
                    DateTimeStyles.None, out _))
                .OrderByDescending(d => d.Name)
                .ToList();

            foreach (var dir in dateDirs)
            {
                var files = Directory.GetFiles(dir.FullName, "*.png")
                    .OrderByDescending(f => new FileInfo(f).CreationTime)
                    .ToList();
                if (files.Count > 0)
                {
                    lock (_historyFiles)
                    {
                        _historyByDate[dir.Name] = files;
                        _historyAvailableDates.Add(dir.Name);
                        _historyAvailableDateSet.Add(dir.Name);
                    }
                }
            }
        });

        DispatcherQueue.TryEnqueue(() =>
        {
            string? targetDate = requestedDate;
            if (targetDate != null && !IsHistoryDateSelectable(targetDate))
                targetDate = null;

            if (_historyAvailableDates.Count > 0)
            {
                _selectedHistoryDate = targetDate ?? _historyAvailableDates[0];
                BuildHistoryFileList();
                _historyLoadedCount = Math.Min(HistoryPageSize, _historyFiles.Count);
                RefreshHistoryPanel();

                var date = DateTimeOffset.ParseExact(_selectedHistoryDate, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);
                HistoryDatePicker.Date = date;
            }
            else
            {
                _selectedHistoryDate = targetDate;
                if (_selectedHistoryDate != null)
                {
                    var date = DateTimeOffset.ParseExact(_selectedHistoryDate, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture);
                    HistoryDatePicker.Date = date;
                }
                RefreshHistoryPanel();
            }
        });
    }

    private void AddHistoryItem(string filePath)
    {
        var dateStr = GetDateFromFilePath(filePath);
        if (dateStr == null) return;

        if (!_historyByDate.ContainsKey(dateStr))
        {
            _historyByDate[dateStr] = new List<string>();
            int insertIdx = _historyAvailableDates.FindIndex(
                d => string.Compare(d, dateStr, StringComparison.Ordinal) < 0);
            if (insertIdx < 0) insertIdx = _historyAvailableDates.Count;
            _historyAvailableDates.Insert(insertIdx, dateStr);
            _historyAvailableDateSet.Add(dateStr);
        }
        _historyByDate[dateStr].Insert(0, filePath);

        if (_selectedHistoryDate == null)
        {
            _selectedHistoryDate = dateStr;
            var date = DateTimeOffset.ParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            HistoryDatePicker.Date = date;
        }

        BuildHistoryFileList();
        if (_selectedHistoryDate != null &&
            string.Compare(dateStr, _selectedHistoryDate, StringComparison.Ordinal) <= 0)
        {
            _historyLoadedCount = Math.Min(
                Math.Max(_historyLoadedCount + 1, HistoryPageSize), _historyFiles.Count);
            RefreshHistoryPanel();
        }
    }

    private void BuildHistoryFileList()
    {
        _historyFiles.Clear();
        if (_selectedHistoryDate == null || _historyAvailableDates.Count == 0) return;

        int startIdx = _historyAvailableDates.IndexOf(_selectedHistoryDate);
        if (startIdx < 0)
        {
            if (string.Equals(_selectedHistoryDate, GetTodayHistoryDateString(), StringComparison.Ordinal))
                return;
            startIdx = 0;
        }

        for (int i = startIdx; i < _historyAvailableDates.Count; i++)
        {
            if (_historyByDate.TryGetValue(_historyAvailableDates[i], out var files))
                _historyFiles.AddRange(files);
        }
    }

    private void SetupHistoryDateRefreshTimer()
    {
        _historyTodayDateMarker = GetTodayHistoryDateString();
        _historyDateRefreshTimer = DispatcherQueue.CreateTimer();
        _historyDateRefreshTimer.IsRepeating = false;
        _historyDateRefreshTimer.Tick += (_, _) =>
        {
            RefreshHistoryDatePickerRange();

            var today = GetTodayHistoryDateString();
            if (!string.Equals(today, _historyTodayDateMarker, StringComparison.Ordinal))
            {
                _historyTodayDateMarker = today;
                LoadHistoryAsync(preserveSelection: true);
            }

            ScheduleNextHistoryDateRefresh();
        };
        ScheduleNextHistoryDateRefresh();
    }

    private void ScheduleNextHistoryDateRefresh()
    {
        if (_historyDateRefreshTimer == null) return;

        var now = DateTime.Now;
        var nextRefresh = now.Date.AddDays(1).AddSeconds(1);
        var interval = nextRefresh - now;
        if (interval < TimeSpan.FromSeconds(1))
            interval = TimeSpan.FromSeconds(1);

        _historyDateRefreshTimer.Stop();
        _historyDateRefreshTimer.Interval = interval;
        _historyDateRefreshTimer.Start();
    }

    private void RefreshHistoryDatePickerRange()
    {
        var now = DateTimeOffset.Now;
        HistoryDatePicker.MinDate = now.AddYears(-100);
        HistoryDatePicker.MaxDate = now.AddYears(1);
    }

    private static string? GetDateFromFilePath(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        return dir == null ? null : new DirectoryInfo(dir).Name;
    }

    private static UIElement CreateDateSeparator(string dateStr)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var line1 = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Opacity = 0.4,
        };
        Grid.SetColumn(line1, 0);

        var text = new TextBlock
        {
            Text = dateStr,
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
        };
        Grid.SetColumn(text, 1);

        var line2 = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Opacity = 0.4,
        };
        Grid.SetColumn(line2, 2);

        grid.Children.Add(line1);
        grid.Children.Add(text);
        grid.Children.Add(line2);
        return grid;
    }

    private void OnHistoryDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (!args.NewDate.HasValue) return;
        var dateStr = args.NewDate.Value.ToString("yyyy-MM-dd");
        if (dateStr == _selectedHistoryDate) return;
        if (!IsHistoryDateSelectable(dateStr)) return;

        _selectedHistoryDate = dateStr;
        BuildHistoryFileList();
        _historyLoadedCount = Math.Min(HistoryPageSize, _historyFiles.Count);
        RefreshHistoryPanel();
        HistoryScroller.ChangeView(null, 0, null);
    }

    private static string GetTodayHistoryDateString() => DateTime.Now.ToString("yyyy-MM-dd");

    private bool IsHistoryDateSelectable(string dateStr) =>
        _historyAvailableDateSet.Contains(dateStr) ||
        string.Equals(dateStr, GetTodayHistoryDateString(), StringComparison.Ordinal);

    private void OnHistoryCalendarDayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
    {
        if (args.Item == null) return;
        var dateStr = args.Item.Date.ToString("yyyy-MM-dd");
        bool hasHistory = _historyAvailableDateSet.Contains(dateStr);
        bool isToday = string.Equals(dateStr, GetTodayHistoryDateString(), StringComparison.Ordinal);
        bool isSelectable = hasHistory || isToday;
        args.Item.IsBlackout = false;
        args.Item.IsEnabled = isSelectable;
        args.Item.Opacity = isSelectable ? 1.0 : 0.4;
    }

    private void RefreshHistoryPanel()
    {
        HistoryPanel.Children.Clear();
        _historyLoadedCount = Math.Min(_historyLoadedCount, _historyFiles.Count);

        if (_historyFiles.Count == 0)
        {
            HistoryPanel.Children.Add(new TextBlock
            {
                Text = L("history.empty"),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 20, 0, 0),
            });
            return;
        }

        string? lastDate = null;
        int count = Math.Min(_historyLoadedCount, _historyFiles.Count);
        for (int i = 0; i < count; i++)
        {
            var filePath = _historyFiles[i];
            var fileDate = GetDateFromFilePath(filePath);
            if (fileDate != lastDate)
            {
                if (lastDate != null)
                    HistoryPanel.Children.Add(CreateDateSeparator(fileDate ?? L("history.unknown_date")));
                lastDate = fileDate;
            }
            var border = CreateHistoryThumbnail(filePath);
            HistoryPanel.Children.Add(border);
        }
    }

    private async void OnHistoryScrollViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroller) return;
        if (_historyLoadingMore) return;
        if (_historyLoadedCount >= _historyFiles.Count) return;
        if (scroller.ScrollableHeight <= 0) return;

        if (scroller.VerticalOffset < scroller.ScrollableHeight - 160)
            return;

        _historyLoadingMore = true;
        try
        {
            await Task.Yield();
            int target = Math.Min(_historyLoadedCount + HistoryPageSize, _historyFiles.Count);
            string? lastDate = _historyLoadedCount > 0
                ? GetDateFromFilePath(_historyFiles[_historyLoadedCount - 1])
                : null;

            for (int i = _historyLoadedCount; i < target; i++)
            {
                var filePath = _historyFiles[i];
                var fileDate = GetDateFromFilePath(filePath);
                if (fileDate != lastDate)
                {
                    HistoryPanel.Children.Add(CreateDateSeparator(fileDate ?? L("history.unknown_date")));
                    lastDate = fileDate;
                }
                var border = CreateHistoryThumbnail(filePath);
                HistoryPanel.Children.Add(border);
            }
            _historyLoadedCount = target;
        }
        finally
        {
            _historyLoadingMore = false;
        }
    }

    private Border CreateHistoryThumbnail(string filePath)
    {
        var img = new Image
        {
            Height = 140,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _ = LoadThumbnailAsync(img, filePath);

        var border = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2),
            Tag = filePath,
        };
        border.Child = img;
        border.PointerPressed += OnHistoryItemClick;

        var menu = new MenuFlyout();
        var copyItem = new MenuFlyoutItem
        {
            Text = L("common.copy"), Tag = filePath,
            Icon = new SymbolIcon(Symbol.Copy),
        };
        copyItem.Click += OnHistoryCopyImage;
        menu.Items.Add(copyItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        var readerItem = new MenuFlyoutItem
        {
            Text = L("action.send_to_inspect"), Tag = filePath,
            Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEE6F" },
        };
        readerItem.Click += OnHistorySendToInspect;
        menu.Items.Add(readerItem);
        var postItem = new MenuFlyoutItem
        {
            Text = L("action.send_to_post"), Tag = filePath,
            Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEB3C" },
        };
        postItem.Click += OnHistorySendToEffects;
        menu.Items.Add(postItem);
        var sendItem = new MenuFlyoutItem
        {
            Text = L("action.send_to_i2i"), Tag = filePath,
            Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEDFB" },
        };
        sendItem.Click += OnHistorySendToI2I;
        menu.Items.Add(sendItem);
        var upscaleItem = new MenuFlyoutItem
        {
            Text = L("action.send_to_upscale"), Tag = filePath,
            Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uECE9" },
        };
        upscaleItem.Click += OnHistorySendToUpscale;
        menu.Items.Add(upscaleItem);
        var openFolderItem = new MenuFlyoutItem
        {
            Text = L("action.open_containing_folder"), Tag = filePath,
            Icon = new SymbolIcon(Symbol.OpenLocal),
        };
        openFolderItem.Click += OnHistoryOpenFolder;
        menu.Items.Add(openFolderItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        var useParamsItem = new MenuFlyoutItem
        {
            Text = L("action.use_parameters"), Tag = filePath,
            Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8B6" },
        };
        useParamsItem.Click += OnHistoryUseParams;
        menu.Items.Add(useParamsItem);
        var useParamsNoSeedItem = new MenuFlyoutItem
        {
            Text = L("action.use_parameters_no_seed"), Tag = filePath,
            Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8B5" },
        };
        useParamsNoSeedItem.Click += OnHistoryUseParamsNoSeed;
        menu.Items.Add(useParamsNoSeedItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        var deleteItem = new MenuFlyoutItem
        {
            Text = L("common.delete"), Tag = filePath,
            Icon = new SymbolIcon(Symbol.Delete),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28)),
        };
        deleteItem.Click += OnHistoryDelete;
        menu.Items.Add(deleteItem);
        foreach (var item in menu.Items)
            ApplyMenuTypography(item);
        border.ContextFlyout = menu;

        return border;
    }

    private static async Task LoadThumbnailAsync(Image img, string filePath)
    {
        try
        {
            var bitmapImage = new BitmapImage
            {
                DecodePixelHeight = 140,
            };
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            using var stream = await file.OpenReadAsync();
            await bitmapImage.SetSourceAsync(stream);
            img.Source = bitmapImage;
        }
        catch { }
    }

    private void OnHistoryItemClick(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border && border.Tag is string filePath)
        {
            var pt = e.GetCurrentPoint(border);
            if (pt.Properties.IsLeftButtonPressed)
            {
                var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                    Windows.System.VirtualKey.Control);
                if (ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
                    _ = ApplyHistoryParamsNoSeedAsync(filePath);
                else
                _ = ShowHistoryImageAsync(filePath);
                e.Handled = true;
            }
        }
    }

    private async Task ApplyHistoryParamsNoSeedAsync(string filePath)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            await ApplyDroppedImageMetadata(bytes, Path.GetFileName(filePath), skipSeed: true);
        }
        catch (Exception ex) { TxtStatus.Text = Lf("common.read_failed", ex.Message); }
    }

    private async Task ShowHistoryImageAsync(string filePath)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            _currentGenImageBytes = bytes;
            _currentGenImagePath = filePath;
            GenResultBar.Visibility = Visibility.Collapsed;
            await ShowGenPreviewAsync(bytes);
            UpdateDynamicMenuStates();
        }
        catch (Exception ex) { TxtStatus.Text = Lf("common.load_failed", ex.Message); }
    }

    private void OnHistorySendToI2I(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string filePath)
        {
            _ = SendFileToI2IAsync(filePath);
        }
    }

    private async Task SendFileToI2IAsync(string filePath)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            SendImageToI2I(bytes, filePath);
        }
        catch (Exception ex) { TxtStatus.Text = Lf("i2i.send_failed", ex.Message); }
    }

    private void OnHistoryOpenFolder(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string filePath && File.Exists(filePath))
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
        }
    }

    private async void OnHistoryUseParams(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string filePath)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(filePath);
                await ApplyDroppedImageMetadata(bytes, Path.GetFileName(filePath));
            }
            catch (Exception ex) { TxtStatus.Text = Lf("common.read_failed", ex.Message); }
        }
    }

    private async void OnHistoryUseParamsNoSeed(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string filePath)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(filePath);
                await ApplyDroppedImageMetadata(bytes, Path.GetFileName(filePath), skipSeed: true);
            }
            catch (Exception ex) { TxtStatus.Text = Lf("common.read_failed", ex.Message); }
        }
    }

    private async void OnHistoryDelete(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string filePath)
        {
            try
            {
                int idx = _historyFiles.IndexOf(filePath);
                if (File.Exists(filePath)) File.Delete(filePath);
                var delDateStr = GetDateFromFilePath(filePath);
                if (delDateStr != null && _historyByDate.ContainsKey(delDateStr))
                {
                    _historyByDate[delDateStr].Remove(filePath);
                    if (_historyByDate[delDateStr].Count == 0)
                    {
                        _historyByDate.Remove(delDateStr);
                        _historyAvailableDates.Remove(delDateStr);
                        _historyAvailableDateSet.Remove(delDateStr);
                    }
                }
                _historyFiles.Remove(filePath);
                _historyLoadedCount = Math.Min(_historyLoadedCount, _historyFiles.Count);
                RefreshHistoryPanel();

                if (_currentGenImagePath == filePath)
                {
                    string? nextPath = null;
                    if (idx >= 0 && _historyFiles.Count > 0)
                        nextPath = _historyFiles[Math.Min(idx, _historyFiles.Count - 1)];

                    if (nextPath != null)
                    {
                        await ShowHistoryImageAsync(nextPath);
                        TxtStatus.Text = L("history.deleted_switched_adjacent");
                    }
                    else
                    {
                        _currentGenImageBytes = null;
                        _currentGenImagePath = null;
                        GenPreviewImage.Source = null;
                        GenPlaceholder.Visibility = Visibility.Visible;
                        GenResultBar.Visibility = Visibility.Collapsed;
                        UpdateDynamicMenuStates();
                        TxtStatus.Text = L("common.deleted");
                    }
                }
                else
                {
                    TxtStatus.Text = L("common.deleted");
                }
            }
            catch (Exception ex) { TxtStatus.Text = Lf("common.delete_failed", ex.Message); }
        }
    }
}
