using System;
using System.Numerics;

namespace NAITool.Models;

/// <summary>
/// 视图变换：管理画布的缩放和平移。
/// 所有缩放/平移操作都通过修改此变换矩阵实现，
/// 避免对像素数据做任何 CPU 端重采样。
/// </summary>
public class ViewTransform
{
    public const float MinScale = 0.1f;
    public const float MaxScale = 10.0f;

    public float Scale { get; private set; } = 1.0f;
    public Vector2 Offset { get; private set; } = Vector2.Zero;

    public Matrix3x2 GetMatrix()
    {
        return Matrix3x2.CreateScale(Scale) *
               Matrix3x2.CreateTranslation(Offset);
    }

    public Vector2 ScreenToCanvas(Vector2 screenPos)
    {
        Matrix3x2.Invert(GetMatrix(), out var inverse);
        return Vector2.Transform(screenPos, inverse);
    }

    public Vector2 CanvasToScreen(Vector2 canvasPos)
    {
        return Vector2.Transform(canvasPos, GetMatrix());
    }

    public void ZoomAt(Vector2 screenCenter, float factor)
    {
        var canvasPoint = ScreenToCanvas(screenCenter);
        Scale = Math.Clamp(Scale * factor, MinScale, MaxScale);
        Offset = screenCenter - canvasPoint * Scale;
    }

    public void Pan(Vector2 screenDelta)
    {
        Offset += screenDelta;
    }

    public void FitToView(float canvasW, float canvasH,
                          float viewW, float viewH)
    {
        if (canvasW <= 0 || canvasH <= 0 || viewW <= 0 || viewH <= 0) return;
        Scale = Math.Min(viewW / canvasW, viewH / canvasH);
        Offset = new Vector2(
            (viewW - canvasW * Scale) / 2f,
            (viewH - canvasH * Scale) / 2f);
    }

    public void ResetToActualSize(float canvasW, float canvasH,
                                  float viewW, float viewH,
                                  float dpiScale = 1.0f)
    {
        Scale = 1.0f / dpiScale;
        Offset = new Vector2(
            (viewW - canvasW * Scale) / 2f,
            (viewH - canvasH * Scale) / 2f);
    }

    /// <summary>保持当前缩放级别，将画布居中到视口。</summary>
    public void CenterInView(float canvasW, float canvasH,
                             float viewW, float viewH)
    {
        Offset = new Vector2(
            (viewW - canvasW * Scale) / 2f,
            (viewH - canvasH * Scale) / 2f);
    }
}
