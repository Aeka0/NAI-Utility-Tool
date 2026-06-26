using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using NAITool.Models;
using SkiaSharp;

namespace NAITool.Rendering;

public static class EffectsRenderingHelpers
{
    public static void GetEffectRegionValues(
        EffectEntry effect,
        out double centerX,
        out double centerY,
        out double widthPct,
        out double heightPct)
    {
        if (effect.Type == EffectType.Pixelate)
        {
            centerX = effect.Value2;
            centerY = effect.Value3;
            widthPct = effect.Value4;
            heightPct = effect.Value5;
        }
        else
        {
            centerX = effect.Value1;
            centerY = effect.Value2;
            widthPct = effect.Value3;
            heightPct = effect.Value4;
        }
    }

    public static void SetEffectRegionValues(
        EffectEntry effect,
        double centerX,
        double centerY,
        double widthPct,
        double heightPct)
    {
        if (effect.Type == EffectType.Pixelate)
        {
            effect.Value2 = centerX;
            effect.Value3 = centerY;
            effect.Value4 = widthPct;
            effect.Value5 = heightPct;
        }
        else
        {
            effect.Value1 = centerX;
            effect.Value2 = centerY;
            effect.Value3 = widthPct;
            effect.Value4 = heightPct;
        }
    }

    public static void GetEffectRect(
        int imageWidth,
        int imageHeight,
        double centerXPct,
        double centerYPct,
        double widthPct,
        double heightPct,
        out int left,
        out int top,
        out int right,
        out int bottom)
    {
        float cx = (float)(Math.Clamp(centerXPct, 0, 100) / 100.0 * imageWidth);
        float cy = (float)(Math.Clamp(centerYPct, 0, 100) / 100.0 * imageHeight);
        float halfW = (float)(Math.Clamp(widthPct, 1, 100) / 100.0 * imageWidth / 2.0);
        float halfH = (float)(Math.Clamp(heightPct, 1, 100) / 100.0 * imageHeight / 2.0);

        left = Math.Clamp((int)MathF.Round(cx - halfW), 0, imageWidth - 1);
        top = Math.Clamp((int)MathF.Round(cy - halfH), 0, imageHeight - 1);
        right = Math.Clamp((int)MathF.Round(cx + halfW), left + 1, imageWidth);
        bottom = Math.Clamp((int)MathF.Round(cy + halfH), top + 1, imageHeight);
    }

    public static SKColor? TryParseEffectsColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string value = text.Trim();
        if (value.StartsWith("#")) value = value[1..];

        try
        {
            if (value.Length == 6)
            {
                byte r = Convert.ToByte(value[..2], 16);
                byte g = Convert.ToByte(value.Substring(2, 2), 16);
                byte b = Convert.ToByte(value.Substring(4, 2), 16);
                return new SKColor(r, g, b, 255);
            }

            if (value.Length == 8)
            {
                byte a = Convert.ToByte(value[..2], 16);
                byte r = Convert.ToByte(value.Substring(2, 2), 16);
                byte g = Convert.ToByte(value.Substring(4, 2), 16);
                byte b = Convert.ToByte(value.Substring(6, 2), 16);
                return new SKColor(r, g, b, a);
            }
        }
        catch
        {
        }

        return null;
    }

    public static Windows.UI.Color ToUiColor(SKColor color) =>
        Windows.UI.Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue);

    public static SKColor SampleEffectsPixel(SKColor[] pixels, int width, int height, float x, float y)
    {
        int px = Math.Clamp((int)MathF.Round(x), 0, width - 1);
        int py = Math.Clamp((int)MathF.Round(y), 0, height - 1);
        return pixels[py * width + px];
    }

    public static float HashNoise(int x, int y, int salt)
    {
        unchecked
        {
            uint n = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(salt * 83492791);
            n ^= n >> 13;
            n *= 1274126177;
            n ^= n >> 16;
            return (n & 0x00FFFFFF) / 16777215f;
        }
    }

    public static byte ClampToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    public static string DescribeEffectChain(IReadOnlyList<EffectEntry> effects, int maxEffects = 10)
    {
        if (effects.Count == 0) return "(none)";

        var builder = new StringBuilder();
        int count = Math.Min(effects.Count, maxEffects);
        for (int i = 0; i < count; i++)
        {
            if (i > 0) builder.Append(" > ");
            builder.Append(i + 1).Append(':').Append(DescribeEffect(effects[i]));
        }

        if (effects.Count > count)
        {
            builder.Append(" > +").Append(effects.Count - count).Append(" more");
        }

        return builder.ToString();
    }

    public static string DescribeEffect(EffectEntry effect)
    {
        var builder = new StringBuilder();
        builder.Append(effect.Type)
            .Append("(v1=").Append(FormatEffectNumber(effect.Value1))
            .Append(",v2=").Append(FormatEffectNumber(effect.Value2))
            .Append(",v3=").Append(FormatEffectNumber(effect.Value3))
            .Append(",v4=").Append(FormatEffectNumber(effect.Value4))
            .Append(",v5=").Append(FormatEffectNumber(effect.Value5))
            .Append(",v6=").Append(FormatEffectNumber(effect.Value6));

        if (!string.IsNullOrWhiteSpace(effect.TextValue))
        {
            string text = effect.TextValue.Trim();
            if (text.Length > 32) text = text[..32] + "...";
            builder.Append(",text=").Append(text);
        }

        builder.Append(')');
        return builder.ToString();
    }

    public static string FormatElapsed(Stopwatch stopwatch) =>
        stopwatch.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + "ms";

    public static string FormatEffectNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
