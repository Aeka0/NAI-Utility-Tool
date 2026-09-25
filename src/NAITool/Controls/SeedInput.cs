using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using NAITool.Services;
using Windows.System;

namespace NAITool.Controls;

/// <summary>A text seed editor with exact integer stepping, independent of NumberBox's double value.</summary>
public sealed class SeedInput : UserControl
{
    private readonly TextBox _editor;

    public event EventHandler? ValueChanged;

    public string Value
    {
        get => _editor.Text;
        set => _editor.Text = value ?? "";
    }

    public object Header
    {
        get => _editor.Header;
        set => _editor.Header = value;
    }

    public SeedInput()
    {
        _editor = new TextBox
        {
            Text = "0",
            MinWidth = 0,
            MinHeight = 32,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
        };
        _editor.TextChanged += (_, _) => ValueChanged?.Invoke(this, EventArgs.Empty);
        _editor.KeyDown += OnEditorKeyDown;
        _editor.PointerWheelChanged += (_, e) =>
        {
            if (_editor.FocusState == FocusState.Unfocused) return;
            var pointer = e.GetCurrentPoint(_editor).Properties;
            if (pointer.IsHorizontalMouseWheel || pointer.MouseWheelDelta == 0) return;
            Adjust(Math.Sign(pointer.MouseWheelDelta));
            e.Handled = true;
        };

        var stepButtons = new Grid { Width = 24, Height = 32, VerticalAlignment = VerticalAlignment.Bottom };
        stepButtons.RowDefinitions.Add(new RowDefinition());
        stepButtons.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < 2; i++)
        {
            int delta = i == 0 ? 1 : -1;
            var button = new RepeatButton
            {
                Content = i == 0 ? "+" : "−",
                Padding = new Thickness(0),
                MinHeight = 0,
                MinWidth = 0,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            button.Click += (_, _) => Adjust(delta);
            Grid.SetRow(button, i);
            stepButtons.Children.Add(button);
        }

        var layout = new Grid { ColumnSpacing = 2 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(stepButtons, 1);
        layout.Children.Add(_editor);
        layout.Children.Add(stepButtons);
        Content = layout;
    }

    private void Adjust(int delta)
    {
        Value = SeedValue.Adjust(Value, delta);
        _editor.SelectionStart = _editor.Text.Length;
    }

    private void OnEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        int delta = e.Key switch
        {
            VirtualKey.Up => 1,
            VirtualKey.Down => -1,
            VirtualKey.PageUp => 10,
            VirtualKey.PageDown => -10,
            _ => 0,
        };
        if (delta == 0) return;
        Adjust(delta);
        e.Handled = true;
    }
}
