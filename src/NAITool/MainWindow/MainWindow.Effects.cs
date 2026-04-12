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
    private enum EffectType
    {
        BrightnessContrast,
        SaturationVibrance,
        Temperature,
        Glow,
        RadialBlur,
        Vignette,
        ChromaticAberration,
        Noise,
        Gamma,
        Pixelate,
        SolidBlock,
        Scanline,
    }

    private sealed class EffectEntry
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public EffectType Type { get; init; }
        public double Value1 { get; set; }
        public double Value2 { get; set; }
        public double Value3 { get; set; }
        public double Value4 { get; set; }
        public double Value5 { get; set; }
        public double Value6 { get; set; }
        public string TextValue { get; set; } = "";
    }

    private sealed class EffectsWorkspaceState
    {
        public byte[]? ImageBytes { get; init; }
        public string? ImagePath { get; init; }
        public Guid? SelectedEffectId { get; init; }
        public List<EffectEntry> Effects { get; init; } = new();
    }

    private sealed class EffectsPresetFile
    {
        public string Name { get; set; } = "";
        public DateTime SavedAt { get; set; }
        public List<EffectEntry> Effects { get; set; } = new();
    }

    private static IconElement CreateEffectsIcon() =>
        new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEB3C" };

    private static bool IsRegionEffect(EffectType type) =>
        type == EffectType.Pixelate || type == EffectType.SolidBlock;

    private static bool IsInteractiveEffectCardSource(object? source)
    {
        DependencyObject? current = source as DependencyObject;
        while (current != null)
        {
            if (current is ComboBox or ComboBoxItem or Button or Slider or TextBox or ToggleSwitch or NumberBox)
                return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void MoveEffect(Guid effectId, int direction)
    {
        int index = _effects.FindIndex(x => x.Id == effectId);
        if (index < 0) return;

        int newIndex = Math.Clamp(index + direction, 0, _effects.Count - 1);
        if (newIndex == index) return;

        PushEffectsUndoState();
        var item = _effects[index];
        _effects.RemoveAt(index);
        _effects.Insert(newIndex, item);
        RefreshEffectsPanel();
        QueueEffectsPreviewRefresh(immediate: true);
        UpdateDynamicMenuStates();
        TxtStatus.Text = L("post.status.effects_reordered");
    }

    private EffectEntry? GetSelectedEffect()
    {
        if (_selectedEffectId == null) return null;
        return _effects.FirstOrDefault(x => x.Id == _selectedEffectId.Value);
    }

    private async void OnSendToInpaintFromEffects(object sender, RoutedEventArgs e)
    {
        try
        {
            var bytes = await GetEffectsSaveBytesAsync();
            if (bytes == null || bytes.Length == 0)
            {
                TxtStatus.Text = L("inpaint.error.no_image_to_send");
                return;
            }

            SendImageToInpaint(bytes);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("inpaint.send_failed", ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  效果工作区
    // ═══════════════════════════════════════════════════════════
}
