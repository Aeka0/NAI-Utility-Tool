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
    private static EffectEntry CreateEffect(EffectType type) => type switch
    {
        EffectType.BrightnessContrast => new EffectEntry { Type = type, Value1 = 0, Value2 = 0 },
        EffectType.SaturationVibrance => new EffectEntry { Type = type, Value1 = 0, Value2 = 0 },
        EffectType.Temperature => new EffectEntry { Type = type, Value1 = 0, Value2 = 0 },
        EffectType.Glow => new EffectEntry { Type = type, Value1 = 24, Value2 = 55, Value3 = 70, Value4 = 1.0, Value5 = 0, Value6 = 0 },
        EffectType.RadialBlur => new EffectEntry { Type = type, Value1 = 18, Value2 = 50, Value3 = 50, Value4 = 0 },
        EffectType.Vignette => new EffectEntry { Type = type, Value1 = 35, Value2 = 55 },
        EffectType.ChromaticAberration => new EffectEntry { Type = type, Value1 = 3, Value2 = 0 },
        EffectType.Noise => new EffectEntry { Type = type, Value1 = 0, Value2 = 0 },
        EffectType.Gamma => new EffectEntry { Type = type, Value1 = 1.0, Value2 = 0 },
        EffectType.Pixelate => new EffectEntry { Type = type, Value1 = 8, Value2 = 50, Value3 = 50, Value4 = 30, Value5 = 30 },
        EffectType.SolidBlock => new EffectEntry { Type = type, Value1 = 50, Value2 = 50, Value3 = 30, Value4 = 30, TextValue = "#000000" },
        EffectType.Scanline => new EffectEntry { Type = type, Value1 = 2, Value2 = 4, Value3 = 30, Value4 = 0, Value5 = 50 },
        _ => new EffectEntry { Type = type },
    };

    private string GetEffectTitle(EffectType type) => type switch
    {
        EffectType.BrightnessContrast => L("post.effect.brightness_contrast"),
        EffectType.SaturationVibrance => L("post.effect.saturation_vibrance"),
        EffectType.Temperature => L("post.effect.temperature"),
        EffectType.Glow => L("post.effect.glow"),
        EffectType.RadialBlur => L("post.effect.radial_blur"),
        EffectType.Vignette => L("post.effect.vignette"),
        EffectType.ChromaticAberration => L("post.effect.chromatic_aberration"),
        EffectType.Noise => L("post.effect.noise"),
        EffectType.Gamma => "Gamma",
        EffectType.Pixelate => L("post.effect.pixelate"),
        EffectType.SolidBlock => L("post.effect.solid_block"),
        EffectType.Scanline => L("post.effect.scanline"),
        _ => L("post.effect.unknown"),
    };

    private static EffectEntry CloneEffect(EffectEntry x) => new()
    {
        Id = x.Id,
        Type = x.Type,
        Value1 = x.Value1,
        Value2 = x.Value2,
        Value3 = x.Value3,
        Value4 = x.Value4,
        Value5 = x.Value5,
        Value6 = x.Value6,
        TextValue = x.TextValue,
    };

    private static Brush GetThemeBrush(string key) =>
        (Brush)Application.Current.Resources[key];

    private ElementTheme GetResolvedTheme()
    {
        if (this.Content is not FrameworkElement root) return ElementTheme.Light;
        if (root.RequestedTheme == ElementTheme.Dark) return ElementTheme.Dark;
        if (root.RequestedTheme == ElementTheme.Light) return ElementTheme.Light;
        return root.ActualTheme == ElementTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
    }

    private bool IsDarkTheme() => GetResolvedTheme() == ElementTheme.Dark;

    private Brush CreateEffectsBrush(byte lightR, byte lightG, byte lightB, byte darkR, byte darkG, byte darkB, byte alpha = 255)
    {
        bool isDark = IsDarkTheme();
        return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha,
            isDark ? darkR : lightR,
            isDark ? darkG : lightG,
            isDark ? darkB : lightB));
    }

    private Brush GetEffectsCardBackgroundBrush() => CreateEffectsBrush(251, 251, 251, 37, 37, 38);
    private Brush GetEffectsCardBorderBrush() => CreateEffectsBrush(214, 214, 214, 75, 75, 78);
    private Brush GetEffectsPrimaryTextBrush() => CreateEffectsBrush(26, 26, 26, 245, 245, 245);
    private Brush GetEffectsSecondaryTextBrush() => CreateEffectsBrush(80, 80, 80, 210, 210, 210);
    private Brush GetEffectsTertiaryTextBrush() => CreateEffectsBrush(120, 120, 120, 160, 160, 160);
    private Brush GetEffectsButtonBackgroundBrush() => CreateEffectsBrush(245, 245, 245, 45, 45, 48);
    private Brush GetEffectsButtonBorderBrush() => CreateEffectsBrush(210, 210, 210, 85, 85, 88);
    private Brush GetEffectsTextBoxBackgroundBrush() => CreateEffectsBrush(255, 255, 255, 30, 30, 30);
    private Brush GetEffectsTextBoxBorderBrush() => CreateEffectsBrush(196, 196, 196, 90, 90, 90);

    private void ApplyEffectsButtonTheme(Button button)
    {
        button.RequestedTheme = GetResolvedTheme();
        button.Background = GetEffectsButtonBackgroundBrush();
        button.BorderBrush = GetEffectsButtonBorderBrush();
        button.Foreground = button.Foreground ?? GetEffectsPrimaryTextBrush();
        button.CornerRadius = new CornerRadius(4);
    }

    private void ApplyEffectsTextBoxTheme(TextBox textBox)
    {
        textBox.RequestedTheme = GetResolvedTheme();
        textBox.Background = GetEffectsTextBoxBackgroundBrush();
        textBox.BorderBrush = GetEffectsTextBoxBorderBrush();
        textBox.Foreground = GetEffectsPrimaryTextBrush();
    }

    private bool HasEffectsWorkspaceState() => _effectsImageBytes != null || _effects.Count > 0;

    private EffectsWorkspaceState CaptureEffectsWorkspaceState() => new()
    {
        ImageBytes = _effectsImageBytes?.ToArray(),
        ImagePath = _effectsImagePath,
        SelectedEffectId = _selectedEffectId,
        Effects = _effects.Select(CloneEffect).ToList(),
    };

    private void PushEffectsUndoState()
    {
        if (_effectsApplyingHistory || !HasEffectsWorkspaceState()) return;
        MarkEffectsWorkspaceDirty();
        _effectsUndoStack.Push(CaptureEffectsWorkspaceState());
        while (_effectsUndoStack.Count > 60)
        {
            var trimmed = _effectsUndoStack.Take(60).Reverse().ToArray();
            _effectsUndoStack.Clear();
            foreach (var state in trimmed) _effectsUndoStack.Push(state);
        }
        _effectsRedoStack.Clear();
        UpdateDynamicMenuStates();
    }

    private async Task RestoreEffectsWorkspaceStateAsync(EffectsWorkspaceState state)
    {
        _effectsApplyingHistory = true;
        try
        {
            _effectsImageBytes = state.ImageBytes?.ToArray();
            _effectsPreviewImageBytes = _effectsImageBytes;
            ReplaceEffectsSourceBitmap(_effectsImageBytes);
            _effectsImagePath = state.ImagePath;
            _selectedEffectId = state.SelectedEffectId;

            _effects.Clear();
            _effects.AddRange(state.Effects.Select(CloneEffect));

            RefreshEffectsPanel();
            if (_effectsImageBytes == null)
            {
                EffectsPreviewImage.Source = null;
                EffectsImagePlaceholder.Visibility = Visibility.Visible;
                RefreshEffectsOverlay();
                UpdateDynamicMenuStates();
                UpdateFileMenuState();
                return;
            }

            QueueEffectsPreviewRefresh(immediate: true);
            await Task.Yield();
            RefreshEffectsOverlay();
            UpdateDynamicMenuStates();
            UpdateFileMenuState();
        }
        finally
        {
            _effectsApplyingHistory = false;
        }
    }

    private Button CreateEffectsCardIconButton(string glyph, Brush iconBrush, bool isEnabled, string toolTip)
    {
        var icon = new FontIcon
        {
            FontFamily = SymbolFontFamily,
            Glyph = glyph,
            FontSize = 12,
            Foreground = iconBrush,
        };

        var button = new Button
        {
            Width = 28,
            Height = 28,
            MinWidth = 28,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsEnabled = isEnabled,
            Content = icon,
            Foreground = GetEffectsPrimaryTextBrush(),
        };
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, toolTip);
        return button;
    }

    private void RefreshEffectsPanel()
    {
        EffectsPanel.Children.Clear();

        for (int i = 0; i < _effects.Count; i++)
        {
            var effect = _effects[i];
            bool isSelectedEffect = effect.Id == _selectedEffectId;
            var card = new Border
            {
                RequestedTheme = GetResolvedTheme(),
                Background = GetEffectsCardBackgroundBrush(),
                BorderBrush = isSelectedEffect
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 215))
                    : GetEffectsCardBorderBrush(),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
            };
            card.Tapped += (_, args) =>
            {
                if (IsInteractiveEffectCardSource(args.OriginalSource)) return;
                _selectedEffectId = effect.Id;
                RefreshEffectsPanel();
                RefreshEffectsOverlay();
                TxtStatus.Text = IsRegionEffect(effect.Type)
                    ? L("post.status.region_selected")
                    : Lf("post.status.selected_effect", GetEffectTitle(effect.Type));
            };

            var stack = new StackPanel { Spacing = 10 };
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = $"{i + 1}. {GetEffectTitle(effect.Type)}",
                Style = (Style)((Grid)this.Content).Resources["InspectCaptionStyle"],
                Foreground = isSelectedEffect
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 215))
                    : GetEffectsPrimaryTextBrush(),
                VerticalAlignment = VerticalAlignment.Center,
            };
            header.Children.Add(title);

            var upBtn = CreateEffectsCardIconButton("\uE70E", GetEffectsSecondaryTextBrush(), i > 0, L("references.action.move_up"));
            ApplyEffectsButtonTheme(upBtn);
            upBtn.Margin = new Thickness(0, 0, 4, 0);
            upBtn.Click += (_, _) => MoveEffect(effect.Id, -1);
            Grid.SetColumn(upBtn, 1);
            header.Children.Add(upBtn);

            var downBtn = CreateEffectsCardIconButton("\uE70D", GetEffectsSecondaryTextBrush(), i < _effects.Count - 1, L("references.action.move_down"));
            ApplyEffectsButtonTheme(downBtn);
            downBtn.Margin = new Thickness(0, 0, 4, 0);
            downBtn.Click += (_, _) => MoveEffect(effect.Id, 1);
            Grid.SetColumn(downBtn, 2);
            header.Children.Add(downBtn);

            var deleteBtn = CreateEffectsCardIconButton("\uE74D",
                new SolidColorBrush(Windows.UI.Color.FromArgb(255, 232, 72, 86)),
                true, L("button.delete"));
            ApplyEffectsButtonTheme(deleteBtn);
            deleteBtn.Click += (_, _) =>
            {
                PushEffectsUndoState();
                _effects.RemoveAll(x => x.Id == effect.Id);
                if (_selectedEffectId == effect.Id) _selectedEffectId = null;
                RefreshEffectsPanel();
                QueueEffectsPreviewRefresh();
                UpdateDynamicMenuStates();
                UpdateFileMenuState();
                RefreshEffectsOverlay();
                TxtStatus.Text = L("post.status.removed_effect");
            };
            Grid.SetColumn(deleteBtn, 3);
            header.Children.Add(deleteBtn);

            stack.Children.Add(header);

            switch (effect.Type)
            {
                case EffectType.BrightnessContrast:
                    AddEffectSlider(stack, L("post.slider.brightness"), -100, 100, 1, effect.Value1, "F0", v => effect.Value1 = v);
                    AddEffectSlider(stack, L("post.slider.contrast"), -100, 100, 1, effect.Value2, "F0", v => effect.Value2 = v);
                    break;
                case EffectType.SaturationVibrance:
                    AddEffectSlider(stack, L("post.slider.saturation"), -100, 100, 1, effect.Value1, "F0", v => effect.Value1 = v);
                    AddEffectSlider(stack, L("post.slider.vibrance"), -100, 100, 1, effect.Value2, "F0", v => effect.Value2 = v);
                    break;
                case EffectType.Temperature:
                    AddEffectSlider(stack, L("post.slider.temperature"), -100, 100, 1, effect.Value1, "F0", v => effect.Value1 = v);
                    AddEffectSlider(stack, L("post.slider.tint"), -100, 100, 1, effect.Value2, "F0", v => effect.Value2 = v);
                    break;
                case EffectType.Glow:
                    AddEffectSlider(stack, L("post.slider.glow_size"), 1, 120, 1, effect.Value1, "F0", v => effect.Value1 = v);
                    AddEffectSlider(stack, L("post.slider.glow_threshold"), 0, 100, 1, effect.Value2, "F0", v => effect.Value2 = v);
                    AddEffectSlider(stack, L("post.slider.glow_intensity"), 0, 200, 1, effect.Value3, "F0", v => effect.Value3 = v);
                    AddEffectCenteredLogSlider(stack, L("post.slider.glow_aspect"), 0.05, 1.0, 8.0, effect.Value4, "F2", v => effect.Value4 = v);
                    AddEffectSlider(stack, L("post.slider.glow_tilt"), -90, 90, 1, effect.Value6, "F0", v => effect.Value6 = v);
                    AddEffectSlider(stack, L("post.slider.glow_saturation"), -100, 100, 1, effect.Value5, "F0", v => effect.Value5 = v);
                    break;
                case EffectType.RadialBlur:
                    AddEffectCombo(stack, L("post.slider.algorithm"),
                        new[] { L("post.slider.algorithm_radial"), L("post.slider.algorithm_spin"), L("post.slider.algorithm_progressive") },
                        (int)Math.Clamp(effect.Value4, 0, 2),
                        v => effect.Value4 = v);
                    AddEffectSlider(stack, L("post.slider.strength"), 0, 100, 1, effect.Value1, "F0", v => effect.Value1 = v);
                    AddEffectSlider(stack, L("post.slider.center_x"), 0, 100, 1, effect.Value2, "F0", v => effect.Value2 = v);
                    AddEffectSlider(stack, L("post.slider.center_y"), 0, 100, 1, effect.Value3, "F0", v => effect.Value3 = v);
                    break;
                case EffectType.Vignette:
                    AddEffectSlider(stack, L("post.slider.vignette_strength"), 0, 100, 1, effect.Value1, "F0", v => effect.Value1 = v);
                    AddEffectSlider(stack, L("post.slider.feather"), 0, 100, 1, effect.Value2, "F0", v => effect.Value2 = v);
                    break;
                case EffectType.ChromaticAberration:
                    AddEffectSlider(stack, L("post.slider.aberration_strength"), 0, 20, 0.1, effect.Value1, "F1", v => effect.Value1 = v);
                    break;
                case EffectType.Noise:
                    AddEffectSlider(stack, L("post.slider.mono_noise"), 0, 100, 1, effect.Value1, "F0", v => effect.Value1 = v);
                    AddEffectSlider(stack, L("post.slider.color_noise"), 0, 100, 1, effect.Value2, "F0", v => effect.Value2 = v);
                    break;
                case EffectType.Gamma:
                    AddEffectSlider(stack, "Gamma", 0.2, 3.0, 0.05, effect.Value1, "F2", v => effect.Value1 = v);
                    break;
                case EffectType.Pixelate:
                    AddEffectSlider(stack, L("post.slider.pixel_size"), 1, 64, 1, effect.Value1, "F0", v => effect.Value1 = v);
                    AddEffectRegionSliders(stack,
                        centerX: effect.Value2,
                        centerY: effect.Value3,
                        width: effect.Value4,
                        height: effect.Value5,
                        centerXSetter: v => effect.Value2 = v,
                        centerYSetter: v => effect.Value3 = v,
                        widthSetter: v => effect.Value4 = v,
                        heightSetter: v => effect.Value5 = v);
                    break;
                case EffectType.SolidBlock:
                    AddEffectColorTextBox(stack, L("post.slider.color_hex"), effect.TextValue, v => effect.TextValue = v);
                    AddEffectRegionSliders(stack,
                        centerX: effect.Value1,
                        centerY: effect.Value2,
                        width: effect.Value3,
                        height: effect.Value4,
                        centerXSetter: v => effect.Value1 = v,
                        centerYSetter: v => effect.Value2 = v,
                        widthSetter: v => effect.Value3 = v,
                        heightSetter: v => effect.Value4 = v);
                    break;
                case EffectType.Scanline:
                    AddEffectSlider(stack, L("post.slider.line_width"), 0.5, 10, 0.1, effect.Value1, "F1", v => effect.Value1 = v);
                    AddEffectSlider(stack, L("post.slider.spacing"), 0.5, 20, 0.1, effect.Value2, "F1", v => effect.Value2 = v);
                    AddEffectSlider(stack, L("post.slider.softness"), 0, 100, 1, effect.Value3, "F0", v => effect.Value3 = v);
                    AddEffectSlider(stack, L("post.slider.rotation"), -90, 90, 1, effect.Value4, "F0", v => effect.Value4 = v);
                    AddEffectSlider(stack, L("post.slider.opacity"), 0, 100, 1, effect.Value5, "F0", v => effect.Value5 = v);
                    break;
            }

            card.Child = stack;
            EffectsPanel.Children.Add(card);
            EffectsPanel.Children.Add(new Border
            {
                Height = 1,
                Background = GetEffectsCardBorderBrush(),
                Margin = new Thickness(0, 2, 0, 2),
            });
        }

        EffectsPanel.Children.Add(CreateAddEffectButton());
    }

    private void AddEffectSlider(
        Panel parent,
        string label,
        double min,
        double max,
        double step,
        double value,
        string format,
        Action<double> setValue)
    {
        var row = new StackPanel { Spacing = 4 };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = GetEffectsSecondaryTextBrush(),
        });

        var valueText = new TextBlock
        {
            Text = value.ToString(format),
            Foreground = GetEffectsTertiaryTextBrush(),
        };
        Grid.SetColumn(valueText, 1);
        header.Children.Add(valueText);

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            StepFrequency = step,
            Value = value,
            RequestedTheme = GetResolvedTheme(),
        };
        slider.PointerPressed += (_, _) => PushEffectsUndoState();
        slider.ValueChanged += (_, args) =>
        {
            setValue(args.NewValue);
            valueText.Text = args.NewValue.ToString(format);
            QueueEffectsPreviewRefresh();
            UpdateDynamicMenuStates();
            UpdateFileMenuState();
        };
        slider.PointerCaptureLost += (_, _) => QueueEffectsPreviewRefresh(immediate: true);
        slider.PointerReleased += (_, _) => QueueEffectsPreviewRefresh(immediate: true);

        row.Children.Add(header);
        row.Children.Add(slider);
        parent.Children.Add(row);
    }

    private void AddEffectCenteredLogSlider(
        Panel parent,
        string label,
        double min,
        double center,
        double max,
        double value,
        string format,
        Action<double> setValue)
    {
        var row = new StackPanel { Spacing = 4 };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = GetEffectsSecondaryTextBrush(),
        });

        var valueText = new TextBlock
        {
            Text = value.ToString(format),
            Foreground = GetEffectsTertiaryTextBrush(),
        };
        Grid.SetColumn(valueText, 1);
        header.Children.Add(valueText);

        double normalizedValue = value <= center
            ? 0.5 * Math.Log(value / min) / Math.Log(center / min)
            : 0.5 + 0.5 * Math.Log(value / center) / Math.Log(max / center);
        normalizedValue = Math.Clamp(normalizedValue, 0, 1);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            StepFrequency = 0.001,
            Value = normalizedValue,
            RequestedTheme = GetResolvedTheme(),
        };
        slider.PointerPressed += (_, _) => PushEffectsUndoState();
        slider.ValueChanged += (_, args) =>
        {
            double t = Math.Clamp(args.NewValue, 0, 1);
            double mapped = t <= 0.5
                ? min * Math.Pow(center / min, t / 0.5)
                : center * Math.Pow(max / center, (t - 0.5) / 0.5);
            mapped = Math.Clamp(mapped, min, max);
            setValue(mapped);
            valueText.Text = mapped.ToString(format);
            QueueEffectsPreviewRefresh();
            UpdateDynamicMenuStates();
            UpdateFileMenuState();
        };
        slider.PointerCaptureLost += (_, _) => QueueEffectsPreviewRefresh(immediate: true);
        slider.PointerReleased += (_, _) => QueueEffectsPreviewRefresh(immediate: true);

        row.Children.Add(header);
        row.Children.Add(slider);
        parent.Children.Add(row);
    }

    private void AddEffectCombo(Panel parent, string label, IReadOnlyList<string> options, int selectedIndex, Action<int> setValue)
    {
        var row = new StackPanel { Spacing = 4 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = GetEffectsSecondaryTextBrush(),
        });

        var combo = new ComboBox
        {
            RequestedTheme = GetResolvedTheme(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 32,
            FontFamily = UiTextFontFamily,
        };
        foreach (var option in options)
            combo.Items.Add(CreateTextComboBoxItem(option));
        combo.SelectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, options.Count - 1));
        combo.SelectionChanged += (_, _) =>
        {
            setValue(Math.Max(0, combo.SelectedIndex));
            QueueEffectsPreviewRefresh();
            UpdateFileMenuState();
        };
        row.Children.Add(combo);
        parent.Children.Add(row);
    }

    private void AddEffectRegionSliders(
        Panel parent,
        double centerX,
        double centerY,
        double width,
        double height,
        Action<double> centerXSetter,
        Action<double> centerYSetter,
        Action<double> widthSetter,
        Action<double> heightSetter)
    {
        AddEffectSlider(parent, L("post.slider.center_x"), 0, 100, 1, centerX, "F0", centerXSetter);
        AddEffectSlider(parent, L("post.slider.center_y"), 0, 100, 1, centerY, "F0", centerYSetter);
        AddEffectSlider(parent, L("post.slider.region_width"), 1, 100, 1, width, "F0", widthSetter);
        AddEffectSlider(parent, L("post.slider.region_height"), 1, 100, 1, height, "F0", heightSetter);
    }

    private void AddEffectColorTextBox(Panel parent, string label, string value, Action<string> setValue)
    {
        var row = new StackPanel { Spacing = 4 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = GetEffectsSecondaryTextBrush(),
        });

        var colorRow = new Grid();
        colorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        colorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        colorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var previewBorder = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = GetEffectsTextBoxBorderBrush(),
            Background = new SolidColorBrush(ToUiColor(TryParseEffectsColor(value) ?? new SKColor(0, 0, 0, 255))),
            Margin = new Thickness(0, 0, 8, 0),
        };
        colorRow.Children.Add(previewBorder);

        var textBox = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(value) ? "#000000" : value,
            PlaceholderText = "#000000",
        };
        ApplyEffectsTextBoxTheme(textBox);
        textBox.GotFocus += (_, _) => PushEffectsUndoState();
        textBox.TextChanged += (_, _) =>
        {
            string newValue = textBox.Text.Trim();
            setValue(newValue);
            var parsed = TryParseEffectsColor(newValue) ?? new SKColor(0, 0, 0, 255);
            previewBorder.Background = new SolidColorBrush(ToUiColor(parsed));
            QueueEffectsPreviewRefresh();
            UpdateFileMenuState();
        };
        Grid.SetColumn(textBox, 1);
        colorRow.Children.Add(textBox);

        var picker = new Microsoft.UI.Xaml.Controls.ColorPicker
        {
            IsAlphaEnabled = true,
            Color = ToUiColor(TryParseEffectsColor(value) ?? new SKColor(0, 0, 0, 255)),
            RequestedTheme = GetResolvedTheme(),
        };
        picker.ColorChanged += (_, args) =>
        {
            string hex = args.NewColor.A == 255
                ? $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}"
                : $"#{args.NewColor.A:X2}{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
            textBox.Text = hex;
            previewBorder.Background = new SolidColorBrush(args.NewColor);
            setValue(hex);
            QueueEffectsPreviewRefresh();
            UpdateFileMenuState();
        };

        var flyout = new Flyout { Content = picker };
        var pickerBtn = new Button
        {
            Content = L("post.button.color_picker"),
            Margin = new Thickness(8, 0, 0, 0),
        };
        ApplyEffectsButtonTheme(pickerBtn);
        pickerBtn.Click += (_, _) => PushEffectsUndoState();
        pickerBtn.Click += (_, _) => flyout.ShowAt(pickerBtn);
        Grid.SetColumn(pickerBtn, 2);
        colorRow.Children.Add(pickerBtn);

        row.Children.Add(colorRow);
        parent.Children.Add(row);
    }

    private Button CreateAddEffectButton()
    {
        var btn = new Button
        {
            Content = L("post.button.add_effect"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = _effects.Count < 10,
        };
        ApplyEffectsButtonTheme(btn);

        var flyout = new MenuFlyout();
        foreach (var type in Enum.GetValues<EffectType>())
        {
            var item = new MenuFlyoutItem
            {
                Text = GetEffectTitle(type),
                IsEnabled = _effects.Count < 10,
            };
            item.Click += (_, _) =>
            {
                if (_effects.Count >= 10)
                {
                    TxtStatus.Text = L("post.status.max_effects");
                    return;
                }

                PushEffectsUndoState();
                var entry = CreateEffect(type);
                _effects.Add(entry);
                if (IsRegionEffect(type))
                    _selectedEffectId = entry.Id;
                RefreshEffectsPanel();
                QueueEffectsPreviewRefresh();
                UpdateDynamicMenuStates();
                UpdateFileMenuState();
                RefreshEffectsOverlay();
                TxtStatus.Text = Lf("post.status.added_effect", GetEffectTitle(type));
            };
            flyout.Items.Add(item);
        }

        foreach (var item in flyout.Items)
            ApplyMenuTypography(item);
        btn.ContextFlyout = flyout;
        btn.Click += (_, _) => flyout.ShowAt(btn);
        return btn;
    }
}
