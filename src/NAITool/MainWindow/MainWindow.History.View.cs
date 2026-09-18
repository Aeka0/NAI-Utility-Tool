using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace NAITool;

public sealed partial class MainWindow
{
    private readonly HistoryRowSource _historyRows = new();
    private ScrollViewer? _historyListScrollViewer;
    private XamlRoot? _historyXamlRoot;
    private bool _historyRefreshQueued;
    private bool _historyResetScrollRequested;
    private int _historyColumns = 1;
    private double _historyCellWidth = 220;
    private HistoryAnchor? _historyPendingAnchor;
    private int _historyAnchorRow = -1;
    private bool _historyRestoringAnchor;
    private sealed record HistoryAnchor(string? Path, string? PendingId, string? Date, int Row, double Top);

    private void RefreshHistoryPanel(bool resetScroll = false)
    {
        _historyResetScrollRequested |= resetScroll;
        if (_historyRefreshQueued || _historyClosed) return;
        _historyPendingAnchor ??= CaptureHistoryAnchor();
        _historyRefreshQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _historyRefreshQueued = false;
            if (_historyClosed) return;
            bool reset = _historyResetScrollRequested;
            _historyResetScrollRequested = false;
            var anchor = reset ? new HistoryAnchor(null, null, null, 0, 0) : CaptureHistoryAnchor();
            _historyRestoringAnchor = false;
            _historyRows.Reset(_historyFiles, _historyPendingItems.Where(item =>
                    item.DateKey != null && IsHistoryDateVisibleForSelection(item.DateKey, _selectedHistoryDate)),
                _historyColumns, _historyCellWidth);
            HistoryEmptyState.Text = L("history.empty");
            HistoryEmptyState.Visibility = _historyRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryListView.Visibility = _historyRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            _historyPendingAnchor = anchor;
            RestoreHistoryAnchor();
            QueueHistoryThumbnailPump();
        });
    }

    private void OnHistoryListSizeChanged(object sender, SizeChangedEventArgs e) => UpdateHistoryLayout();

    private void UpdateHistoryLayout()
    {
        if (HistoryListView == null) return;
        // Reserve the vertical scrollbar gutter. All sizes here are DIPs, including at high DPI.
        double width = Math.Max(80, HistoryListView.ActualWidth - 16);
        int columns = _currentMode == AppMode.Gallery ? Math.Max(1, (int)(width / 200)) : 1;
        double cellWidth = Math.Floor(width / columns) - 16;
        if (columns == _historyColumns && Math.Abs(cellWidth - _historyCellWidth) < 1) return;
        _historyPendingAnchor ??= CaptureHistoryAnchor();
        _historyColumns = columns;
        _historyCellWidth = cellWidth;
        RefreshHistoryPanel();
    }

    private HistoryAnchor? CaptureHistoryAnchor()
    {
        if (_historyPendingAnchor != null) return _historyPendingAnchor;
        if (_historyListScrollViewer == null || HistoryListView.ItemsPanelRoot == null) return null;
        var viewport = _historyListScrollViewer;
        return HistoryListView.ItemsPanelRoot.Children.OfType<ListViewItem>()
            .Select(container => (Container: container, Top: container.TransformToVisual(viewport).TransformPoint(new Point()).Y))
            .Where(entry => entry.Top + entry.Container.ActualHeight > 0 && entry.Top < viewport.ViewportHeight)
            .OrderBy(entry => entry.Top)
            .Select(entry => entry.Container.Content is HistoryRow row
                ? new HistoryAnchor(row.Items.FirstOrDefault()?.FilePath, row.Items.FirstOrDefault()?.PendingId,
                    row.DateLabel, row.Index, entry.Top)
                : null).FirstOrDefault(anchor => anchor != null);
    }

    private void RestoreHistoryAnchor()
    {
        if (_historyRows.Count == 0 || _historyPendingAnchor == null)
        {
            _historyPendingAnchor = null;
            return;
        }
        var anchor = _historyPendingAnchor;
        int row = _historyRows.FindRow(anchor.Path, anchor.PendingId, anchor.Date);
        _historyAnchorRow = row < 0 ? Math.Clamp(anchor.Row, 0, _historyRows.Count - 1) : row;
        _historyRestoringAnchor = true;
        HistoryListView.ScrollIntoView(_historyRows.GetRow(_historyAnchorRow), ScrollIntoViewAlignment.Leading);
    }

    private void OnHistoryLayoutUpdated(object? sender, object e)
    {
        if (!_historyRestoringAnchor || _historyRefreshQueued || _historyPendingAnchor == null ||
            _historyListScrollViewer == null || HistoryListView.ContainerFromIndex(_historyAnchorRow) is not ListViewItem container ||
            _historyRows.IndexOf(container.Content) != _historyAnchorRow)
            return;

        var anchor = _historyPendingAnchor;
        double top = container.TransformToVisual(_historyListScrollViewer).TransformPoint(new Point()).Y;
        _historyRestoringAnchor = false;
        _historyPendingAnchor = null;
        _historyListScrollViewer.ChangeView(null,
            Math.Max(0, _historyListScrollViewer.VerticalOffset + top - anchor.Top), null, disableAnimation: true);
    }

    private void OnHistoryListViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_historyXamlRoot != null) _historyXamlRoot.Changed -= OnHistoryXamlRootChanged;
        _historyXamlRoot = HistoryListView.XamlRoot;
        if (_historyXamlRoot != null) _historyXamlRoot.Changed += OnHistoryXamlRootChanged;
        _historyListScrollViewer = FindHistoryDescendant<ScrollViewer>(HistoryListView);
        if (_historyListScrollViewer != null)
        {
            _historyListScrollViewer.ViewChanged -= OnHistoryThumbnailViewportChanged;
            _historyListScrollViewer.ViewChanged += OnHistoryThumbnailViewportChanged;
        }
        UpdateHistoryLayout();
        RestoreHistoryAnchor();
        QueueHistoryThumbnailPump();
    }

    private void OnHistoryListViewUnloaded(object sender, RoutedEventArgs e)
    {
        if (_historyXamlRoot != null) _historyXamlRoot.Changed -= OnHistoryXamlRootChanged;
        _historyXamlRoot = null;
        if (_historyListScrollViewer != null) _historyListScrollViewer.ViewChanged -= OnHistoryThumbnailViewportChanged;
        _historyListScrollViewer = null;
        CancelHistoryThumbnailRequests();
    }

    private void OnHistoryThumbnailViewportChanged(object? sender, ScrollViewerViewChangedEventArgs e) => QueueHistoryThumbnailPump();

    private void OnHistoryXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => QueueHistoryThumbnailPump();

    private static T? FindHistoryDescendant<T>(DependencyObject? root, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        if (root == null) return null;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && (predicate == null || predicate(match))) return match;
            var nested = FindHistoryDescendant(child, predicate);
            if (nested != null) return nested;
        }
        return null;
    }
}
