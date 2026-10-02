using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAITool.Controls;
using NAITool.Services;

namespace NAITool;

public sealed partial class MainWindow
{
    private const string SizePresetProtectionHintTag = "size-preset-protection-hint";

    private void OnOpenSizePresets(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor) return;
        var flyout = new MenuFlyout();
        foreach (var group in CreateSizePresetGroups())
        {
            ApplyMenuTypography(group);
            flyout.Items.Add(group);
        }
        flyout.ShowAt(anchor);
    }

    private IEnumerable<MenuFlyoutItemBase> CreateSizePresetGroups()
    {
        foreach (var category in GenerationSizePresets.Defaults.GroupBy(p => p.Category))
        {
            var group = new MenuFlyoutSubItem { Text = L("size.presets." + category.Key) };
            foreach (var preset in category)
                group.Items.Add(CreateSizePresetItem(preset.Width, preset.Height));
            yield return group;
        }

        var extra = new MenuFlyoutSubItem { Text = L("size.presets.extra") };
        foreach (var preset in MaskCanvasControl.CanvasPresets)
        {
            if (!GenerationSizePresets.Defaults.Any(p => p.Width == preset.W && p.Height == preset.H))
                extra.Items.Add(CreateSizePresetItem(preset.W, preset.H));
        }
        if (extra.Items.Count > 0) yield return extra;

        var hintVisibility = IsAssetProtectionSizeLimitEnabled() ? Visibility.Visible : Visibility.Collapsed;
        yield return new MenuFlyoutSeparator { Tag = SizePresetProtectionHintTag, Visibility = hintVisibility };
        yield return new MenuFlyoutItem
        {
            Text = L("size.presets.protection_hint"), IsEnabled = false,
            Tag = SizePresetProtectionHintTag, Visibility = hintVisibility,
        };
    }

    private MenuFlyoutItem CreateSizePresetItem(int width, int height)
    {
        string glyph = width == height ? "\uF16B" : width > height ? "\uF5A1" : "\uF599";
        var item = new MenuFlyoutItem
        {
            Tag = (width, height),
            Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = glyph },
        };
        UpdateSizePresetItem(item, width, height);
        item.Click += OnPresetResolutionSelected;
        return item;
    }

    private void UpdateSizePresetItem(MenuFlyoutItem item, int width, int height)
    {
        item.Text = $"{width} × {height}";
        item.IsEnabled = !IsAssetProtectionSizeLimitEnabled() || (long)width * height <= 1024L * 1024;
    }

    private void RefreshSizePresetMenuStates()
    {
        if (MenuEdit == null) return;
        foreach (var submenu in MenuEdit.Items.OfType<MenuFlyoutSubItem>())
            if (HasMenuCommand(submenu, "preset_resolution"))
                RefreshSizePresetItems(submenu.Items);
    }

    private void RefreshSizePresetItems(IEnumerable<MenuFlyoutItemBase> items)
    {
        foreach (var item in items)
        {
            if (item.Tag is string tag && tag == SizePresetProtectionHintTag)
                item.Visibility = IsAssetProtectionSizeLimitEnabled() ? Visibility.Visible : Visibility.Collapsed;
            else if (item is MenuFlyoutSubItem submenu)
                RefreshSizePresetItems(submenu.Items);
            else if (item is MenuFlyoutItem preset && item.Tag is (int width, int height))
                UpdateSizePresetItem(preset, width, height);
        }
    }
}
