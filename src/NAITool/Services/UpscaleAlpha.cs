using SkiaSharp;

namespace NAITool.Services;

/// <summary>Separates straight RGB and coverage without treating NAI's alpha LSB as transparency.</summary>
internal static class UpscaleAlpha
{
    // ImageMetadataService writes stealth bits as 0xFE | bit. Ignore those two
    // near-opaque levels only; even a small genuinely transparent detail must survive.
    internal const byte TransparencyThreshold = 253;

    internal static SKBitmap DecodeStraight(byte[] bytes)
    {
        using var stream = new SKMemoryStream(bytes);
        using var codec = SKCodec.Create(stream);
        if (codec == null) throw new InvalidOperationException(LocalizationService.Instance.GetString("upscale.error.decode_failed"));
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) == SKCodecResult.Success) return bitmap;
        bitmap.Dispose();
        throw new InvalidOperationException(LocalizationService.Instance.GetString("upscale.error.decode_failed"));
    }

    internal static unsafe bool HasTransparency(SKBitmap straight, CancellationToken ct)
    {
        for (int y = 0; y < straight.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            byte* row = (byte*)straight.GetPixels() + y * straight.RowBytes;
            for (int x = 0; x < straight.Width; x++)
                if (row[x * 4 + 3] <= TransparencyThreshold) return true;
        }
        return false;
    }

    // Mutate only our decoded copy. Making RGB opaque before any tiling/drawing
    // prevents canvas compositing from darkening semitransparent or hidden colors.
    internal static unsafe SKBitmap? Split(SKBitmap straight, CancellationToken ct)
    {
        SKBitmap? alpha = HasTransparency(straight, ct)
            ? new SKBitmap(straight.Width, straight.Height, SKColorType.Rgba8888, SKAlphaType.Opaque)
            : null;
        try
        {
            var rgbPixels = straight.GetPixels();
            int rgbStride = straight.RowBytes;
            var alphaPixels = alpha?.GetPixels() ?? IntPtr.Zero;
            int alphaStride = alpha?.RowBytes ?? 0;
            ProcessRows(straight.Width, straight.Height, ct, y =>
            {
                byte* rgbRow = (byte*)rgbPixels + y * rgbStride;
                byte* alphaRow = (byte*)alphaPixels + y * alphaStride;
                for (int x = 0; x < straight.Width; x++)
                {
                    int i = x * 4;
                    if (alphaPixels != IntPtr.Zero)
                    {
                        byte a = rgbRow[i + 3];
                        alphaRow[i] = alphaRow[i + 1] = alphaRow[i + 2] = a;
                        alphaRow[i + 3] = 255;
                    }
                    rgbRow[i + 3] = 255;
                }
            });
            return alpha;
        }
        catch
        {
            alpha?.Dispose();
            throw;
        }
    }

    internal static unsafe SKBitmap Merge(SKBitmap rgb, SKBitmap alpha, CancellationToken ct)
    {
        if (rgb.Width != alpha.Width || rgb.Height != alpha.Height)
            throw new InvalidOperationException("RGB and alpha upscale dimensions must match.");
        var result = new SKBitmap(rgb.Width, rgb.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        try
        {
            var rgbPixels = rgb.GetPixels();
            var alphaPixels = alpha.GetPixels();
            var resultPixels = result.GetPixels();
            int rgbStride = rgb.RowBytes, alphaStride = alpha.RowBytes, resultStride = result.RowBytes;
            ProcessRows(rgb.Width, rgb.Height, ct, y =>
            {
                byte* rgbRow = (byte*)rgbPixels + y * rgbStride;
                byte* alphaRow = (byte*)alphaPixels + y * alphaStride;
                byte* resultRow = (byte*)resultPixels + y * resultStride;
                for (int x = 0; x < rgb.Width; x++)
                {
                    int i = x * 4;
                    resultRow[i] = rgbRow[i];
                    resultRow[i + 1] = rgbRow[i + 1];
                    resultRow[i + 2] = rgbRow[i + 2];
                    // A three-channel model can tint grayscale. Recover coverage with
                    // the RGB-to-gray weights used by Real-ESRGAN's alpha path (OpenCV).
                    // https://github.com/xinntao/Real-ESRGAN/blob/master/realesrgan/utils.py
                    resultRow[i + 3] = (byte)((299 * alphaRow[i] + 587 * alphaRow[i + 1] + 114 * alphaRow[i + 2] + 500) / 1000);
                }
            });
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static void ProcessRows(int width, int height, CancellationToken ct, Action<int> action)
    {
        if ((long)width * height >= 262_144)
            Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, action);
        else
            for (int y = 0; y < height; y++)
            {
                ct.ThrowIfCancellationRequested();
                action(y);
            }
    }
}
