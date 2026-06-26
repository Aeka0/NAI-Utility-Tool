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
using NAITool.Rendering;
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
    private static void GetEffectRegionValues(
        EffectEntry effect,
        out double centerX,
        out double centerY,
        out double widthPct,
        out double heightPct) =>
        EffectsRenderingHelpers.GetEffectRegionValues(effect, out centerX, out centerY, out widthPct, out heightPct);

    private static void SetEffectRegionValues(
        EffectEntry effect,
        double centerX,
        double centerY,
        double widthPct,
        double heightPct) =>
        EffectsRenderingHelpers.SetEffectRegionValues(effect, centerX, centerY, widthPct, heightPct);

    private static void GetEffectRect(
        int imageWidth,
        int imageHeight,
        double centerXPct,
        double centerYPct,
        double widthPct,
        double heightPct,
        out int left,
        out int top,
        out int right,
        out int bottom) =>
        EffectsRenderingHelpers.GetEffectRect(
            imageWidth,
            imageHeight,
            centerXPct,
            centerYPct,
            widthPct,
            heightPct,
            out left,
            out top,
            out right,
            out bottom);

    private static SKColor? TryParseEffectsColor(string? text) =>
        EffectsRenderingHelpers.TryParseEffectsColor(text);

    private static Windows.UI.Color ToUiColor(SKColor color) =>
        EffectsRenderingHelpers.ToUiColor(color);

}
