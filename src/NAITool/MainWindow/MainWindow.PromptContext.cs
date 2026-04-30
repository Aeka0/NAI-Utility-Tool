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
    // ═══════════════════════════════════════════════════════════
    //  随机风格提示词
    // ═══════════════════════════════════════════════════════════

    private void SetupPromptContextFlyouts()
    {
        var promptFlyout = new MenuFlyout();
        promptFlyout.Opening += (_, _) => ConfigurePromptContextFlyout(promptFlyout, TxtPrompt, isStyleBox: false, allowQuickRandomStyle: true);
        TxtPrompt.ContextFlyout = promptFlyout;

        var styleFlyout = new MenuFlyout();
        styleFlyout.Opening += (_, _) => ConfigurePromptContextFlyout(styleFlyout, TxtStylePrompt, isStyleBox: true, allowQuickRandomStyle: true);
        TxtStylePrompt.ContextFlyout = styleFlyout;
    }

    private void ConfigurePromptContextFlyout(MenuFlyout flyout, PromptTextBox textBox, bool isStyleBox, bool allowQuickRandomStyle)
    {
        flyout.Items.Clear();

        var undoItem = new MenuFlyoutItem { Text = L("prompt.context.undo"), IsEnabled = textBox.CanUndo, Icon = new SymbolIcon(Symbol.Undo) };
        undoItem.Click += (_, _) =>
        {
            textBox.Focus(FocusState.Programmatic);
            if (textBox.CanUndo) textBox.Undo();
        };
        flyout.Items.Add(undoItem);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var cutItem = new MenuFlyoutItem { Text = L("prompt.context.cut"), IsEnabled = textBox.SelectionLength > 0, Icon = new SymbolIcon(Symbol.Cut) };
        cutItem.Click += (_, _) =>
        {
            textBox.Focus(FocusState.Programmatic);
            textBox.CutSelectionToClipboard();
        };
        flyout.Items.Add(cutItem);

        var copyItem = new MenuFlyoutItem { Text = L("common.copy"), IsEnabled = textBox.SelectionLength > 0, Icon = new SymbolIcon(Symbol.Copy) };
        copyItem.Click += (_, _) =>
        {
            textBox.Focus(FocusState.Programmatic);
            textBox.CopySelectionToClipboard();
        };
        flyout.Items.Add(copyItem);

        var pasteItem = new MenuFlyoutItem { Text = L("prompt.context.paste"), Icon = new SymbolIcon(Symbol.Paste) };
        pasteItem.Click += (_, _) =>
        {
            textBox.Focus(FocusState.Programmatic);
            textBox.PasteFromClipboard();
        };
        flyout.Items.Add(pasteItem);

        var deleteItem = new MenuFlyoutItem { Text = L("button.delete"), IsEnabled = textBox.SelectionLength > 0, Icon = new SymbolIcon(Symbol.Delete) };
        deleteItem.Click += (_, _) =>
        {
            textBox.Focus(FocusState.Programmatic);
            textBox.SelectedText = "";
        };
        flyout.Items.Add(deleteItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var selectAllItem = new MenuFlyoutItem
        {
            Text = L("prompt.context.select_all"),
            IsEnabled = !string.IsNullOrEmpty(textBox.Text),
            Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8B3" },
        };
        selectAllItem.Click += (_, _) =>
        {
            textBox.Focus(FocusState.Programmatic);
            textBox.SelectAll();
        };
        flyout.Items.Add(selectAllItem);

        if (!isStyleBox)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            var actionInteractionSub = new MenuFlyoutSubItem
            {
                Text = L("prompt.context.interaction"),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE805" },
            };

            void AddActionInteractionItem(string label, string prefix, string glyph)
            {
                var item = new MenuFlyoutItem
                {
                    Text = label,
                    Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = glyph },
                };
                item.Click += (_, _) => InsertInteractionPrefixIntoPromptSelection(textBox, prefix);
                actionInteractionSub.Items.Add(item);
            }

            AddActionInteractionItem(L("prompt.context.interaction.source"), "source#", "\uE87B");
            AddActionInteractionItem(L("prompt.context.interaction.target"), "target#", "\uE879");
            AddActionInteractionItem(L("prompt.context.interaction.mutual"), "mutual#", "\uE7FD");
            flyout.Items.Add(actionInteractionSub);
        }

        bool shouldShow =
            allowQuickRandomStyle &&
            (_currentMode == AppMode.ImageGeneration || _currentMode == AppMode.I2I) &&
            _isPositiveTab;

        if (!shouldShow)
        {
            foreach (var item in flyout.Items)
                ApplyMenuTypography(item);
            return;
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        var quickItem = new MenuFlyoutItem
        {
            Text = L("random_style.quick_insert"),
            Icon = new SymbolIcon(Symbol.Shuffle),
        };
        quickItem.Click += OnQuickRandomStylePrompt;
        flyout.Items.Add(quickItem);
        foreach (var item in flyout.Items)
            ApplyMenuTypography(item);
    }

    private void InsertInteractionPrefixIntoPromptSelection(PromptTextBox textBox, string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return;

        int selectionStart = textBox.SelectionStart;
        int selectionLength = textBox.SelectionLength;
        string text = textBox.Text ?? string.Empty;

        textBox.Focus(FocusState.Programmatic);
        textBox.Text = text.Insert(selectionStart, prefix);
        textBox.Select(selectionStart + prefix.Length, selectionLength);

        if (ReferenceEquals(textBox, TxtPrompt) || ReferenceEquals(textBox, TxtStylePrompt))
        {
            SaveCurrentPromptToBuffer();
            UpdatePromptHighlights();
            UpdateStyleHighlights();
        }
        else
        {
            CharacterEntry? entry = CurrentCharacterEntries.FirstOrDefault(x => ReferenceEquals(x.PromptBox, textBox));
            if (entry != null)
            {
                SaveCharacterPrompt(entry);
                UpdateCharacterHighlight(entry);
            }
        }

        TxtStatus.Text = selectionLength > 0
            ? Lf("prompt.context.inserted_before_selection", prefix)
            : Lf("prompt.context.inserted_at_cursor", prefix);
    }

    private RandomStyleOptions GetRandomStyleOptions() => new(
        Math.Clamp(_settings.Settings.RandomStyleTagCount, 1, 10),
        Math.Max(0, _settings.Settings.RandomStyleMinCount),
        _settings.Settings.RandomStyleUseWeight);

    private void SaveRandomStyleOptions(RandomStyleOptions options)
    {
        _settings.Settings.RandomStyleTagCount = options.TagCount;
        _settings.Settings.RandomStyleMinCount = options.MinCount;
        _settings.Settings.RandomStyleUseWeight = options.UseWeight;
        _settings.Save();
    }

    private string? BuildRandomStylePrefixForRequest()
    {
        if (!TryBuildRandomStylePrompt(GetRandomStyleOptions(), out string result, out _))
            return null;
        return result;
    }

    private string? BuildRandomStylePrefixForRequest(RandomStyleOptions options)
    {
        if (!TryBuildRandomStylePrompt(options, out string result, out _))
            return null;
        return result;
    }

    private bool TryBuildRandomStylePrompt(RandomStyleOptions options, out string result, out int tagCount)
    {
        result = "";
        tagCount = 0;

        if (!_tagService.IsLoaded)
        {
            TxtStatus.Text = L("random_style.no_tagsheet");
            return false;
        }

        var tags = _tagService.GetRandomTags(options.TagCount, 1, options.MinCount);
        if (tags.Count == 0)
        {
            TxtStatus.Text = L("random_style.min_count_too_high");
            return false;
        }

        bool isSplit = _isSplitPrompt && _isPositiveTab;
        var rng = Random.Shared;
        var parts = new List<string>(tags.Count);
        foreach (var t in tags)
        {
            string tagText = t.Tag.Replace('_', ' ');
            if (options.UseWeight)
            {
                double w = Math.Round(0.5 + rng.NextDouble() * 1.5, 1);
                parts.Add($"{w:F1}::{tagText}::");
            }
            else
            {
                parts.Add(tagText);
            }
        }

        result = isSplit ? string.Join(", ", parts) : string.Concat(parts.Select(p => p + ", "));
        tagCount = tags.Count;
        return true;
    }

    private bool ApplyRandomStylePrompt(RandomStyleOptions options)
    {
        if (!TryBuildRandomStylePrompt(options, out string result, out int tagCount))
            return false;

        bool isSplit = _isSplitPrompt && _isPositiveTab;
        if (isSplit)
        {
            TxtStylePrompt.Text = result;
            TxtStylePrompt.SelectionStart = TxtStylePrompt.Text.Length;
            TxtStylePrompt.Focus(FocusState.Programmatic);
        }
        else
        {
            string existing = TxtPrompt.Text;
            TxtPrompt.Text = result + existing;
            TxtPrompt.SelectionStart = result.Length;
            TxtPrompt.Focus(FocusState.Programmatic);
        }

        TxtStatus.Text = Lf("random_style.inserted", tagCount);
        return true;
    }

    private async void OnRandomStylePrompt(object sender, RoutedEventArgs e)
    {
        if (!_tagService.IsLoaded)
        {
            var noTagDlg = new ContentDialog
            {
                Title = L("random_style.title"),
                Content = new TextBlock { Text = L("random_style.no_tagsheet") },
                CloseButtonText = L("common.ok"),
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
            };
            await noTagDlg.ShowAsync();
            return;
        }

        var lastOptions = GetRandomStyleOptions();
        var sliderCount = new Slider
        {
            Minimum = 1, Maximum = 10, Value = lastOptions.TagCount, StepFrequency = 1,
            Header = L("random_style.tag_count"),
        };
        var nbMinCount = new NumberBox
        {
            Header = L("random_style.min_booru_count"), Minimum = 0, Value = lastOptions.MinCount,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var chkRandomWeight = new CheckBox { Content = L("random_style.random_weight"), IsChecked = lastOptions.UseWeight };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(sliderCount);
        panel.Children.Add(nbMinCount);
        panel.Children.Add(chkRandomWeight);

        var dialog = new ContentDialog
        {
            Title = L("random_style.title"),
            Content = panel,
            PrimaryButtonText = L("button.generate_now"),
            CloseButtonText = L("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var options = new RandomStyleOptions(
            (int)sliderCount.Value,
            (int)nbMinCount.Value,
            chkRandomWeight.IsChecked == true);

        if (!ApplyRandomStylePrompt(options))
            return;

        SaveRandomStyleOptions(options);
    }

    private void OnQuickRandomStylePrompt(object sender, RoutedEventArgs e)
    {
        _ = sender;
        ApplyRandomStylePrompt(GetRandomStyleOptions());
    }

    private async void OnPromptShortcuts(object sender, RoutedEventArgs e)
    {
        _ = sender;

        var rowsHost = new StackPanel { Spacing = 0 };
        var rowEditors = new List<(TextBox Shortcut, TextBox Prompt, Border Row)>();

        Border CreateSheetRow(string shortcut = "", string prompt = "", bool isHeader = false)
        {
            var rowGrid = new Grid { ColumnSpacing = 8, Padding = new Thickness(10, 8, 10, 8) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

            if (isHeader)
            {
                var left = new TextBlock
                {
                    Text = L("prompt_shortcuts.shortcut"),
                    Style = (Style)((Grid)this.Content).Resources["InspectCaptionStyle"],
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var right = new TextBlock
                {
                    Text = L("prompt_shortcuts.full_prompt"),
                    Style = (Style)((Grid)this.Content).Resources["InspectCaptionStyle"],
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(right, 1);
                rowGrid.Children.Add(left);
                rowGrid.Children.Add(right);
            }
            else
            {
                var shortcutBox = new TextBox
                {
                    PlaceholderText = L("prompt_shortcuts.shortcut_placeholder"),
                    Text = shortcut,
                };
                var promptBox = new TextBox
                {
                    PlaceholderText = L("prompt_shortcuts.prompt_placeholder"),
                    Text = prompt,
                };
                Grid.SetColumn(promptBox, 1);

                var deleteBtn = new Button
                {
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Content = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE74D", FontSize = 12 },
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28)),
                };

                var rowBorder = new Border
                {
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = rowGrid,
                };
                deleteBtn.Click += (_, _) =>
                {
                    rowsHost.Children.Remove(rowBorder);
                    rowEditors.RemoveAll(x => ReferenceEquals(x.Row, rowBorder));
                };
                Grid.SetColumn(deleteBtn, 2);

                rowGrid.Children.Add(shortcutBox);
                rowGrid.Children.Add(promptBox);
                rowGrid.Children.Add(deleteBtn);
                rowEditors.Add((shortcutBox, promptBox, rowBorder));
                return rowBorder;
            }

            bool isDark = IsDarkTheme();
            var headerBg = isDark
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 40, 40, 40))
                : (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];
            return new Border
            {
                Background = headerBg,
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = rowGrid,
            };
        }

        void AddShortcutRow(string shortcut = "", string prompt = "") =>
            rowsHost.Children.Add(CreateSheetRow(shortcut, prompt));

        var tips = new TextBlock
        {
            Text = L("prompt_shortcuts.hint"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        };
        var addBtn = new Button
        {
            Content = L("prompt_shortcuts.add_row"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        addBtn.Click += (_, _) => AddShortcutRow();

        if (_promptShortcuts.Count == 0)
            AddShortcutRow();
        else
            foreach (var item in _promptShortcuts)
                AddShortcutRow(item.Shortcut, item.Prompt);

        var sheetStack = new StackPanel { Spacing = 0 };
        sheetStack.Children.Add(CreateSheetRow(isHeader: true));
        sheetStack.Children.Add(new ScrollViewer
        {
            Content = rowsHost,
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(tips);
        panel.Children.Add(new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = sheetStack,
        });
        panel.Children.Add(addBtn);

        var dialog = new ContentDialog
        {
            Title = L("prompt_shortcuts.title"),
            Content = panel,
            PrimaryButtonText = L("common.save"),
            CloseButtonText = L("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            var items = rowEditors.Select(x => new PromptShortcutEntry
            {
                Shortcut = x.Shortcut.Text.Trim(),
                Prompt = x.Prompt.Text.Trim(),
            }).ToList();
            SavePromptShortcuts(items);
            TxtStatus.Text = Lf("prompt_shortcuts.saved", _promptShortcuts.Count);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("prompt_shortcuts.save_failed", ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  尺寸选择
    // ═══════════════════════════════════════════════════════════

}
