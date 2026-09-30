using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAITool.Controls;
using NAITool.Services;

namespace NAITool;

public sealed partial class MainWindow
{
    private void OnOpenSizePresets(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor) return;
        var flyout = new MenuFlyout();
        foreach (var group in CreateSizePresetGroups()) flyout.Items.Add(group);
        flyout.ShowAt(anchor);
    }

    private IEnumerable<MenuFlyoutItemBase> CreateSizePresetGroups()
    {
        foreach (var category in GenerationSizePresets.Defaults.GroupBy(p => p.Category))
        {
            var group = new MenuFlyoutSubItem { Text = L("size.presets." + category.Key) };
            foreach (var preset in category)
                group.Items.Add(CreateSizePresetItem(preset.Width, preset.Height,
                    $"{L("size.presets." + preset.Orientation)}  {preset.Width} × {preset.Height}"));
            yield return group;
        }

        var extra = new MenuFlyoutSubItem { Text = L("size.presets.extra") };
        foreach (var preset in MaskCanvasControl.CanvasPresets)
        {
            if (GenerationSizePresets.Defaults.Any(p => p.Width == preset.W && p.Height == preset.H))
                continue;
            extra.Items.Add(CreateSizePresetItem(preset.W, preset.H, preset.Label));
        }
        if (extra.Items.Count > 0) yield return extra;

        if (IsAssetProtectionSizeLimitEnabled())
        {
            yield return new MenuFlyoutSeparator();
            yield return new MenuFlyoutItem { Text = L("size.presets.protection_hint"), IsEnabled = false };
        }
    }

    private MenuFlyoutItem CreateSizePresetItem(int width, int height, string label)
    {
        bool selected = _customWidth == width && _customHeight == height;
        var item = new MenuFlyoutItem
        {
            Text = selected ? "✓ " + label : label,
            Tag = (width, height),
            IsEnabled = !IsAssetProtectionSizeLimitEnabled() || (long)width * height <= 1024L * 1024,
        };
        item.Click += OnPresetResolutionSelected;
        ApplyMenuTypography(item);
        return item;
    }
}
