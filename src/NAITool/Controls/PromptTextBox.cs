using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace NAITool.Controls;

public readonly record struct PromptTextHighlight(int Start, int Length, Color Color);

public sealed class PromptTextBox : UserControl
{
    private readonly Grid _root;
    private readonly TextBlock _highlightText;
    private readonly TextBox _editor;
    private readonly TranslateTransform _highlightTransform = new();
    private readonly SolidColorBrush _transparentBrush = new(Color.FromArgb(0, 0, 0, 0));
    private ScrollViewer? _editorScrollViewer;

    public event TextChangedEventHandler? TextChanged;

    public PromptTextBox()
    {
        _highlightText = new TextBlock
        {
            IsHitTestVisible = false,
            Foreground = _transparentBrush,
            TextWrapping = TextWrapping.Wrap,
            RenderTransform = _highlightTransform,
            Padding = new Thickness(8, 5, 8, 5),
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
        _root.Children.Add(_highlightText);
        Content = _root;

        _editor.TextChanged += OnEditorTextChanged;
        _editor.Loaded += (_, _) => HookEditorScrollViewer();
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
            _highlightText.Text = value;
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
            _highlightText.TextWrapping = value;
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
            _highlightText.Padding = value;
        }
    }

    public new double FontSize
    {
        get => _editor.FontSize;
        set
        {
            _editor.FontSize = value;
            _highlightText.FontSize = value;
        }
    }

    public new FontFamily FontFamily
    {
        get => _editor.FontFamily;
        set
        {
            _editor.FontFamily = value;
            _highlightText.FontFamily = value;
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
            _highlightText.TextHighlighters.Clear();
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

                var textHighlighter = new TextHighlighter
                {
                    Background = new SolidColorBrush(highlight.Color),
                    Foreground = _transparentBrush,
                };
                textHighlighter.Ranges.Add(new TextRange { StartIndex = start, Length = end - start });
                _highlightText.TextHighlighters.Add(textHighlighter);
            }
        }
        finally
        {
            IsApplyingHighlights = false;
        }
    }

    public void ClearHighlights()
    {
        ApplyHighlights(Array.Empty<PromptTextHighlight>());
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        _highlightText.Text = _editor.Text;
        SyncHighlightScroll();
        TextChanged?.Invoke(this, e);
    }

    private void HookEditorScrollViewer()
    {
        if (_editorScrollViewer != null)
            return;

        _editorScrollViewer = FindVisualChild<ScrollViewer>(_editor);
        if (_editorScrollViewer != null)
        {
            _editorScrollViewer.ViewChanged += (_, _) => SyncHighlightScroll();
            SyncHighlightScroll();
        }

        SyncHighlightLayout();
    }

    private void SyncHighlightLayout()
    {
        _highlightText.Width = Math.Max(0, ActualWidth);
        _root.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)) };
        SyncHighlightScroll();
    }

    private void SyncHighlightScroll()
    {
        if (_editorScrollViewer == null)
            return;

        _highlightTransform.X = -_editorScrollViewer.HorizontalOffset;
        _highlightTransform.Y = -_editorScrollViewer.VerticalOffset;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;

            var nested = FindVisualChild<T>(child);
            if (nested != null)
                return nested;
        }

        return null;
    }
}
