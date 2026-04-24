using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NAITool.Commands;
using NAITool.Input;
using NAITool.Models;
using NAITool.Rendering;
using NAITool.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.UI;
using Colors = Microsoft.UI.Colors;
using WinRT;

namespace NAITool.Controls;

/// <summary>
/// 遮罩绘制画布控件。
/// </summary>
public sealed partial class MaskCanvasControl : UserControl
{
    private MaskDocument _document = new();
    private readonly ViewTransform _viewTransform = new();
    private readonly BrushSettings _brushSettings = new();
    private readonly UndoManager _undoManager = new();
    private readonly StrokeInterpolator _interpolator = new();

    private CanvasAnimatedControl? _canvas;
    private CanvasDevice? _device;
    private bool _resourcesReady;
    private CanvasRenderTarget? _checkerboardCache;
    private ColorMatrixEffect? _maskOverlayEffect;
    private ColorMatrixEffect? _thumbnailMaskOverlayEffect;

    private bool _isDrawing;
    private readonly object _stateLock = new();
    private readonly object _renderLock = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<StrokeSegment> _strokeQueue = new();
    private bool _showCursor;

    private bool _isRectDrawing;
    private Vector2 _rectStartCanvas;
    private Vector2 _rectCurrentCanvas;

    private bool _isPanning;
    private Vector2 _panStartScreenPos;

    private bool _isImageDragging;
    private Vector2 _imageDragStartPos;
    private Vector2 _imageDragOriginOffset;
    private Vector2 _imageDragAccumDelta;
    private int _imageDragLockedAxis; // 0=未锁定, 1=X, 2=Y
    private long _lastMiddleClickTicks;

    private CanvasBitmap? _previewBitmap;
    private bool _isComparing;
    private string? _loadedFilePath;
    private CanvasBitmap? _cleanOriginalImage;
    private Vector2 _cleanImageOffset;
    private int _cleanCanvasWidth;
    private int _cleanCanvasHeight;
    private byte[]? _cleanMaskPixels;
    private InputCursor? _toolCursor;
    private nint _toolCursorHandle;
    private int _toolCursorSize;
    private int _toolCursorRadiusPx;
    private StrokeTool _toolCursorTool;
    private InputCursor? _appliedCursor;
    private readonly List<nint> _retiredToolCursorHandles = [];

    private bool _previewMaskOnly;
    private int _canvasWidth = 832;
    private int _canvasHeight = 1216;
    private volatile float _cachedControlWidth;
    private volatile float _cachedControlHeight;
    private const int CanvasSizeStep = 64;
    private const int ToolCursorMinSize = 32;
    private const int ToolCursorMaxSize = 256;
    private const int ToolCursorSizeStep = 32;
    private const int AssetProtectionMinMatchedSide = 512;
    private const int AssetProtectionMaxMatchedSide = 2048;
    private const long AssetProtectionMaxPixels = 1024L * 1024L;
    private const int CheckerCellSize = 16;
    private const float BackgroundPulseBrightnessDelta = 20f;
    private const double BackgroundPulsePeriodSeconds = 2.4;
    private const double BackgroundPulseFadeSeconds = 0.28;
    private static readonly Color CheckerLightColor = Color.FromArgb(255, 204, 204, 204);
    private static readonly Color CheckerDarkColor = Color.FromArgb(255, 170, 170, 170);
    private static readonly Matrix5x4 MaskOverlayMatrix = new()
    {
        M11 = 0, M12 = 0, M13 = 0, M14 = 0,
        M21 = 0, M22 = 0, M23 = 0, M24 = 0,
        M31 = 0, M32 = 0, M33 = 0, M34 = 0,
        M41 = 0, M42 = 0, M43 = 0, M44 = 0.5f,
        M51 = 1, M52 = 0.2f, M53 = 0.2f, M54 = 0,
    };

    private static readonly Color CanvasAreaTint = Color.FromArgb(220, 28, 28, 32);
    private double _backgroundPulseAmount;
    private bool _backgroundPulseDesired;
    private double _backgroundPulseFade;
    private double _backgroundPulseLastFrameSeconds;
    private readonly System.Diagnostics.Stopwatch _backgroundPulseClock = new();

    public static readonly (int W, int H, string Label)[] CanvasPresets =
    [
        (1024, 1024, "1024 × 1024"),
        (1088, 960,  "1088 × 960"),
        (1152, 896,  "1152 × 896"),
        (1216, 832,  "1216 × 832"),
        (1344, 768,  "1344 × 768"),
        (1472, 704,  "1472 × 704"),
        (1600, 640,  "1600 × 640"),
        (960, 1088,  "960 × 1088"),
        (896, 1152,  "896 × 1152"),
        (832, 1216,  "832 × 1216"),
        (768, 1344,  "768 × 1344"),
        (704, 1472,  "704 × 1472"),
        (640, 1600,  "640 × 1600"),
    ];

    private float DpiScale => _canvas?.Dpi / 96.0f ?? 1.0f;

    public BrushSettings Brush => _brushSettings;
    public ViewTransform View => _viewTransform;
    public MaskDocument Document => _document;
    public UndoManager UndoMgr => _undoManager;
    public int CanvasW => _canvasWidth;
    public int CanvasH => _canvasHeight;
    public bool PreviewMaskOnly
    {
        get => _previewMaskOnly;
        set { _previewMaskOnly = value; }
    }

    public bool IsActivelyDrawing => _isDrawing || _isRectDrawing || _isPanning || _isImageDragging;
    public bool IsInPreviewMode => _previewBitmap != null;
    public bool IsImageFileDropEnabled { get; set; } = true;
    private bool _isImageMoveLocked;
    public bool IsImageMoveLocked
    {
        get => _isImageMoveLocked;
        set
        {
            if (_isImageMoveLocked == value) return;
            _isImageMoveLocked = value;
            if (value)
                _isImageDragging = false;
        }
    }
    public bool CanMoveImage => !_isImageMoveLocked && !IsInPreviewMode;
    private bool _isMaskEditingEnabled = true;
    public bool IsMaskEditingEnabled
    {
        get => _isMaskEditingEnabled;
        set
        {
            if (_isMaskEditingEnabled == value) return;
            _isMaskEditingEnabled = value;
            ApplySystemCursorVisibility();
        }
    }
    public bool IsMaskOverlayVisible { get; set; } = true;
    public bool UseAssetProtectionCanvasSizing { get; set; } = true;

    public void SetBackgroundPulseActive(bool active)
    {
        _backgroundPulseDesired = active;
        if (active && !_backgroundPulseClock.IsRunning)
            _backgroundPulseClock.Start();
        _backgroundPulseLastFrameSeconds = _backgroundPulseClock.Elapsed.TotalSeconds;
        if (_canvas != null)
            _canvas.Paused = false;
    }

    /// <summary>导入图片的原始文件路径（用于"保存"覆盖写回）。</summary>
    public string? LoadedFilePath => _loadedFilePath;

    public void SetLoadedFilePath(string? filePath)
    {
        _loadedFilePath = !string.IsNullOrWhiteSpace(filePath) ? filePath : null;
    }

    /// <summary>对比模式：按住时临时隐藏预览结果，显示底图。</summary>
    public bool IsComparing
    {
        get => _isComparing;
        set { _isComparing = value; }
    }

    public event Action<float>? ZoomChanged;
    public event Action? ContentChanged;
    public event Action<string>? StatusMessage;
    public event Action<string>? ImageFileLoaded;

    public static (int W, int H) ResolveImportCanvasSize(int imageWidth, int imageHeight, bool assetProtectionMode)
    {
        int imgW = Math.Max(1, imageWidth);
        int imgH = Math.Max(1, imageHeight);

        if (!assetProtectionMode)
            return (RoundUpToMultipleOf64(imgW), RoundUpToMultipleOf64(imgH));

        foreach (var preset in CanvasPresets)
        {
            if (imgW == preset.W && imgH == preset.H)
                return (preset.W, preset.H);
        }

        if (TryResolveAssetProtectionMatchedEdge(imgW, imgH, out var matchedEdgeSize))
            return matchedEdgeSize;

        if ((long)imgW * imgH > AssetProtectionMaxPixels &&
            TrySelectContainedPreset(imgW, imgH, out var containedLargeSize))
            return containedLargeSize;

        if (TrySelectCoveringPreset(imgW, imgH, out var coveringSize))
            return coveringSize;

        if (TrySelectContainedPreset(imgW, imgH, out var containedSize))
            return containedSize;

        return ResolveAssetProtectionFallbackSize(imgW, imgH);
    }

    private static int RoundUpToMultipleOf64(int value)
    {
        value = Math.Max(1, value);
        return Math.Max(CanvasSizeStep, ((value + CanvasSizeStep - 1) / CanvasSizeStep) * CanvasSizeStep);
    }

    private static int RoundDownToMultipleOf64(int value)
    {
        value = Math.Max(1, value);
        return Math.Max(CanvasSizeStep, (value / CanvasSizeStep) * CanvasSizeStep);
    }

    private static bool IsAssetProtectionMatchedSide(int value) =>
        value >= AssetProtectionMinMatchedSide &&
        value <= AssetProtectionMaxMatchedSide &&
        value % CanvasSizeStep == 0;

    private static bool TryResolveAssetProtectionMatchedEdge(int imgW, int imgH, out (int W, int H) size)
    {
        bool found = false;
        size = default;
        (int W, int H) bestSize = default;
        double imageRatio = (double)imgW / imgH;
        double bestSquareScore = double.MaxValue;
        double bestRatioScore = double.MaxValue;

        void AddCandidate(int w, int h)
        {
            if ((long)w * h > AssetProtectionMaxPixels)
                return;

            double candidateRatio = (double)w / h;
            double squareScore = Math.Abs(Math.Log(candidateRatio));
            double ratioScore = Math.Abs(Math.Log(candidateRatio / imageRatio));
            if (!found ||
                squareScore < bestSquareScore - 0.000001 ||
                (Math.Abs(squareScore - bestSquareScore) <= 0.000001 && ratioScore < bestRatioScore))
            {
                found = true;
                bestSize = (w, h);
                bestSquareScore = squareScore;
                bestRatioScore = ratioScore;
            }
        }

        if (IsAssetProtectionMatchedSide(imgW))
            AddCandidate(imgW, ResolveOtherAssetProtectionSide(imgH, imgW));

        if (IsAssetProtectionMatchedSide(imgH))
            AddCandidate(ResolveOtherAssetProtectionSide(imgW, imgH), imgH);

        size = bestSize;
        return found;
    }

    private static int ResolveOtherAssetProtectionSide(int originalOtherSide, int matchedSide)
    {
        int maxOtherSide = RoundDownToMultipleOf64((int)(AssetProtectionMaxPixels / matchedSide));
        int preferred = RoundUpToMultipleOf64(originalOtherSide);
        return Math.Clamp(preferred, CanvasSizeStep, maxOtherSide);
    }

    private static bool TrySelectContainedPreset(int imgW, int imgH, out (int W, int H) size)
    {
        bool found = false;
        size = default;
        double imageRatio = (double)imgW / imgH;
        double bestSquareScore = double.MaxValue;
        double bestRatioScore = double.MaxValue;

        foreach (var preset in CanvasPresets)
        {
            if (preset.W >= imgW || preset.H >= imgH)
                continue;

            double presetRatio = (double)preset.W / preset.H;
            double squareScore = Math.Abs(Math.Log(presetRatio));
            double ratioScore = Math.Abs(Math.Log(presetRatio / imageRatio));
            if (!found ||
                squareScore < bestSquareScore - 0.000001 ||
                (Math.Abs(squareScore - bestSquareScore) <= 0.000001 && ratioScore < bestRatioScore))
            {
                found = true;
                size = (preset.W, preset.H);
                bestSquareScore = squareScore;
                bestRatioScore = ratioScore;
            }
        }

        return found;
    }

    private static bool TrySelectCoveringPreset(int imgW, int imgH, out (int W, int H) size)
    {
        bool found = false;
        size = default;
        double imageRatio = (double)imgW / imgH;
        double bestRatioScore = double.MaxValue;
        long bestOvershoot = long.MaxValue;

        foreach (var preset in CanvasPresets)
        {
            if (preset.W < imgW || preset.H < imgH)
                continue;

            double presetRatio = (double)preset.W / preset.H;
            double ratioScore = Math.Abs(Math.Log(presetRatio / imageRatio));
            long overshoot = (long)(preset.W - imgW) * (preset.H - imgH);
            if (!found ||
                ratioScore < bestRatioScore - 0.000001 ||
                (Math.Abs(ratioScore - bestRatioScore) <= 0.000001 && overshoot < bestOvershoot))
            {
                found = true;
                size = (preset.W, preset.H);
                bestRatioScore = ratioScore;
                bestOvershoot = overshoot;
            }
        }

        return found;
    }

    private static (int W, int H) ResolveAssetProtectionFallbackSize(int imgW, int imgH)
    {
        int coverW = Math.Min(AssetProtectionMaxMatchedSide, RoundUpToMultipleOf64(imgW));
        int coverH = Math.Min(AssetProtectionMaxMatchedSide, RoundUpToMultipleOf64(imgH));
        if ((long)coverW * coverH <= AssetProtectionMaxPixels)
            return (coverW, coverH);

        double scale = Math.Sqrt(AssetProtectionMaxPixels / Math.Max(1d, (double)imgW * imgH));
        scale = Math.Min(scale, (double)AssetProtectionMaxMatchedSide / imgW);
        scale = Math.Min(scale, (double)AssetProtectionMaxMatchedSide / imgH);

        int fitW = RoundDownToMultipleOf64((int)Math.Floor(imgW * scale));
        int fitH = RoundDownToMultipleOf64((int)Math.Floor(imgH * scale));
        while ((long)fitW * fitH > AssetProtectionMaxPixels)
        {
            if (fitW >= fitH && fitW > CanvasSizeStep)
                fitW -= CanvasSizeStep;
            else if (fitH > CanvasSizeStep)
                fitH -= CanvasSizeStep;
            else
                break;
        }

        return (fitW, fitH);
    }

    private static Color ApplyBackgroundPulse(Color color, double amount)
    {
        if (Math.Abs(amount) < 0.001)
            return color;

        float delta = (float)(amount * BackgroundPulseBrightnessDelta);
        return Color.FromArgb(
            color.A,
            (byte)Math.Clamp(color.R + delta, 0f, 255f),
            (byte)Math.Clamp(color.G + delta, 0f, 255f),
            (byte)Math.Clamp(color.B + delta, 0f, 255f));
    }

    private void UpdateBackgroundPulse()
    {
        if (!_backgroundPulseDesired && _backgroundPulseFade <= 0)
        {
            _backgroundPulseAmount = 0;
            return;
        }

        if (!_backgroundPulseClock.IsRunning)
            _backgroundPulseClock.Start();

        double now = _backgroundPulseClock.Elapsed.TotalSeconds;
        double deltaSeconds = now - _backgroundPulseLastFrameSeconds;
        _backgroundPulseLastFrameSeconds = now;
        if (deltaSeconds <= 0 || deltaSeconds > 0.25)
            deltaSeconds = 1d / 60d;

        double fadeTarget = _backgroundPulseDesired ? 1d : 0d;
        double fadeStep = deltaSeconds / BackgroundPulseFadeSeconds;
        if (_backgroundPulseFade < fadeTarget)
            _backgroundPulseFade = Math.Min(fadeTarget, _backgroundPulseFade + fadeStep);
        else if (_backgroundPulseFade > fadeTarget)
            _backgroundPulseFade = Math.Max(fadeTarget, _backgroundPulseFade - fadeStep);

        double wave = Math.Sin(now * Math.Tau / BackgroundPulsePeriodSeconds);
        _backgroundPulseAmount = _backgroundPulseFade * wave;
    }

    public MaskCanvasControl()
    {
        this.InitializeComponent();

        _device = CanvasDevice.GetSharedDevice();

        _canvas = new CanvasAnimatedControl();
        _canvas.IsFixedTimeStep = false;
        _canvas.CustomDevice = _device;
        _canvas.CreateResources += OnCreateResources;
        _canvas.Draw += OnCanvasDraw;
        _canvas.Update += OnCanvasUpdate;
        RootGrid.Children.Add(_canvas);

        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerEntered += OnPointerEntered;
        _canvas.PointerExited += OnPointerExited;
        _canvas.PointerWheelChanged += OnPointerWheelChanged;

        this.Unloaded += OnUnloaded;
    }

    private void OnCreateResources(CanvasAnimatedControl sender, CanvasCreateResourcesEventArgs args)
    {
        args.TrackAsyncAction(CreateResourcesAsync(sender).AsAsyncAction());
    }

    private Task CreateResourcesAsync(CanvasAnimatedControl sender)
    {
        _document.Initialize(_device!, _canvasWidth, _canvasHeight);
        RegenerateCheckerboard();
        _maskOverlayEffect = new ColorMatrixEffect { ColorMatrix = MaskOverlayMatrix };
        _thumbnailMaskOverlayEffect = new ColorMatrixEffect { ColorMatrix = MaskOverlayMatrix };
        _resourcesReady = true;
        
        DispatcherQueue.TryEnqueue(() => FitToScreen());
        return Task.CompletedTask;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _resourcesReady = false;
        RestoreSystemCursor();
        if (_canvas != null)
        {
            _canvas.RemoveFromVisualTree();
            _canvas = null;
        }
        _checkerboardCache?.Dispose();
        _checkerboardCache = null;
        _maskOverlayEffect?.Dispose();
        _maskOverlayEffect = null;
        _thumbnailMaskOverlayEffect?.Dispose();
        _thumbnailMaskOverlayEffect = null;
        _previewBitmap = null;
        foreach (var retiredHandle in _retiredToolCursorHandles)
        {
            if (retiredHandle != 0)
                DestroyCursor(retiredHandle);
        }
        _retiredToolCursorHandles.Clear();
        if (_toolCursorHandle != 0)
        {
            DestroyCursor(_toolCursorHandle);
            _toolCursorHandle = 0;
        }
        _document.Dispose();
    }


    private void RegenerateCheckerboard()
    {
        _checkerboardCache?.Dispose();
        if (_device == null) return;
        _checkerboardCache = new CanvasRenderTarget(_device, _canvasWidth, _canvasHeight, 96f);
        using var ds = _checkerboardCache.CreateDrawingSession();
        ds.Clear(CheckerLightColor);
        for (int y = 0; y < _canvasHeight; y += CheckerCellSize)
            for (int x = 0; x < _canvasWidth; x += CheckerCellSize)
                if (((x / CheckerCellSize) + (y / CheckerCellSize)) % 2 == 1)
                    ds.FillRectangle(x, y, Math.Min(CheckerCellSize, _canvasWidth - x), Math.Min(CheckerCellSize, _canvasHeight - y), CheckerDarkColor);
    }

    public void InitializeCanvas(int width, int height)
    {
        _canvasWidth = width;
        _canvasHeight = height;
        if (_device != null)
        {
            _resourcesReady = false;
            lock (_renderLock) { _document.Initialize(_device, width, height); }
            RegenerateCheckerboard();
            _resourcesReady = true;
            _undoManager.Clear();
            FitToScreen();
            MarkWorkspaceClean();
            ContentChanged?.Invoke();
        }
    }

    private bool ApplyImportCanvasSize(uint imageWidth, uint imageHeight)
    {
        if (_device == null) return false;

        var size = ResolveImportCanvasSize((int)imageWidth, (int)imageHeight, UseAssetProtectionCanvasSizing);
        if (_canvasWidth == size.W && _canvasHeight == size.H)
            return false;

        _canvasWidth = size.W;
        _canvasHeight = size.H;
        lock (_renderLock) { _document.Initialize(_device, size.W, size.H); }
        RegenerateCheckerboard();
        return true;
    }

    public async Task LoadImageAsync(StorageFile file)
    {
        if (_device == null || _canvas == null) return;
        try
        {
            using var stream = await file.OpenReadAsync();
            var bitmap = await CanvasBitmap.LoadAsync(_device, stream, 96f);

            uint imgW = bitmap.SizeInPixels.Width;
            uint imgH = bitmap.SizeInPixels.Height;
            bool sizeChanged = false;

            _resourcesReady = false;
            sizeChanged = ApplyImportCanvasSize(imgW, imgH);

            _document.SetOriginalImage(bitmap);
            lock (_renderLock) { _document.ClearMask(); }
            _resourcesReady = true;
            _undoManager.Clear();
            _loadedFilePath = file.Path;
            FitToScreen();
            MarkWorkspaceClean();
            ContentChanged?.Invoke();

            string msg = LocalizationService.Instance.Format("mask_canvas.image_loaded", file.Name, imgW, imgH);
            if (sizeChanged)
                msg += " " + LocalizationService.Instance.Format("mask_canvas.canvas_auto_matched", _canvasWidth, _canvasHeight);
            StatusMessage?.Invoke(msg);
            ImageFileLoaded?.Invoke(file.Path);
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke(LocalizationService.Instance.Format("common.load_failed", ex.Message));
        }
    }

    public void LoadImageFromBitmap(CanvasBitmap bitmap, string? filePath = null)
    {
        if (_device == null || _canvas == null) return;

        uint imgW = bitmap.SizeInPixels.Width;
        uint imgH = bitmap.SizeInPixels.Height;
        bool sizeChanged = false;

        _resourcesReady = false;
        sizeChanged = ApplyImportCanvasSize(imgW, imgH);
        _document.SetOriginalImage(bitmap);
        lock (_renderLock) { _document.ClearMask(); }
        _resourcesReady = true;
        _undoManager.Clear();
        _loadedFilePath = !string.IsNullOrWhiteSpace(filePath) ? filePath : null;
        FitToScreen();
        MarkWorkspaceClean();
        ContentChanged?.Invoke();

        string msg = LocalizationService.Instance.Format("mask_canvas.image_loaded_simple", imgW, imgH);
        if (sizeChanged)
            msg += " " + LocalizationService.Instance.Format("mask_canvas.canvas_auto_matched", _canvasWidth, _canvasHeight);
        StatusMessage?.Invoke(msg);
    }

    public async Task ReloadImagePreservingWorkspaceAsync(string filePath)
    {
        if (_device == null || _canvas == null) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            using var stream = await file.OpenReadAsync();
            var bitmap = await CanvasBitmap.LoadAsync(_device, stream, 96f);

            uint imgW = bitmap.SizeInPixels.Width;
            uint imgH = bitmap.SizeInPixels.Height;

            _resourcesReady = false;
            _document.SetOriginalImage(bitmap, preserveImageOffset: true);
            _resourcesReady = true;
            _loadedFilePath = file.Path;
            ContentChanged?.Invoke();

            StatusMessage?.Invoke(LocalizationService.Instance.Format(
                "image.reload.loaded",
                file.Name,
                imgW,
                imgH));
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke(LocalizationService.Instance.Format("common.load_failed", ex.Message));
        }
    }

    public void PerformUndo()
    {
        var current = _document.GetMaskSnapshot();
        if (current == null) return;

        var snapshot = _undoManager.Undo(current, _document.ImageOffset, _canvasWidth, _canvasHeight);
        if (snapshot.HasValue)
        {
            RestoreUndoSnapshot(snapshot.Value);
            ContentChanged?.Invoke();
        }
    }

    public void PerformRedo()
    {
        var current = _document.GetMaskSnapshot();
        if (current == null) return;

        var snapshot = _undoManager.Redo(current, _document.ImageOffset, _canvasWidth, _canvasHeight);
        if (snapshot.HasValue)
        {
            RestoreUndoSnapshot(snapshot.Value);
            ContentChanged?.Invoke();
        }
    }

    private void RestoreUndoSnapshot(UndoManager.MaskSnapshot snapshot)
    {
        if (_device == null)
            return;

        bool canvasSizeChanged = _canvasWidth != snapshot.CanvasWidth || _canvasHeight != snapshot.CanvasHeight;
        if (canvasSizeChanged)
            _resourcesReady = false;

        lock (_renderLock)
        {
            if (canvasSizeChanged)
            {
                _canvasWidth = snapshot.CanvasWidth;
                _canvasHeight = snapshot.CanvasHeight;
                _document.Initialize(_device, snapshot.CanvasWidth, snapshot.CanvasHeight);
            }

            _document.RestoreMaskSnapshot(snapshot.MaskPixels);
        }

        lock (_stateLock)
        {
            _document.ImageOffset = snapshot.ImageOffset;
        }

        if (canvasSizeChanged)
        {
            RegenerateCheckerboard();
            _resourcesReady = true;
        }
    }

    public void ClearMask()
    {
        var current = _document.GetMaskSnapshot();
        if (current != null && current.Length > 0)
            _undoManager.PushState(current, _document.ImageOffset, _canvasWidth, _canvasHeight);
        lock (_renderLock) { _document.ClearMask(); }
        ContentChanged?.Invoke();
    }

    public void BeginMoveImage()
    {
        if (!CanMoveImage) return;

        var snapshot = _document.GetMaskSnapshot();
        if (snapshot != null && snapshot.Length > 0)
            _undoManager.PushState(snapshot, _document.ImageOffset, _canvasWidth, _canvasHeight);
    }

    public void MoveImage(Vector2 canvasDelta)
    {
        if (!CanMoveImage) return;

        lock (_stateLock)
        {
            _document.ImageOffset += canvasDelta;
        }
        ContentChanged?.Invoke();
    }

    public void AlignImage(string alignment)
    {
        if (_document.OriginalImage == null || !CanMoveImage) return;

        BeginMoveImage();

        float imgW = (float)_document.OriginalImage.SizeInPixels.Width;
        float imgH = (float)_document.OriginalImage.SizeInPixels.Height;
        var cur = _document.ImageOffset;

        float ox = cur.X, oy = cur.Y;

        if (alignment.Length == 2)
        {
            ox = alignment[1] switch
            {
                'L' => 0, 'C' => (_canvasWidth - imgW) / 2f, 'R' => _canvasWidth - imgW, _ => ox,
            };
            oy = alignment[0] switch
            {
                'T' => 0, 'C' => (_canvasHeight - imgH) / 2f, 'B' => _canvasHeight - imgH, _ => oy,
            };
        }
        else if (alignment.Length == 1)
        {
            switch (alignment[0])
            {
                case 'T': oy = 0; break;
                case 'B': oy = _canvasHeight - imgH; break;
                case 'L': ox = 0; break;
                case 'R': ox = _canvasWidth - imgW; break;
            }
        }

        lock (_stateLock)
        {
            _document.ImageOffset = new Vector2(ox, oy);
        }
        ApplySystemCursorVisibility();
        ContentChanged?.Invoke();
    }

    public void FitToScreen()
    {
        if (_canvas == null) return;
        var size = _canvas.ActualSize;
        if (size.X <= 0 || size.Y <= 0) return;
        _cachedControlWidth = size.X;
        _cachedControlHeight = size.Y;
        lock (_stateLock)
        {
            _viewTransform.FitToView(_canvasWidth, _canvasHeight, size.X, size.Y);
        }
        ZoomChanged?.Invoke(_viewTransform.Scale * DpiScale);
        ApplySystemCursorVisibility();
    }

    public new void ActualSize()
    {
        if (_canvas == null) return;
        var size = _canvas.ActualSize;
        _cachedControlWidth = size.X;
        _cachedControlHeight = size.Y;
        lock (_stateLock)
        {
            _viewTransform.ResetToActualSize(_canvasWidth, _canvasHeight, size.X, size.Y, DpiScale);
        }
        ZoomChanged?.Invoke(_viewTransform.Scale * DpiScale);
        ApplySystemCursorVisibility();
    }

    public void CenterView()
    {
        if (_canvas == null) return;
        var size = _canvas.ActualSize;
        if (size.X <= 0 || size.Y <= 0) return;
        _cachedControlWidth = size.X;
        _cachedControlHeight = size.Y;
        lock (_stateLock)
        {
            _viewTransform.CenterInView(_canvasWidth, _canvasHeight, size.X, size.Y);
        }
        ApplySystemCursorVisibility();
    }

    public void ZoomIn()
    {
        if (_canvas == null) return;
        var c = new Vector2(_canvas.ActualSize.X / 2f, _canvas.ActualSize.Y / 2f);
        lock (_stateLock)
        {
            _viewTransform.ZoomAt(c, 1.25f);
        }
        ZoomChanged?.Invoke(_viewTransform.Scale * DpiScale);
        ApplySystemCursorVisibility();
    }

    public void ZoomOut()
    {
        if (_canvas == null) return;
        var c = new Vector2(_canvas.ActualSize.X / 2f, _canvas.ActualSize.Y / 2f);
        lock (_stateLock)
        {
            _viewTransform.ZoomAt(c, 0.8f);
        }
        ZoomChanged?.Invoke(_viewTransform.Scale * DpiScale);
        ApplySystemCursorVisibility();
    }

    public CanvasDevice? GetDevice() => _device;

    public void RefreshCanvas()
    {
        ContentChanged?.Invoke();
    }

    public void RefreshToolCursor()
    {
        ApplySystemCursorVisibility();
    }

    public void MarkWorkspaceClean()
    {
        lock (_stateLock)
        {
            _cleanOriginalImage = _document.OriginalImage;
            _cleanImageOffset = _document.ImageOffset;
            _cleanCanvasWidth = _canvasWidth;
            _cleanCanvasHeight = _canvasHeight;
        }

        var maskPixels = _document.GetMaskSnapshot();
        _cleanMaskPixels = maskPixels == null ? null : (byte[])maskPixels.Clone();
    }

    public bool HasWorkspaceChangesSinceClean()
    {
        CanvasBitmap? originalImage;
        Vector2 imageOffset;
        int canvasWidth;
        int canvasHeight;

        lock (_stateLock)
        {
            originalImage = _document.OriginalImage;
            imageOffset = _document.ImageOffset;
            canvasWidth = _canvasWidth;
            canvasHeight = _canvasHeight;
        }

        bool imageChanged = !ReferenceEquals(originalImage, _cleanOriginalImage)
            || canvasWidth != _cleanCanvasWidth
            || canvasHeight != _cleanCanvasHeight
            || Vector2.DistanceSquared(imageOffset, _cleanImageOffset) > 0.01f;

        return imageChanged || HasMaskChangedSinceClean();
    }

    private bool HasMaskChangedSinceClean()
    {
        var current = _document.GetMaskSnapshot();
        if (current == null || current.Length == 0)
            return _cleanMaskPixels is { Length: > 0 };

        if (_cleanMaskPixels == null || current.Length != _cleanMaskPixels.Length)
            return true;

        for (int i = 0; i < current.Length; i++)
        {
            if (current[i] != _cleanMaskPixels[i])
                return true;
        }

        return false;
    }

    /// <summary>
    /// 一键填充空白区域：将画布上没有被原始图片覆盖的区域全部填充为遮罩（白色）。
    /// </summary>
    public void FillEmptyAreas()
    {
        if (_document.MaskTarget == null || _device == null) return;

        var snapshot = _document.GetMaskSnapshot();
        if (snapshot != null && snapshot.Length > 0)
            _undoManager.PushState(snapshot, _document.ImageOffset, _canvasWidth, _canvasHeight);

        using var filler = new CanvasRenderTarget(_device, _canvasWidth, _canvasHeight, 96f);
        using (var ds = filler.CreateDrawingSession())
        {
            ds.Clear(Color.FromArgb(255, 255, 255, 255));

            if (_document.OriginalImage != null)
            {
                ds.Blend = CanvasBlend.Copy;
                var off = _document.PixelAlignedImageOffset;
                float iw = (float)_document.OriginalImage.SizeInPixels.Width;
                float ih = (float)_document.OriginalImage.SizeInPixels.Height;
                ds.FillRectangle(off.X, off.Y, iw, ih, Color.FromArgb(0, 0, 0, 0));
                ds.Blend = CanvasBlend.SourceOver;
            }
        }

        lock (_renderLock)
        {
            using (var maskDs = _document.MaskTarget.CreateDrawingSession())
            {
                maskDs.DrawImage(filler);
            }
        }

        ContentChanged?.Invoke();
    }

    /// <summary>
    /// 修剪画布：将画布裁剪到原图尺寸，以原图在画布上的位置为基准，
    /// 舍弃超出原图范围的像素，遮罩同步偏移。
    /// </summary>
    public bool TrimCanvas()
    {
        if (_device == null || _document.OriginalImage == null) return false;

        int imgW = (int)_document.OriginalImage.SizeInPixels.Width;
        int imgH = (int)_document.OriginalImage.SizeInPixels.Height;
        var offset = _document.PixelAlignedImageOffset;

        if (_canvasWidth == imgW && _canvasHeight == imgH
            && offset.X == 0 && offset.Y == 0)
            return false;

        var snapshot = _document.GetMaskSnapshot();
        if (snapshot != null && snapshot.Length > 0)
            _undoManager.PushState(snapshot, _document.ImageOffset, _canvasWidth, _canvasHeight);

        _resourcesReady = false;

        lock (_renderLock)
        {
            CanvasRenderTarget? oldMask = null;
            if (_document.MaskTarget != null)
            {
                oldMask = new CanvasRenderTarget(_device, _canvasWidth, _canvasHeight, 96f);
                using var ds = oldMask.CreateDrawingSession();
                ds.DrawImage(_document.MaskTarget);
            }

            _canvasWidth = imgW;
            _canvasHeight = imgH;
            _document.Initialize(_device, imgW, imgH);

            if (oldMask != null && _document.MaskTarget != null)
            {
                using var maskDs = _document.MaskTarget.CreateDrawingSession();
                maskDs.DrawImage(oldMask, -offset.X, -offset.Y);
                oldMask.Dispose();
            }
        }

        lock (_stateLock) { _document.ImageOffset = Vector2.Zero; }
        RegenerateCheckerboard();
        _resourcesReady = true;
        FitToScreen();
        ContentChanged?.Invoke();
        return true;
    }

    public void SetPreview(CanvasBitmap bitmap)
    {
        _previewBitmap = bitmap;
        ApplySystemCursorVisibility();
        ContentChanged?.Invoke();
    }

    public void ClearPreview()
    {
        _previewBitmap = null;
        ApplySystemCursorVisibility();
        ContentChanged?.Invoke();
    }

    public bool HasMaskContent()
    {
        if (_document.MaskTarget == null) return false;
        var pixels = _document.MaskTarget.GetPixelBytes();
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] > 0) return true;
        return false;
    }

    public void InvertMask()
    {
        if (_document.MaskTarget == null) return;
        var snapshot = _document.GetMaskSnapshot();
        if (snapshot != null && snapshot.Length > 0) _undoManager.PushState(snapshot, _document.ImageOffset, _canvasWidth, _canvasHeight);

        var px = _document.MaskTarget.GetPixelBytes();
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] > 0)
            { px[i] = 0; px[i + 1] = 0; px[i + 2] = 0; px[i + 3] = 0; }
            else
            { px[i] = 255; px[i + 1] = 255; px[i + 2] = 255; px[i + 3] = 255; }
        }
        lock (_renderLock) { _document.MaskTarget.SetPixelBytes(px); }
        ContentChanged?.Invoke();
    }

    public void ExpandMask()
    {
        if (_document.MaskTarget == null) return;
        var snapshot = _document.GetMaskSnapshot();
        if (snapshot != null && snapshot.Length > 0) _undoManager.PushState(snapshot, _document.ImageOffset, _canvasWidth, _canvasHeight);

        var src = _document.MaskTarget.GetPixelBytes();
        int w = _canvasWidth, h = _canvasHeight;
        var dst = new byte[src.Length];
        Array.Copy(src, dst, src.Length);

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int idx = (y * w + x) * 4;
            if (src[idx + 3] > 0) continue;
            bool found = false;
            for (int dy = -1; dy <= 1 && !found; dy++)
            for (int dx = -1; dx <= 1 && !found; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                if (src[(ny * w + nx) * 4 + 3] > 0) found = true;
            }
            if (found) { dst[idx] = 255; dst[idx + 1] = 255; dst[idx + 2] = 255; dst[idx + 3] = 255; }
        }
        lock (_renderLock) { _document.MaskTarget.SetPixelBytes(dst); }
        ContentChanged?.Invoke();
    }

    public void ShrinkMask()
    {
        if (_document.MaskTarget == null) return;
        var snapshot = _document.GetMaskSnapshot();
        if (snapshot != null && snapshot.Length > 0) _undoManager.PushState(snapshot, _document.ImageOffset, _canvasWidth, _canvasHeight);

        var src = _document.MaskTarget.GetPixelBytes();
        int w = _canvasWidth, h = _canvasHeight;
        var dst = new byte[src.Length];
        Array.Copy(src, dst, src.Length);

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int idx = (y * w + x) * 4;
            if (src[idx + 3] == 0) continue;
            bool edge = false;
            for (int dy = -1; dy <= 1 && !edge; dy++)
            for (int dx = -1; dx <= 1 && !edge; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || nx >= w || ny < 0 || ny >= h) { edge = true; continue; }
                if (src[(ny * w + nx) * 4 + 3] == 0) edge = true;
            }
            if (edge) { dst[idx] = 0; dst[idx + 1] = 0; dst[idx + 2] = 0; dst[idx + 3] = 0; }
        }
        lock (_renderLock) { _document.MaskTarget.SetPixelBytes(dst); }
        ContentChanged?.Invoke();
    }

    private bool ImageExtendsOutsideCanvas()
    {
        if (_document.OriginalImage == null) return false;

        var imageOffset = _document.PixelAlignedImageOffset;
        float left = imageOffset.X;
        float top = imageOffset.Y;
        float right = left + _document.OriginalImage.SizeInPixels.Width;
        float bottom = top + _document.OriginalImage.SizeInPixels.Height;

        return left < 0 || top < 0 || right > _canvasWidth || bottom > _canvasHeight;
    }

    private void OnCanvasUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
    {
        if (!_resourcesReady || _document.MaskTarget == null) return;

        try
        {
            bool hasStrokes = false;
            if (!_strokeQueue.IsEmpty)
            {
                lock (_renderLock)
                {
                    if (_document.MaskTarget == null) return;
                    using var maskDs = _document.MaskTarget.CreateDrawingSession();
                    while (_strokeQueue.TryDequeue(out var segment))
                    {
                        BrushStampRenderer.DrawSegment(maskDs, segment);
                        hasStrokes = true;
                    }
                }
            }

            if (hasStrokes)
            {
                DispatcherQueue.TryEnqueue(() => ContentChanged?.Invoke());
            }
        }
        catch (Exception) { }
    }

    private void OnCanvasDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        if (!_resourcesReady) return;

        try
        {
            var ds = args.DrawingSession;

            Vector2 imageOffset;
            Matrix3x2 viewMatrix;
            float viewScale;
            lock (_stateLock)
            {
                imageOffset = _document.PixelAlignedImageOffset;
                viewMatrix = _viewTransform.GetMatrix();
                viewScale = _viewTransform.Scale;
            }

            UpdateBackgroundPulse();
            ds.Clear(ApplyBackgroundPulse(CanvasAreaTint, _backgroundPulseAmount));
            ds.Transform = viewMatrix;

            var interp = viewScale > 1.5f
                ? CanvasImageInterpolation.NearestNeighbor
                : CanvasImageInterpolation.Linear;

            var origImg = _document.OriginalImage;
            if (origImg != null && ImageExtendsOutsideCanvas())
            {
                ds.DrawImage(origImg, imageOffset, origImg.Bounds, 0.35f, interp);
            }

            var checker = _checkerboardCache;
            if (checker != null)
                ds.DrawImage(checker);

            var preview = _previewBitmap;
            if (preview != null && !_isComparing)
            {
                var destRect = new Windows.Foundation.Rect(0, 0, _canvasWidth, _canvasHeight);
                ds.DrawImage(preview, destRect, preview.Bounds, 1f, interp);
            }
            else
            {
                if (origImg != null)
                {
                    float imgW = (float)origImg.SizeInPixels.Width;
                    float imgH = (float)origImg.SizeInPixels.Height;

                    float destX = Math.Max(0, imageOffset.X);
                    float destY = Math.Max(0, imageOffset.Y);
                    float destRight = Math.Min(_canvasWidth, imageOffset.X + imgW);
                    float destBottom = Math.Min(_canvasHeight, imageOffset.Y + imgH);

                    if (destRight > destX && destBottom > destY)
                    {
                        var destRect = new Windows.Foundation.Rect(destX, destY, destRight - destX, destBottom - destY);
                        var srcRect = new Windows.Foundation.Rect(destX - imageOffset.X, destY - imageOffset.Y, destRight - destX, destBottom - destY);
                        ds.DrawImage(origImg, destRect, srcRect, 1f, interp);
                    }
                }

                if (!_isComparing && IsMaskOverlayVisible)
                {
                    lock (_renderLock)
                    {
                        var mask = _document.MaskTarget;
                        if (mask != null)
                        {
                            if (_previewMaskOnly)
                            {
                                ds.FillRectangle(0, 0, _canvasWidth, _canvasHeight, Color.FromArgb(255, 0, 0, 0));
                                ds.DrawImage(mask);
                            }
                            else if (_maskOverlayEffect != null)
                            {
                                _maskOverlayEffect.Source = mask;
                                ds.DrawImage(_maskOverlayEffect);
                            }
                        }
                    }
                }

                if (_isRectDrawing)
                {
                    float rx = Math.Min(_rectStartCanvas.X, _rectCurrentCanvas.X);
                    float ry = Math.Min(_rectStartCanvas.Y, _rectCurrentCanvas.Y);
                    float rw = Math.Abs(_rectCurrentCanvas.X - _rectStartCanvas.X);
                    float rh = Math.Abs(_rectCurrentCanvas.Y - _rectStartCanvas.Y);
                    if (rw > 0 && rh > 0)
                    {
                        ds.FillRectangle(rx, ry, rw, rh, Color.FromArgb(60, 255, 255, 255));
                        ds.DrawRectangle(rx, ry, rw, rh, Colors.White, 1.5f / viewScale);
                    }
                }

            }

            float bw = 1f / viewScale;
            ds.DrawRectangle(0, 0, _canvasWidth, _canvasHeight, Color.FromArgb(80, 128, 128, 128), bw);

            ds.Transform = Matrix3x2.Identity;
        }
        catch (Exception) { }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_canvas == null || !_resourcesReady || _document.MaskTarget == null) return;
        var point = e.GetCurrentPoint(_canvas);
        var screenPos = new Vector2((float)point.Position.X, (float)point.Position.Y);

        // 中键 → 平移视口 / 双击居中（任何模式均可用）
        if (point.Properties.IsMiddleButtonPressed)
        {
            long now = Environment.TickCount64;
            if (now - _lastMiddleClickTicks < 600)
            {
                CenterView();
                _lastMiddleClickTicks = 0;
                e.Handled = true;
                return;
            }
            _lastMiddleClickTicks = now;

            _isPanning = true;
            _canvas.CapturePointer(e.Pointer);
            _panStartScreenPos = screenPos;
            e.Handled = true;
            return;
        }

        var altState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Menu);
        bool altDown = altState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        // Alt+左键 或 右键 → 拖拽底图位置（预览模式下禁止）
        if ((point.Properties.IsLeftButtonPressed && altDown) || point.Properties.IsRightButtonPressed)
        {
            if (!CanMoveImage) return;
            var snapshot = _document.GetMaskSnapshot();
            if (snapshot != null && snapshot.Length > 0)
                _undoManager.PushState(snapshot, _document.ImageOffset, _canvasWidth, _canvasHeight);
            _isImageDragging = true;
            _canvas.CapturePointer(e.Pointer);
            _imageDragStartPos = screenPos;
            _imageDragOriginOffset = _document.ImageOffset;
            _imageDragAccumDelta = Vector2.Zero;
            _imageDragLockedAxis = 0;
            e.Handled = true;
        }
        // 左键（无 Alt）→ 绘制
        else if (point.Properties.IsLeftButtonPressed && !_isPanning && IsMaskEditingEnabled)
        {
            Vector2 canvasPos;
            lock (_stateLock)
            {
                canvasPos = _viewTransform.ScreenToCanvas(screenPos);
            }

            var snapshot = _document.GetMaskSnapshot();
            if (snapshot != null && snapshot.Length > 0)
                _undoManager.PushState(snapshot, _document.ImageOffset, _canvasWidth, _canvasHeight);

            if (_brushSettings.CurrentTool == StrokeTool.Rectangle)
            {
                _isRectDrawing = true;
                _canvas.CapturePointer(e.Pointer);
                lock (_stateLock)
                {
                    _rectStartCanvas = canvasPos;
                    _rectCurrentCanvas = canvasPos;
                }
            }
            else
            {
                _isDrawing = true;
                _canvas.CapturePointer(e.Pointer);
                _interpolator.Reset();
                _interpolator.AddPoint(canvasPos);

                _strokeQueue.Enqueue(new StrokeSegment
                {
                    From = canvasPos, To = canvasPos,
                    BrushRadius = _brushSettings.BrushRadius,
                    Tool = _brushSettings.CurrentTool,
                    Pressure = 1f, IsFirstPoint = true,
                });
            }
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_canvas == null || !_resourcesReady) return;
        var points = e.GetIntermediatePoints(_canvas);
        if (points.Count == 0) return;

        var lastPt = points[points.Count - 1];
        var newCursorPos = new Vector2((float)lastPt.Position.X, (float)lastPt.Position.Y);

        if (_isRectDrawing)
        {
            lock (_stateLock)
            {
                _rectCurrentCanvas = _viewTransform.ScreenToCanvas(newCursorPos);
            }
            e.Handled = true;
        }
        else if (_isDrawing && _document.MaskTarget != null)
        {
            foreach (var pt in points)
            {
                var sp = new Vector2((float)pt.Position.X, (float)pt.Position.Y);
                Vector2 cp;
                lock (_stateLock)
                {
                    cp = _viewTransform.ScreenToCanvas(sp);
                }
                var interp = _interpolator.AddPoint(cp);
                for (int i = 1; i < interp.Count; i++)
                {
                    _strokeQueue.Enqueue(new StrokeSegment
                    {
                        From = interp[i - 1], To = interp[i],
                        BrushRadius = _brushSettings.BrushRadius,
                        Tool = _brushSettings.CurrentTool,
                        Pressure = 1f, IsFirstPoint = false,
                    });
                }
            }
            e.Handled = true;
        }
        else if (_isImageDragging)
        {
            var delta = newCursorPos - _imageDragStartPos;
            _imageDragStartPos = newCursorPos;
            
            lock (_stateLock)
            {
                _imageDragAccumDelta += delta / _viewTransform.Scale;

                var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(
                    Windows.System.VirtualKey.Shift);
                bool shiftDown = shiftState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

                if (shiftDown)
                {
                    // 首次达到阈值时锁定主轴，之后不再切换
                    if (_imageDragLockedAxis == 0)
                    {
                        const float threshold = 3f;
                        if (Math.Abs(_imageDragAccumDelta.X) >= threshold || Math.Abs(_imageDragAccumDelta.Y) >= threshold)
                            _imageDragLockedAxis = Math.Abs(_imageDragAccumDelta.X) >= Math.Abs(_imageDragAccumDelta.Y) ? 1 : 2;
                    }

                    if (_imageDragLockedAxis == 1)
                        _document.ImageOffset = _imageDragOriginOffset + new Vector2(_imageDragAccumDelta.X, 0);
                    else if (_imageDragLockedAxis == 2)
                        _document.ImageOffset = _imageDragOriginOffset + new Vector2(0, _imageDragAccumDelta.Y);
                    else
                        _document.ImageOffset = _imageDragOriginOffset; // 阈值未达到，保持原位
                }
                else
                {
                    _imageDragLockedAxis = 0; // 松开 Shift 时重置锁定
                    _document.ImageOffset = _imageDragOriginOffset + _imageDragAccumDelta;
                }
            }

            ContentChanged?.Invoke();
            e.Handled = true;
        }
        else if (_isPanning)
        {
            var delta = newCursorPos - _panStartScreenPos;
            lock (_stateLock)
            {
                _viewTransform.Pan(delta);
            }
            _panStartScreenPos = newCursorPos;
            e.Handled = true;
        }
        else
        {
            _showCursor = true;
            ApplySystemCursorVisibility();
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_canvas == null) return;

        if (_isRectDrawing)
        {
            if (_document.MaskTarget != null)
            {
                float rx = Math.Min(_rectStartCanvas.X, _rectCurrentCanvas.X);
                float ry = Math.Min(_rectStartCanvas.Y, _rectCurrentCanvas.Y);
                float rw = Math.Abs(_rectCurrentCanvas.X - _rectStartCanvas.X);
                float rh = Math.Abs(_rectCurrentCanvas.Y - _rectStartCanvas.Y);
                if (rw > 1 && rh > 1)
                {
                    lock (_renderLock)
                    {
                        using var maskDs = _document.MaskTarget!.CreateDrawingSession();
                        maskDs.FillRectangle(rx, ry, rw, rh, Color.FromArgb(255, 255, 255, 255));
                    }
                }
            }
            _isRectDrawing = false;
            _canvas.ReleasePointerCapture(e.Pointer);
            ContentChanged?.Invoke();
            e.Handled = true;
        }
        else if (_isDrawing)
        {
            if (_document.MaskTarget != null)
            {
                var finalPts = _interpolator.Finish();
                if (finalPts.Count > 1)
                {
                    for (int i = 1; i < finalPts.Count; i++)
                    {
                        _strokeQueue.Enqueue(new StrokeSegment
                        {
                            From = finalPts[i - 1], To = finalPts[i],
                            BrushRadius = _brushSettings.BrushRadius,
                            Tool = _brushSettings.CurrentTool,
                            Pressure = 1f, IsFirstPoint = false,
                        });
                    }
                }
            }
            _isDrawing = false;
            _canvas.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
        else if (_isImageDragging)
        {
            _isImageDragging = false;
            _canvas.ReleasePointerCapture(e.Pointer);
            ContentChanged?.Invoke();
            e.Handled = true;
        }
        else if (_isPanning)
        {
            _isPanning = false;
            _canvas.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _showCursor = true;
        ApplySystemCursorVisibility();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _showCursor = false;
        RestoreSystemCursor();
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_canvas == null) return;
        var point = e.GetCurrentPoint(_canvas);
        int delta = point.Properties.MouseWheelDelta;
        float factor = delta > 0 ? 1.1f : 1f / 1.1f;
        var sp = new Vector2((float)point.Position.X, (float)point.Position.Y);
        lock (_stateLock)
        {
            _viewTransform.ZoomAt(sp, factor);
        }
        ZoomChanged?.Invoke(_viewTransform.Scale * DpiScale);
        ApplySystemCursorVisibility();
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!IsImageFileDropEnabled)
            return;

        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = LocalizationService.Instance.GetString("mask_canvas.drag_caption");
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!IsImageFileDropEnabled)
            return;

        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is StorageFile file && IsImageFile(file.FileType))
            {
                await LoadImageAsync(file);
                break;
            }
        }
    }

    private static bool IsImageFile(string ext) =>
        ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);

    private void ApplySystemCursorVisibility()
    {
        if (!_showCursor || !ShouldShowEditingCursor())
        {
            RestoreSystemCursor();
            return;
        }

        var cursor = CreateOrUpdateToolCursor();
        if (cursor == null)
        {
            RestoreSystemCursor();
            return;
        }

        if (!ReferenceEquals(_appliedCursor, cursor))
        {
            if (_canvas != null)
                SetProtectedCursor(_canvas, cursor);
            ProtectedCursor = cursor;
            _appliedCursor = cursor;
        }
    }

    private void RestoreSystemCursor()
    {
        if (_appliedCursor == null)
            return;

        if (_canvas != null)
            SetProtectedCursor(_canvas, null);
        ProtectedCursor = null;
        _appliedCursor = null;
    }

    private InputCursor? CreateOrUpdateToolCursor()
    {
        var tool = _brushSettings.CurrentTool;
        float viewScale;
        lock (_stateLock)
        {
            viewScale = _viewTransform.Scale;
        }

        int radiusPx = tool == StrokeTool.Rectangle
            ? Math.Max(8, (int)MathF.Round(10f * DpiScale))
            : Math.Clamp((int)MathF.Round(_brushSettings.BrushRadius * viewScale * DpiScale), 1, (ToolCursorMaxSize / 2) - 4);
        int thicknessPx = Math.Max(1, (int)MathF.Round(1.5f * DpiScale));
        int cursorSize = ResolveToolCursorSize(tool, radiusPx, thicknessPx);

        if (_toolCursor != null &&
            _toolCursorTool == tool &&
            _toolCursorRadiusPx == radiusPx &&
            _toolCursorSize == cursorSize)
        {
            return _toolCursor;
        }

        var handle = CreateToolCursorHandle(tool, cursorSize, radiusPx, thicknessPx);
        if (handle == 0)
            return null;

        try
        {
            var cursor = CreateInputCursorFromHandle(handle);
            if (cursor == null)
            {
                DestroyCursor(handle);
                return null;
            }

            if (_toolCursorHandle != 0)
                _retiredToolCursorHandles.Add(_toolCursorHandle);

            _toolCursorHandle = handle;
            _toolCursor = cursor;
            _toolCursorTool = tool;
            _toolCursorRadiusPx = radiusPx;
            _toolCursorSize = cursorSize;
            return _toolCursor;
        }
        catch
        {
            DestroyCursor(handle);
            return null;
        }
    }

    private static int ResolveToolCursorSize(StrokeTool tool, int radiusPx, int thicknessPx)
    {
        int desired = tool == StrokeTool.Rectangle
            ? Math.Max(ToolCursorMinSize, (radiusPx + thicknessPx + 2) * 2)
            : Math.Max(ToolCursorMinSize, (radiusPx + (thicknessPx * 3) + 2) * 2);

        desired = Math.Clamp(desired, ToolCursorMinSize, ToolCursorMaxSize);
        return Math.Min(ToolCursorMaxSize,
            ((desired + ToolCursorSizeStep - 1) / ToolCursorSizeStep) * ToolCursorSizeStep);
    }

    private static nint CreateToolCursorHandle(StrokeTool tool, int cursorSize, int radiusPx, int thicknessPx)
    {
        cursorSize = Math.Clamp(cursorSize, ToolCursorMinSize, ToolCursorMaxSize);
        cursorSize = Math.Max(ToolCursorSizeStep,
            (cursorSize / ToolCursorSizeStep) * ToolCursorSizeStep);

        int stride = cursorSize / 8;
        var andMask = new byte[stride * cursorSize];
        var xorMask = new byte[stride * cursorSize];
        Array.Fill(andMask, (byte)0xFF);

        int center = cursorSize / 2;
        if (tool == StrokeTool.Rectangle)
            DrawXorCrosshair(xorMask, stride, cursorSize, center, radiusPx, thicknessPx);
        else
            DrawXorCircle(xorMask, stride, cursorSize, center, radiusPx, thicknessPx);

        return CreateCursor(nint.Zero, center, center, cursorSize, cursorSize, andMask, xorMask);
    }

    private static void DrawXorCircle(byte[] xorMask, int stride, int size, int center, int radiusPx, int thicknessPx)
    {
        float halfThickness = Math.Max(1f, thicknessPx) * 0.5f;
        int min = Math.Max(0, center - radiusPx - thicknessPx - 1);
        int max = Math.Min(size - 1, center + radiusPx + thicknessPx + 1);

        for (int y = min; y <= max; y++)
        {
            float dy = y - center;
            for (int x = min; x <= max; x++)
            {
                float dx = x - center;
                float distance = MathF.Sqrt((dx * dx) + (dy * dy));
                if (MathF.Abs(distance - radiusPx) <= halfThickness)
                    SetMaskBit(xorMask, stride, x, y);
            }
        }
    }

    private static void DrawXorCrosshair(byte[] xorMask, int stride, int size, int center, int halfLen, int thicknessPx)
    {
        halfLen = Math.Clamp(halfLen, 4, (size / 2) - 2);
        int halfThickness = Math.Max(0, thicknessPx / 2);
        int start = center - halfLen;
        int end = center + halfLen;

        for (int t = -halfThickness; t <= halfThickness; t++)
        {
            int horizontalY = center + t;
            int verticalX = center + t;
            for (int p = start; p <= end; p++)
            {
                SetMaskBit(xorMask, stride, p, horizontalY);
                SetMaskBit(xorMask, stride, verticalX, p);
            }
        }
    }

    private static void SetMaskBit(byte[] mask, int stride, int x, int y)
    {
        if (x < 0 || y < 0)
            return;

        int index = (y * stride) + (x >> 3);
        if ((uint)index >= (uint)mask.Length)
            return;

        mask[index] |= (byte)(0x80 >> (x & 7));
    }

    private static InputCursor? CreateInputCursorFromHandle(nint cursorHandle)
    {
        if (cursorHandle == 0)
            return null;

        var factory = InputCursor.As<IInputCursorStaticsInterop>();
        Marshal.ThrowExceptionForHR(factory.CreateFromHCursor(cursorHandle, out var cursorAbi));
        return MarshalInterface<InputCursor>.FromAbi(cursorAbi);
    }

    private static void SetProtectedCursor(UIElement element, InputCursor? cursor)
    {
        typeof(UIElement).InvokeMember(
            "ProtectedCursor",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.SetProperty,
            binder: null,
            target: element,
            args: [cursor]);
    }

    private bool ShouldShowEditingCursor()
    {
        return _isMaskEditingEnabled && !IsInPreviewMode;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateCursor(
        nint hInst,
        int xHotSpot,
        int yHotSpot,
        int nWidth,
        int nHeight,
        byte[] pvANDPlane,
        byte[] pvXORPlane);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyCursor(nint hCursor);

    [ComImport]
    [Guid("ac6f5065-90c4-46ce-beb7-05e138e54117")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInputCursorStaticsInterop
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();

        [PreserveSig]
        int CreateFromHCursor(nint hCursor, out nint inputCursor);
    }

    public void RenderThumbnail(CanvasDrawingSession ds, float targetWidth, float targetHeight)
    {
        if (!_resourcesReady || _device == null) return;

        try
        {
            ds.Clear(Color.FromArgb(255, 40, 40, 40));

            float scale = Math.Min(targetWidth / _canvasWidth, targetHeight / _canvasHeight);
            float ox = (targetWidth - _canvasWidth * scale) / 2f;
            float oy = (targetHeight - _canvasHeight * scale) / 2f;

            var oldTransform = ds.Transform;
            ds.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(ox, oy);

            ds.FillRectangle(0, 0, _canvasWidth, _canvasHeight, Color.FromArgb(255, 200, 200, 200));

            Vector2 imageOffset;
            Matrix3x2 viewMatrix;
            lock (_stateLock)
            {
                imageOffset = _document.PixelAlignedImageOffset;
                viewMatrix = _viewTransform.GetMatrix();
            }

            var origImg = _document.OriginalImage;
            if (origImg != null)
                ds.DrawImage(origImg, imageOffset,
                    origImg.Bounds, 1f, CanvasImageInterpolation.NearestNeighbor);

            lock (_renderLock)
            {
                var mask = _document.MaskTarget;
                if (IsMaskOverlayVisible && mask != null && _thumbnailMaskOverlayEffect != null)
                {
                    _thumbnailMaskOverlayEffect.Source = mask;
                    ds.DrawImage(_thumbnailMaskOverlayEffect);
                }
            }

            float ctlW = _cachedControlWidth, ctlH = _cachedControlHeight;
            if (ctlW > 0 && ctlH > 0 && Matrix3x2.Invert(viewMatrix, out var invView))
            {
                var p0 = Vector2.Transform(Vector2.Zero, invView);
                var p1 = Vector2.Transform(new Vector2(ctlW, ctlH), invView);
                ds.DrawRectangle(p0.X, p0.Y, p1.X - p0.X, p1.Y - p0.Y,
                    Color.FromArgb(180, 255, 255, 255), 2f / scale);
            }

            ds.Transform = oldTransform;
        }
        catch (Exception) { }
    }

    public float GetThumbnailScale(float thumbWidth, float thumbHeight)
    {
        return Math.Min(thumbWidth / _canvasWidth, thumbHeight / _canvasHeight);
    }
}
