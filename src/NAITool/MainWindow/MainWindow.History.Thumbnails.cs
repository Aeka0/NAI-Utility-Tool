using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using NAITool.Services;
using Windows.Foundation;

namespace NAITool;

public sealed partial class MainWindow
{
    private const int HistoryThumbnailCacheLimit = 96;
    private const int HistoryThumbnailMaxConcurrentLoads = 2;
    private readonly HashSet<Image> _historyRealizedImages = [];
    private readonly Dictionary<Image, HistoryThumbnailCacheEntry> _historyImageSources = [];
    private readonly Dictionary<string, HistoryThumbnailCacheEntry> _historyThumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _historyThumbnailCacheLru = [];
    private readonly Dictionary<string, HistoryThumbnailRequest> _historyThumbnailRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _historyThumbnailFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _historyThumbnailRevealPendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _historyThumbnailPumpQueued;
    private sealed record HistoryThumbnailCacheEntry(WriteableBitmap Bitmap, int Width, int Height);
    private sealed record HistoryThumbnailRequest(CancellationTokenSource Cancellation, int Width, int Height);

    private void OnHistoryThumbnailHostLoaded(object sender, RoutedEventArgs e) => UpdateHistoryThumbnailHost(sender as Border);
    private void OnHistoryThumbnailHostDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) => UpdateHistoryThumbnailHost(sender as Border);

    private void UpdateHistoryThumbnailHost(Border? border)
    {
        if (border == null) return;
        border.Tag = (border.DataContext as HistoryListItem)?.FilePath;
        // Build menus only when opened, not while hundreds of cells are recycled during scrolling.
        if (border.ContextFlyout != null) return;
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            if (border.Tag is not string path) return;
            var menu = BuildHistoryContextFlyout(path);
            while (menu.Items.Count > 0)
            {
                var item = menu.Items[0];
                menu.Items.RemoveAt(0);
                flyout.Items.Add(item);
            }
        };
        border.ContextFlyout = flyout;
    }

    private void OnHistoryThumbnailImageLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image) return;
        _historyRealizedImages.Add(image);
        UpdateHistoryThumbnailImage(image);
    }

    private void OnHistoryThumbnailImageDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is Image image) UpdateHistoryThumbnailImage(image);
    }

    private void OnHistoryThumbnailImageUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image) return;
        _historyRealizedImages.Remove(image);
        _historyImageSources.Remove(image);
        image.Tag = null;
        image.Source = null;
        QueueHistoryThumbnailPump();
    }

    private void UpdateHistoryThumbnailImage(Image image)
    {
        _historyImageSources.Remove(image);
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(image).StopAnimation("Opacity");
        image.Tag = (image.DataContext as HistoryListItem)?.FilePath;
        image.Source = null;
        image.Opacity = 1;
        SetHistoryThumbnailPlaceholder(image, loading: true);
        QueueHistoryThumbnailPump();
    }

    private static void SetHistoryThumbnailPlaceholder(Image image, bool loading, bool failed = false)
    {
        if (image.Parent is not Grid grid) return;
        var placeholder = grid.Children.OfType<Border>().FirstOrDefault();
        if (placeholder == null) return;
        placeholder.Visibility = loading || failed ? Visibility.Visible : Visibility.Collapsed;
        if (FindHistoryDescendant<ProgressRing>(placeholder) is { } progress)
        {
            progress.IsActive = loading;
            progress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        }
        if (FindHistoryDescendant<FontIcon>(placeholder) is { } error)
            error.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void QueueHistoryThumbnailPump()
    {
        if (_historyThumbnailPumpQueued || _historyClosed) return;
        _historyThumbnailPumpQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _historyThumbnailPumpQueued = false;
            if (!_historyClosed) PumpHistoryThumbnails();
        });
    }

    private double GetHistoryThumbnailPriority(Image image)
    {
        if (_historyListScrollViewer == null || image.XamlRoot == null) return double.MaxValue;
        double y = image.TransformToVisual(_historyListScrollViewer).TransformPoint(new Point()).Y;
        return Math.Abs(y + 70 - _historyListScrollViewer.ViewportHeight / 2);
    }

    private void PumpHistoryThumbnails()
    {
        // Every collection here is UI-thread-owned. Only the decoder operates off-thread.
        // Scan the bounded realized set, never the entire catalog.
        var wanted = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double scale = HistoryListView.XamlRoot?.RasterizationScale ?? 1;
        int width = (int)Math.Clamp(Math.Ceiling(_historyCellWidth * scale), 1, 1024);
        int height = (int)Math.Clamp(Math.Ceiling(140 * scale), 1, 1024);
        if (PanelHistory.Visibility == Visibility.Visible)
        {
            foreach (var image in _historyRealizedImages)
            {
                if (!image.IsLoaded || image.Tag is not string path) continue;
                if (_historyImageSources.TryGetValue(image, out var source) && source.Width >= width && source.Height >= height)
                    continue;
                if (_historyThumbnailCache.TryGetValue(path, out var cached) && cached.Width >= width && cached.Height >= height)
                {
                    TouchHistoryThumbnailCacheEntry(path);
                    SetHistoryThumbnailSource(image, cached);
                    continue;
                }
                if (_historyThumbnailFailures.Contains(path))
                {
                    SetHistoryThumbnailPlaceholder(image, loading: false, failed: true);
                    continue;
                }
                double priority = GetHistoryThumbnailPriority(image);
                wanted[path] = Math.Min(wanted.GetValueOrDefault(path, double.MaxValue), priority);
            }
        }

        foreach (var pair in _historyThumbnailRequests)
            if (!wanted.ContainsKey(pair.Key) || pair.Value.Width < width || pair.Value.Height < height)
                pair.Value.Cancellation.Cancel();

        foreach (var pair in wanted.OrderBy(pair => pair.Value))
        {
            if (_historyThumbnailRequests.Count >= HistoryThumbnailMaxConcurrentLoads) break;
            if (_historyThumbnailRequests.ContainsKey(pair.Key)) continue;
            var request = new HistoryThumbnailRequest(new CancellationTokenSource(), width, height);
            _historyThumbnailRequests.Add(pair.Key, request);
            _ = LoadHistoryThumbnailAsync(pair.Key, request);
        }
    }

    private async Task LoadHistoryThumbnailAsync(string path, HistoryThumbnailRequest request)
    {
        try
        {
            var pixels = await HistoryThumbnailService.DecodeAsync(path, request.Width, request.Height, request.Cancellation.Token);
            if (request.Cancellation.IsCancellationRequested || _historyClosed) return;
            var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
            using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels.Pixels);
            bitmap.Invalidate();
            var entry = new HistoryThumbnailCacheEntry(bitmap, request.Width, request.Height);
            _historyThumbnailCache[path] = entry;
            TouchHistoryThumbnailCacheEntry(path);
            while (_historyThumbnailCache.Count > HistoryThumbnailCacheLimit && _historyThumbnailCacheLru.Last is { } oldest)
            {
                _historyThumbnailCache.Remove(oldest.Value);
                _historyThumbnailCacheLru.RemoveLast();
            }
            foreach (var image in _historyRealizedImages)
                if (image.Tag is string target && string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
                    SetHistoryThumbnailSource(image, entry);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!request.Cancellation.IsCancellationRequested && !_historyClosed) _historyThumbnailFailures.Add(path);
        }
        finally
        {
            _historyThumbnailRequests.Remove(path);
            request.Cancellation.Dispose();
            QueueHistoryThumbnailPump();
        }
    }

    private void SetHistoryThumbnailSource(Image image, HistoryThumbnailCacheEntry entry)
    {
        if (ReferenceEquals(image.Source, entry.Bitmap)) return;
        _historyImageSources[image] = entry;
        image.Source = entry.Bitmap;
        SetHistoryThumbnailPlaceholder(image, loading: false);
        if (image.Tag is string path && _historyThumbnailRevealPendingPaths.Remove(path))
        {
            // Composition animation belongs to this realized element; it does not alter row height.
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(image);
            var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0, 0);
            animation.InsertKeyFrame(1, 1);
            animation.Duration = TimeSpan.FromMilliseconds(220);
            visual.StartAnimation("Opacity", animation);
        }
    }

    private void TouchHistoryThumbnailCacheEntry(string path)
    {
        var node = _historyThumbnailCacheLru.Find(path);
        if (node != null) _historyThumbnailCacheLru.Remove(node);
        _historyThumbnailCacheLru.AddFirst(path);
    }

    private void RemoveHistoryThumbnailCacheEntry(string path)
    {
        _historyThumbnailCache.Remove(path);
        _historyThumbnailCacheLru.Remove(path);
        _historyThumbnailFailures.Remove(path);
        _historyThumbnailRevealPendingPaths.Remove(path);
        if (_historyThumbnailRequests.TryGetValue(path, out var request)) request.Cancellation.Cancel();
    }

    private void CancelHistoryThumbnailRequests()
    {
        foreach (var request in _historyThumbnailRequests.Values) request.Cancellation.Cancel();
    }
}
