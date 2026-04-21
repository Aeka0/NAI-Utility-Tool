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
    private static byte[] RenderEffects(byte[] sourceBytes, List<EffectEntry> effects)
    {
        using var bitmap = RenderEffectsPreview(null, sourceBytes, effects);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray() ?? sourceBytes;
    }

    private static SKBitmap RenderEffectsPreview(
        SKBitmap? cachedSourceBitmap,
        byte[] sourceBytes,
        List<EffectEntry> effects)
    {
        SKBitmap? baseBitmap = cachedSourceBitmap?.Copy() ?? SKBitmap.Decode(sourceBytes);
        if (baseBitmap == null)
            throw new InvalidOperationException(LocalizationService.Instance.GetString("post.error.decode_source_failed"));

        foreach (var effect in effects)
        {
            switch (effect.Type)
            {
                case EffectType.BrightnessContrast:
                    ApplyBrightnessContrast(baseBitmap, effect.Value1, effect.Value2);
                    break;
                case EffectType.SaturationVibrance:
                    ApplySaturationVibrance(baseBitmap, effect.Value1, effect.Value2);
                    break;
                case EffectType.Temperature:
                    ApplyTemperature(baseBitmap, effect.Value1, effect.Value2);
                    break;
                case EffectType.Glow:
                    ApplyGlow(baseBitmap, effect.Value1, effect.Value2, effect.Value3, effect.Value4, effect.Value6, effect.Value5);
                    break;
                case EffectType.RadialBlur:
                    ApplyRadialBlur(baseBitmap, effect.Value1, effect.Value2, effect.Value3, (int)Math.Round(effect.Value4));
                    break;
                case EffectType.Vignette:
                    ApplyVignette(baseBitmap, effect.Value1, effect.Value2);
                    break;
                case EffectType.ChromaticAberration:
                    ApplyChromaticAberration(baseBitmap, effect.Value1);
                    break;
                case EffectType.Noise:
                    ApplyNoise(baseBitmap, effect.Value1, effect.Value2);
                    break;
                case EffectType.Gamma:
                    ApplyGamma(baseBitmap, effect.Value1);
                    break;
                case EffectType.Pixelate:
                    ApplyPixelateRegion(baseBitmap, effect.Value1, effect.Value2, effect.Value3, effect.Value4, effect.Value5);
                    break;
                case EffectType.SolidBlock:
                    ApplySolidBlock(baseBitmap, effect.TextValue, effect.Value1, effect.Value2, effect.Value3, effect.Value4);
                    break;
                case EffectType.Scanline:
                    ApplyScanline(baseBitmap, effect.Value1, effect.Value2, effect.Value3, effect.Value4, effect.Value5);
                    break;
            }
        }

        return baseBitmap;
    }

    private static void ApplyBrightnessContrast(SKBitmap bitmap, double brightness, double contrast)
    {
        float b = (float)(brightness / 100.0 * 255.0);
        float c = (float)(1.0 + contrast / 100.0);
        var pixels = bitmap.Pixels;
        Parallel.For(0, pixels.Length, i =>
        {
            var px = pixels[i];
            byte r = ClampToByte((px.Red - 128f) * c + 128f + b);
            byte g = ClampToByte((px.Green - 128f) * c + 128f + b);
            byte bl = ClampToByte((px.Blue - 128f) * c + 128f + b);
            pixels[i] = new SKColor(r, g, bl, px.Alpha);
        });
        bitmap.Pixels = pixels;
    }

    private static void ApplySaturationVibrance(SKBitmap bitmap, double saturation, double vibrance)
    {
        float sat = (float)(1.0 + saturation / 100.0);
        float vib = (float)(vibrance / 100.0);
        var pixels = bitmap.Pixels;
        Parallel.For(0, pixels.Length, i =>
        {
            var px = pixels[i];
            float r = px.Red;
            float g = px.Green;
            float b = px.Blue;

            float gray = 0.299f * r + 0.587f * g + 0.114f * b;
            r = gray + (r - gray) * sat;
            g = gray + (g - gray) * sat;
            b = gray + (b - gray) * sat;

            float max = Math.Max(r, Math.Max(g, b));
            float avg = (r + g + b) / 3f;
            float amt = vib * (1f - Math.Abs(max - avg) / 255f);
            r += (r - avg) * amt;
            g += (g - avg) * amt;
            b += (b - avg) * amt;

            pixels[i] = new SKColor(ClampToByte(r), ClampToByte(g), ClampToByte(b), px.Alpha);
        });
        bitmap.Pixels = pixels;
    }

    private static void ApplyTemperature(SKBitmap bitmap, double temperature, double tint)
    {
        float delta = (float)(temperature / 100.0 * 45.0);
        float tintDelta = (float)(tint / 100.0 * 35.0);
        var pixels = bitmap.Pixels;
        Parallel.For(0, pixels.Length, i =>
        {
            var px = pixels[i];
            float r = px.Red + delta + tintDelta * 0.55f;
            float g = px.Green + delta * 0.15f - tintDelta;
            float b = px.Blue - delta + tintDelta * 0.55f;
            pixels[i] = new SKColor(ClampToByte(r), ClampToByte(g), ClampToByte(b), px.Alpha);
        });
        bitmap.Pixels = pixels;
    }

    private static void ApplyGlow(
        SKBitmap bitmap,
        double sizeValue,
        double thresholdValue,
        double strengthValue,
        double aspectRatioValue,
        double tiltValue,
        double saturationValue)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        if (width <= 1 || height <= 1) return;

        float glowSize = (float)Math.Clamp(sizeValue, 1, 500);
        float threshold = (float)Math.Clamp(thresholdValue, 0, 100) / 100f * 255f;
        float strength = (float)Math.Clamp(strengthValue, 0, 200) / 100f;
        float aspectRatio = (float)Math.Clamp(aspectRatioValue, 0.05, 8.0);
        float tiltDegrees = (float)Math.Clamp(tiltValue, -90, 90);
        float saturationNorm = (float)Math.Clamp(saturationValue, -100, 100) / 100f;
        float saturation = saturationNorm >= 0f
            ? MathF.Pow(4f, saturationNorm)
            : MathF.Pow(0.25f, -saturationNorm);
        
        // 更紧凑的软阈值 (Tighter soft-knee)，防止泛光过度溢出到暗部/中间调
        float knee = MathF.Max(1f, threshold * 0.15f);

        float ratioPow = MathF.Pow(aspectRatio, 1.25f);
        float sigmaX = MathF.Max(0.1f, glowSize * ratioPow / 3.0f);
        float sigmaY = MathF.Max(0.1f, glowSize / MathF.Max(0.05f, ratioPow) / 3.0f);

        var src = bitmap.Pixels;
        var brightPixels = new SKColor[src.Length];

        Parallel.For(0, src.Length, i =>
        {
            var px = src[i];
            
            // 修复“泛光偏白”：使用 Max(R,G,B) 而不是 Luminance。
            // 亮度(Luminance)会极大地压低高饱和度颜色（如纯蓝、纯红）的权重，导致只有白色能发光。
            // 使用 Max 可以让高饱和度的亮色与白色同等发光，从而保留泛光的色彩。
            float maxColor = Math.Max(px.Red, Math.Max(px.Green, px.Blue));
            
            float soft = maxColor - threshold + knee;
            soft = Math.Clamp(soft, 0f, 2f * knee);
            soft = soft * soft / (4f * knee + 0.0001f);
            
            float contribution = Math.Max(soft, maxColor - threshold);
            float factor = maxColor > 0.0001f ? contribution / maxColor : 0f;

            // 修复“边缘灼烧/硬截断”：强制 Alpha 为 255。
            // 之前低于阈值的像素 Alpha 为 0，导致高斯模糊时 Alpha 通道产生锐利边缘，进而引发色彩断层。
            brightPixels[i] = new SKColor(
                ClampToByte(px.Red * factor),
                ClampToByte(px.Green * factor),
                ClampToByte(px.Blue * factor),
                255);
        });

        using var bright = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        bright.Pixels = brightPixels;
        using var blurred = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);

        if (Math.Abs(tiltDegrees) < 0.01f)
        {
            using var canvas = new SKCanvas(blurred);
            using var paint = new SKPaint
            {
                IsAntialias = false,
                ImageFilter = SKImageFilter.CreateBlur(sigmaX, sigmaY),
            };
            using var brightImage = SKImage.FromBitmap(bright);
            canvas.Clear(SKColors.Black); // 必须用纯黑不透明底色
            canvas.DrawImage(brightImage, 0, 0, sampling, paint);
            canvas.Flush();
        }
        else
        {
            int glowPadding = (int)Math.Ceiling(Math.Max(sigmaX, sigmaY) * 3f) + 2;
            int rotatedSize = (int)Math.Ceiling(Math.Sqrt(width * width + height * height) + glowPadding * 2 + 2);
            int sourceX = (rotatedSize - width) / 2;
            int sourceY = (rotatedSize - height) / 2;
            float center = rotatedSize / 2f;

            using var rotatedInput = new SKBitmap(rotatedSize, rotatedSize, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var rotatedInputCanvas = new SKCanvas(rotatedInput))
            {
                rotatedInputCanvas.Clear(SKColors.Black);
                rotatedInputCanvas.Translate(center, center);
                rotatedInputCanvas.RotateDegrees(tiltDegrees);
                rotatedInputCanvas.Translate(-center, -center);
                rotatedInputCanvas.DrawBitmap(bright, sourceX, sourceY);
                rotatedInputCanvas.Flush();
            }

            using var rotatedBlurred = new SKBitmap(rotatedSize, rotatedSize, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var rotatedBlurCanvas = new SKCanvas(rotatedBlurred))
            using (var paint = new SKPaint
            {
                IsAntialias = false,
                ImageFilter = SKImageFilter.CreateBlur(sigmaX, sigmaY),
            })
            {
                using var rotatedInputImage = SKImage.FromBitmap(rotatedInput);
                rotatedBlurCanvas.Clear(SKColors.Black);
                rotatedBlurCanvas.DrawImage(rotatedInputImage, 0, 0, sampling, paint);
                rotatedBlurCanvas.Flush();
            }

            using var untilted = new SKBitmap(rotatedSize, rotatedSize, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var untiltedCanvas = new SKCanvas(untilted))
            {
                untiltedCanvas.Clear(SKColors.Black);
                untiltedCanvas.Translate(center, center);
                untiltedCanvas.RotateDegrees(-tiltDegrees);
                untiltedCanvas.Translate(-center, -center);
                untiltedCanvas.DrawBitmap(rotatedBlurred, 0, 0);
                untiltedCanvas.Flush();
            }

            using var canvas = new SKCanvas(blurred);
            canvas.Clear(SKColors.Black);
            canvas.DrawBitmap(
                untilted,
                new SKRect(sourceX, sourceY, sourceX + width, sourceY + height),
                new SKRect(0, 0, width, height));
            canvas.Flush();
        }

        var glowPixels = blurred.Pixels;
        var outPixels = new SKColor[src.Length];

        Parallel.For(0, outPixels.Length, i =>
        {
            var basePx = src[i];
            var glowPx = glowPixels[i];

            float gr = glowPx.Red;
            float gg = glowPx.Green;
            float gb = glowPx.Blue;

            // 对泛光本身做更强的色度增强，并保持峰值亮度，避免“饱和度拉高但仍偏白”。
            float peakBefore = MathF.Max(gr, MathF.Max(gg, gb));
            float gGray = 0.299f * gr + 0.587f * gg + 0.114f * gb;
            gr = gGray + (gr - gGray) * saturation;
            gg = gGray + (gg - gGray) * saturation;
            gb = gGray + (gb - gGray) * saturation;
            float peakAfter = MathF.Max(gr, MathF.Max(gg, gb));
            if (peakBefore > 0.001f && peakAfter > 0.001f)
            {
                float preservePeak = peakBefore / peakAfter;
                gr *= preservePeak;
                gg *= preservePeak;
                gb *= preservePeak;
            }

            // 恢复为 Linear Additive (线性叠加) 混合模式。
            // 之前的 Screen 模式会压制亮部背景上的泛光，导致泛光显得无力且偏白。
            float glowR = Math.Max(0f, gr * strength);
            float glowG = Math.Max(0f, gg * strength);
            float glowB = Math.Max(0f, gb * strength);

            float r = basePx.Red + glowR;
            float g = basePx.Green + glowG;
            float b = basePx.Blue + glowB;

            outPixels[i] = new SKColor(ClampToByte(r), ClampToByte(g), ClampToByte(b), basePx.Alpha);
        });

        bitmap.Pixels = outPixels;
    }

    private static void ApplyRadialBlur(SKBitmap bitmap, double strengthValue, double centerXPct, double centerYPct, int mode)
    {
        float strength = (float)Math.Clamp(strengthValue, 0, 100);
        if (strength <= 0.01f) return;

        int width = bitmap.Width;
        int height = bitmap.Height;
        var source = bitmap.Pixels;
        var result = new SKColor[source.Length];

        float cx = (float)(Math.Clamp(centerXPct, 0, 100) / 100.0 * (width - 1));
        float cy = (float)(Math.Clamp(centerYPct, 0, 100) / 100.0 * (height - 1));
        int sampleCount = 4 + GetRadialBlurSampleCount(strength, mode) * 2;
        float zoomRadius = 0.0025f + strength / 100f * 0.075f;
        float spinAngle = strength / 100f * 0.22f;
        float maxDist = MathF.Sqrt(MathF.Max(cx, width - 1 - cx) * MathF.Max(cx, width - 1 - cx) +
                                   MathF.Max(cy, height - 1 - cy) * MathF.Max(cy, height - 1 - cy));
        maxDist = MathF.Max(maxDist, 1f);

        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                float dx = x - cx;
                float dy = y - cy;
                float accumR = 0, accumG = 0, accumB = 0, accumA = 0, weightSum = 0;

                for (int i = 0; i < sampleCount; i++)
                {
                    float t = sampleCount == 1 ? 0f : (i / (float)(sampleCount - 1) - 0.5f) * 2f;
                    float sampleX;
                    float sampleY;
                    float weight;

                    switch (mode)
                    {
                        case 1: // 旋转
                            float angle = t * spinAngle;
                            float cos = MathF.Cos(angle);
                            float sin = MathF.Sin(angle);
                            sampleX = cx + dx * cos - dy * sin;
                            sampleY = cy + dx * sin + dy * cos;
                            weight = 1f - MathF.Abs(t) * 0.5f;
                            break;
                        case 2: // 高斯
                            float gaussianScale = t * zoomRadius;
                            sampleX = x - dx * gaussianScale;
                            sampleY = y - dy * gaussianScale;
                            weight = MathF.Exp(-(t * t) * 4f);
                            break;
                        default: // 放射
                            float scale = t * zoomRadius;
                            sampleX = x - dx * scale;
                            sampleY = y - dy * scale;
                            weight = 1f;
                            break;
                    }

                    SKColor sample;
                    if (mode == 2) // 渐进：离中心越远，越进行各向同性模糊
                    {
                        float distNorm = MathF.Sqrt(dx * dx + dy * dy) / maxDist;
                        float localRadius = distNorm * (0.5f + strength / 100f * 14f);
                        if (localRadius < 0.75f)
                        {
                            sample = SampleEffectsPixel(source, width, height, x, y);
                        }
                        else
                        {
                            float angleStep = MathF.Tau / sampleCount;
                            float ang = i * angleStep;
                            sampleX = x + MathF.Cos(ang) * localRadius;
                            sampleY = y + MathF.Sin(ang) * localRadius;
                            sample = SampleEffectsPixel(source, width, height, sampleX, sampleY);
                        }
                        weight = 1f;
                    }
                    else
                    {
                        sample = SampleEffectsPixel(source, width, height, sampleX, sampleY);
                    }
                    accumR += sample.Red * weight;
                    accumG += sample.Green * weight;
                    accumB += sample.Blue * weight;
                    accumA += sample.Alpha * weight;
                    weightSum += weight;
                }

                if (weightSum <= 0.0001f)
                {
                    result[y * width + x] = source[y * width + x];
                    continue;
                }

                result[y * width + x] = new SKColor(
                    ClampToByte(accumR / weightSum),
                    ClampToByte(accumG / weightSum),
                    ClampToByte(accumB / weightSum),
                    ClampToByte(accumA / weightSum));
            }
        });

        bitmap.Pixels = result;
    }

    private static int GetRadialBlurSampleCount(float strength, int mode)
    {
        int baseCount = mode switch
        {
            1 => 16, // 旋转更依赖采样
            2 => 14, // 渐进模糊
            _ => 3, // 放射默认更轻
        };
        int scaled = mode switch
        {
            0 => baseCount + (int)MathF.Round(strength / 100f * 12f),
            _ => baseCount + (int)MathF.Round(strength / 100f * 24f),
        };
        return Math.Clamp(scaled, baseCount, 40);
    }

    private static void ApplyVignette(SKBitmap bitmap, double strengthValue, double featherValue)
    {
        float strength = (float)(strengthValue / 100.0);
        float softness = 0.15f + (float)(featherValue / 100.0) * 0.75f;
        float start = Math.Clamp(1f - softness, 0.05f, 0.95f);
        float cx = (bitmap.Width - 1) / 2f;
        float cy = (bitmap.Height - 1) / 2f;
        float maxDist = MathF.Sqrt(cx * cx + cy * cy);
        int width = bitmap.Width;
        int height = bitmap.Height;
        var pixels = bitmap.Pixels;

        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                var px = pixels[idx];
                float dx = x - cx;
                float dy = y - cy;
                float dist = MathF.Sqrt(dx * dx + dy * dy) / maxDist;
                float t = Math.Clamp((dist - start) / Math.Max(softness, 0.001f), 0f, 1f);
                float factor = 1f - strength * t * t;

                pixels[idx] = new SKColor(
                    ClampToByte(px.Red * factor),
                    ClampToByte(px.Green * factor),
                    ClampToByte(px.Blue * factor),
                    px.Alpha);
            }
        });
        bitmap.Pixels = pixels;
    }

    private static void ApplyChromaticAberration(SKBitmap bitmap, double amountValue)
    {
        float shift = (float)(amountValue / 20.0 * 6.0);
        if (shift <= 0.01f) return;

        int width = bitmap.Width;
        int height = bitmap.Height;
        var source = bitmap.Pixels;
        var result = new SKColor[source.Length];

        float cx = (width - 1) / 2f;
        float cy = (height - 1) / 2f;

        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                float dx = x - cx;
                float dy = y - cy;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                float ux = len > 0.001f ? dx / len : 0f;
                float uy = len > 0.001f ? dy / len : 0f;

                var center = SampleEffectsPixel(source, width, height, x, y);
                var red = SampleEffectsPixel(source, width, height, x + ux * shift, y + uy * shift);
                var blue = SampleEffectsPixel(source, width, height, x - ux * shift, y - uy * shift);

                result[y * width + x] = new SKColor(red.Red, center.Green, blue.Blue, center.Alpha);
            }
        });
        bitmap.Pixels = result;
    }

    private static void ApplyNoise(SKBitmap bitmap, double monoValue, double colorValue)
    {
        float monoStrength = (float)(monoValue / 100.0 * 64.0);
        float colorStrength = (float)(colorValue / 100.0 * 64.0);
        if (monoStrength <= 0.01f && colorStrength <= 0.01f) return;

        int width = bitmap.Width;
        int height = bitmap.Height;
        var pixels = bitmap.Pixels;

        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                var px = pixels[idx];

                float monoNoise = monoStrength > 0.01f
                    ? (HashNoise(x, y, 0) * 2f - 1f) * monoStrength
                    : 0f;

                float colorNoiseR = colorStrength > 0.01f
                    ? (HashNoise(x, y, 1) * 2f - 1f) * colorStrength
                    : 0f;
                float colorNoiseG = colorStrength > 0.01f
                    ? (HashNoise(x, y, 2) * 2f - 1f) * colorStrength
                    : 0f;
                float colorNoiseB = colorStrength > 0.01f
                    ? (HashNoise(x, y, 3) * 2f - 1f) * colorStrength
                    : 0f;

                pixels[idx] = new SKColor(
                    ClampToByte(px.Red + monoNoise + colorNoiseR),
                    ClampToByte(px.Green + monoNoise + colorNoiseG),
                    ClampToByte(px.Blue + monoNoise + colorNoiseB),
                    px.Alpha);
            }
        });

        bitmap.Pixels = pixels;
    }

    private static void ApplyGamma(SKBitmap bitmap, double gammaValue)
    {
        float gamma = Math.Clamp((float)gammaValue, 0.2f, 3.0f);
        if (Math.Abs(gamma - 1f) < 0.001f) return;

        float invGamma = 1f / gamma;
        var pixels = bitmap.Pixels;
        Parallel.For(0, pixels.Length, i =>
        {
            var px = pixels[i];
            pixels[i] = new SKColor(
                ClampToByte(MathF.Pow(px.Red / 255f, invGamma) * 255f),
                ClampToByte(MathF.Pow(px.Green / 255f, invGamma) * 255f),
                ClampToByte(MathF.Pow(px.Blue / 255f, invGamma) * 255f),
                px.Alpha);
        });
        bitmap.Pixels = pixels;
    }

    private static void ApplyScanline(SKBitmap bitmap, double lineWidth, double spacing, double softness, double angle, double opacity)
    {
        float lw = MathF.Max(0.1f, (float)lineWidth);
        float sp = MathF.Max(0.1f, (float)spacing);
        float period = lw + sp;
        float soft = Math.Clamp((float)softness / 100f, 0f, 1f);
        float alpha = Math.Clamp((float)opacity / 100f, 0f, 1f);
        if (alpha <= 0.001f) return;

        // angle=0 -> horizontal (project onto Y axis); ±90 -> vertical
        float rad = (float)(angle * Math.PI / 180.0);
        float cosA = MathF.Cos(rad);
        float sinA = MathF.Sin(rad);

        // Signed-distance transition half-width
        float blur = soft * period * 0.5f;

        int w = bitmap.Width;
        int h = bitmap.Height;
        var pixels = bitmap.Pixels;

        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float projected = -x * sinA + y * cosA;
                float pos = projected - MathF.Floor(projected / period) * period;

                // Signed distance to line band [0, lw]: positive = inside line
                float sd;
                if (pos <= lw)
                    sd = MathF.Min(pos, lw - pos);
                else
                    sd = -MathF.Min(pos - lw, period - pos);

                float darken;
                if (blur > 0.01f)
                    darken = alpha * Math.Clamp((sd + blur) / (2f * blur), 0f, 1f);
                else
                    darken = sd >= 0f ? alpha : 0f;

                if (darken > 0.001f)
                {
                    int idx = y * w + x;
                    var px = pixels[idx];
                    float keep = 1f - darken;
                    pixels[idx] = new SKColor(
                        ClampToByte(px.Red * keep),
                        ClampToByte(px.Green * keep),
                        ClampToByte(px.Blue * keep),
                        px.Alpha);
                }
            }
        });

        bitmap.Pixels = pixels;
    }

    private static void ApplyPixelateRegion(SKBitmap bitmap, double blockSizeValue, double centerX, double centerY, double widthPct, double heightPct)
    {
        int blockSize = Math.Max(1, (int)Math.Round(blockSizeValue));
        GetEffectRect(bitmap.Width, bitmap.Height, centerX, centerY, widthPct, heightPct, out int left, out int top, out int right, out int bottom);
        if (right <= left || bottom <= top) return;

        int width = bitmap.Width;
        var pixels = bitmap.Pixels;

        for (int y = top; y < bottom; y += blockSize)
        for (int x = left; x < right; x += blockSize)
        {
            int blockRight = Math.Min(x + blockSize, right);
            int blockBottom = Math.Min(y + blockSize, bottom);
            int count = 0;
            int sumR = 0, sumG = 0, sumB = 0, sumA = 0;

            for (int yy = y; yy < blockBottom; yy++)
            for (int xx = x; xx < blockRight; xx++)
            {
                var px = pixels[yy * width + xx];
                sumR += px.Red;
                sumG += px.Green;
                sumB += px.Blue;
                sumA += px.Alpha;
                count++;
            }

            if (count == 0) continue;
            var avg = new SKColor(
                (byte)(sumR / count),
                (byte)(sumG / count),
                (byte)(sumB / count),
                (byte)(sumA / count));

            for (int yy = y; yy < blockBottom; yy++)
            for (int xx = x; xx < blockRight; xx++)
                pixels[yy * width + xx] = avg;
        }

        bitmap.Pixels = pixels;
    }

    private static void ApplySolidBlock(SKBitmap bitmap, string colorText, double centerX, double centerY, double widthPct, double heightPct)
    {
        GetEffectRect(bitmap.Width, bitmap.Height, centerX, centerY, widthPct, heightPct, out int left, out int top, out int right, out int bottom);
        if (right <= left || bottom <= top) return;

        var color = TryParseEffectsColor(colorText) ?? new SKColor(0, 0, 0, 255);
        int width = bitmap.Width;
        var pixels = bitmap.Pixels;
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
            pixels[y * width + x] = color;
        bitmap.Pixels = pixels;
    }

    private static void GetEffectRegionValues(EffectEntry effect, out double centerX, out double centerY, out double widthPct, out double heightPct)
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

    private static void SetEffectRegionValues(EffectEntry effect, double centerX, double centerY, double widthPct, double heightPct)
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

    private static SKColor SampleEffectsPixel(SKColor[] pixels, int width, int height, float x, float y)
    {
        int px = Math.Clamp((int)MathF.Round(x), 0, width - 1);
        int py = Math.Clamp((int)MathF.Round(y), 0, height - 1);
        return pixels[py * width + px];
    }

    private static void GetEffectRect(int imageWidth, int imageHeight, double centerXPct, double centerYPct, double widthPct, double heightPct,
        out int left, out int top, out int right, out int bottom)
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

    private static SKColor? TryParseEffectsColor(string? text)
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
        catch { }

        return null;
    }

    private static Windows.UI.Color ToUiColor(SKColor color) =>
        Windows.UI.Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue);

    private static float HashNoise(int x, int y, int salt)
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

    private static byte ClampToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
}
