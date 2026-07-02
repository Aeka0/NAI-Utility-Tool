using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NAITool.Models;
using NAITool.Services;
using SkiaSharp;
using static NAITool.Rendering.EffectsRenderingHelpers;

namespace NAITool.Rendering;

public static class CpuEffectsRenderer
{
    public static byte[] RenderEffects(
        byte[] sourceBytes,
        List<EffectEntry> effects,
        CancellationToken cancellationToken = default)
    {
        using var bitmap = RenderEffectsPreview(null, sourceBytes, effects, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray() ?? sourceBytes;
    }

    public static SKBitmap RenderEffectsPreview(
        SKBitmap? cachedSourceBitmap,
        byte[] sourceBytes,
        List<EffectEntry> effects,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SKBitmap? baseBitmap = cachedSourceBitmap?.Copy() ?? SKBitmap.Decode(sourceBytes);
        if (baseBitmap == null)
            throw new InvalidOperationException(LocalizationService.Instance.GetString("post.error.decode_source_failed"));

        foreach (var effect in effects)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                    ApplyGlow(baseBitmap, effect.Value1, effect.Value2, effect.Value3, effect.Value4, effect.Value6, effect.Value5, cancellationToken);
                    break;
                case EffectType.RadialBlur:
                    ApplyRadialBlur(baseBitmap, effect.Value1, effect.Value2, effect.Value3, (int)Math.Round(effect.Value4), cancellationToken);
                    break;
                case EffectType.Vignette:
                    ApplyVignette(baseBitmap, effect.Value1, effect.Value2);
                    break;
                case EffectType.ChromaticAberration:
                    ApplyChromaticAberration(baseBitmap, effect.Value1, effect.Value2, effect.Value3);
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
                case EffectType.JpegLoss:
                    ApplyJpegLoss(baseBitmap, effect.Value1, effect.Value2, effect.Value3, effect.Value4);
                    break;
            }
            cancellationToken.ThrowIfCancellationRequested();
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
        double saturationValue,
        CancellationToken cancellationToken)
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
        
        // 鏇寸揣鍑戠殑杞槇鍊?(Tighter soft-knee)锛岄槻姝㈡硾鍏夎繃搴︽孩鍑哄埌鏆楅儴/涓棿璋?
        float knee = MathF.Max(1f, threshold * 0.15f);

        float ratioPow = MathF.Pow(aspectRatio, 1.25f);
        float sigmaX = MathF.Max(0.1f, glowSize * ratioPow / 3.0f);
        float sigmaY = MathF.Max(0.1f, glowSize / MathF.Max(0.05f, ratioPow) / 3.0f);

        var src = bitmap.Pixels;
        var brightPixels = new SKColor[src.Length];
        var parallelOptions = new ParallelOptions { CancellationToken = cancellationToken };

        Parallel.For(0, src.Length, parallelOptions, i =>
        {
            var px = src[i];
            
            // 淇鈥滄硾鍏夊亸鐧解€濓細浣跨敤 Max(R,G,B) 鑰屼笉鏄?Luminance銆?
            // 浜害(Luminance)浼氭瀬澶у湴鍘嬩綆楂橀ケ鍜屽害棰滆壊锛堝绾摑銆佺函绾級鐨勬潈閲嶏紝瀵艰嚧鍙湁鐧借壊鑳藉彂鍏夈€?
            // 浣跨敤 Max 鍙互璁╅珮楗卞拰搴︾殑浜壊涓庣櫧鑹插悓绛夊彂鍏夛紝浠庤€屼繚鐣欐硾鍏夌殑鑹插僵銆?
            float maxColor = Math.Max(px.Red, Math.Max(px.Green, px.Blue));
            
            float soft = maxColor - threshold + knee;
            soft = Math.Clamp(soft, 0f, 2f * knee);
            soft = soft * soft / (4f * knee + 0.0001f);
            
            float contribution = Math.Max(soft, maxColor - threshold);
            float factor = maxColor > 0.0001f ? contribution / maxColor : 0f;

            // 淇鈥滆竟缂樼伡鐑?纭埅鏂€濓細寮哄埗 Alpha 涓?255銆?
            // 涔嬪墠浣庝簬闃堝€肩殑鍍忕礌 Alpha 涓?0锛屽鑷撮珮鏂ā绯婃椂 Alpha 閫氶亾浜х敓閿愬埄杈圭紭锛岃繘鑰屽紩鍙戣壊褰╂柇灞傘€?
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
        cancellationToken.ThrowIfCancellationRequested();

        if (Math.Abs(tiltDegrees) < 0.01f)
        {
            using var canvas = new SKCanvas(blurred);
            using var paint = new SKPaint
            {
                IsAntialias = false,
                ImageFilter = SKImageFilter.CreateBlur(sigmaX, sigmaY),
            };
            using var brightImage = SKImage.FromBitmap(bright);
            canvas.Clear(SKColors.Black); // 蹇呴』鐢ㄧ函榛戜笉閫忔槑搴曡壊
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
                cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
                using var rotatedInputImage = SKImage.FromBitmap(rotatedInput);
                rotatedBlurCanvas.Clear(SKColors.Black);
                rotatedBlurCanvas.DrawImage(rotatedInputImage, 0, 0, sampling, paint);
                rotatedBlurCanvas.Flush();
            }

            using var untilted = new SKBitmap(rotatedSize, rotatedSize, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var untiltedCanvas = new SKCanvas(untilted))
            {
                cancellationToken.ThrowIfCancellationRequested();
                untiltedCanvas.Clear(SKColors.Black);
                untiltedCanvas.Translate(center, center);
                untiltedCanvas.RotateDegrees(-tiltDegrees);
                untiltedCanvas.Translate(-center, -center);
                untiltedCanvas.DrawBitmap(rotatedBlurred, 0, 0);
                untiltedCanvas.Flush();
            }

            using var canvas = new SKCanvas(blurred);
            cancellationToken.ThrowIfCancellationRequested();
            canvas.Clear(SKColors.Black);
            canvas.DrawBitmap(
                untilted,
                new SKRect(sourceX, sourceY, sourceX + width, sourceY + height),
                new SKRect(0, 0, width, height));
            canvas.Flush();
        }

        var glowPixels = blurred.Pixels;
        var outPixels = new SKColor[src.Length];
        cancellationToken.ThrowIfCancellationRequested();

        Parallel.For(0, outPixels.Length, parallelOptions, i =>
        {
            var basePx = src[i];
            var glowPx = glowPixels[i];

            float gr = glowPx.Red;
            float gg = glowPx.Green;
            float gb = glowPx.Blue;

            // 瀵规硾鍏夋湰韬仛鏇村己鐨勮壊搴﹀寮猴紝骞朵繚鎸佸嘲鍊间寒搴︼紝閬垮厤鈥滈ケ鍜屽害鎷夐珮浣嗕粛鍋忕櫧鈥濄€?
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

            // 鎭㈠涓?Linear Additive (绾挎€у彔鍔? 娣峰悎妯″紡銆?
            // 涔嬪墠鐨?Screen 妯″紡浼氬帇鍒朵寒閮ㄨ儗鏅笂鐨勬硾鍏夛紝瀵艰嚧娉涘厜鏄惧緱鏃犲姏涓斿亸鐧姐€?
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

    private static void ApplyRadialBlur(
        SKBitmap bitmap,
        double strengthValue,
        double centerXPct,
        double centerYPct,
        int mode,
        CancellationToken cancellationToken)
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
        float rawStrength = strength / 100f;
        float shapedStrength = mode == 0 ? rawStrength : ShapeRadialBlurStrength(strength);
        float zoomRadius = 0.0025f + rawStrength * 0.075f;
        float spinAngle = shapedStrength * 0.22f;
        float maxDist = MathF.Sqrt(MathF.Max(cx, width - 1 - cx) * MathF.Max(cx, width - 1 - cx) +
                                   MathF.Max(cy, height - 1 - cy) * MathF.Max(cy, height - 1 - cy));
        maxDist = MathF.Max(maxDist, 1f);
        var parallelOptions = new ParallelOptions { CancellationToken = cancellationToken };

        Parallel.For(0, height, parallelOptions, y =>
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
                        case 1: // 鏃嬭浆
                            float angle = t * spinAngle;
                            float cos = MathF.Cos(angle);
                            float sin = MathF.Sin(angle);
                            sampleX = cx + dx * cos - dy * sin;
                            sampleY = cy + dx * sin + dy * cos;
                            weight = 1f - MathF.Abs(t) * 0.5f;
                            break;
                        case 2: // 楂樻柉
                            float gaussianScale = t * zoomRadius;
                            sampleX = x - dx * gaussianScale;
                            sampleY = y - dy * gaussianScale;
                            weight = MathF.Exp(-(t * t) * 4f);
                            break;
                        default: // 鏀惧皠
                            float scale = t * zoomRadius;
                            sampleX = x - dx * scale;
                            sampleY = y - dy * scale;
                            weight = 1f;
                            break;
                    }

                    SKColor sample;
                    if (mode == 2) // 娓愯繘锛氱涓績瓒婅繙锛岃秺杩涜鍚勫悜鍚屾€фā绯?
                    {
                        float distNorm = MathF.Sqrt(dx * dx + dy * dy) / maxDist;
                            float localRadius = distNorm * (0.5f + shapedStrength * 14f);
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
            1 => 8, // 鏃嬭浆鏇翠緷璧栭噰鏍?
            2 => 8, // 娓愯繘妯＄硦
            _ => 3, // 鏀惧皠榛樿鏇磋交
        };
        int scaled = mode switch
        {
            0 => baseCount + (int)MathF.Round(strength / 100f * 8f),
            _ => baseCount + (int)MathF.Round(strength / 100f * 14f),
        };
        return Math.Clamp(scaled, baseCount, 22);
    }

    private static float ShapeRadialBlurStrength(float strength)
    {
        float raw = Math.Clamp(strength, 0f, 100f) / 100f;
        float shaped = MathF.Log(1f + raw) / MathF.Log(2f);
        return shaped * shaped;
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

    private static void ApplyChromaticAberration(SKBitmap bitmap, double amountValue, double colorPairValue, double iterationsValue)
    {
        float shift = (float)(-amountValue / 20.0 * 6.0);
        if (MathF.Abs(shift) <= 0.01f) return;

        int width = bitmap.Width;
        int height = bitmap.Height;
        var source = bitmap.Pixels;
        var result = new SKColor[source.Length];

        float cx = (width - 1) / 2f;
        float cy = (height - 1) / 2f;
        int colorPair = Math.Clamp((int)Math.Round(colorPairValue), 0, 3);
        int samples = Math.Clamp((int)Math.Round(iterationsValue <= 0 ? 3 : iterationsValue), 3, 16);
        float redTarget = 1f;
        float greenTarget = 0f;
        float blueTarget = -1f;
        switch (colorPair)
        {
            case 1:
                redTarget = -1f;
                greenTarget = 1f;
                blueTarget = 1f;
                break;
            case 2:
                redTarget = -1f;
                greenTarget = 1f;
                blueTarget = -1f;
                break;
            case 3:
                redTarget = 1f;
                greenTarget = 1f;
                blueTarget = -1f;
                break;
        }

        float sampleDenominator = MathF.Max(samples - 1f, 1f);
        float bandWidth = MathF.Max(2.7f / sampleDenominator, 0.42f);

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
                float sumR = 0f;
                float sumG = 0f;
                float sumB = 0f;
                float weightR = 0f;
                float weightG = 0f;
                float weightB = 0f;

                for (int i = 0; i < samples; i++)
                {
                    float t = -1f + 2f * i / sampleDenominator;
                    float wr = SmoothBandWeight(t, redTarget, bandWidth);
                    float wg = SmoothBandWeight(t, greenTarget, bandWidth);
                    float wb = SmoothBandWeight(t, blueTarget, bandWidth);
                    var sampled = SampleEffectsPixel(source, width, height, x + ux * shift * t, y + uy * shift * t);
                    sumR += sampled.Red * wr;
                    sumG += sampled.Green * wg;
                    sumB += sampled.Blue * wb;
                    weightR += wr;
                    weightG += wg;
                    weightB += wb;
                }

                result[y * width + x] = new SKColor(
                    ClampToByte(weightR > 0.0001f ? sumR / weightR : center.Red),
                    ClampToByte(weightG > 0.0001f ? sumG / weightG : center.Green),
                    ClampToByte(weightB > 0.0001f ? sumB / weightB : center.Blue),
                    center.Alpha);
            }
        });
        bitmap.Pixels = result;
    }

    private static float SmoothBandWeight(float value, float target, float bandWidth)
    {
        float weight = Math.Clamp(1f - MathF.Abs(value - target) / bandWidth, 0f, 1f);
        return weight * weight * (3f - 2f * weight);
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

        // angle=0 -> horizontal (project onto Y axis); 卤90 -> vertical
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

    private static void ApplyJpegLoss(SKBitmap bitmap, double lossValue, double iterationsValue, double blockSizeValue, double chromaBleedValue)
    {
        float loss = Math.Clamp((float)lossValue / 100f, 0f, 1f);
        if (loss <= 0.0001f) return;

        float iterations = Math.Clamp((float)(iterationsValue <= 0 ? 3 : iterationsValue), 1f, 24f);
        float blockSize = MathF.Max(1f, (float)(blockSizeValue <= 0 ? 8 : blockSizeValue));
        float chromaBleed = Math.Clamp((float)chromaBleedValue / 100f, 0f, 1f);
        float cumulativeLoss = 1f - MathF.Pow(1f - loss * 0.82f, iterations * 0.32f);
        float severeLoss = cumulativeLoss * cumulativeLoss;

        int width = bitmap.Width;
        int height = bitmap.Height;
        var source = bitmap.Pixels;
        var result = new SKColor[source.Length];

        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                var px = source[y * width + x];
                float blockOriginX = MathF.Floor(x / blockSize) * blockSize;
                float blockOriginY = MathF.Floor(y / blockSize) * blockSize;
                float blockCenterX = blockOriginX + blockSize * 0.5f;
                float blockCenterY = blockOriginY + blockSize * 0.5f;
                float blockLocalX = Frac(x / blockSize);
                float blockLocalY = Frac(y / blockSize);

                float avgR = 0f;
                float avgG = 0f;
                float avgB = 0f;
                for (int oy = -1; oy <= 1; oy++)
                {
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        var sample = SampleEffectsPixel(
                            source,
                            width,
                            height,
                            blockCenterX + ox * blockSize * 0.28f,
                            blockCenterY + oy * blockSize * 0.28f);
                        avgR += sample.Red;
                        avgG += sample.Green;
                        avgB += sample.Blue;
                    }
                }

                avgR /= 9f;
                avgG /= 9f;
                avgB /= 9f;

                RgbToYcbcr(px.Red, px.Green, px.Blue, out float yValue, out float cb, out float cr);
                RgbToYcbcr(avgR, avgG, avgB, out float blockY, out float blockCb, out float blockCr);

                float yLevels = Lerp(220f, 12f, severeLoss);
                float cLevels = Lerp(160f, 7f, severeLoss);
                yValue = MathF.Floor(Lerp(yValue, blockY, severeLoss * 0.45f) * yLevels + 0.5f) / yLevels;
                cb = MathF.Floor(Lerp(cb, blockCb, cumulativeLoss * chromaBleed) * cLevels + 0.5f) / cLevels;
                cr = MathF.Floor(Lerp(cr, blockCr, cumulativeLoss * chromaBleed) * cLevels + 0.5f) / cLevels;
                YcbcrToRgb(yValue, cb, cr, out float compressedR, out float compressedG, out float compressedB);

                var right = SampleEffectsPixel(source, width, height, x + 1, y);
                var left = SampleEffectsPixel(source, width, height, x - 1, y);
                var down = SampleEffectsPixel(source, width, height, x, y + 1);
                var up = SampleEffectsPixel(source, width, height, x, y - 1);
                float blurR = (right.Red + left.Red + down.Red + up.Red) * 0.25f;
                float blurG = (right.Green + left.Green + down.Green + up.Green) * 0.25f;
                float blurB = (right.Blue + left.Blue + down.Blue + up.Blue) * 0.25f;
                float edge = Math.Clamp(
                    MathF.Sqrt(Square(px.Red - blurR) + Square(px.Green - blurG) + Square(px.Blue - blurB)) / 255f * 4f,
                    0f,
                    1f);
                float ringingPattern = MathF.Cos((blockLocalX - 0.5f) * MathF.Tau) * MathF.Cos((blockLocalY - 0.5f) * MathF.Tau);
                float blockBoundary = MathF.Max(
                    SmoothStep(0f, 0.16f, 0.16f - MathF.Min(blockLocalX, 1f - blockLocalX)),
                    SmoothStep(0f, 0.16f, 0.16f - MathF.Min(blockLocalY, 1f - blockLocalY)));
                float mosquito = (HashNoise(x, y, (int)MathF.Round(iterations + 11f)) - 0.5f) * edge * cumulativeLoss * 255f * 0.12f;
                float ringing = ringingPattern * edge * cumulativeLoss * 255f * 0.06f + mosquito;
                compressedR += ringing;
                compressedG += ringing;
                compressedB += ringing;
                compressedR = Lerp(compressedR, avgR, blockBoundary * severeLoss * 0.18f);
                compressedG = Lerp(compressedG, avgG, blockBoundary * severeLoss * 0.18f);
                compressedB = Lerp(compressedB, avgB, blockBoundary * severeLoss * 0.18f);

                float mix = Math.Clamp(cumulativeLoss * 1.18f, 0f, 1f);
                result[y * width + x] = new SKColor(
                    ClampToByte(Lerp(px.Red, compressedR, mix)),
                    ClampToByte(Lerp(px.Green, compressedG, mix)),
                    ClampToByte(Lerp(px.Blue, compressedB, mix)),
                    px.Alpha);
            }
        });

        bitmap.Pixels = result;
    }

    private static void RgbToYcbcr(float r, float g, float b, out float y, out float cb, out float cr)
    {
        r /= 255f;
        g /= 255f;
        b /= 255f;
        y = r * 0.299f + g * 0.587f + b * 0.114f;
        cb = r * -0.168736f + g * -0.331264f + b * 0.5f + 0.5f;
        cr = r * 0.5f + g * -0.418688f + b * -0.081312f + 0.5f;
    }

    private static void YcbcrToRgb(float y, float cb, float cr, out float r, out float g, out float b)
    {
        cb -= 0.5f;
        cr -= 0.5f;
        r = (y + 1.402f * cr) * 255f;
        g = (y - 0.344136f * cb - 0.714136f * cr) * 255f;
        b = (y + 1.772f * cb) * 255f;
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / MathF.Max(edge1 - edge0, 0.0001f), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Frac(float value) => value - MathF.Floor(value);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Square(float value) => value * value;

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

}
