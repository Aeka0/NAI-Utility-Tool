using System;
using System.Collections.Generic;
using System.Numerics;

namespace NAITool.Input;

/// <summary>
/// 笔触插值器：使用 Catmull-Rom 样条在采样点之间生成平滑曲线。
/// 
/// 为什么需要插值？
/// - 即使使用 GetIntermediatePoints()，快速笔触仍可能产生间距较大的点
/// - 简单直线连接会导致明显的折线感
/// - Catmull-Rom 样条经过所有控制点，适合手绘笔触
/// 
/// 用法：
/// - 每次 PointerPressed 时调用 Reset()
/// - 每个采样点调用 AddPoint()，获取密集的插值点序列
/// </summary>
public class StrokeInterpolator
{
    // 缓存最近 4 个点用于 Catmull-Rom 计算
    private readonly List<Vector2> _pointBuffer = new(4);

    // 插值密度：每像素距离的细分数
    private const float SubdivisionDensity = 0.15f;

    // 最小细分步数
    private const int MinSubdivisions = 2;

    // 最大细分步数（防止超长线段产生过多点）
    private const int MaxSubdivisions = 64;

    /// <summary>
    /// 重置插值器。每次新笔触开始时调用。
    /// </summary>
    public void Reset()
    {
        _pointBuffer.Clear();
    }

    /// <summary>
    /// 添加一个新的采样点，返回从上一个点到当前点之间的插值点序列。
    /// </summary>
    /// <param name="point">新的采样点（画布坐标）</param>
    /// <returns>插值后的密集点序列（包含终点，不包含起点）</returns>
    public IReadOnlyList<Vector2> AddPoint(Vector2 point)
    {
        _pointBuffer.Add(point);
        var result = new List<Vector2>();

        switch (_pointBuffer.Count)
        {
            case 1:
                // 第一个点，直接返回
                result.Add(point);
                break;

            case 2:
                // 只有两个点，线性插值
                InterpolateLinear(_pointBuffer[0], _pointBuffer[1], result);
                break;

            case 3:
                // 三个点，用镜像第一个点构造 Catmull-Rom
                var p0Mirror = 2 * _pointBuffer[0] - _pointBuffer[1];
                InterpolateCatmullRom(p0Mirror, _pointBuffer[0],
                                       _pointBuffer[1], _pointBuffer[2],
                                       result);
                break;

            default:
                // >=4 个点，标准 Catmull-Rom
                int n = _pointBuffer.Count;
                InterpolateCatmullRom(
                    _pointBuffer[n - 4], _pointBuffer[n - 3],
                    _pointBuffer[n - 2], _pointBuffer[n - 1],
                    result);

                // 保留最近 4 个点，释放更早的内存
                if (_pointBuffer.Count > 8)
                {
                    _pointBuffer.RemoveRange(0, _pointBuffer.Count - 4);
                }
                break;
        }

        return result;
    }

    /// <summary>
    /// 结束笔触时调用，生成最后一段的插值点。
    /// </summary>
    public IReadOnlyList<Vector2> Finish()
    {
        var result = new List<Vector2>();

        if (_pointBuffer.Count >= 3)
        {
            int n = _pointBuffer.Count;
            // 镜像最后一个点作为 P3
            var pnMirror = 2 * _pointBuffer[n - 1] - _pointBuffer[n - 2];
            InterpolateCatmullRom(
                _pointBuffer[n - 3], _pointBuffer[n - 2],
                _pointBuffer[n - 1], pnMirror,
                result);
        }

        Reset();
        return result;
    }

    /// <summary>
    /// Catmull-Rom 样条插值。
    /// 在 P1 和 P2 之间生成平滑曲线点。
    /// </summary>
    private static void InterpolateCatmullRom(
        Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3,
        List<Vector2> output)
    {
        float distance = Vector2.Distance(p1, p2);
        int subdivisions = Math.Clamp(
            (int)(distance * SubdivisionDensity),
            MinSubdivisions, MaxSubdivisions);

        float step = 1.0f / subdivisions;

        for (int i = 1; i <= subdivisions; i++)
        {
            float t = i * step;
            float t2 = t * t;
            float t3 = t2 * t;

            // Catmull-Rom 矩阵乘法
            // Q(t) = 0.5 * ((2*P1) + (-P0+P2)*t + (2*P0-5*P1+4*P2-P3)*t² + (-P0+3*P1-3*P2+P3)*t³)
            Vector2 result = 0.5f * (
                (2f * p1) +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3);

            output.Add(result);
        }
    }

    /// <summary>
    /// 简单线性插值（仅在只有两个点时使用）。
    /// </summary>
    private static void InterpolateLinear(Vector2 from, Vector2 to, List<Vector2> output)
    {
        float distance = Vector2.Distance(from, to);
        int subdivisions = Math.Clamp(
            (int)(distance * SubdivisionDensity),
            MinSubdivisions, MaxSubdivisions);

        float step = 1.0f / subdivisions;

        for (int i = 1; i <= subdivisions; i++)
        {
            float t = i * step;
            output.Add(Vector2.Lerp(from, to, t));
        }
    }
}
