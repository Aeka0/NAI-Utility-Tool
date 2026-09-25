using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using NAITool.Models;
using Windows.Foundation;

namespace NAITool.Controls;

public sealed partial class MaskCanvasControl
{
    private bool _isMaskMoving;
    private byte[]? _maskMoveSnapshot;
    private Vector2 _maskMoveStart;
    private Vector2 _maskMoveOffset;
    private uint _maskMovePointerId;
    private int _maskMoveLockedAxis;
    private readonly InputCursor _maskMoveCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);

    private void BeginMaskMove(Vector2 canvasPosition, PointerRoutedEventArgs e)
    {
        if (!CanMoveImage || !IsMaskEditingEnabled || _canvas == null) return;
        if (canvasPosition.X < 0 || canvasPosition.Y < 0 ||
            canvasPosition.X >= _canvasWidth || canvasPosition.Y >= _canvasHeight) return;

        lock (_renderLock)
        {
            FlushPendingMaskStrokes();
            var pixels = _document.GetMaskSnapshot();
            if (pixels == null) return;
            bool hasContent = false;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] == 0) continue;
                hasContent = true;
                break;
            }
            if (!hasContent) return;

            _maskMoveSnapshot = pixels;
            _maskMoveStart = canvasPosition;
            _maskMoveOffset = Vector2.Zero;
            _maskMovePointerId = e.Pointer.PointerId;
            _maskMoveLockedAxis = 0;
            _isMaskMoving = true;
        }
        if (!_canvas.CapturePointer(e.Pointer)) CancelMaskMove();
        e.Handled = true;
    }

    private void UpdateMaskMove(PointerRoutedEventArgs e)
    {
        if (_canvas == null || e.Pointer.PointerId != _maskMovePointerId) return;
        var point = e.GetCurrentPoint(_canvas);
        Vector2 position;
        lock (_stateLock)
            position = _viewTransform.ScreenToCanvas(new Vector2((float)point.Position.X, (float)point.Position.Y));

        Vector2 delta = position - _maskMoveStart;
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shift)
        {
            if (_maskMoveLockedAxis == 0 && Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y)) >= 3)
                _maskMoveLockedAxis = Math.Abs(delta.X) >= Math.Abs(delta.Y) ? 1 : 2;
            if (_maskMoveLockedAxis == 1) delta.Y = 0;
            else if (_maskMoveLockedAxis == 2) delta.X = 0;
            else delta = Vector2.Zero;
        }
        else _maskMoveLockedAxis = 0;
        lock (_renderLock)
            _maskMoveOffset = new Vector2(
                Math.Clamp(MathF.Round(delta.X), -_canvasWidth, _canvasWidth),
                Math.Clamp(MathF.Round(delta.Y), -_canvasHeight, _canvasHeight));
        e.Handled = true;
    }

    private void CompleteMaskMove(PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _maskMovePointerId) return;
        UpdateMaskMove(e);
        bool changed = false;
        try
        {
            lock (_renderLock)
            {
                if (_maskMoveSnapshot != null && _maskMoveOffset != Vector2.Zero && _document.MaskTarget != null)
                {
                    var moved = MaskTranslation.Translate(_maskMoveSnapshot, _canvasWidth, _canvasHeight,
                        (int)_maskMoveOffset.X, (int)_maskMoveOffset.Y);
                    if (!moved.AsSpan().SequenceEqual(_maskMoveSnapshot))
                    {
                        _document.RestoreMaskSnapshot(moved);
                        _undoManager.PushState(_maskMoveSnapshot, _document.ImageOffset, _canvasWidth, _canvasHeight);
                        changed = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke(Services.LocalizationService.Instance.Format("inpaint.error.move_mask_failed", ex.Message));
        }
        finally
        {
            CancelMaskMove();
        }
        if (changed) ContentChanged?.Invoke();
        e.Handled = true;
    }

    /// <summary>The document is unchanged until release, so cancellation needs no restore or undo entry.</summary>
    public bool CancelMaskMove()
    {
        bool wasMoving;
        lock (_renderLock)
        {
            wasMoving = _isMaskMoving;
            _isMaskMoving = false;
            _maskMoveSnapshot = null;
            _maskMoveOffset = Vector2.Zero;
            _maskMovePointerId = 0;
            _maskMoveLockedAxis = 0;
        }
        if (wasMoving) _canvas?.ReleasePointerCaptures();
        return wasMoving;
    }

    private void OnMaskMoveCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isMaskMoving && e.Pointer.PointerId == _maskMovePointerId)
            CancelMaskMove();
    }

    // Called under _renderLock by both the main canvas and the thumbnail.
    private void DrawMovingMask(CanvasDrawingSession ds, ICanvasImage mask)
    {
        using var clip = ds.CreateLayer(1, new Rect(0, 0, _canvasWidth, _canvasHeight));
        ds.DrawImage(mask, _isMaskMoving ? _maskMoveOffset : Vector2.Zero);
    }

    // Finish queued brush input before taking a move snapshot, even if the render loop has not run yet.
    private bool FlushPendingMaskStrokes()
    {
        if (_strokeQueue.IsEmpty || _document.MaskTarget == null) return false;
        using var ds = _document.MaskTarget.CreateDrawingSession();
        bool changed = false;
        while (_strokeQueue.TryDequeue(out var segment))
        {
            Rendering.BrushStampRenderer.DrawSegment(ds, segment);
            changed = true;
        }
        return changed;
    }
}
