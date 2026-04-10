using System.Numerics;

namespace NAITool.Models;

/// <summary>
/// 笔触线段数据，不可变结构体，避免堆分配。
/// </summary>
public readonly record struct StrokeSegment
{
    /// <summary>起始点（画布坐标）</summary>
    public Vector2 From { get; init; }

    /// <summary>终止点（画布坐标）</summary>
    public Vector2 To { get; init; }

    /// <summary>笔刷半径（像素）</summary>
    public float BrushRadius { get; init; }

    /// <summary>绘制工具类型</summary>
    public StrokeTool Tool { get; init; }

    /// <summary>指针压感 (0.0~1.0, 无压感设备为 1.0)</summary>
    public float Pressure { get; init; }

    /// <summary>是否是笔触的第一个点（需要画一个圆点而非线段）</summary>
    public bool IsFirstPoint { get; init; }
}

/// <summary>绘制工具枚举</summary>
public enum StrokeTool : byte
{
    Brush = 0,
    Eraser = 1,
    Rectangle = 2,
}
