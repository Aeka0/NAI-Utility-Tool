using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NAITool.Services;

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

    private HistoryFileIndex _historyFiles = HistoryFileIndex.Empty;
    private readonly Dictionary<string, string[]> _historyByDate = new(StringComparer.Ordinal);
    private readonly List<string> _historyAvailableDates = [];
    private readonly HashSet<string> _historyAvailableDateSet = new(StringComparer.Ordinal);
    private readonly List<HistoryListItem> _historyPendingItems = [];
    private readonly Dictionary<string, bool> _historyScanChanges = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _historyLoadCts;
    private bool _historyClosed;
    private string? _selectedHistoryDate;
    private int _historyPendingSequence;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _historyDateRefreshTimer;
    private string _historyTodayDateMarker = DateTime.Now.ToString("yyyy-MM-dd");

    private async void LoadHistoryAsync(bool preserveSelection = false)
    {
        _historyLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _historyLoadCts = cts;
        try
        {
            var snapshot = await HistoryCatalogScanner.ScanAsync(OutputBaseDir, cts.Token);
            if (cts.IsCancellationRequested || _historyClosed) return;

            // Generation/deletion can finish while disk enumeration is running. Replay those
            // changes over the snapshot instead of letting the scan resurrect or lose a file.
            _historyByDate.Clear();
            foreach (var pair in snapshot) _historyByDate[pair.Key] = pair.Value;
            foreach (var change in _historyScanChanges)
                UpdateHistoryCatalogFile(change.Key, change.Value);
            _historyScanChanges.Clear();
            _historyThumbnailFailures.Clear();
            RefreshHistoryAvailableDates();

            if (!preserveSelection || _selectedHistoryDate == null || !IsHistoryDateSelectable(_selectedHistoryDate))
                _selectedHistoryDate = _historyAvailableDates.FirstOrDefault();
            BuildHistoryFileList();
            RefreshHistoryPanel(resetScroll: !preserveSelection);
            if (_selectedHistoryDate != null)
                HistoryDatePicker.Date = DateTimeOffset.ParseExact(_selectedHistoryDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_historyClosed && !cts.IsCancellationRequested)
                TxtStatus.Text = Lf("common.load_failed", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_historyLoadCts, cts)) _historyLoadCts = null;
            cts.Dispose();
        }
    }

    private void RefreshHistoryAvailableDates()
    {
        _historyAvailableDates.Clear();
        _historyAvailableDates.AddRange(_historyByDate.Keys.OrderByDescending(date => date, StringComparer.Ordinal));
        _historyAvailableDateSet.Clear();
        _historyAvailableDateSet.UnionWith(_historyAvailableDates);
    }

    private void UpdateHistoryCatalogFile(string path, bool add)
    {
        var date = GetDateFromFilePath(path);
        if (date == null) return;
        var existing = _historyByDate.GetValueOrDefault(date) ?? [];
        int index = Array.FindIndex(existing, file => string.Equals(file, path, StringComparison.OrdinalIgnoreCase));
        if (add)
        {
            if (index < 0) _historyByDate[date] = [path, .. existing];
        }
        else if (index >= 0)
        {
            if (existing.Length == 1) _historyByDate.Remove(date);
            else _historyByDate[date] = [.. existing.Take(index), .. existing.Skip(index + 1)];
        }
    }

    private void AddHistoryItem(string filePath)
    {
        var date = GetDateFromFilePath(filePath);
        if (date == null) return;
        UpdateHistoryCatalogFile(filePath, add: true);
        if (_historyLoadCts != null) _historyScanChanges[filePath] = true;
        RefreshHistoryAvailableDates();

        bool resetScroll = _settings.Settings.ScrollHistoryToTopAfterGeneration;
        if (_selectedHistoryDate == null || resetScroll)
        {
            _selectedHistoryDate = date;
            HistoryDatePicker.Date = DateTimeOffset.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        BuildHistoryFileList();
        if (IsHistoryDateVisibleForSelection(date, _selectedHistoryDate))
            RefreshHistoryPanel(resetScroll);
    }

    private void RemoveHistoryFile(string path)
    {
        UpdateHistoryCatalogFile(path, add: false);
        if (_historyLoadCts != null) _historyScanChanges[path] = false;
        RefreshHistoryAvailableDates();
        BuildHistoryFileList();
        RemoveHistoryThumbnailCacheEntry(path);
    }

    private void BuildHistoryFileList()
    {
        _historyFiles = _selectedHistoryDate == null ? HistoryFileIndex.Empty : new HistoryFileIndex(
            _historyAvailableDates.Where(date => IsHistoryDateVisibleForSelection(date, _selectedHistoryDate))
                .Select(date => new KeyValuePair<string, string[]>(date, _historyByDate[date])));
    }

    private static bool IsHistoryDateVisibleForSelection(string date, string? selectedDate) =>
        selectedDate == null || string.CompareOrdinal(date, selectedDate) <= 0;

    private static string? GetDateFromFilePath(string filePath) => Path.GetFileName(Path.GetDirectoryName(filePath));

    private string AddPendingHistoryItem()
    {
        var date = GetTodayHistoryDateString();
        bool resetScroll = _settings.Settings.ScrollHistoryToTopAfterGeneration;
        if (_selectedHistoryDate == null || resetScroll)
        {
            _selectedHistoryDate = date;
            HistoryDatePicker.Date = DateTimeOffset.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        string pendingId = $"pending_{++_historyPendingSequence}";
        _historyPendingItems.Insert(0, HistoryListItem.CreatePending(pendingId, date, 140, 140));
        BuildHistoryFileList();
        if (IsHistoryDateVisibleForSelection(date, _selectedHistoryDate)) RefreshHistoryPanel(resetScroll);
        return pendingId;
    }

    private void ResolvePendingHistoryItem(string pendingId, string filePath)
    {
        // Keep an anchor on a pending row valid when it becomes a saved file.
        var anchor = CaptureHistoryAnchor();
        if (anchor?.PendingId == pendingId) anchor = anchor with { Path = filePath, PendingId = null };
        _historyPendingAnchor = anchor;
        _historyPendingItems.RemoveAll(item => item.PendingId == pendingId);
        // An offscreen result may never be realized; do not retain animation markers forever.
        if (_historyThumbnailRevealPendingPaths.Count >= HistoryThumbnailCacheLimit)
            _historyThumbnailRevealPendingPaths.Clear();
        _historyThumbnailRevealPendingPaths.Add(filePath);
        AddHistoryItem(filePath);
    }

    private void RemovePendingHistoryItem(string pendingId)
    {
        if (_historyPendingItems.RemoveAll(item => item.PendingId == pendingId) > 0)
            RefreshHistoryPanel();
    }

    private void DisposeHistory()
    {
        _historyClosed = true;
        _historyLoadCts?.Cancel();
        CancelHistoryThumbnailRequests();
        if (_historyXamlRoot != null) _historyXamlRoot.Changed -= OnHistoryXamlRootChanged;
        HistoryListView.LayoutUpdated -= OnHistoryLayoutUpdated;
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
        var useSeedItem = new MenuFlyoutItem
        {
            Text = L("action.use_seed"), Tag = filePath,
            Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uF0B9" },
        };
        useSeedItem.Click += OnHistoryUseSeed;
        menu.Items.Add(useSeedItem);
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

    private void OnHistoryItemClick(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border && border.Tag is string filePath)
        {
            var pt = e.GetCurrentPoint(border);
            if (pt.Properties.IsLeftButtonPressed)
            {
                var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                    Windows.System.VirtualKey.Control);
                if (_currentMode == AppMode.Gallery)
                    SwitchMode(AppMode.ImageGeneration);

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
            if (!_genResultBarResident)
                SetGenResultBarRequested(false);
            else
                UpdateFloatingResultBarsVisibility();
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

    private async void OnHistoryUseSeed(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string filePath)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(filePath);
                await ApplyImageSeedToGenerationAsync(bytes, Path.GetFileName(filePath));
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
                if (!TryDeleteImageFileWithConfiguredBehavior(filePath))
                    return;

                RemoveHistoryFile(filePath);
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
