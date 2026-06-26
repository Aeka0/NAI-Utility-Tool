using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ComputeSharp;
using ComputeSharp.D2D1;
using ComputeSharp.D2D1.Descriptors;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NAITool.Models;
using SkiaSharp;
using Windows.Foundation;
using Windows.Graphics.Effects;
using Windows.Storage.Streams;
using static NAITool.Rendering.EffectsRenderingHelpers;

namespace NAITool.Rendering;

public sealed class Win2dEffectsRenderer
{
    private readonly CanvasDevice _device;

    public Win2dEffectsRenderer()
    {
        _device = CanvasDevice.GetSharedDevice();
    }

    public async Task<EffectsPreviewRenderResult> RenderPreviewAsync(
        byte[] sourceBytes,
        IReadOnlyList<EffectEntry> effects,
        Action<string>? debugLog,
        string requestLabel,
        CancellationToken cancellationToken)
    {
        ThrowIfUnsupported(effects, debugLog, requestLabel);
        using var inputStream = new InMemoryRandomAccessStream();
        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] Preview input stream write start | Bytes={sourceBytes.Length}");
        await WriteBytesToStreamAsync(inputStream, sourceBytes, cancellationToken);
        inputStream.Seek(0);
        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] CanvasBitmap load start");
        using CanvasBitmap source = await CanvasBitmap.LoadAsync(_device, inputStream, 96f);
        cancellationToken.ThrowIfCancellationRequested();

        int sourceWidth = checked((int)source.SizeInPixels.Width);
        int sourceHeight = checked((int)source.SizeInPixels.Height);
        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] CanvasBitmap loaded | Size={sourceWidth}x{sourceHeight}");
        ICanvasImage image = BuildEffectGraph(source, effects, sourceWidth, sourceHeight, debugLog, requestLabel, cancellationToken);
        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] CanvasImageSource create start | Size={sourceWidth}x{sourceHeight}");
        var imageSource = new CanvasImageSource(
            _device,
            sourceWidth,
            sourceHeight,
            96f);
        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] Preview drawing session create start");
        using (var ds = imageSource.CreateDrawingSession(Windows.UI.Color.FromArgb(0, 0, 0, 0)))
        {
            debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] Preview DrawImage start");
            ds.DrawImage(image);
            debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] Preview DrawImage completed");
        }
        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] Preview drawing session disposed");

        return new EffectsPreviewRenderResult
        {
            ImageSource = imageSource,
            PixelWidth = sourceWidth,
            PixelHeight = sourceHeight,
        };
    }

    public async Task<byte[]> RenderPngAsync(
        byte[] sourceBytes,
        IReadOnlyList<EffectEntry> effects,
        Action<string>? debugLog,
        string requestLabel,
        CancellationToken cancellationToken)
    {
        ThrowIfUnsupported(effects, debugLog, requestLabel);
        cancellationToken.ThrowIfCancellationRequested();

        using var inputStream = new InMemoryRandomAccessStream();
        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] PNG input stream write start | Bytes={sourceBytes.Length}");
        await WriteBytesToStreamAsync(inputStream, sourceBytes, cancellationToken);
        inputStream.Seek(0);
        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] CanvasBitmap load start");
        using CanvasBitmap source = await CanvasBitmap.LoadAsync(_device, inputStream, 96f);
        cancellationToken.ThrowIfCancellationRequested();

        int sourceWidth = checked((int)source.SizeInPixels.Width);
        int sourceHeight = checked((int)source.SizeInPixels.Height);
        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] CanvasBitmap loaded | Size={sourceWidth}x{sourceHeight}");
        ICanvasImage image = BuildEffectGraph(source, effects, sourceWidth, sourceHeight, debugLog, requestLabel, cancellationToken);

        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] CanvasRenderTarget create start | Size={sourceWidth}x{sourceHeight}");
        using var target = new CanvasRenderTarget(
            _device,
            sourceWidth,
            sourceHeight,
            96f);
        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] PNG drawing session create start");
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] PNG DrawImage start");
            ds.DrawImage(image);
            debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] PNG DrawImage completed");
        }
        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] PNG drawing session disposed");

        using var outputStream = new InMemoryRandomAccessStream();
        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] PNG SaveAsync start");
        await target.SaveAsync(outputStream, CanvasBitmapFileFormat.Png);
        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] PNG SaveAsync completed | StreamSize={outputStream.Size}");
        outputStream.Seek(0);
        byte[] bytes = await ReadStreamBytesAsync(outputStream, cancellationToken);
        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] PNG output stream read completed | OutputBytes={bytes.Length}");
        return bytes;
    }

    private static ICanvasImage BuildEffectGraph(
        ICanvasImage source,
        IReadOnlyList<EffectEntry> effects,
        int imageWidth,
        int imageHeight,
        Action<string>? debugLog,
        string requestLabel,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] Build graph start | Size={imageWidth}x{imageHeight} | Effects={effects.Count}");
        ICanvasImage image = source;
        for (int i = 0; i < effects.Count; i++)
        {
            var effect = effects[i];
            cancellationToken.ThrowIfCancellationRequested();
            var effectStopwatch = Stopwatch.StartNew();
            debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] Build effect {i + 1}/{effects.Count} start | {DescribeEffect(effect)}");
            image = ApplyEffect(image, effect, imageWidth, imageHeight, debugLog, requestLabel);
            debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] Build effect {i + 1}/{effects.Count} completed | Type={effect.Type} | Elapsed={FormatElapsed(effectStopwatch)}");
        }

        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] Build graph completed | Elapsed={FormatElapsed(stopwatch)}");
        return image;
    }

    private static void ThrowIfUnsupported(
        IReadOnlyList<EffectEntry> effects,
        Action<string>? debugLog,
        string requestLabel)
    {
        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] Support check start | Effects={effects.Count}");
        for (int i = 0; i < effects.Count; i++)
        {
            var effect = effects[i];
            if (!IsSupported(effect.Type))
            {
                debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] Support check failed | Index={i + 1} | Type={effect.Type}");
                throw new NotSupportedException($"GPU renderer does not yet support {effect.Type}.");
            }
        }

        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] Support check completed");
    }

    private static bool IsSupported(EffectType type) => type is
        EffectType.BrightnessContrast or
        EffectType.SaturationVibrance or
        EffectType.Temperature or
        EffectType.Glow or
        EffectType.RadialBlur or
        EffectType.Vignette or
        EffectType.ChromaticAberration or
        EffectType.Noise or
        EffectType.Gamma or
        EffectType.SolidBlock or
        EffectType.Scanline;

    private static ICanvasImage ApplyEffect(
        ICanvasImage source,
        EffectEntry effect,
        float imageWidth,
        float imageHeight,
        Action<string>? debugLog,
        string requestLabel)
    {
        return effect.Type switch
        {
            EffectType.BrightnessContrast => CreateShaderEffect(
                source,
                new BrightnessContrastShader(
                    (float)(effect.Value1 / 100.0),
                    (float)(1.0 + effect.Value2 / 100.0))),
            EffectType.SaturationVibrance => CreateShaderEffect(
                source,
                new SaturationVibranceShader(
                    (float)(1.0 + effect.Value1 / 100.0),
                    (float)(effect.Value2 / 100.0))),
            EffectType.Temperature => CreateShaderEffect(
                source,
                new TemperatureShader(
                    (float)(effect.Value1 / 100.0 * 45.0 / 255.0),
                    (float)(effect.Value2 / 100.0 * 35.0 / 255.0))),
            EffectType.Glow => CreateGlowEffect(source, effect, debugLog, requestLabel),
            EffectType.RadialBlur => CreateRadialBlurEffect(source, effect, imageWidth, imageHeight, debugLog, requestLabel),
            EffectType.Vignette => CreateShaderEffect(
                source,
                new VignetteShader(
                    (float)(effect.Value1 / 100.0),
                    0.15f + (float)(effect.Value2 / 100.0) * 0.75f,
                    imageWidth,
                    imageHeight)),
            EffectType.ChromaticAberration => CreateShaderEffect(
                source,
                new ChromaticAberrationShader(
                    (float)(effect.Value1 / 20.0 * 6.0),
                    imageWidth,
                    imageHeight)),
            EffectType.Noise => CreateShaderEffect(
                source,
                new NoiseShader(
                    (float)(effect.Value1 / 100.0 * 64.0 / 255.0),
                    (float)(effect.Value2 / 100.0 * 64.0 / 255.0))),
            EffectType.Gamma => CreateShaderEffect(
                source,
                new GammaShader(1f / Math.Clamp((float)effect.Value1, 0.2f, 3.0f))),
            EffectType.SolidBlock => CreateSolidBlockEffect(source, effect, imageWidth, imageHeight),
            EffectType.Scanline => CreateShaderEffect(
                source,
                new ScanlineShader(
                    MathF.Max(0.1f, (float)effect.Value1),
                    MathF.Max(0.1f, (float)effect.Value2),
                    Math.Clamp((float)effect.Value3 / 100f, 0f, 1f),
                    (float)(effect.Value4 * Math.PI / 180.0),
                    Math.Clamp((float)effect.Value5 / 100f, 0f, 1f))),
            _ => throw new NotSupportedException($"GPU renderer does not yet support {effect.Type}."),
        };
    }

    private static ICanvasImage CreateGlowEffect(
        ICanvasImage source,
        EffectEntry effect,
        Action<string>? debugLog,
        string requestLabel)
    {
        float glowSize = Math.Clamp((float)effect.Value1, 1f, 500f);
        float threshold = Math.Clamp((float)effect.Value2, 0f, 100f) / 100f;
        float strength = Math.Clamp((float)effect.Value3, 0f, 200f) / 100f;
        float aspectRatio = Math.Clamp((float)effect.Value4, 0.05f, 8f);
        float tiltDegrees = Math.Clamp((float)effect.Value6, -90f, 90f);
        float saturationNorm = Math.Clamp((float)effect.Value5, -100f, 100f) / 100f;
        float saturation = saturationNorm >= 0f
            ? MathF.Pow(4f, saturationNorm)
            : MathF.Pow(0.25f, -saturationNorm);
        float ratioPow = MathF.Pow(aspectRatio, 1.25f);
        float sigmaX = MathF.Max(0.1f, glowSize * ratioPow / 3.0f);
        float sigmaY = MathF.Max(0.1f, glowSize / MathF.Max(0.05f, ratioPow) / 3.0f);
        float blurAmount = MathF.Max(0.1f, (sigmaX + sigmaY) * 0.5f);

        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] Glow graph | Size={FormatEffectNumber(glowSize)} | Threshold={FormatEffectNumber(threshold)} | Strength={FormatEffectNumber(strength)} | Aspect={FormatEffectNumber(aspectRatio)} | Tilt={FormatEffectNumber(tiltDegrees)} | Saturation={FormatEffectNumber(saturation)} | SigmaX={FormatEffectNumber(sigmaX)} | SigmaY={FormatEffectNumber(sigmaY)} | BlurAmount={FormatEffectNumber(blurAmount)} | Note=Win2D GaussianBlurEffect uses isotropic blur; CPU tilt/anisotropic blur is approximated");
        var bright = CreateShaderEffect(source, new GlowExtractShader(threshold));
        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] Glow bright-pass shader created");
        var blurred = new GaussianBlurEffect
        {
            Source = bright,
            BlurAmount = blurAmount,
            Optimization = EffectOptimization.Quality,
            BorderMode = EffectBorderMode.Soft,
        };
        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] Glow GaussianBlurEffect created | Optimization=Quality | Border=Soft");

        ICanvasImage result = CreateShaderEffect(
            source,
            new GlowCompositeShader(strength, saturation),
            blurred);
        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] Glow composite shader created");
        return result;
    }

    private static ICanvasImage CreateRadialBlurEffect(
        ICanvasImage source,
        EffectEntry effect,
        float imageWidth,
        float imageHeight,
        Action<string>? debugLog,
        string requestLabel)
    {
        float strength = Math.Clamp((float)effect.Value1, 0f, 100f);
        float centerX = (float)(Math.Clamp(effect.Value2, 0, 100) / 100.0 * (imageWidth - 1));
        float centerY = (float)(Math.Clamp(effect.Value3, 0, 100) / 100.0 * (imageHeight - 1));
        int mode = Math.Clamp((int)Math.Round(effect.Value4), 0, 2);
        int sampleCount = 4 + GetRadialBlurSampleCount(strength, mode) * 2;
        debugLog?.Invoke($"[Effects][{requestLabel}][Win2D] RadialBlur graph | Strength={FormatEffectNumber(strength)} | Center={FormatEffectNumber(centerX)},{FormatEffectNumber(centerY)} | Mode={mode} | Samples={sampleCount} | Size={FormatEffectNumber(imageWidth)}x{FormatEffectNumber(imageHeight)}");

        return CreateShaderEffect(
            source,
            new RadialBlurShader(
                strength / 100f,
                centerX,
                centerY,
                mode,
                sampleCount,
                imageWidth,
                imageHeight));
    }

    private static int GetRadialBlurSampleCount(float strength, int mode)
    {
        int baseCount = mode switch
        {
            1 => 16,
            2 => 14,
            _ => 3,
        };
        int scaled = mode switch
        {
            0 => baseCount + (int)MathF.Round(strength / 100f * 12f),
            _ => baseCount + (int)MathF.Round(strength / 100f * 24f),
        };
        return Math.Clamp(scaled, baseCount, 40);
    }

    private static ICanvasImage CreateSolidBlockEffect(
        ICanvasImage source,
        EffectEntry effect,
        float imageWidth,
        float imageHeight)
    {
        GetEffectRect(
            (int)MathF.Round(imageWidth),
            (int)MathF.Round(imageHeight),
            effect.Value1,
            effect.Value2,
            effect.Value3,
            effect.Value4,
            out int left,
            out int top,
            out int right,
            out int bottom);

        var color = TryParseEffectsColor(effect.TextValue) ?? new SKColor(0, 0, 0, 255);
        var shaderColor = new float4(
            color.Red / 255f,
            color.Green / 255f,
            color.Blue / 255f,
            color.Alpha / 255f);

        return CreateShaderEffect(
            source,
            new SolidBlockShader(shaderColor, left, top, right, bottom));
    }

    private static PixelShaderEffect<T> CreateShaderEffect<T>(ICanvasImage source, T constants)
        where T : unmanaged, ID2D1PixelShader, ID2D1PixelShaderDescriptor<T>
    {
        return CreateShaderEffect(source, constants, null);
    }

    private static PixelShaderEffect<T> CreateShaderEffect<T>(
        ICanvasImage source,
        T constants,
        ICanvasImage? secondarySource)
        where T : unmanaged, ID2D1PixelShader, ID2D1PixelShaderDescriptor<T>
    {
        if (source is not IGraphicsEffectSource graphicsSource)
            throw new NotSupportedException("Win2D image is not a graphics effect source.");

        var shader = new PixelShaderEffect<T>
        {
            ConstantBuffer = constants,
        };
        shader.Sources[0] = graphicsSource;
        if (secondarySource != null)
        {
            if (secondarySource is not IGraphicsEffectSource secondaryGraphicsSource)
                throw new NotSupportedException("Win2D secondary image is not a graphics effect source.");
            shader.Sources[1] = secondaryGraphicsSource;
        }
        return shader;
    }

    private static async Task WriteBytesToStreamAsync(
        InMemoryRandomAccessStream stream,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        using var writer = new DataWriter(stream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync().AsTask(cancellationToken);
        writer.DetachStream();
    }

    private static async Task<byte[]> ReadStreamBytesAsync(
        InMemoryRandomAccessStream stream,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size).AsTask(cancellationToken);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
