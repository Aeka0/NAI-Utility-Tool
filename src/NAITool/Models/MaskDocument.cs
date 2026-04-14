using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;

namespace NAITool.Models;

/// <summary>
/// 遮罩文档模型：管理原始图片和遮罩 RenderTarget。
/// 单线程设计（UI 线程），不含同步原语。
/// </summary>
public class MaskDocument : IDisposable
{
    private CanvasBitmap? _originalImage;
    private CanvasRenderTarget? _maskTarget;

    public CanvasBitmap? OriginalImage => _originalImage;
    public CanvasRenderTarget? MaskTarget => _maskTarget;
    public Vector2 ImageOffset { get; set; } = Vector2.Zero;
    public Vector2 PixelAlignedImageOffset =>
        new(MathF.Round(ImageOffset.X), MathF.Round(ImageOffset.Y));
    public int CanvasWidth { get; private set; }
    public int CanvasHeight { get; private set; }

    /// <summary>初始化画布（创建空白遮罩 RenderTarget）。</summary>
    public void Initialize(CanvasDevice device, int width, int height)
    {
        CanvasWidth = width;
        CanvasHeight = height;

        _maskTarget?.Dispose();
        _maskTarget = new CanvasRenderTarget(device, width, height, 96f);
        ClearMask();
    }

    /// <summary>清空遮罩为全透明。</summary>
    public void ClearMask()
    {
        if (_maskTarget == null) return;
        using var ds = _maskTarget.CreateDrawingSession();
        ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    /// <summary>设置原始图片并自动居中。</summary>
    public void SetOriginalImage(CanvasBitmap? bitmap, bool preserveImageOffset = false)
    {
        var previousOffset = ImageOffset;
        _originalImage?.Dispose();
        _originalImage = bitmap;

        if (bitmap != null)
        {
            ImageOffset = preserveImageOffset
                ? previousOffset
                : new Vector2(
                    (CanvasWidth - (float)bitmap.SizeInPixels.Width) / 2f,
                    (CanvasHeight - (float)bitmap.SizeInPixels.Height) / 2f);
        }
        else
        {
            ImageOffset = Vector2.Zero;
        }
    }

    /// <summary>获取遮罩快照（像素数据）。</summary>
    public byte[]? GetMaskSnapshot()
    {
        return _maskTarget?.GetPixelBytes();
    }

    /// <summary>恢复遮罩快照。</summary>
    public void RestoreMaskSnapshot(byte[] pixels)
    {
        _maskTarget?.SetPixelBytes(pixels);
    }

    /// <summary>创建合成导出图（原图 + 遮罩通道，API 提交用）。</summary>
    public CanvasRenderTarget? CreateCompositeForExport(CanvasDevice device)
    {
        if (_maskTarget == null) return null;

        var export = new CanvasRenderTarget(device, CanvasWidth, CanvasHeight, 96f);
        using var ds = export.CreateDrawingSession();
        ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));

        if (_originalImage != null)
        {
            ds.DrawImage(_originalImage, PixelAlignedImageOffset);
        }

        return export;
    }

    public void Dispose()
    {
        _maskTarget?.Dispose();
        _originalImage?.Dispose();
    }
}
