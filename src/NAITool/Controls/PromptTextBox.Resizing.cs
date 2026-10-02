using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Windows.System;

namespace NAITool.Controls;

public sealed partial class PromptTextBox
{
    private PromptResizeCorner _resizeCorner = null!;
    private bool _resizingEnabled;
    private bool _resizePointerOver;
    private double _minimumResizeHeight = 60;
    private uint? _resizePointerId;
    private UIElement? _resizeCoordinateRoot;
    private double _resizeStartY;
    private double _resizeStartHeight;
    private double _lastResizeHeight;
    private bool _keyboardResizeChanged;

    public event Action<double>? ResizeHeightRequested;
    public event Action? ResizeCompleted;
    public event Action? AutoSizeRequested;

    internal bool IsResizeCornerFocused => _resizeCorner.FocusState != FocusState.Unfocused;

    internal static double NormalizeEditorHeight(double height, double minimum = 60) =>
        double.IsFinite(height) && height > 0 ? Math.Clamp(height, minimum, 4096) : 0;

    private void InitializeResizing()
    {
        _resizeCorner = new PromptResizeCorner
        {
            Width = 20,
            Height = 20,
            Margin = new Thickness(0, 0, 2, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
            Opacity = 0,
            IsHitTestVisible = false,
            IsTabStop = true,
            UseSystemFocusVisuals = true,
        };
        // Overlay the editor so the affordance never consumes a row or changes its text layout.
        _root.Children.Add(_resizeCorner);
        AddHandler(PointerEnteredEvent, new PointerEventHandler(OnResizeHoverChanged), true);
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnResizeHoverChanged), true);
        AddHandler(PointerExitedEvent, new PointerEventHandler(OnResizeHoverChanged), true);
        _resizeCorner.GotFocus += (_, _) => UpdateResizeCornerVisibility();
        _resizeCorner.LostFocus += (_, _) =>
        {
            FinishKeyboardResizing();
            UpdateResizeCornerVisibility();
        };
        _resizeCorner.PointerPressed += OnResizeCornerPressed;
        _resizeCorner.PointerMoved += OnResizeCornerMoved;
        _resizeCorner.PointerReleased += (_, e) =>
        {
            if (_resizePointerId != e.Pointer.PointerId) return;
            FinishResizing();
            e.Handled = true;
        };
        _resizeCorner.PointerCaptureLost += (_, _) => FinishResizing();
        _resizeCorner.PointerCanceled += (_, _) => FinishResizing();
        _resizeCorner.DoubleTapped += (_, e) =>
        {
            AutoSizeRequested?.Invoke();
            e.Handled = true;
        };
        _resizeCorner.KeyDown += OnResizeCornerKeyDown;
        _resizeCorner.KeyUp += (_, e) =>
        {
            if (e.Key is VirtualKey.Up or VirtualKey.Down) FinishKeyboardResizing();
        };
        Unloaded += (_, _) =>
        {
            _resizePointerOver = false;
            FinishResizing();
            FinishKeyboardResizing();
            UpdateResizeCornerVisibility();
        };
    }

    public void EnableResizing(string helpText, double minimumHeight = 60)
    {
        _resizingEnabled = true;
        _minimumResizeHeight = minimumHeight;
        _resizeCorner.Visibility = Visibility.Visible;
        ToolTipService.SetToolTip(_resizeCorner, helpText);
        AutomationProperties.SetName(_resizeCorner, helpText);
        UpdateResizeCornerVisibility();
    }

    private void OnResizeHoverChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Position;
        _resizePointerOver = point.X >= 0 && point.Y >= 0 && point.X < ActualWidth && point.Y < ActualHeight;
        UpdateResizeCornerVisibility();
    }

    private void UpdateResizeCornerVisibility()
    {
        bool visible = _resizingEnabled && (_resizePointerOver || _resizePointerId != null ||
            _resizeCorner.FocusState == FocusState.Keyboard);
        if (_resizeCorner.IsHitTestVisible == visible) return;
        _resizeCorner.Opacity = visible ? 0.8 : 0;
        _resizeCorner.IsHitTestVisible = visible;
    }

    private void OnResizeCornerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_resizePointerId != null || !e.GetCurrentPoint(_resizeCorner).Properties.IsLeftButtonPressed)
            return;
        if (!_resizeCorner.CapturePointer(e.Pointer)) return;

        _resizePointerId = e.Pointer.PointerId;
        // Window coordinates stay fixed while this corner moves with the resized editor.
        _resizeCoordinateRoot = XamlRoot.Content;
        _resizeStartY = e.GetCurrentPoint(_resizeCoordinateRoot).Position.Y;
        _resizeStartHeight = _lastResizeHeight = ActualHeight;
        UpdateResizeCornerVisibility();
        e.Handled = true;
    }

    private void OnResizeCornerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resizePointerId != e.Pointer.PointerId) return;
        double delta = e.GetCurrentPoint(_resizeCoordinateRoot).Position.Y - _resizeStartY;
        double height = Math.Clamp(_resizeStartHeight + delta, _minimumResizeHeight, 4096);
        if (Math.Abs(height - _lastResizeHeight) >= 0.5)
        {
            _lastResizeHeight = height;
            ResizeHeightRequested?.Invoke(height);
        }
        e.Handled = true;
    }

    private void FinishResizing()
    {
        if (_resizePointerId == null) return;
        _resizePointerId = null;
        _resizeCoordinateRoot = null;
        _resizeCorner.ReleasePointerCaptures();
        UpdateResizeCornerVisibility();
        if (Math.Abs(_lastResizeHeight - _resizeStartHeight) >= 0.5)
            ResizeCompleted?.Invoke();
    }

    private void OnResizeCornerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Up or VirtualKey.Down)
        {
            ResizeHeightRequested?.Invoke(Math.Clamp(ActualHeight + (e.Key == VirtualKey.Up ? -20 : 20),
                _minimumResizeHeight, 4096));
            _keyboardResizeChanged = true;
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Home)
        {
            _keyboardResizeChanged = false;
            AutoSizeRequested?.Invoke();
            e.Handled = true;
        }
    }

    private void FinishKeyboardResizing()
    {
        if (!_keyboardResizeChanged) return;
        _keyboardResizeChanged = false;
        ResizeCompleted?.Invoke();
    }

    private sealed class PromptResizeCorner : Control
    {
        public PromptResizeCorner()
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
            Template = (ControlTemplate)XamlReader.Load(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                "<Grid Background='Transparent'><Path Data='M 3,15 L 15,3 M 8,15 L 15,8 M 13,15 L 15,13' " +
                "Stroke='{ThemeResource TextFillColorSecondaryBrush}' StrokeThickness='1.3' " +
                "HorizontalAlignment='Center' VerticalAlignment='Center'/></Grid></ControlTemplate>");
        }
    }
}
