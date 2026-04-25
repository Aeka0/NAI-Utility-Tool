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

        var snapshot = await Task.Run(() =>
        {
            var byDate = new Dictionary<string, List<string>>();
            var availableDates = new List<string>();
            var availableDateSet = new HashSet<string>();

            if (!Directory.Exists(OutputBaseDir))
                return new HistoryLoadSnapshot
                {
                    ByDate = byDate,
                    AvailableDates = availableDates,
                    AvailableDateSet = availableDateSet,
                };

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
                if (files.Count == 0)
                    continue;

                byDate[dir.Name] = files;
                availableDates.Add(dir.Name);
                availableDateSet.Add(dir.Name);
            }

            return new HistoryLoadSnapshot
            {
                ByDate = byDate,
                AvailableDates = availableDates,
                AvailableDateSet = availableDateSet,
            };
        });

        DispatcherQueue.TryEnqueue(() =>
        {
            _historyByDate.Clear();
            foreach (var pair in snapshot.ByDate)
                _historyByDate[pair.Key] = pair.Value;

            _historyAvailableDates.Clear();
            _historyAvailableDates.AddRange(snapshot.AvailableDates);
            _historyAvailableDateSet.Clear();
            _historyAvailableDateSet.UnionWith(snapshot.AvailableDateSet);
            _historyFiles.Clear();
            TrimHistoryFileItemCache(snapshot.ByDate);

            string? targetDate = requestedDate;
            if (targetDate != null && !IsHistoryDateSelectable(targetDate))
                targetDate = null;

            if (_historyAvailableDates.Count > 0)
            {
                _selectedHistoryDate = targetDate ?? _historyAvailableDates[0];
                BuildHistoryFileList();
                RefreshHistoryPanel(resetScroll: true);

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

        bool resetScroll = _settings.Settings.ScrollHistoryToTopAfterGeneration;
        if (resetScroll && !string.Equals(_selectedHistoryDate, dateStr, StringComparison.Ordinal))
        {
            _selectedHistoryDate = dateStr;
            HistoryDatePicker.Date = DateTimeOffset.ParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

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
            RefreshHistoryPanel(resetScroll);
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

    private Task<List<HistoryListItem>> BuildHistoryListItemsAsync()
    {
        if (_historyFiles.Count == 0 && _historyPendingItems.Count == 0)
            return Task.FromResult(new List<HistoryListItem>());

        var allFiles = _historyFiles.ToArray();
        var selectedDate = _selectedHistoryDate;
        var pendingItems = _historyPendingItems
            .Where(item => item.DateKey != null && IsHistoryDateVisibleForSelection(item.DateKey, selectedDate))
            .ToArray();
        return Task.Run(() =>
        {
            var items = new List<HistoryListItem>(
                allFiles.Length + pendingItems.Length + Math.Min((allFiles.Length + pendingItems.Length) / 4, 24));
            string? lastDate = null;
            foreach (var pendingItem in pendingItems)
            {
                var itemDate = pendingItem.DateKey ?? GetTodayHistoryDateString();
                if (itemDate != lastDate)
                {
                    items.Add(HistoryListItem.CreateSeparator(itemDate));
                    lastDate = itemDate;
                }

                items.Add(pendingItem);
            }

            foreach (var filePath in allFiles)
            {
                var fileDate = GetDateFromFilePath(filePath) ?? L("history.unknown_date");
                if (fileDate != lastDate)
                {
                    items.Add(HistoryListItem.CreateSeparator(fileDate));
                    lastDate = fileDate;
                }

                items.Add(GetOrCreateHistoryFileItem(filePath));
            }

            return items;
        });
    }

    private static bool IsHistoryDateVisibleForSelection(string dateStr, string? selectedDate) =>
        selectedDate == null ||
        string.Compare(dateStr, selectedDate, StringComparison.Ordinal) <= 0;

    private static double ComputeHistoryThumbnailHeight(int width, int height) => HistoryThumbnailHeight;

    private static double ComputeHistoryThumbnailWidth(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return HistoryThumbnailHeight;

        return Math.Max(56, Math.Round(HistoryThumbnailHeight * width / (double)height));
    }

    private static string? GetDateFromFilePath(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        return dir == null ? null : new DirectoryInfo(dir).Name;
    }

    private void OnHistoryDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (!args.NewDate.HasValue) return;
        var dateStr = args.NewDate.Value.ToString("yyyy-MM-dd");
        if (dateStr == _selectedHistoryDate) return;
        if (!IsHistoryDateSelectable(dateStr)) return;

        _selectedHistoryDate = dateStr;
        BuildHistoryFileList();
        RefreshHistoryPanel(resetScroll: true);
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

    private void RefreshHistoryPanel(bool resetScroll = false)
    {
        _ = RefreshHistoryPanelAsync(resetScroll);
    }

    private HistoryListItem GetOrCreateHistoryFileItem(string filePath)
    {
        if (_historyFileItemsByPath.TryGetValue(filePath, out var existing))
            return existing;

        var (pixelWidth, pixelHeight) = TryReadHistoryThumbnailDimensions(filePath);
        var created = HistoryListItem.CreateThumbnail(
            filePath,
            ComputeHistoryThumbnailWidth(pixelWidth, pixelHeight),
            HistoryThumbnailHeight);
        _historyFileItemsByPath[filePath] = created;
        return created;
    }

    private void TrimHistoryFileItemCache(Dictionary<string, List<string>> historyByDate)
    {
        var knownPaths = new HashSet<string>(
            historyByDate.SelectMany(pair => pair.Value),
            StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in _historyPendingItems.Select(item => item.FilePath).Where(path => !string.IsNullOrEmpty(path)))
            knownPaths.Add(filePath!);

        var stalePaths = _historyFileItemsByPath.Keys
            .Where(path => !knownPaths.Contains(path))
            .ToArray();
        foreach (var stalePath in stalePaths)
            _historyFileItemsByPath.Remove(stalePath);
    }

    private async Task RefreshHistoryPanelAsync(bool resetScroll)
    {
        int itemsVersion = Interlocked.Increment(ref _historyItemsVersion);
        ResetHistoryThumbnailQueue();
        var items = await BuildHistoryListItemsAsync();
        if (itemsVersion != _historyItemsVersion)
            return;

        HistoryEmptyState.Text = L("history.empty");
        HistoryEmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryListView.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplyHistoryListDiff(items);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            RefreshRealizedHistoryThumbnails);

        if (resetScroll && items.Count > 0)
            QueueHistoryScrollToTop(itemsVersion);
    }

    private void QueueHistoryScrollToTop(int itemsVersion)
    {
        void ScrollToTop()
        {
            if (itemsVersion != _historyItemsVersion || HistoryListView.Items.Count == 0)
                return;

            _historyListScrollViewer ??= FindHistoryDescendant<ScrollViewer>(HistoryListView);
            _historyListScrollViewer?.ChangeView(null, 0, null, disableAnimation: true);
            HistoryListView.ScrollIntoView(HistoryListView.Items[0], ScrollIntoViewAlignment.Leading);
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            ScrollToTop();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ScrollToTop);
        });
    }

    private void ApplyHistoryListDiff(IReadOnlyList<HistoryListItem> targetItems)
    {
        int prefix = 0;
        int currentCount = _historyListItems.Count;
        int targetCount = targetItems.Count;
        while (prefix < currentCount &&
            prefix < targetCount &&
            _historyListItems[prefix].HasSameIdentity(targetItems[prefix]))
        {
            prefix++;
        }

        int currentSuffix = currentCount - 1;
        int targetSuffix = targetCount - 1;
        while (currentSuffix >= prefix &&
            targetSuffix >= prefix &&
            _historyListItems[currentSuffix].HasSameIdentity(targetItems[targetSuffix]))
        {
            currentSuffix--;
            targetSuffix--;
        }

        for (int i = currentSuffix; i >= prefix; i--)
            _historyListItems.RemoveAt(i);

        for (int i = prefix; i <= targetSuffix; i++)
            _historyListItems.Insert(i, targetItems[i]);
    }

    private string AddPendingHistoryItem(int width, int height)
    {
        var dateStr = GetTodayHistoryDateString();
        if (_selectedHistoryDate == null)
        {
            _selectedHistoryDate = dateStr;
            HistoryDatePicker.Date = DateTimeOffset.ParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        string pendingId = $"pending_{Interlocked.Increment(ref _historyPendingSequence)}";
        _historyPendingItems.Insert(0,
            HistoryListItem.CreatePending(
                pendingId,
                dateStr,
                ComputeHistoryThumbnailWidth(width, height),
                ComputeHistoryThumbnailHeight(width, height)));

        bool resetScroll = _settings.Settings.ScrollHistoryToTopAfterGeneration;
        if (resetScroll && !string.Equals(_selectedHistoryDate, dateStr, StringComparison.Ordinal))
        {
            _selectedHistoryDate = dateStr;
            HistoryDatePicker.Date = DateTimeOffset.ParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        BuildHistoryFileList();
        if (IsHistoryDateVisibleForSelection(dateStr, _selectedHistoryDate))
            RefreshHistoryPanel(resetScroll);

        return pendingId;
    }

    private void ResolvePendingHistoryItem(string pendingId, string filePath, byte[]? thumbnailBytes = null)
    {
        HistoryListItem? resolvedItem = null;
        int pendingIndex = _historyPendingItems.FindIndex(item =>
            string.Equals(item.PendingId, pendingId, StringComparison.Ordinal));
        if (pendingIndex >= 0)
        {
            var pendingItem = _historyPendingItems[pendingIndex];
            pendingItem.Resolve(filePath);
            _historyFileItemsByPath[filePath] = pendingItem;
            _historyPendingItems.RemoveAt(pendingIndex);
            _historyThumbnailRevealPendingPaths.Add(filePath);
            RefreshVisibleHistoryItem(pendingItem, animateReveal: true);
            resolvedItem = pendingItem;
        }

        if (thumbnailBytes != null)
            _ = PrimeResolvedHistoryThumbnailAsync(filePath, thumbnailBytes, resolvedItem);

        AddHistoryItem(filePath);
    }

    private void RemovePendingHistoryItem(string pendingId)
    {
        int pendingIndex = _historyPendingItems.FindIndex(item =>
            string.Equals(item.PendingId, pendingId, StringComparison.Ordinal));
        if (pendingIndex < 0)
            return;

        string dateStr = _historyPendingItems[pendingIndex].DateKey ?? GetTodayHistoryDateString();
        _historyPendingItems.RemoveAt(pendingIndex);
        if (IsHistoryDateVisibleForSelection(dateStr, _selectedHistoryDate))
            RefreshHistoryPanel();
    }

    private int FindVisibleHistoryItemIndexByPendingId(string pendingId)
    {
        for (int i = 0; i < _historyListItems.Count; i++)
        {
            if (string.Equals(_historyListItems[i].PendingId, pendingId, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private void RefreshVisibleHistoryItem(HistoryListItem item, bool animateReveal)
    {
        if (HistoryListView.ContainerFromItem(item) is not ListViewItem listViewItem)
            return;

        var host = FindHistoryThumbnailHost(listViewItem);
        var image = FindHistoryThumbnailImage(listViewItem);
        if (host == null || image == null)
            return;

        if (animateReveal && item.FilePath != null)
            _historyThumbnailRevealPendingPaths.Add(item.FilePath);

        UpdateHistoryThumbnailHost(host);
        _ = UpdateHistoryThumbnailImageAsync(image);
    }

    private void RefreshRealizedHistoryThumbnails()
    {
        foreach (var item in _historyListItems)
        {
            if (HistoryListView.ContainerFromItem(item) is not ListViewItem listViewItem)
                continue;

            var host = FindHistoryThumbnailHost(listViewItem);
            var image = FindHistoryThumbnailImage(listViewItem);
            if (host == null || image == null)
                continue;

            UpdateHistoryThumbnailHost(host);
            _ = UpdateHistoryThumbnailImageAsync(image);
        }
    }

    private void OnHistoryListViewLoaded(object sender, RoutedEventArgs e)
    {
        _historyListScrollViewer ??= FindHistoryDescendant<ScrollViewer>(HistoryListView);
        if (_historyListScrollViewer != null)
        {
            _historyListScrollViewer.ViewChanged -= OnHistoryThumbnailViewportChanged;
            _historyListScrollViewer.ViewChanged += OnHistoryThumbnailViewportChanged;
        }

        RefreshRealizedHistoryThumbnails();
        ReprioritizeHistoryThumbnailQueue();
    }

    private void OnHistoryListViewUnloaded(object sender, RoutedEventArgs e)
    {
        if (_historyListScrollViewer != null)
            _historyListScrollViewer.ViewChanged -= OnHistoryThumbnailViewportChanged;

        _historyListScrollViewer = null;
    }

    private void OnHistoryThumbnailViewportChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        ReprioritizeHistoryThumbnailQueue();
    }

    private static T? FindHistoryDescendant<T>(DependencyObject? root, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        if (root == null)
            return null;

        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match && (predicate == null || predicate(match)))
                return match;

            var nested = FindHistoryDescendant(child, predicate);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private static Border? FindHistoryThumbnailHost(DependencyObject? root) =>
        FindHistoryDescendant<Border>(root,
            border => string.Equals(border.Name, "HistoryThumbnailHost", StringComparison.Ordinal));

    private static Image? FindHistoryThumbnailImage(DependencyObject? root) =>
        FindHistoryDescendant<Image>(root,
            image => string.Equals(image.Name, "HistoryThumbnailImage", StringComparison.Ordinal));

    private static (int Width, int Height) TryReadHistoryThumbnailDimensions(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            byte[] header = new byte[24];
            if (stream.Read(header, 0, header.Length) < header.Length)
                return (0, 0);

            if (header[0] != 0x89 ||
                header[1] != 0x50 ||
                header[2] != 0x4E ||
                header[3] != 0x47 ||
                header[4] != 0x0D ||
                header[5] != 0x0A ||
                header[6] != 0x1A ||
                header[7] != 0x0A)
            {
                return (0, 0);
            }

            int width = ReadBigEndianInt32(header, 16);
            int height = ReadBigEndianInt32(header, 20);
            return width > 0 && height > 0 ? (width, height) : (0, 0);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static int ReadBigEndianInt32(byte[] buffer, int offset) =>
        (buffer[offset] << 24) |
        (buffer[offset + 1] << 16) |
        (buffer[offset + 2] << 8) |
        buffer[offset + 3];

    private MenuFlyout BuildHistoryContextFlyout(string filePath)
    {
        var menu = new MenuFlyout();
        var copyItem = new MenuFlyoutItem
        {
            Text = L("common.copy"), Tag = filePath,
            Icon = new SymbolIcon(Symbol.Copy),
        };
        copyItem.Click += OnHistoryCopyImage;
        menu.Items.Add(copyItem);
        var enhanceItem = new MenuFlyoutItem
        {
            Text = L("button.enhance"), Tag = filePath,
            Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE771" },
            IsEnabled = !_generateRequestRunning,
        };
        enhanceItem.Click += OnHistoryEnhance;
        menu.Items.Add(enhanceItem);
        var saveAsItem = new MenuFlyoutItem
        {
            Text = L("menu.file.save_as"), Tag = filePath,
            Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE792" },
        };
        saveAsItem.Click += OnHistorySaveAs;
        menu.Items.Add(saveAsItem);
        var saveAsStrippedItem = new MenuFlyoutItem
        {
            Text = L("menu.file.save_as_stripped"), Tag = filePath,
            Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE792" },
        };
        saveAsStrippedItem.Click += OnHistorySaveAsStripped;
        menu.Items.Add(saveAsStrippedItem);
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

        return menu;
    }

    private void OnHistoryThumbnailHostLoaded(object sender, RoutedEventArgs e)
    {
        UpdateHistoryThumbnailHost(sender as Border);
    }

    private void OnHistoryThumbnailHostDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        UpdateHistoryThumbnailHost(sender as Border);
    }

    private void UpdateHistoryThumbnailHost(Border? border)
    {
        if (border?.DataContext is not HistoryListItem item || item.IsSeparator || item.IsPending || item.FilePath == null)
        {
            if (border != null)
            {
                border.Tag = null;
                border.ContextFlyout = null;
            }
            return;
        }

        border.Tag = item.FilePath;
        border.ContextFlyout = BuildHistoryContextFlyout(item.FilePath);
    }

    private void OnHistoryThumbnailImageLoaded(object sender, RoutedEventArgs e)
    {
        _ = UpdateHistoryThumbnailImageAsync(sender as Image);
    }

    private void OnHistoryThumbnailImageDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        _ = UpdateHistoryThumbnailImageAsync(sender as Image);
    }

    private void OnHistoryThumbnailImageUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image img)
            return;

        if (img.Tag is string filePath)
            RemoveHistoryThumbnailWaiter(filePath, img);

        img.Tag = null;
        img.Source = null;
        img.Opacity = 1;
    }

    private Task UpdateHistoryThumbnailImageAsync(Image? img)
    {
        if (img == null)
            return Task.CompletedTask;

        var placeholder = GetHistoryThumbnailPlaceholder(img);
        if (img.DataContext is not HistoryListItem item || item.IsSeparator)
        {
            img.Tag = null;
            img.Source = null;
            img.Opacity = 1;
            if (placeholder != null)
            {
                placeholder.Visibility = Visibility.Collapsed;
                placeholder.Opacity = 0;
            }
            return Task.CompletedTask;
        }

        if (item.IsPending || item.FilePath == null)
        {
            img.Tag = null;
            img.Source = null;
            img.Opacity = 0;
            if (placeholder != null)
            {
                placeholder.Visibility = Visibility.Visible;
                placeholder.Opacity = 1;
            }
            return Task.CompletedTask;
        }

        string filePath = item.FilePath;
        img.Tag = filePath;

        if (TryGetCachedHistoryThumbnail(filePath, out var cachedThumbnail))
        {
            SetHistoryThumbnailSource(img, cachedThumbnail);
            return Task.CompletedTask;
        }

        img.Source = null;
        QueueHistoryThumbnailRequest(img, filePath, _historyItemsVersion);
        return Task.CompletedTask;
    }

    private Border? GetHistoryThumbnailPlaceholder(Image img) =>
        img.Parent is Grid grid
            ? grid.Children.OfType<Border>().FirstOrDefault(border =>
                string.Equals(border.Name, "HistoryThumbnailPlaceholder", StringComparison.Ordinal))
            : null;

    private async Task PrimeResolvedHistoryThumbnailAsync(
        string filePath,
        byte[] thumbnailBytes,
        HistoryListItem? resolvedItem)
    {
        var thumbnail = await CreateHistoryThumbnailBitmapAsync(filePath, thumbnailBytes);
        if (thumbnail == null)
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyHistoryThumbnailToResolvedItem(filePath, thumbnail, resolvedItem);
            RefreshRealizedHistoryThumbnails();
        });
    }

    private void ApplyHistoryThumbnailToResolvedItem(
        string filePath,
        BitmapImage thumbnail,
        HistoryListItem? resolvedItem)
    {
        if (resolvedItem != null &&
            TryApplyHistoryThumbnailToItem(resolvedItem, filePath, thumbnail))
        {
            return;
        }

        if (_historyFileItemsByPath.TryGetValue(filePath, out var mappedItem) &&
            TryApplyHistoryThumbnailToItem(mappedItem, filePath, thumbnail))
        {
            return;
        }

        foreach (var item in _historyListItems)
        {
            if (item.IsSeparator ||
                item.IsPending ||
                !string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryApplyHistoryThumbnailToItem(item, filePath, thumbnail))
                return;
        }
    }

    private bool TryApplyHistoryThumbnailToItem(
        HistoryListItem item,
        string filePath,
        BitmapImage thumbnail)
    {
        if (HistoryListView.ContainerFromItem(item) is not ListViewItem listViewItem)
            return false;

        var host = FindHistoryThumbnailHost(listViewItem);
        if (host != null)
            UpdateHistoryThumbnailHost(host);

        var image = FindHistoryThumbnailImage(listViewItem);
        if (image == null)
            return false;

        image.Tag = filePath;
        SetHistoryThumbnailSource(image, thumbnail);
        return true;
    }

    private void SetHistoryThumbnailSource(Image img, BitmapImage? thumbnail)
    {
        if (thumbnail == null)
            return;

        var placeholder = GetHistoryThumbnailPlaceholder(img);
        bool animateReveal = img.Tag is string filePath &&
            _historyThumbnailRevealPendingPaths.Remove(filePath);
        img.Source = thumbnail;

        if (animateReveal && placeholder != null)
        {
            placeholder.Visibility = Visibility.Visible;
            placeholder.Opacity = 1;
            img.Opacity = 0;
            AnimateHistoryThumbnailReveal(img, placeholder);
            return;
        }

        img.Opacity = 1;
        if (placeholder != null)
        {
            placeholder.Visibility = Visibility.Collapsed;
            placeholder.Opacity = 0;
        }
    }

    private static void AnimateHistoryThumbnailReveal(Image img, Border placeholder)
    {
        var storyboard = new Storyboard();
        var duration = new Duration(TimeSpan.FromMilliseconds(220));

        var imageFade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = duration,
        };
        Storyboard.SetTarget(imageFade, img);
        Storyboard.SetTargetProperty(imageFade, "Opacity");
        storyboard.Children.Add(imageFade);

        var placeholderFade = new DoubleAnimation
        {
            From = placeholder.Opacity,
            To = 0,
            Duration = duration,
        };
        Storyboard.SetTarget(placeholderFade, placeholder);
        Storyboard.SetTargetProperty(placeholderFade, "Opacity");
        storyboard.Children.Add(placeholderFade);

        storyboard.Completed += (_, _) =>
        {
            img.Opacity = 1;
            placeholder.Opacity = 0;
            placeholder.Visibility = Visibility.Collapsed;
        };
        storyboard.Begin();
    }

    private bool TryGetCachedHistoryThumbnail(string filePath, out BitmapImage? thumbnail)
    {
        lock (_historyThumbnailCacheLock)
        {
            if (_historyThumbnailCache.TryGetValue(filePath, out var cached))
            {
                TouchHistoryThumbnailCacheEntry(filePath);
                thumbnail = cached;
                return true;
            }
        }

        thumbnail = null;
        return false;
    }

    private void QueueHistoryThumbnailRequest(Image img, string filePath, int itemsVersion)
    {
        bool shouldProcess;
        lock (_historyThumbnailRequestLock)
        {
            RegisterHistoryThumbnailWaiter(filePath, img);

            if (!_historyThumbnailQueuedPaths.Contains(filePath) &&
                !_historyThumbnailInFlightPaths.Contains(filePath))
            {
                _historyThumbnailQueue.Add(new HistoryThumbnailQueueEntry
                {
                    FilePath = filePath,
                    ItemsVersion = itemsVersion,
                    Sequence = ++_historyThumbnailRequestSequence,
                    Priority = GetHistoryThumbnailPriority(img),
                });
                _historyThumbnailQueuedPaths.Add(filePath);
            }
            else
            {
                UpdateHistoryThumbnailQueueEntryPriority(filePath, GetHistoryThumbnailPriority(img));
            }

            shouldProcess = true;
        }

        if (shouldProcess)
            ProcessHistoryThumbnailQueue();
    }

    private void RegisterHistoryThumbnailWaiter(string filePath, Image img)
    {
        if (!_historyThumbnailWaiters.TryGetValue(filePath, out var waiters))
        {
            waiters = [];
            _historyThumbnailWaiters[filePath] = waiters;
        }

        waiters.RemoveAll(w =>
        {
            if (!w.TryGetTarget(out var target))
                return true;

            return ReferenceEquals(target, img);
        });

        waiters.Add(new WeakReference<Image>(img));
    }

    private void RemoveHistoryThumbnailWaiter(string filePath, Image img)
    {
        lock (_historyThumbnailRequestLock)
        {
            if (!_historyThumbnailWaiters.TryGetValue(filePath, out var waiters))
                return;

            waiters.RemoveAll(w =>
            {
                if (!w.TryGetTarget(out var target))
                    return true;

                return ReferenceEquals(target, img);
            });

            if (waiters.Count == 0)
                _historyThumbnailWaiters.Remove(filePath);
        }
    }

    private void ResetHistoryThumbnailQueue()
    {
        lock (_historyThumbnailRequestLock)
        {
            _historyThumbnailQueue.Clear();
            _historyThumbnailQueuedPaths.Clear();
            _historyThumbnailWaiters.Clear();
        }
    }

    private void ReprioritizeHistoryThumbnailQueue()
    {
        lock (_historyThumbnailRequestLock)
        {
            for (int i = _historyThumbnailQueue.Count - 1; i >= 0; i--)
            {
                var entry = _historyThumbnailQueue[i];
                if (!TryGetHistoryThumbnailLiveWaiters(entry.FilePath, out var waiters))
                {
                    _historyThumbnailQueue.RemoveAt(i);
                    _historyThumbnailQueuedPaths.Remove(entry.FilePath);
                    continue;
                }

                entry.Priority = waiters.Min(GetHistoryThumbnailPriority);
            }
        }

        ProcessHistoryThumbnailQueue();
    }

    private bool TryGetHistoryThumbnailLiveWaiters(string filePath, out List<Image> liveWaiters)
    {
        liveWaiters = [];
        if (!_historyThumbnailWaiters.TryGetValue(filePath, out var waiters))
            return false;

        for (int i = waiters.Count - 1; i >= 0; i--)
        {
            if (!waiters[i].TryGetTarget(out var target) ||
                !string.Equals(target.Tag as string, filePath, StringComparison.Ordinal))
            {
                waiters.RemoveAt(i);
                continue;
            }

            liveWaiters.Add(target);
        }

        if (waiters.Count == 0)
        {
            _historyThumbnailWaiters.Remove(filePath);
            return false;
        }

        return liveWaiters.Count > 0;
    }

    private void UpdateHistoryThumbnailQueueEntryPriority(string filePath, int priority)
    {
        for (int i = 0; i < _historyThumbnailQueue.Count; i++)
        {
            if (string.Equals(_historyThumbnailQueue[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                _historyThumbnailQueue[i].Priority = Math.Min(_historyThumbnailQueue[i].Priority, priority);
                break;
            }
        }
    }

    private int GetHistoryThumbnailPriority(Image img)
    {
        if (_historyListScrollViewer == null || img.XamlRoot == null)
            return int.MaxValue / 4;

        try
        {
            var transform = img.TransformToVisual(_historyListScrollViewer);
            var topLeft = transform.TransformPoint(new Point(0, 0));
            double centerY = topLeft.Y + Math.Max(img.ActualHeight, 140) / 2d;
            double viewportCenterY = _historyListScrollViewer.ActualHeight / 2d;
            return (int)Math.Min(int.MaxValue / 8, Math.Abs(centerY - viewportCenterY) * 100);
        }
        catch
        {
            return int.MaxValue / 4;
        }
    }

    private void ProcessHistoryThumbnailQueue()
    {
        while (true)
        {
            HistoryThumbnailQueueEntry? nextEntry = null;
            lock (_historyThumbnailRequestLock)
            {
                if (_historyThumbnailActiveLoads >= HistoryThumbnailMaxConcurrentLoads ||
                    _historyThumbnailQueue.Count == 0)
                {
                    return;
                }

                int bestIndex = 0;
                for (int i = 1; i < _historyThumbnailQueue.Count; i++)
                {
                    var candidate = _historyThumbnailQueue[i];
                    var best = _historyThumbnailQueue[bestIndex];
                    if (candidate.Priority < best.Priority ||
                        (candidate.Priority == best.Priority && candidate.Sequence < best.Sequence))
                    {
                        bestIndex = i;
                    }
                }

                nextEntry = _historyThumbnailQueue[bestIndex];
                _historyThumbnailQueue.RemoveAt(bestIndex);
                _historyThumbnailQueuedPaths.Remove(nextEntry.FilePath);
                _historyThumbnailInFlightPaths.Add(nextEntry.FilePath);
                _historyThumbnailActiveLoads++;
            }

            _ = RunHistoryThumbnailRequestAsync(nextEntry);
        }
    }

    private async Task RunHistoryThumbnailRequestAsync(HistoryThumbnailQueueEntry entry)
    {
        try
        {
            if (entry.ItemsVersion != _historyItemsVersion)
                return;

            lock (_historyThumbnailRequestLock)
            {
                if (!TryGetHistoryThumbnailLiveWaiters(entry.FilePath, out _))
                    return;
            }

            var thumbnail = await LoadHistoryThumbnailAsync(entry.FilePath, entry.ItemsVersion);
            if (thumbnail == null)
                return;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (entry.ItemsVersion != _historyItemsVersion)
                    return;

                lock (_historyThumbnailRequestLock)
                {
                    if (!TryGetHistoryThumbnailLiveWaiters(entry.FilePath, out var waiters))
                        return;

                    foreach (var waiter in waiters)
                    {
                        if (string.Equals(waiter.Tag as string, entry.FilePath, StringComparison.Ordinal))
                            SetHistoryThumbnailSource(waiter, thumbnail);
                    }
                }
            });
        }
        finally
        {
            lock (_historyThumbnailRequestLock)
            {
                _historyThumbnailInFlightPaths.Remove(entry.FilePath);
                _historyThumbnailActiveLoads = Math.Max(0, _historyThumbnailActiveLoads - 1);
            }

            ProcessHistoryThumbnailQueue();
        }
    }

    private async Task<BitmapImage?> LoadHistoryThumbnailAsync(string filePath, int itemsVersion)
    {
        if (TryGetCachedHistoryThumbnail(filePath, out var cached))
            return cached;

        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            if (itemsVersion != _historyItemsVersion)
                return null;

            return await CreateHistoryThumbnailBitmapAsync(filePath, bytes);
        }
        catch
        {
            return null;
        }
    }

    private Task<BitmapImage?> CreateHistoryThumbnailBitmapAsync(string filePath, byte[] bytes)
    {
        var tcs = new TaskCompletionSource<BitmapImage?>();
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            if (TryGetCachedHistoryThumbnail(filePath, out var cached))
            {
                tcs.TrySetResult(cached);
                return;
            }

            try
            {
                var bitmapImage = new BitmapImage
                {
                    DecodePixelHeight = 140,
                };

                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                using var writer = new Windows.Storage.Streams.DataWriter(stream);
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
                stream.Seek(0);
                await bitmapImage.SetSourceAsync(stream);

                lock (_historyThumbnailCacheLock)
                {
                    _historyThumbnailCache[filePath] = bitmapImage;
                    TouchHistoryThumbnailCacheEntry(filePath);
                    while (_historyThumbnailCache.Count > HistoryThumbnailCacheLimit &&
                        _historyThumbnailCacheLru.Last?.Value is string oldestPath)
                    {
                        _historyThumbnailCacheLru.RemoveLast();
                        _historyThumbnailCache.Remove(oldestPath);
                    }
                }

                tcs.TrySetResult(bitmapImage);
            }
            catch
            {
                tcs.TrySetResult(null);
            }
        }))
        {
            tcs.TrySetResult(null);
        }

        return tcs.Task;
    }

    private void TouchHistoryThumbnailCacheEntry(string filePath)
    {
        var node = _historyThumbnailCacheLru.Find(filePath);
        if (node != null)
            _historyThumbnailCacheLru.Remove(node);

        _historyThumbnailCacheLru.AddFirst(filePath);
    }

    private void RemoveHistoryThumbnailCacheEntry(string filePath)
    {
        lock (_historyThumbnailCacheLock)
        {
            _historyThumbnailCache.Remove(filePath);
            var node = _historyThumbnailCacheLru.Find(filePath);
            if (node != null)
                _historyThumbnailCacheLru.Remove(node);
        }

        _historyFileItemsByPath.Remove(filePath);
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
            SetGenResultBarRequested(false);
            await ShowGenPreviewAsync(bytes);
            UpdateDynamicMenuStates();
        }
        catch (Exception ex) { TxtStatus.Text = Lf("common.load_failed", ex.Message); }
    }

    private async void OnHistoryEnhance(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string filePath)
            return;

        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            await BeginGenEnhanceAsync(bytes, filePath);
        }
        catch (Exception ex) { TxtStatus.Text = Lf("common.read_failed", ex.Message); }
    }

    private async void OnHistorySaveAs(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string filePath)
            return;

        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            await SaveImageBytesAsAsync(bytes, stripMetadata: false, filePath);
        }
        catch (Exception ex) { TxtStatus.Text = Lf("common.read_failed", ex.Message); }
    }

    private async void OnHistorySaveAsStripped(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string filePath)
            return;

        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            await SaveImageBytesAsAsync(bytes, stripMetadata: true, filePath);
        }
        catch (Exception ex) { TxtStatus.Text = Lf("common.read_failed", ex.Message); }
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
                DeleteImageFileWithConfiguredBehavior(filePath);
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
                RemoveHistoryThumbnailCacheEntry(filePath);
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
                        ClearCurrentGenPreview();
                        SetGenResultBarRequested(false);
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
