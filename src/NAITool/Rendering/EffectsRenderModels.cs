using SkiaSharp;
using Microsoft.UI.Xaml.Media;

namespace NAITool.Rendering;

public sealed class EffectsPreviewRenderResult : System.IDisposable
{
    public SKBitmap? Bitmap { get; init; }
    public ImageSource? ImageSource { get; init; }
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public bool UsedCpuFallback { get; init; }
    public string? FallbackReason { get; init; }

    public void Dispose() => Bitmap?.Dispose();
}

public sealed class EffectsPngRenderResult
{
    public required byte[] Bytes { get; init; }
    public bool UsedCpuFallback { get; init; }
    public string? FallbackReason { get; init; }
}
