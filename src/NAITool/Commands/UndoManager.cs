using System;
using System.Collections.Generic;
using System.Numerics;

namespace NAITool.Commands;

/// <summary>
/// 遮罩撤销/重做管理器。
/// 
/// 存储策略：全量快照（GPU → CPU 像素数据）。
/// 
/// 为什么选择全量快照而非命令回放：
/// 1. GPU→CPU 拷贝 1024×1024 ≈ 1ms，可以接受
/// 2. 恢复是 O(1) 而非 O(N) 回放
/// 3. 实现简单，不易出错
/// 4. 内存开销可控（50 步 × 4MB ≈ 200MB，可后续优化为增量）
/// 
/// 线程说明：
/// - SaveState() 在渲染线程上调用（获取 GPU 快照）
/// - Undo()/Redo() 在 UI 线程上调用，返回快照数据
/// - 快照通过渲染信号传递给渲染线程执行恢复
/// </summary>
public class UndoManager
{
    /// <summary>遮罩快照</summary>
    public record struct MaskSnapshot(byte[] MaskPixels, Vector2 ImageOffset, int CanvasWidth, int CanvasHeight);

    private readonly Stack<MaskSnapshot> _undoStack = new();
    private readonly Stack<MaskSnapshot> _redoStack = new();
    private readonly int _maxSteps;

    /// <summary>是否可以撤销</summary>
    public bool CanUndo => _undoStack.Count > 0;

    /// <summary>是否可以重做</summary>
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>撤销步数</summary>
    public int UndoCount => _undoStack.Count;

    /// <summary>重做步数</summary>
    public int RedoCount => _redoStack.Count;

    public UndoManager(int maxSteps = 50)
    {
        _maxSteps = maxSteps;
    }

    /// <summary>
    /// 保存当前状态到撤销栈。
    /// 在渲染线程调用（因为需要从 GPU 读取像素数据）。
    /// </summary>
    public void PushState(byte[] maskPixels, Vector2 imageOffset, int canvasWidth, int canvasHeight)
    {
        // 限制栈深度，释放最老的快照
        if (_undoStack.Count >= _maxSteps)
        {
            // Stack 不支持直接移除底部，使用数组重建
            var items = _undoStack.ToArray();
            _undoStack.Clear();
            // 保留最近的 maxSteps-1 个（ToArray 返回的是栈顶到栈底的顺序）
            for (int i = Math.Min(items.Length - 1, _maxSteps - 2); i >= 0; i--)
            {
                _undoStack.Push(items[i]);
            }
        }

        _undoStack.Push(new MaskSnapshot(maskPixels, imageOffset, canvasWidth, canvasHeight));
        _redoStack.Clear(); // 新操作清空重做栈
    }

    /// <summary>
    /// 撤销：弹出上一个状态。
    /// 调用者需要先保存当前状态到 redo 栈，再应用返回的快照。
    /// </summary>
    /// <param name="currentMaskPixels">当前遮罩像素（将推入重做栈）</param>
    /// <param name="currentImageOffset">当前图片偏移</param>
    /// <returns>需要恢复到的快照，或 null（无法撤销）</returns>
    public MaskSnapshot? Undo(byte[] currentMaskPixels, Vector2 currentImageOffset, int currentCanvasWidth, int currentCanvasHeight)
    {
        if (_undoStack.Count == 0) return null;

        _redoStack.Push(new MaskSnapshot(currentMaskPixels, currentImageOffset, currentCanvasWidth, currentCanvasHeight));
        return _undoStack.Pop();
    }

    /// <summary>
    /// 重做：弹出下一个状态。
    /// </summary>
    public MaskSnapshot? Redo(byte[] currentMaskPixels, Vector2 currentImageOffset, int currentCanvasWidth, int currentCanvasHeight)
    {
        if (_redoStack.Count == 0) return null;

        _undoStack.Push(new MaskSnapshot(currentMaskPixels, currentImageOffset, currentCanvasWidth, currentCanvasHeight));
        return _redoStack.Pop();
    }

    /// <summary>
    /// 清空所有历史记录。
    /// </summary>
    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }
}
