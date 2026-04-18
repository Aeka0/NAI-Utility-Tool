using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace NAITool.Controls;

public readonly record struct PromptTextHighlight(int Start, int Length, Color Color);

public sealed class PromptTextBox : UserControl
{
    private const double HighlightHorizontalOffset = 2.0;
    private const double HighlightVerticalInset = 3.0;

    private readonly Grid _root;
    private readonly Canvas _highlightCanvas;
    private readonly TextBox _editor;
    private readonly SolidColorBrush _transparentBrush = new(Color.FromArgb(0, 0, 0, 0));
    private readonly List<PromptTextHighlight> _highlights = new();
    private readonly List<ScrollViewer> _editorScrollViewers = new();
    private bool _isHighlightRedrawQueued;
    private bool _isScrollMonitorAttached;
    private double _lastHorizontalOffset = double.NaN;
    private double _lastVerticalOffset = double.NaN;
    private Point _scrollOffsetCompensation;

    public event TextChangedEventHandler? TextChanged;

    public PromptTextBox()
    {
        _highlightCanvas = new Canvas
        {
            IsHitTestVisible = false,
        };

        _editor = new TextBox
        {
            Background = _transparentBrush,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            IsSpellCheckEnabled = false,
            Padding = new Thickness(8, 5, 8, 5),
        };

        _root = new Grid
        {
        };
        _root.Children.Add(_editor);
        _root.Children.Add(_highlightCanvas);
        Content = _root;

        _editor.TextChanged += OnEditorTextChanged;
        _editor.Loaded += (_, _) =>
        {
            EnsureEditorScrollViewer();
            StartScrollMonitor();
        };
        _editor.Unloaded += (_, _) => StopScrollMonitor();
        _editor.SizeChanged += (_, _) =>
        {
            EnsureEditorScrollViewer();
            QueueHighlightRedraw();
        };
        _editor.SelectionChanged += (_, _) => QueueHighlightRedraw();
        _editor.KeyUp += (_, _) => QueueHighlightRedraw();
        _editor.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler((_, _) => QueueHighlightRedraw()),
            handledEventsToo: true);
        SizeChanged += (_, _) => SyncHighlightLayout();
    }

    public string Text
    {
        get => _editor.Text;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_editor.Text, value, StringComparison.Ordinal))
                return;

            _editor.Text = value;
            QueueHighlightRedraw();
        }
    }

    public int SelectionStart
    {
        get => _editor.SelectionStart;
        set => _editor.SelectionStart = value;
    }

    public int SelectionLength
    {
        get => _editor.SelectionLength;
        set => _editor.SelectionLength = value;
    }

    public string SelectedText
    {
        get => _editor.SelectedText;
        set => _editor.SelectedText = value ?? string.Empty;
    }

    public bool AcceptsReturn
    {
        get => _editor.AcceptsReturn;
        set => _editor.AcceptsReturn = value;
    }

    public TextWrapping TextWrapping
    {
        get => _editor.TextWrapping;
        set
        {
            _editor.TextWrapping = value;
            QueueHighlightRedraw();
        }
    }

    public bool IsSpellCheckEnabled
    {
        get => _editor.IsSpellCheckEnabled;
        set => _editor.IsSpellCheckEnabled = value;
    }

    public string PlaceholderText
    {
        get => _editor.PlaceholderText;
        set => _editor.PlaceholderText = value ?? string.Empty;
    }

    public new Brush Background
    {
        get => _editor.Background;
        set => _editor.Background = value;
    }

    public new Brush Foreground
    {
        get => _editor.Foreground;
        set => _editor.Foreground = value;
    }

    public new FlyoutBase? ContextFlyout
    {
        get => _editor.ContextFlyout;
        set => _editor.ContextFlyout = value;
    }

    public new Thickness Padding
    {
        get => _editor.Padding;
        set
        {
            _editor.Padding = value;
            QueueHighlightRedraw();
        }
    }

    public new double FontSize
    {
        get => _editor.FontSize;
        set
        {
            _editor.FontSize = value;
            QueueHighlightRedraw();
        }
    }

    public new FontFamily FontFamily
    {
        get => _editor.FontFamily;
        set
        {
            _editor.FontFamily = value;
            QueueHighlightRedraw();
        }
    }

    public bool CanUndo => _editor.CanUndo;

    public bool IsApplyingHighlights { get; private set; }

    public void Undo() => _editor.Undo();

    public void CutSelectionToClipboard() => _editor.CutSelectionToClipboard();

    public void CopySelectionToClipboard() => _editor.CopySelectionToClipboard();

    public void PasteFromClipboard() => _editor.PasteFromClipboard();

    public void SelectAll() => _editor.SelectAll();

    public void Select(int start, int length) => _editor.Select(start, length);

    public Rect GetRectFromCharacterIndex(int charIndex, bool trailingEdge) =>
        _editor.GetRectFromCharacterIndex(charIndex, trailingEdge);

    public new bool Focus(FocusState value) => _editor.Focus(value);

    public void ApplyHighlights(IReadOnlyList<PromptTextHighlight> highlights)
    {
        IsApplyingHighlights = true;
        try
        {
            _highlights.Clear();
            int textLength = _editor.Text.Length;
            if (textLength == 0)
                return;

            foreach (var highlight in highlights)
            {
                if (highlight.Length <= 0 || highlight.Start >= textLength)
                    continue;

                int start = Math.Clamp(highlight.Start, 0, textLength);
                int end = Math.Clamp(highlight.Start + highlight.Length, start, textLength);
                if (end <= start)
                    continue;

                _highlights.Add(new PromptTextHighlight(start, end - start, highlight.Color));
            }
        }
        finally
        {
            IsApplyingHighlights = false;
            QueueHighlightRedraw();
        }
    }

    public void ClearHighlights()
    {
        ApplyHighlights(Array.Empty<PromptTextHighlight>());
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        QueueHighlightRedraw();
        TextChanged?.Invoke(this, e);
    }

    private void EnsureEditorScrollViewer()
    {
        _editor.ApplyTemplate();
        var scrollViewers = new List<ScrollViewer>();
        FindVisualChildren(_editor, scrollViewers);

        foreach (var scrollViewer in scrollViewers)
        {
            if (_editorScrollViewers.Contains(scrollViewer))
                continue;

            _editorScrollViewers.Add(scrollViewer);
            scrollViewer.ViewChanged += (_, _) => QueueHighlightRedraw();
            QueueHighlightRedraw();
        }
    }

    private void SyncHighlightLayout()
    {
        double width = Math.Max(0, ActualWidth);
        double height = Math.Max(0, ActualHeight);
        _highlightCanvas.Width = width;
        _highlightCanvas.Height = height;
        _root.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };
        QueueHighlightRedraw();
    }

    private void QueueHighlightRedraw()
    {
        if (_isHighlightRedrawQueued)
            return;

        _isHighlightRedrawQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _isHighlightRedrawQueued = false;
            EnsureEditorScrollViewer();
            RedrawHighlights();
        }))
        {
            _isHighlightRedrawQueued = false;
            EnsureEditorScrollViewer();
            RedrawHighlights();
        }
    }

    private void RedrawHighlights()
    {
        _highlightCanvas.Children.Clear();

        string text = _editor.Text;
        if (_highlights.Count == 0 || text.Length == 0 || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        _scrollOffsetCompensation = GetScrollOffsetCompensation(text);
        foreach (var highlight in _highlights)
        {
            int start = Math.Clamp(highlight.Start, 0, text.Length);
            int end = Math.Clamp(highlight.Start + highlight.Length, start, text.Length);
            if (end <= start)
                continue;

            DrawHighlightRange(text, start, end, highlight.Color);
        }
    }

    private void DrawHighlightRange(string text, int start, int end, Color color)
    {
        var brush = new SolidColorBrush(color);
        int pos = start;

        while (pos < end)
        {
            if (IsLineBreak(text[pos]))
            {
                pos++;
                continue;
            }

            if (!TryGetCharacterBox(pos, out double lineLeft, out double lineTop, out double lineRight, out double lineBottom))
            {
                pos++;
                continue;
            }

            pos++;
            while (pos < end)
            {
                if (IsLineBreak(text[pos]))
                    break;

                if (!TryGetCharacterBox(pos, out double left, out double top, out double right, out double bottom))
                {
                    pos++;
                    continue;
                }

                if (!IsSameTextLine(lineTop, lineBottom, top, bottom))
                    break;

                lineLeft = Math.Min(lineLeft, left);
                lineTop = Math.Min(lineTop, top);
                lineRight = Math.Max(lineRight, right);
                lineBottom = Math.Max(lineBottom, bottom);
                pos++;
            }

            AddHighlightRectangle(lineLeft, lineTop, lineRight, lineBottom, brush);
        }
    }

    private bool TryGetCharacterBox(int index, out double left, out double top, out double right, out double bottom)
    {
        left = top = right = bottom = 0;

        try
        {
            Rect leading = _editor.GetRectFromCharacterIndex(index, false);
            Rect trailing = _editor.GetRectFromCharacterIndex(index, true);
            if (!IsUsableRect(leading) || !IsUsableRect(trailing))
                return false;

            left = Math.Min(leading.X, trailing.X);
            right = Math.Max(leading.X, trailing.X);
            top = Math.Min(leading.Y, trailing.Y);
            bottom = Math.Max(leading.Y + leading.Height, trailing.Y + trailing.Height);

            if (right - left < 0.5 && index + 1 < _editor.Text.Length)
            {
                Rect nextLeading = _editor.GetRectFromCharacterIndex(index + 1, false);
                if (IsUsableRect(nextLeading)
                    && IsSameTextLine(top, bottom, nextLeading.Y, nextLeading.Y + nextLeading.Height))
                {
                    left = Math.Min(left, nextLeading.X);
                    right = Math.Max(right, nextLeading.X);
                    top = Math.Min(top, nextLeading.Y);
                    bottom = Math.Max(bottom, nextLeading.Y + nextLeading.Height);
                }
            }

            if (right - left < 0.5)
                right = left + 1;

            Point origin = GetTextContentOrigin();
            left += origin.X;
            right += origin.X;
            top += origin.Y;
            bottom += origin.Y;

            left -= _scrollOffsetCompensation.X;
            right -= _scrollOffsetCompensation.X;
            top -= _scrollOffsetCompensation.Y;
            bottom -= _scrollOffsetCompensation.Y;

            return bottom > top;
        }
        catch
        {
            return false;
        }
    }

    private Point GetTextContentOrigin()
    {
        double x = _editor.Padding.Left;
        double y = _editor.Padding.Top;

        var scrollViewer = GetActiveEditorScrollViewer();
        if (scrollViewer != null)
        {
            try
            {
                Point viewportOrigin = scrollViewer.TransformToVisual(_highlightCanvas)
                    .TransformPoint(new Point(0, 0));
                x += viewportOrigin.X;
                y += viewportOrigin.Y;
            }
            catch
            {
            }
        }

        return new Point(x, y);
    }

    private Point GetScrollOffsetCompensation(string text)
    {
        var scrollViewer = GetActiveEditorScrollViewer();
        if (scrollViewer == null)
            return new Point(0, 0);

        double horizontalOffset = scrollViewer.HorizontalOffset;
        double verticalOffset = scrollViewer.VerticalOffset;
        if (AreClose(horizontalOffset, 0) && AreClose(verticalOffset, 0))
            return new Point(0, 0);

        int probeIndex = GetFirstDrawableCharacterIndex(text);
        if (probeIndex >= 0)
        {
            try
            {
                Rect probe = _editor.GetRectFromCharacterIndex(probeIndex, false);
                if (IsUsableRect(probe)
                    && ((verticalOffset > 0 && probe.Y < -1)
                        || (horizontalOffset > 0 && probe.X < -1)))
                {
                    return new Point(0, 0);
                }
            }
            catch
            {
            }
        }

        return new Point(horizontalOffset, verticalOffset);
    }

    private ScrollViewer? GetActiveEditorScrollViewer()
    {
        ScrollViewer? fallback = null;
        foreach (var scrollViewer in _editorScrollViewers)
        {
            fallback ??= scrollViewer;
            if (scrollViewer.ScrollableHeight > 0
                || scrollViewer.ScrollableWidth > 0
                || scrollViewer.VerticalOffset != 0
                || scrollViewer.HorizontalOffset != 0)
            {
                return scrollViewer;
            }
        }

        return fallback;
    }

    private void StartScrollMonitor()
    {
        if (_isScrollMonitorAttached)
            return;

        _isScrollMonitorAttached = true;
        CompositionTarget.Rendering += OnCompositionRendering;
    }

    private void StopScrollMonitor()
    {
        if (!_isScrollMonitorAttached)
            return;

        _isScrollMonitorAttached = false;
        CompositionTarget.Rendering -= OnCompositionRendering;
        _lastHorizontalOffset = double.NaN;
        _lastVerticalOffset = double.NaN;
    }

    private void OnCompositionRendering(object? sender, object e)
    {
        EnsureEditorScrollViewer();
        var scrollViewer = GetActiveEditorScrollViewer();
        if (scrollViewer == null)
            return;

        double horizontalOffset = scrollViewer.HorizontalOffset;
        double verticalOffset = scrollViewer.VerticalOffset;
        if (AreClose(horizontalOffset, _lastHorizontalOffset) && AreClose(verticalOffset, _lastVerticalOffset))
            return;

        _lastHorizontalOffset = horizontalOffset;
        _lastVerticalOffset = verticalOffset;
        QueueHighlightRedraw();
    }

    private void AddHighlightRectangle(double left, double top, double right, double bottom, Brush brush)
    {
        left += HighlightHorizontalOffset;
        right += HighlightHorizontalOffset;
        top += HighlightVerticalInset;
        bottom -= HighlightVerticalInset;

        left = Math.Max(0, left);
        top = Math.Max(0, top);
        right = Math.Min(Math.Max(0, ActualWidth), right);
        bottom = Math.Min(Math.Max(0, ActualHeight), bottom);

        double width = right - left;
        double height = bottom - top;
        if (width <= 0 || height <= 0)
            return;

        var rectangle = new Rectangle
        {
            Width = width,
            Height = height,
            Fill = brush,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
        _highlightCanvas.Children.Add(rectangle);
    }

    private static bool IsLineBreak(char ch) => ch is '\r' or '\n';

    private static bool IsUsableRect(Rect rect) =>
        !double.IsNaN(rect.X)
        && !double.IsNaN(rect.Y)
        && !double.IsInfinity(rect.X)
        && !double.IsInfinity(rect.Y)
        && rect.Height > 0;

    private static bool IsSameTextLine(double top, double bottom, double otherTop, double otherBottom)
    {
        double overlap = Math.Min(bottom, otherBottom) - Math.Max(top, otherTop);
        double height = Math.Min(bottom - top, otherBottom - otherTop);
        return overlap > Math.Max(1, height * 0.45);
    }

    private static bool AreClose(double left, double right) =>
        Math.Abs(left - right) < 0.1;

    private static int GetFirstDrawableCharacterIndex(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (!IsLineBreak(text[i]))
                return i;
        }

        return -1;
    }

    private static void FindVisualChildren<T>(DependencyObject parent, List<T> results) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                results.Add(match);

            FindVisualChildren(child, results);
        }
    }
}
