using System;

namespace NAITool.Models;

/// <summary>Pixel-aligned mask translation. Clipped pixels are discarded, never wrapped.</summary>
internal static class MaskTranslation
{
    internal static byte[] Translate(byte[] source, int width, int height, int dx, int dy)
    {
        if (width <= 0 || height <= 0 || source.Length % 4 != 0 ||
            (long)width * height != source.LongLength / 4)
            throw new ArgumentException("Mask dimensions do not match its pixel buffer.");

        var result = new byte[source.Length];
        // Check before negating offsets, including int.MinValue.
        if (dx <= -width || dx >= width || dy <= -height || dy >= height)
            return result;

        int sourceX = Math.Max(0, -dx);
        int sourceY = Math.Max(0, -dy);
        int targetX = Math.Max(0, dx);
        int targetY = Math.Max(0, dy);
        int rowBytes = (width - Math.Abs(dx)) * 4;
        int rows = height - Math.Abs(dy);
        int stride = width * 4;
        for (int row = 0; row < rows; row++)
            Buffer.BlockCopy(source, (sourceY + row) * stride + sourceX * 4,
                result, (targetY + row) * stride + targetX * 4, rowBytes);

        return result;
    }
}
