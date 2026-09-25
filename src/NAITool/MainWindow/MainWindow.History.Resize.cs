using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using NAITool.Services;

namespace NAITool;

public sealed partial class MainWindow
{
    private uint? _historyResizePointerId;
    private double _historyResizeStartX;
    private double _historyResizeStartWidth;
    private double _historyResizeSavedWidth;

    private void OnHistorySidebarContainerSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateHistorySidebarWidth();

    private double ClampHistorySidebarWidth(double width)
    {
        double maximum = AppSettings.MaxHistorySidebarWidth;
        if (MainContentGrid.ActualWidth > 0)
        {
            double available = Math.Max(0, MainContentGrid.ActualWidth
                - MainContentGrid.ColumnDefinitions[0].Width.Value
                - MainContentGrid.ColumnDefinitions[1].Width.Value);
            // Keep 320 DIPs for the preview, or 60% of the remaining space in very small windows.
            maximum = Math.Min(maximum, available - Math.Min(320, available * 0.6));
        }
        return Math.Clamp(width, Math.Min(AppSettings.MinHistorySidebarWidth, maximum), maximum);
    }

    private void UpdateHistorySidebarWidth()
    {
        if (RightSidebarColumn == null) return;
        double width = _currentMode == AppMode.ImageGeneration
            ? ClampHistorySidebarWidth(_settings.Settings.HistorySidebarWidth)
            : AppSettings.DefaultHistorySidebarWidth;
        if (Math.Abs(RightSidebarColumn.Width.Value - width) >= 0.5)
            RightSidebarColumn.Width = new GridLength(width);
    }

    private void OnHistoryResizePressed(object sender, PointerRoutedEventArgs e)
    {
        if (_currentMode != AppMode.ImageGeneration || _historyResizePointerId != null) return;
        var point = e.GetCurrentPoint(MainContentGrid);
        if (!point.Properties.IsLeftButtonPressed || !HistoryResizeHandle.CapturePointer(e.Pointer)) return;

        _historyResizePointerId = e.Pointer.PointerId;
        _historyResizeStartX = point.Position.X;
        _historyResizeStartWidth = RightSidebarColumn.ActualWidth;
        _historyResizeSavedWidth = _settings.Settings.HistorySidebarWidth;
        HistoryResizeGrip.Opacity = 1;
        e.Handled = true;
    }

    private void OnHistoryResizeMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_historyResizePointerId != e.Pointer.PointerId) return;
        // Use the stationary parent coordinate space so moving the handle cannot cause feedback.
        double width = ClampHistorySidebarWidth(_historyResizeStartWidth
            + _historyResizeStartX - e.GetCurrentPoint(MainContentGrid).Position.X);
        _settings.Settings.HistorySidebarWidth = Math.Max(AppSettings.MinHistorySidebarWidth, width);
        UpdateHistorySidebarWidth();
        e.Handled = true;
    }

    private void OnHistoryResizeReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_historyResizePointerId != e.Pointer.PointerId) return;
        FinishHistorySidebarResize();
        e.Handled = true;
    }

    private void FinishHistorySidebarResize()
    {
        if (_historyResizePointerId == null) return;
        _historyResizePointerId = null;
        HistoryResizeHandle.ReleasePointerCaptures();
        HistoryResizeGrip.Opacity = 0.45;
        // Persist once per gesture, never on each pointer move or automatic window-size adjustment.
        if (Math.Abs(_settings.Settings.HistorySidebarWidth - _historyResizeSavedWidth) >= 0.5)
            _settings.Save();
    }

    private void OnHistoryResizeEntered(object sender, PointerRoutedEventArgs e) => HistoryResizeGrip.Opacity = 1;

    private void OnHistoryResizeExited(object sender, PointerRoutedEventArgs e)
    {
        if (_historyResizePointerId == null) HistoryResizeGrip.Opacity = 0.45;
    }
}
