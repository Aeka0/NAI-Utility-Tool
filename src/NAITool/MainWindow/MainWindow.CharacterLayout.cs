using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace NAITool;

public sealed partial class MainWindow
{
    private async Task ShowCharacterLayoutDialogAsync()
    {
        SaveAllCharacterPrompts();
        var entries = CurrentCharacterEntries.Where(character => !character.IsDisabled).ToList();
        if (entries.Count == 0) return;

        const double maxSide = 290;
        double aspect = Math.Clamp((double)_customWidth / Math.Max(1, _customHeight), 0.25, 4);
        double width = aspect >= 1 ? maxSide : maxSide * aspect;
        double height = aspect >= 1 ? maxSide / aspect : maxSide;
        var positions = entries.ToDictionary(character => character,
            character => (X: character.CenterX, Y: character.CenterY));
        var dots = new List<Border>();
        var coordinateLabels = new List<TextBlock>();

        var canvas = new Canvas
        {
            Width = width,
            Height = height,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
        };
        for (int line = 1; line < 5; line++)
        {
            double fraction = line / 5d;
            canvas.Children.Add(new Line
            {
                X1 = fraction * width, X2 = fraction * width, Y1 = 0, Y2 = height,
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(70, 128, 128, 128)),
                StrokeThickness = 1,
                IsHitTestVisible = false,
            });
            canvas.Children.Add(new Line
            {
                X1 = 0, X2 = width, Y1 = fraction * height, Y2 = fraction * height,
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(70, 128, 128, 128)),
                StrokeThickness = 1,
                IsHitTestVisible = false,
            });
        }

        var custom = new CheckBox
        {
            Content = L("character.layout.custom"),
            IsChecked = entries.Any(character => character.UseCustomPosition),
        };

        void UpdateDot(int index)
        {
            var position = positions[entries[index]];
            Canvas.SetLeft(dots[index], position.X * width - dots[index].Width / 2);
            Canvas.SetTop(dots[index], position.Y * height - dots[index].Height / 2);
            coordinateLabels[index].Text = Lf("character.layout.coordinate", index + 1,
                position.X, position.Y);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            int index = i;
            var label = new TextBlock { FontSize = 12 };
            coordinateLabels.Add(label);
            var dot = new Border
            {
                Width = 31, Height = 31, CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255,
                    (byte)(58 + (i * 43) % 170), (byte)(95 + (i * 71) % 130),
                    (byte)(130 + (i * 29) % 100))),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                },
            };
            dots.Add(dot);
            canvas.Children.Add(dot);
            UpdateDot(i);

            bool dragging = false;
            dot.PointerPressed += (sender, args) =>
            {
                dragging = true;
                dot.CapturePointer(args.Pointer);
                custom.IsChecked = true;
                args.Handled = true;
            };
            dot.PointerMoved += (sender, args) =>
            {
                if (!dragging) return;
                var point = args.GetCurrentPoint(canvas).Position;
                positions[entries[index]] =
                    (Math.Round(Math.Clamp(point.X / width, 0, 1), 3),
                     Math.Round(Math.Clamp(point.Y / height, 0, 1), 3));
                UpdateDot(index);
                args.Handled = true;
            };
            dot.PointerReleased += (sender, args) =>
            {
                dragging = false;
                dot.ReleasePointerCapture(args.Pointer);
                args.Handled = true;
            };
        }

        var arrange = new Button { Content = L("character.layout.arrange") };
        arrange.Click += (_, _) =>
        {
            int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(entries.Count * aspect)));
            int rows = (int)Math.Ceiling((double)entries.Count / columns);
            for (int i = 0; i < entries.Count; i++)
            {
                positions[entries[i]] = (((i % columns) + 0.5) / columns,
                    ((i / columns) + 0.5) / rows);
                UpdateDot(i);
            }
            custom.IsChecked = true;
        };

        var coordinates = new StackPanel { Spacing = 3 };
        foreach (var label in coordinateLabels) coordinates.Children.Add(label);
        var stack = new StackPanel { Spacing = 10, Width = 340 };
        stack.Children.Add(new TextBlock
        {
            Text = Lf("character.layout.size", _customWidth, _customHeight),
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(custom);
        stack.Children.Add(new Border
        {
            Child = canvas,
            HorizontalAlignment = HorizontalAlignment.Center,
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 128, 128, 128)),
            BorderThickness = new Thickness(1),
        });
        stack.Children.Add(arrange);
        stack.Children.Add(new ScrollViewer { Content = coordinates, MaxHeight = 125 });

        var dialog = new ContentDialog
        {
            Title = L("character.layout.title"),
            Content = stack,
            PrimaryButtonText = L("common.apply"),
            CloseButtonText = L("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        foreach (var character in entries)
        {
            var position = positions[character];
            character.CenterX = position.X;
            character.CenterY = position.Y;
            character.UseCustomPosition = custom.IsChecked == true;
        }
        RefreshCharacterPanel();
    }
}
