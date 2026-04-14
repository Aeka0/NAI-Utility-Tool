using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using NAITool.Controls;
using NAITool.Models;
using NAITool.Services;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using System.Runtime.InteropServices.WindowsRuntime;

namespace NAITool;

public sealed partial class MainWindow
{
    private class CharacterEntry
    {
        public string PositivePrompt { get; set; } = "";
        public string NegativePrompt { get; set; } = "";
        public double CenterX { get; set; } = 0.5;
        public double CenterY { get; set; } = 0.5;
        public bool IsPositiveTab { get; set; } = true;
        public bool IsCollapsed { get; set; }
        public bool IsDisabled { get; set; }
        public bool UseCustomPosition { get; set; }
        public TextBox? PromptBox { get; set; }
        public Canvas? HighlightCanvas { get; set; }
        public int HighlightVersion { get; set; }
    }

    private sealed class PromptShortcutEntry
    {
        public string Shortcut { get; set; } = "";
        public string Prompt { get; set; } = "";
    }

    private void OnAddCharacter(object sender, RoutedEventArgs e)
    {
        if (_genCharacters.Count >= MaxCharacters) return;
        _genCharacters.Add(new CharacterEntry());
        RefreshCharacterPanel();
    }

    private void RefreshCharacterPanel()
    {
        SaveAllCharacterPrompts();
        CharacterContainer.Children.Clear();
        for (int i = 0; i < _genCharacters.Count; i++)
            CharacterContainer.Children.Add(BuildCharacterUI(_genCharacters[i], i));
        RefreshVibeTransferPanel();
        RefreshPreciseReferencePanel();
        UpdateReferenceButtonAndPanelState();
        UpdateGenerateButtonWarning();
    }

    private UIElement BuildCharacterUI(CharacterEntry entry, int index)
    {
        var container = new StackPanel { Spacing = 4 };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tabPanel = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tabPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tabPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tabPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rootGrid = (Grid)this.Content;
        var collapseBtn = CreateCharacterCollapseButton(entry.IsCollapsed);
        Grid.SetColumn(collapseBtn, 0);
        headerGrid.Children.Add(collapseBtn);

        var label = new TextBlock
        {
            Text = Lf("character.label", index + 1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)rootGrid.Resources["InspectCaptionStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (entry.IsDisabled)
            label.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
        Grid.SetColumn(label, 0);
        tabPanel.Children.Add(label);

        var tabPos = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
        {
            Content = CreateTabHeaderText(L("prompt.positive_compact_character"), 11), IsChecked = entry.IsPositiveTab,
            CornerRadius = new CornerRadius(4, 0, 0, 4),
            MinWidth = 0, Height = 26, Padding = new Thickness(8, 2, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontSize = 11,
        };
        var tabNeg = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
        {
            Content = CreateTabHeaderText(L("prompt.negative_compact_character"), 11), IsChecked = !entry.IsPositiveTab,
            CornerRadius = new CornerRadius(0, 4, 4, 0),
            MinWidth = 0, Height = 26, Padding = new Thickness(8, 2, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontSize = 11,
        };
        Grid.SetColumn(tabPos, 1);
        Grid.SetColumn(tabNeg, 2);
        tabPanel.Children.Add(tabPos);
        tabPanel.Children.Add(tabNeg);
        Grid.SetColumn(tabPanel, 1);
        headerGrid.Children.Add(tabPanel);

        var movePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var upBtn = CreateCharacterActionButton("\uE70E", L("references.action.move_up"), index > 0);
        var downBtn = CreateCharacterActionButton("\uE70D", L("references.action.move_down"), index < _genCharacters.Count - 1);
        movePanel.Children.Add(upBtn);
        movePanel.Children.Add(downBtn);
        Grid.SetColumn(movePanel, 2);
        headerGrid.Children.Add(movePanel);

        int capturedMoveIdx = index;
        upBtn.Click += (_, _) => MoveCharacter(capturedMoveIdx, -1);
        downBtn.Click += (_, _) => MoveCharacter(capturedMoveIdx, 1);

        var posBtn = CreateCharacterActionButton("\uE819", L("character.position"), true);
        posBtn.Margin = new Thickness(2, 0, 0, 0);
        Grid.SetColumn(posBtn, 3);
        headerGrid.Children.Add(posBtn);

        var disableBtn = CreateCharacterActionButton(
            entry.IsDisabled ? "\uE8FA" : "\uE8F8",
            entry.IsDisabled ? L("character.restore") : L("character.disable"),
            true);
        disableBtn.Margin = new Thickness(2, 0, 0, 0);
        Grid.SetColumn(disableBtn, 4);
        headerGrid.Children.Add(disableBtn);

        var delBtn = CreateCharacterActionButton("\uE74D", L("character.delete"), true, isDelete: true);
        delBtn.Margin = new Thickness(2, 0, 0, 0);
        Grid.SetColumn(delBtn, 5);
        headerGrid.Children.Add(delBtn);

        var textGrid = new Grid { MinHeight = 50, MaxHeight = 120 };
        var highlightCanvas = new Canvas { IsHitTestVisible = false };
        var textBox = new TextBox
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorTransparentBrush"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            IsSpellCheckEnabled = false,
            PlaceholderText = entry.IsPositiveTab ? L("character.prompt_positive_placeholder") : L("character.prompt_negative_placeholder"),
            Text = entry.IsPositiveTab ? entry.PositivePrompt : entry.NegativePrompt,
            MinHeight = 50, MaxHeight = 120,
            FontSize = 12,
        };
        textBox.TextChanged += (_, _) =>
        {
            UpdateCharacterHighlight(entry);
            if (!_acInserting) TriggerAutoComplete(textBox);
        };
        textBox.SizeChanged += (_, _) => UpdateCharacterHighlight(entry);
        textBox.PreviewKeyDown += OnPromptPreviewKeyDown;
        textBox.KeyDown += OnPromptKeyDown;
        textBox.LostFocus += (_, _) => CloseAutoComplete();
        var promptFlyout = new MenuFlyout();
        promptFlyout.Opening += (_, _) => ConfigurePromptContextFlyout(promptFlyout, textBox, isStyleBox: false, allowQuickRandomStyle: false);
        textBox.ContextFlyout = promptFlyout;
        textGrid.Children.Add(textBox);
        textGrid.Children.Add(highlightCanvas);
        entry.PromptBox = textBox;
        entry.HighlightCanvas = highlightCanvas;

        tabPos.Click += (_, _) =>
        {
            if (entry.IsPositiveTab) { tabPos.IsChecked = true; return; }
            SaveCharacterPrompt(entry);
            entry.IsPositiveTab = true;
            tabPos.IsChecked = true; tabNeg.IsChecked = false;
            textBox.Text = entry.PositivePrompt;
            textBox.PlaceholderText = L("character.prompt_positive_placeholder");
        };
        tabNeg.Click += (_, _) =>
        {
            if (!entry.IsPositiveTab) { tabNeg.IsChecked = true; return; }
            SaveCharacterPrompt(entry);
            entry.IsPositiveTab = false;
            tabNeg.IsChecked = true; tabPos.IsChecked = false;
            textBox.Text = entry.NegativePrompt;
            textBox.PlaceholderText = L("character.prompt_negative_placeholder");
        };

        posBtn.Click += (_, _) => ShowCharacterPositionFlyout(posBtn, entry);

        disableBtn.Click += (_, _) =>
        {
            SaveCharacterPrompt(entry);
            entry.IsDisabled = !entry.IsDisabled;
            RefreshCharacterPanel();
        };

        int capturedIndex = index;
        delBtn.Click += (_, _) =>
        {
            SaveCharacterPrompt(entry);
            _genCharacters.Remove(entry);
            RefreshCharacterPanel();
        };

        collapseBtn.Click += (_, _) =>
        {
            SaveCharacterPrompt(entry);
            entry.IsCollapsed = !entry.IsCollapsed;
            RefreshCharacterPanel();
        };

        Visibility collapsedVisibility = entry.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        tabPos.Visibility = collapsedVisibility;
        tabNeg.Visibility = collapsedVisibility;
        movePanel.Visibility = collapsedVisibility;
        posBtn.Visibility = collapsedVisibility;
        disableBtn.Visibility = collapsedVisibility;
        delBtn.Visibility = collapsedVisibility;
        textGrid.Visibility = collapsedVisibility;

        container.Children.Add(headerGrid);
        if (!entry.IsCollapsed)
            container.Children.Add(textGrid);
        return container;
    }

    private Button CreateCharacterCollapseButton(bool isCollapsed)
    {
        var button = new Button
        {
            Width = 24,
            Height = 24,
            MinWidth = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(-2, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon
            {
                FontFamily = SymbolFontFamily,
                Glyph = isCollapsed ? "\uE76C" : "\uE70D",
                FontSize = 10,
            },
        };

        if (this.Content is FrameworkElement root &&
            root.Resources.TryGetValue("SubtleButtonStyle", out object? rootStyleObj) &&
            rootStyleObj is Style rootSubtleStyle)
        {
            button.Style = rootSubtleStyle;
        }
        else if (Application.Current.Resources.TryGetValue("SubtleButtonStyle", out object? styleObj) && styleObj is Style subtleStyle)
        {
            button.Style = subtleStyle;
        }

        // 保底：确保非悬停状态是平面视觉，不出现凸起。
        button.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
        button.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
        button.BorderThickness = new Thickness(0);

        ToolTipService.SetToolTip(button, isCollapsed ? L("character.expand") : L("character.collapse"));
        return button;
    }

    private Button CreateCharacterActionButton(string glyph, string toolTip, bool isEnabled, bool isDelete = false)
    {
        var button = new Button
        {
            Width = 22,
            Height = 22,
            MinWidth = 22,
            Padding = new Thickness(0),
            IsEnabled = isEnabled,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon
            {
                FontFamily = SymbolFontFamily,
                Glyph = glyph,
                FontSize = 10,
            },
        };
        if (this.Content is FrameworkElement root &&
            root.Resources.TryGetValue("SubtleButtonStyle", out object? rootStyleObj) &&
            rootStyleObj is Style rootSubtleStyle)
        {
            button.Style = rootSubtleStyle;
        }
        else if (Application.Current.Resources.TryGetValue("SubtleButtonStyle", out object? styleObj) && styleObj is Style subtleStyle)
        {
            button.Style = subtleStyle;
        }

        var transparent = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
        button.Background = transparent;
        button.BorderBrush = transparent;
        button.BorderThickness = new Thickness(0);

        button.Resources["ButtonBackgroundDisabled"] = transparent;
        button.Resources["ButtonBorderBrushDisabled"] = transparent;

        if (!isEnabled)
        {
            var dimColor = IsDarkTheme()
                ? Windows.UI.Color.FromArgb(255, 80, 80, 80)
                : Windows.UI.Color.FromArgb(255, 180, 180, 180);
            var dimBrush = new SolidColorBrush(dimColor);
            button.Foreground = dimBrush;
            button.Resources["ButtonForegroundDisabled"] = dimBrush;
        }
        else if (isDelete)
        {
            button.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28));
        }

        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, toolTip);
        return button;
    }

    private void ShowCharacterPositionFlyout(Button anchor, CharacterEntry entry)
    {
        const int padSize = 120;
        var flyout = new Flyout();

        var panel = new StackPanel { Spacing = 8, Width = padSize + 16 };

        var rootGrid = (Grid)this.Content;
        var titleText = new TextBlock
        {
            Text = L("character.position"),
            Style = (Style)rootGrid.Resources["InspectCaptionStyle"],
        };
        panel.Children.Add(titleText);

        var customPosToggle = new Microsoft.UI.Xaml.Controls.ToggleSwitch
        {
            Header = L("character.custom_position"),
            IsOn = entry.UseCustomPosition,
            FontSize = 12,
        };
        panel.Children.Add(customPosToggle);

        var padBorder = new Border
        {
            Width = padSize, Height = padSize,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
        };

        var padCanvas = new Canvas { Width = padSize, Height = padSize };
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 12, Height = 12,
            Fill = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.White),
            StrokeThickness = 2,
        };
        Canvas.SetLeft(dot, entry.CenterX * padSize - 6);
        Canvas.SetTop(dot, entry.CenterY * padSize - 6);
        padCanvas.Children.Add(dot);
        padBorder.Child = padCanvas;

        var coordText = new TextBlock
        {
            Text = $"X: {entry.CenterX:F2}    Y: {entry.CenterY:F2}",
            HorizontalAlignment = HorizontalAlignment.Center,
            Style = (Style)rootGrid.Resources["InspectSubLabelStyle"],
        };

        var resetBtn = new Button
        {
            Content = L("character.reset_to_center"), HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 11,
        };
        resetBtn.Click += (_, _) =>
        {
            entry.CenterX = 0.5; entry.CenterY = 0.5;
            Canvas.SetLeft(dot, padSize / 2.0 - 6);
            Canvas.SetTop(dot, padSize / 2.0 - 6);
            coordText.Text = "X: 0.50    Y: 0.50";
        };

        bool dragging = false;
        void UpdatePosition(double localX, double localY)
        {
            double nx = Math.Clamp(localX / padSize, 0, 1);
            double ny = Math.Clamp(localY / padSize, 0, 1);
            entry.CenterX = Math.Round(nx, 2);
            entry.CenterY = Math.Round(ny, 2);
            Canvas.SetLeft(dot, entry.CenterX * padSize - 6);
            Canvas.SetTop(dot, entry.CenterY * padSize - 6);
            coordText.Text = $"X: {entry.CenterX:F2}    Y: {entry.CenterY:F2}";
        }

        padCanvas.PointerPressed += (s, e) =>
        {
            dragging = true;
            (s as UIElement)?.CapturePointer(e.Pointer);
            var pt = e.GetCurrentPoint(padCanvas);
            UpdatePosition(pt.Position.X, pt.Position.Y);
        };
        padCanvas.PointerMoved += (s, e) =>
        {
            if (!dragging) return;
            var pt = e.GetCurrentPoint(padCanvas);
            UpdatePosition(pt.Position.X, pt.Position.Y);
        };
        padCanvas.PointerReleased += (s, e) =>
        {
            dragging = false;
            (s as UIElement)?.ReleasePointerCapture(e.Pointer);
        };

        var posControlsPanel = new StackPanel { Spacing = 8 };
        posControlsPanel.Children.Add(padBorder);
        posControlsPanel.Children.Add(coordText);
        posControlsPanel.Children.Add(resetBtn);
        posControlsPanel.Opacity = entry.UseCustomPosition ? 1.0 : 0.4;
        padCanvas.IsHitTestVisible = entry.UseCustomPosition;
        resetBtn.IsEnabled = entry.UseCustomPosition;

        customPosToggle.Toggled += (_, _) =>
        {
            entry.UseCustomPosition = customPosToggle.IsOn;
            posControlsPanel.Opacity = customPosToggle.IsOn ? 1.0 : 0.4;
            padCanvas.IsHitTestVisible = customPosToggle.IsOn;
            resetBtn.IsEnabled = customPosToggle.IsOn;
        };

        panel.Children.Add(posControlsPanel);

        flyout.Content = panel;
        flyout.ShowAt(anchor);
    }

    private void UpdateCharacterHighlight(CharacterEntry entry)
    {
        if (entry.HighlightCanvas == null || entry.PromptBox == null) return;
        entry.HighlightCanvas.Children.Clear();
        if (string.IsNullOrEmpty(entry.PromptBox.Text) || !_settings.Settings.WeightHighlight) return;
        entry.HighlightVersion++;
        int version = entry.HighlightVersion;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (version != entry.HighlightVersion) return;
            DrawHighlightsFor(entry.PromptBox, entry.HighlightCanvas);
        });
    }

    private static void SaveCharacterPrompt(CharacterEntry entry)
    {
        if (entry.PromptBox == null) return;
        if (entry.IsPositiveTab)
            entry.PositivePrompt = entry.PromptBox.Text;
        else
            entry.NegativePrompt = entry.PromptBox.Text;
    }

    private void SaveAllCharacterPrompts()
    {
        foreach (var entry in _genCharacters)
            SaveCharacterPrompt(entry);
    }

    private static readonly System.Text.RegularExpressions.Regex CharCountPrefixRegex =
        new(@"\b1(girl|boy|other)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StripCharCountPrefix(string prompt)
    {
        return CharCountPrefixRegex.Replace(prompt, "$1");
    }

    private void MoveCharacter(int index, int direction)
    {
        int newIndex = index + direction;
        if (newIndex < 0 || newIndex >= _genCharacters.Count) return;
        SaveAllCharacterPrompts();
        var entry = _genCharacters[index];
        _genCharacters.RemoveAt(index);
        _genCharacters.Insert(newIndex, entry);
        RefreshCharacterPanel();
    }

    private void ApplyCharCountPrefixStrip()
    {
        SaveAllCharacterPrompts();
        foreach (var entry in _genCharacters)
        {
            if (entry.IsDisabled) continue;
            entry.PositivePrompt = StripCharCountPrefix(entry.PositivePrompt);
            entry.NegativePrompt = StripCharCountPrefix(entry.NegativePrompt);
            if (entry.PromptBox != null)
            {
                entry.PromptBox.Text = entry.IsPositiveTab ? entry.PositivePrompt : entry.NegativePrompt;
            }
        }
    }

    private List<CharacterPromptInfo> GetCharacterData(WildcardExpandContext? wildcardContext = null)
    {
        SaveAllCharacterPrompts();
        var result = new List<CharacterPromptInfo>();
        foreach (var entry in _genCharacters)
        {
            if (entry.IsDisabled) continue;
            result.Add(new CharacterPromptInfo
            {
                PositivePrompt = wildcardContext == null
                    ? ExpandPromptShortcuts(entry.PositivePrompt)
                    : ExpandPromptFeatures(entry.PositivePrompt, wildcardContext),
                NegativePrompt = wildcardContext == null
                    ? ExpandPromptShortcuts(entry.NegativePrompt)
                    : ExpandPromptFeatures(entry.NegativePrompt, wildcardContext, isNegativeText: true),
                CenterX = entry.CenterX,
                CenterY = entry.CenterY,
                UseCustomPosition = entry.UseCustomPosition,
            });
        }
        return result;
    }

    private void SetGenCharactersFromMetadata(ImageMetadata meta)
    {
        _genCharacters.Clear();
        int count = Math.Min(meta.CharacterPrompts.Count, MaxCharacters);
        for (int i = 0; i < count; i++)
        {
            var entry = new CharacterEntry
            {
                PositivePrompt = meta.CharacterPrompts[i],
                NegativePrompt = i < meta.CharacterNegativePrompts.Count
                    ? meta.CharacterNegativePrompts[i] : "",
                CenterX = i < meta.CharacterCenters.Count ? meta.CharacterCenters[i].X : 0.5,
                CenterY = i < meta.CharacterCenters.Count ? meta.CharacterCenters[i].Y : 0.5,
            };
            _genCharacters.Add(entry);
        }
    }
}
