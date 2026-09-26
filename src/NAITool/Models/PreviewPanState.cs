using System;

namespace NAITool.Models;

/// <summary>Keeps only the latest pointer position until the next preview frame.</summary>
public sealed class PreviewPanState
{
    private double _startX, _startY, _startHorizontal, _startVertical;
    private double _pendingHorizontal, _pendingVertical, _lastHorizontal, _lastVertical;
    private bool _pending;

    public bool IsActive { get; private set; }

    public void Begin(double x, double y, double horizontal, double vertical)
    {
        _startX = x;
        _startY = y;
        _startHorizontal = _lastHorizontal = horizontal;
        _startVertical = _lastVertical = vertical;
        _pending = false;
        IsActive = true;
    }

    public void Move(double x, double y)
    {
        if (!IsActive || !double.IsFinite(x) || !double.IsFinite(y)) return;
        _pendingHorizontal = _startHorizontal + _startX - x;
        _pendingVertical = _startVertical + _startY - y;
        _pending = true;
    }

    public bool TryTakeOffsets(double maxHorizontal, double maxVertical, out double horizontal, out double vertical)
    {
        horizontal = vertical = 0;
        if (!IsActive || !_pending) return false;
        _pending = false;
        horizontal = Math.Clamp(_pendingHorizontal, 0, Math.Max(0, maxHorizontal));
        vertical = Math.Clamp(_pendingVertical, 0, Math.Max(0, maxVertical));
        if (horizontal == _lastHorizontal && vertical == _lastVertical) return false;
        _lastHorizontal = horizontal;
        _lastVertical = vertical;
        return true;
    }

    public void End()
    {
        IsActive = false;
        _pending = false;
    }
}
