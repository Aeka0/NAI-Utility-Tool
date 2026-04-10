using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using NAITool.Models;
using Windows.UI;

namespace NAITool.Rendering;

/// <summary>
/// 笔触印章渲染器：将 StrokeSegment 绘制到遮罩 RenderTarget 上。
/// 
/// 在渲染线程上调用，使用 Win2D GPU 加速绘制。
/// 
/// 绘制策略：
/// - 第一个点：绘制实心圆
/// - 后续点：绘制圆头线段连接上一点和当前点
/// - 橡皮擦：使用 CanvasBlend.Copy 写入透明像素
/// </summary>
public static class BrushStampRenderer
{
    private static readonly CanvasStrokeStyle RoundStrokeStyle = new()
    {
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round,
        LineJoin = CanvasLineJoin.Round,
    };

    // 笔刷颜色：白色不透明 = 遮罩区域
    private static readonly Color BrushColor = Color.FromArgb(255, 255, 255, 255);

    // 橡皮颜色：完全透明 = 擦除
    private static readonly Color EraserColor = Color.FromArgb(0, 0, 0, 0);

    /// <summary>
    /// 将一个笔触线段绘制到遮罩 RenderTarget 上。
    /// </summary>
    public static void DrawSegment(CanvasDrawingSession ds, in StrokeSegment segment)
    {
        Color color;
        bool isEraser = segment.Tool == StrokeTool.Eraser;

        if (isEraser)
        {
            color = EraserColor;
            ds.Blend = CanvasBlend.Copy;
        }
        else
        {
            color = BrushColor;
        }

        float radius = segment.BrushRadius * segment.Pressure;
        float diameter = radius * 2f;

        if (segment.IsFirstPoint)
        {
            ds.FillEllipse(segment.From, radius, radius, color);
        }
        else
        {
            ds.DrawLine(segment.From, segment.To, color, diameter, RoundStrokeStyle);
        }

        if (isEraser)
        {
            ds.Blend = CanvasBlend.SourceOver;
        }
    }
}
