using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NAITool.Services;
using SkiaSharp;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace NAITool;

public sealed partial class MainWindow
{
    private void RefreshVibeTransferPanel()
    {
        if (VibeTransferContainer == null)
            return;

        VibeTransferContainer.Children.Clear();
        for (int i = 0; i < _genVibeTransfers.Count; i++)
            VibeTransferContainer.Children.Add(BuildVibeTransferUI(_genVibeTransfers[i], i));

        UpdateReferenceButtonAndPanelState();
    }

    private void RefreshPreciseReferencePanel()
    {
        if (PreciseReferenceContainer == null)
            return;

        PreciseReferenceContainer.Children.Clear();
        for (int i = 0; i < _genPreciseReferences.Count; i++)
            PreciseReferenceContainer.Children.Add(BuildPreciseReferenceUI(_genPreciseReferences[i], i));

        UpdateReferenceButtonAndPanelState();
    }

    private UIElement BuildVibeTransferUI(VibeTransferEntry entry, int index)
    {
        var root = new StackPanel { Spacing = 6 };

        string vibeTitle = entry.IsCachedEncoding
            ? Lf("references.vibe.cached_title", index + 1)
            : Lf("references.vibe.title", index + 1);
        var header = BuildReferenceHeader(
            vibeTitle,
            entry.IsCollapsed,
            canMoveUp: index > 0,
            canMoveDown: index < _genVibeTransfers.Count - 1,
            onMoveUp: () => MoveVibeTransfer(index, -1),
            onMoveDown: () => MoveVibeTransfer(index, 1),
            onDelete: () =>
            {
                _genVibeTransfers.Remove(entry);
                RefreshVibeTransferPanel();
                UpdateGenerateButtonWarning();
            },
            onCollapse: () =>
            {
                entry.IsCollapsed = !entry.IsCollapsed;
                RefreshVibeTransferPanel();
            });
        root.Children.Add(header);

        if (!entry.IsCollapsed)
        {
            bool canEdit = CanEditVibeTransferFeature();

            var infoGrid = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 2, 0, 0) };
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var thumbImage = new Image
            {
                Width = 64, Height = 64,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
            };
            var thumbBorder = new Border
            {
                Width = 64, Height = 64,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 128, 128, 128)),
                Child = thumbImage,
            };
            thumbBorder.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, 64, 64),
            };
            _ = LoadVibeThumbAsync(entry, thumbImage);
            Grid.SetColumn(thumbBorder, 0);
            infoGrid.Children.Add(thumbBorder);

            var rightCol = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Top };

            var fileNameBlock = new TextBlock
            {
                Text = entry.FileName,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                Opacity = 0.85,
            };
            rightCol.Children.Add(fileNameBlock);

            if (entry.IsCachedEncoding)
            {
                rightCol.Children.Add(new TextBlock
                {
                    Text = Lf("references.vibe.cached_badge", GetCurrentModelKey()),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 80, 200, 120)),
                    Opacity = 0.85,
                });
            }
            else if (entry.IsEncodedFile && entry.OriginalImageHash == null)
            {
                rightCol.Children.Add(new TextBlock
                {
                    Text = L("references.vibe.encoded_file"),
                    FontSize = 11,
                    Opacity = 0.5,
                });
            }
            else
            {
                rightCol.Children.Add(new TextBlock
                {
                    Text = L("references.vibe.unencoded_cost"),
                    FontSize = 11,
                    Opacity = 0.5,
                });
            }

            var replaceBtn = new Button
            {
                Content = L("references.vibe.replace"),
                FontSize = 12,
                MinWidth = 52, MinHeight = 24,
                Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 0),
            };
            replaceBtn.Click += async (_, _) =>
            {
                var picked = await PickVibeTransferSourceAsync();
                if (picked == null) return;
                entry.FileName = picked.FileName;
                entry.ImageBase64 = picked.ImageBase64;
                entry.IsEncodedFile = picked.IsEncodedFile;
                entry.OriginalImageHash = picked.ImageHash;
                entry.OriginalThumbnailHash = picked.ThumbnailHash;
                entry.OriginalImageBase64 = picked.OriginalBase64;
                entry.IsCachedEncoding = picked.IsCachedHit;
                RefreshVibeTransferPanel();
                UpdateGenerateButtonWarning();
                TxtStatus.Text = Lf("references.vibe.updated", entry.FileName);
            };
            replaceBtn.IsEnabled = canEdit;
            rightCol.Children.Add(replaceBtn);

            Grid.SetColumn(rightCol, 1);
            infoGrid.Children.Add(rightCol);
            infoGrid.IsHitTestVisible = canEdit;
            infoGrid.Opacity = canEdit ? 1.0 : 0.72;
            root.Children.Add(infoGrid);

            var strengthRow = BuildReferenceSliderRow(
                L("references.vibe.reference_strength"),
                0, 1, entry.Strength,
                value => entry.Strength = Math.Round(value, 2));
            strengthRow.IsHitTestVisible = canEdit;
            strengthRow.Opacity = canEdit ? 1.0 : 0.72;
            root.Children.Add(strengthRow);

            var infoRow = BuildReferenceSliderRow(
                L("references.vibe.info_extracted"),
                0, 1, entry.InformationExtracted,
                value =>
                {
                    entry.InformationExtracted = Math.Round(value, 2);
                    RecheckVibeTransferCacheState(refreshPanel: false);
                });
            infoRow.IsHitTestVisible = canEdit;
            infoRow.Opacity = canEdit ? 1.0 : 0.72;
            root.Children.Add(infoRow);
        }

        if (index < _genVibeTransfers.Count - 1)
            root.Children.Add(CreateReferenceSeparator());
        root.Opacity = CanEditVibeTransferFeature() ? 1.0 : 0.72;
        return root;
    }

    private async Task LoadVibeThumbAsync(VibeTransferEntry entry, Image target)
    {
        try
        {
            byte[]? thumbBytes = null;

            if (entry.OriginalImageBase64 != null)
            {
                thumbBytes = Convert.FromBase64String(entry.OriginalImageBase64);
            }
            else if (entry.OriginalImageHash != null && entry.OriginalThumbnailHash != null)
            {
                string cacheDir = VibeCacheService.GetCacheDir(AppRootDir);
                string? thumbPath = VibeCacheService.GetThumbnailPath(
                    cacheDir, entry.OriginalImageHash, entry.OriginalThumbnailHash);
                if (thumbPath != null)
                    thumbBytes = await Task.Run(() => File.ReadAllBytes(thumbPath));
            }

            if (thumbBytes != null && thumbBytes.Length > 0)
            {
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(thumbBytes);
                await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                target.Source = bmp;
            }
            else
            {
                target.Source = null;
            }
        }
        catch
        {
            target.Source = null;
        }
    }

    private UIElement BuildPreciseReferenceUI(PreciseReferenceEntry entry, int index)
    {
        var root = new StackPanel { Spacing = 6 };

        var header = BuildReferenceHeader(
            Lf("references.precise.cached", index + 1),
            entry.IsCollapsed,
            canMoveUp: index > 0,
            canMoveDown: index < _genPreciseReferences.Count - 1,
            onMoveUp: () => MovePreciseReference(index, -1),
            onMoveDown: () => MovePreciseReference(index, 1),
            onDelete: () =>
            {
                _genPreciseReferences.Remove(entry);
                RefreshPreciseReferencePanel();
                UpdateGenerateButtonWarning();
            },
            onCollapse: () =>
            {
                entry.IsCollapsed = !entry.IsCollapsed;
                RefreshPreciseReferencePanel();
            });
        root.Children.Add(header);

        if (!entry.IsCollapsed)
        {
            bool canEdit = CanEditPreciseReferenceFeature();

            var infoGrid = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 2, 0, 0) };
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var thumbImage = new Image
            {
                Width = 64, Height = 64,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
            };
            var thumbBorder = new Border
            {
                Width = 64, Height = 64,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 128, 128, 128)),
                Child = thumbImage,
            };
            thumbBorder.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, 64, 64),
            };
            _ = LoadPreciseRefThumbAsync(entry, thumbImage);
            Grid.SetColumn(thumbBorder, 0);
            infoGrid.Children.Add(thumbBorder);

            var rightCol = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Top };
            rightCol.Children.Add(new TextBlock
            {
                Text = entry.FileName,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                Opacity = 0.85,
            });

            var replaceBtn = new Button
            {
                Content = L("references.precise.replace"),
                FontSize = 12,
                MinWidth = 52, MinHeight = 24,
                Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 0),
            };
            replaceBtn.Click += async (_, _) =>
            {
                var picked = await PickReferenceImageAsync();
                if (picked == null) return;
                entry.FileName = picked.Value.FileName;
                entry.ImageBase64 = picked.Value.ImageBase64;
                RefreshPreciseReferencePanel();
                TxtStatus.Text = Lf("references.precise.updated", entry.FileName);
            };
            replaceBtn.IsEnabled = canEdit;
            rightCol.Children.Add(replaceBtn);

            Grid.SetColumn(rightCol, 1);
            infoGrid.Children.Add(rightCol);
            infoGrid.IsHitTestVisible = canEdit;
            infoGrid.Opacity = canEdit ? 1.0 : 0.72;
            root.Children.Add(infoGrid);

            var typeCombo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 32,
                FontFamily = UiTextFontFamily,
            };
            typeCombo.Items.Add(CreateTextComboBoxItem(L("references.precise.type.both")));
            typeCombo.Items.Add(CreateTextComboBoxItem(L("references.precise.type.character")));
            typeCombo.Items.Add(CreateTextComboBoxItem(L("references.precise.type.style")));
            typeCombo.SelectedIndex = entry.ReferenceType switch
            {
                PreciseReferenceType.CharacterAndStyle => 0,
                PreciseReferenceType.Character => 1,
                _ => 2,
            };
            typeCombo.SelectionChanged += (_, _) =>
            {
                entry.ReferenceType = typeCombo.SelectedIndex switch
                {
                    1 => PreciseReferenceType.Character,
                    2 => PreciseReferenceType.Style,
                    _ => PreciseReferenceType.CharacterAndStyle,
                };
            };
            ApplyMenuTypography(typeCombo);
            typeCombo.IsEnabled = canEdit;
            root.Children.Add(typeCombo);

            var strengthRow = BuildReferenceSliderRow(
                L("references.precise.strength"),
                -1, 1, entry.Strength,
                value => entry.Strength = Math.Round(value, 2));
            strengthRow.IsHitTestVisible = canEdit;
            strengthRow.Opacity = canEdit ? 1.0 : 0.72;
            root.Children.Add(strengthRow);

            var fidelityRow = BuildReferenceSliderRow(
                L("references.precise.fidelity"),
                -1, 1, entry.Fidelity,
                value => entry.Fidelity = Math.Round(value, 2));
            fidelityRow.IsHitTestVisible = canEdit;
            fidelityRow.Opacity = canEdit ? 1.0 : 0.72;
            root.Children.Add(fidelityRow);
        }

        if (index < _genPreciseReferences.Count - 1)
            root.Children.Add(CreateReferenceSeparator());
        return root;
    }

    private async Task LoadPreciseRefThumbAsync(PreciseReferenceEntry entry, Image target)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(entry.ImageBase64)) return;
            byte[] bytes = Convert.FromBase64String(entry.ImageBase64);
            if (bytes.Length == 0) return;
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            await bmp.SetSourceAsync(ms.AsRandomAccessStream());
            target.Source = bmp;
        }
        catch
        {
            target.Source = null;
        }
    }

    private Border CreateReferenceSeparator() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 2, 0, 0),
        Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
    };

    private UIElement BuildReferenceHeader(
        string title,
        bool isCollapsed,
        bool canMoveUp,
        bool canMoveDown,
        Action onMoveUp,
        Action onMoveDown,
        Action onDelete,
        Action onCollapse)
    {
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var collapseBtn = CreateCharacterCollapseButton(isCollapsed);
        collapseBtn.Click += (_, _) => onCollapse();
        Grid.SetColumn(collapseBtn, 0);
        headerGrid.Children.Add(collapseBtn);

        var label = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)((Grid)this.Content).Resources["InspectCaptionStyle"],
        };
        Grid.SetColumn(label, 1);
        headerGrid.Children.Add(label);

        var movePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = isCollapsed ? Visibility.Collapsed : Visibility.Visible,
        };
        var upBtn = CreateCharacterActionButton("\uE70E", L("references.action.move_up"), canMoveUp);
        var downBtn = CreateCharacterActionButton("\uE70D", L("references.action.move_down"), canMoveDown);
        upBtn.Click += (_, _) => onMoveUp();
        downBtn.Click += (_, _) => onMoveDown();
        movePanel.Children.Add(upBtn);
        movePanel.Children.Add(downBtn);
        Grid.SetColumn(movePanel, 2);
        headerGrid.Children.Add(movePanel);

        var delBtn = CreateCharacterActionButton("\uE74D", L("references.action.delete"), true, isDelete: true);
        delBtn.Margin = new Thickness(4, 0, 0, 0);
        delBtn.Click += (_, _) => onDelete();
        delBtn.Visibility = isCollapsed ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(delBtn, 3);
        headerGrid.Children.Add(delBtn);

        return headerGrid;
    }

    // BuildReferenceFileRow removed — replaced by inline thumbnail layout

    private StackPanel BuildReferenceSliderRow(
        string label,
        double min,
        double max,
        double value,
        Action<double> onValueChanged)
    {
        var row = new StackPanel { Spacing = 4 };
        row.Children.Add(CreateThemedSubLabel(label));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            StepFrequency = 0.01,
            Value = value,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var valueText = new TextBlock
        {
            Text = value.ToString("0.00"),
            MinWidth = 38,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, args) =>
        {
            double next = Math.Clamp(args.NewValue, min, max);
            valueText.Text = next.ToString("0.00");
            onValueChanged(next);
            UpdateGenerateButtonWarning();
        };

        Grid.SetColumn(slider, 0);
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(slider);
        grid.Children.Add(valueText);
        row.Children.Add(grid);
        return row;
    }

    private void MoveVibeTransfer(int index, int direction)
    {
        int newIndex = index + direction;
        if (newIndex < 0 || newIndex >= _genVibeTransfers.Count) return;
        var entry = _genVibeTransfers[index];
        _genVibeTransfers.RemoveAt(index);
        _genVibeTransfers.Insert(newIndex, entry);
        RefreshVibeTransferPanel();
    }

    private void MovePreciseReference(int index, int direction)
    {
        int newIndex = index + direction;
        if (newIndex < 0 || newIndex >= _genPreciseReferences.Count) return;
        var entry = _genPreciseReferences[index];
        _genPreciseReferences.RemoveAt(index);
        _genPreciseReferences.Insert(newIndex, entry);
        RefreshPreciseReferencePanel();
    }
}
