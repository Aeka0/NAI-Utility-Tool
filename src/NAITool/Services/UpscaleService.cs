using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace NAITool.Services;

public sealed class UpscaleService : IDisposable
{
    public const double MinTargetScale = 1.0;
    public const double MaxTargetScale = 4.0;
    public const double DefaultTargetScale = 2.0;
    public const double TargetScaleStep = 0.1;

    private readonly object _sync = new();
    private InferenceSession? _session;
    private string? _loadedModelPath;
    private string _inputName = "";
    private string _outputName = "";
    private string _executionProvider = "CPU";
    private bool _loadedPreferCpu;
    private int _modelScale = 4;

    private const int DefaultTileSize = 512;
    private const int TileOverlap = 32;
    private const double ScaleEpsilon = 0.0001;

    private static string L(string key) => LocalizationService.Instance.GetString(key);

    public record UpscaleModelInfo(string DisplayName, string FilePath, int Scale);
    private readonly record struct InferenceOutput(float[] Data, int Width, int Height);

    private sealed class DelegateProgress : IProgress<double>
    {
        private readonly Action<double> _report;

        public DelegateProgress(Action<double> report)
        {
            _report = report;
        }

        public void Report(double value) => _report(value);
    }

    public static List<UpscaleModelInfo> ScanModels(string modelsDirectory)
    {
        var results = new List<UpscaleModelInfo>();
        if (!Directory.Exists(modelsDirectory)) return results;

        foreach (var file in Directory.GetFiles(modelsDirectory, "*.onnx", SearchOption.AllDirectories)
                     .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            int scale = InferScaleFromName(name);
            results.Add(new UpscaleModelInfo(name, file, scale));
        }

        return results;
    }

    private static int InferScaleFromName(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("x2") || lower.Contains("2x")) return 2;
        if (lower.Contains("x3") || lower.Contains("3x")) return 3;
        return 4;
    }

    public static double NormalizeTargetScale(double targetScale)
    {
        if (double.IsNaN(targetScale) || double.IsInfinity(targetScale))
            targetScale = DefaultTargetScale;

        var clamped = Math.Clamp(targetScale, MinTargetScale, MaxTargetScale);
        var stepped = Math.Round(clamped / TargetScaleStep, 0, MidpointRounding.AwayFromZero) * TargetScaleStep;
        return Math.Round(Math.Clamp(stepped, MinTargetScale, MaxTargetScale), 1, MidpointRounding.AwayFromZero);
    }

    public void LoadModel(string modelPath, bool preferCpu = false)
    {
        lock (_sync)
        {
            if (_loadedModelPath == modelPath && _session != null && _loadedPreferCpu == preferCpu)
                return;

            _session?.Dispose();
            _session = null;
            _loadedModelPath = null;
            _loadedPreferCpu = preferCpu;

            var (session, provider) = CreateSession(modelPath, preferCpu);
            _session = session;
            _executionProvider = provider;
            _loadedModelPath = modelPath;

            _inputName = session.InputMetadata.Keys.First();
            _outputName = session.OutputMetadata.Keys.First();

            var inputDims = session.InputMetadata[_inputName].Dimensions;
            var outputDims = session.OutputMetadata[_outputName].Dimensions;
            if (inputDims.Length >= 4 && outputDims.Length >= 4
                && inputDims[2] > 0 && outputDims[2] > 0)
            {
                _modelScale = outputDims[2] / inputDims[2];
            }
            else
            {
                _modelScale = InferScaleFromName(Path.GetFileNameWithoutExtension(modelPath));
            }

            System.Diagnostics.Debug.WriteLine(
                $"[Upscale] Model loaded: {Path.GetFileName(modelPath)} | Provider: {_executionProvider} | Scale: {_modelScale}x");
        }
    }

    public int ModelScale
    {
        get { lock (_sync) return _modelScale; }
    }

    public string ExecutionProvider
    {
        get { lock (_sync) return _executionProvider; }
    }

    public Task<byte[]> UpscaleAsync(
        byte[] imageBytes,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        int modelScale;
        lock (_sync) modelScale = Math.Max(1, _modelScale);
        return UpscaleAsync(imageBytes, modelScale, progress, ct);
    }

    public Task<byte[]> UpscaleAsync(
        byte[] imageBytes,
        double targetScale,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_session == null)
                    throw new InvalidOperationException(L("upscale.error.model_not_loaded"));
            }

            using var sourceBitmap = SKBitmap.Decode(imageBytes)
                ?? throw new InvalidOperationException(L("upscale.error.decode_failed"));

            var srcW = sourceBitmap.Width;
            var srcH = sourceBitmap.Height;
            targetScale = NormalizeTargetScale(targetScale);

            int modelScale;
            lock (_sync) modelScale = Math.Max(1, _modelScale);
            int passCount = GetRequiredPassCount(modelScale, targetScale);

            SKBitmap? currentBitmap = null;
            try
            {
                for (int pass = 0; pass < passCount; pass++)
                {
                    ct.ThrowIfCancellationRequested();
                    var inputBitmap = currentBitmap ?? sourceBitmap;
                    var passProgress = CreatePassProgress(progress, pass, passCount);
                    var passBitmap = RunNativeUpscale(inputBitmap, passProgress, ct);
                    currentBitmap?.Dispose();
                    currentBitmap = passBitmap;
                }

                currentBitmap ??= sourceBitmap.Copy();

                int targetW = Math.Max(1, (int)Math.Round(srcW * targetScale, MidpointRounding.AwayFromZero));
                int targetH = Math.Max(1, (int)Math.Round(srcH * targetScale, MidpointRounding.AwayFromZero));
                if (currentBitmap.Width != targetW || currentBitmap.Height != targetH)
                {
                    using var resized = ResizeBitmap(currentBitmap, targetW, targetH);
                    progress?.Report(1.0);
                    return EncodePng(resized);
                }

                progress?.Report(1.0);
                return EncodePng(currentBitmap);
            }
            finally
            {
                currentBitmap?.Dispose();
            }
        }, ct);
    }

    private static int GetRequiredPassCount(int modelScale, double targetScale)
    {
        if (modelScale <= 1)
            return 1;

        int passCount = 1;
        double cumulativeScale = modelScale;
        while (cumulativeScale + ScaleEpsilon < targetScale && passCount < 3)
        {
            passCount++;
            cumulativeScale *= modelScale;
        }

        return passCount;
    }

    private static IProgress<double>? CreatePassProgress(IProgress<double>? progress, int passIndex, int passCount)
    {
        if (progress == null)
            return null;

        return new DelegateProgress(p =>
        {
            var clamped = Math.Clamp(p, 0.0, 1.0);
            progress.Report((passIndex + clamped) / passCount);
        });
    }

    private SKBitmap RunNativeUpscale(SKBitmap sourceBitmap, IProgress<double>? progress, CancellationToken ct)
    {
        if (sourceBitmap.Width <= DefaultTileSize && sourceBitmap.Height <= DefaultTileSize)
        {
            progress?.Report(0.1);
            var result = RunSingleTile(sourceBitmap, ct);
            progress?.Report(1.0);
            return result;
        }

        int scale;
        lock (_sync) scale = Math.Max(1, _modelScale);
        return RunTiled(sourceBitmap, scale, progress, ct);
    }

    private SKBitmap RunSingleTile(SKBitmap bitmap, CancellationToken ct)
    {
        var tensor = BitmapToTensor(bitmap);
        var output = RunInference(tensor, ct);
        return TensorToBitmap(output);
    }

    private SKBitmap RunTiled(SKBitmap source, int scale,
        IProgress<double>? progress, CancellationToken ct)
    {
        int srcW = source.Width, srcH = source.Height;
        int tileSize = DefaultTileSize;
        int step = tileSize - TileOverlap * 2;

        var tilesX = (int)Math.Ceiling((double)srcW / step);
        var tilesY = (int)Math.Ceiling((double)srcH / step);
        int totalTiles = tilesX * tilesY;
        int doneTiles = 0;

        var outW = srcW * scale;
        var outH = srcH * scale;

        using var output = new SKBitmap(outW, outH, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(output);

        for (int ty = 0; ty < tilesY; ty++)
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                ct.ThrowIfCancellationRequested();

                int sx = Math.Min(tx * step, srcW - tileSize);
                int sy = Math.Min(ty * step, srcH - tileSize);
                sx = Math.Max(sx, 0);
                sy = Math.Max(sy, 0);
                int sw = Math.Min(tileSize, srcW - sx);
                int sh = Math.Min(tileSize, srcH - sy);

                using var tileBitmap = new SKBitmap(sw, sh, SKColorType.Rgba8888, SKAlphaType.Premul);
                using (var tileCanvas = new SKCanvas(tileBitmap))
                {
                    tileCanvas.DrawBitmap(source,
                        new SKRect(sx, sy, sx + sw, sy + sh),
                        new SKRect(0, 0, sw, sh));
                }

                var tensor = BitmapToTensor(tileBitmap);
                var tileOut = RunInference(tensor, ct);
                using var outTileBitmap = TensorToBitmap(tileOut);

                int ox = sx * scale;
                int oy = sy * scale;
                canvas.DrawBitmap(outTileBitmap, ox, oy);

                doneTiles++;
                progress?.Report((double)doneTiles / totalTiles);
            }
        }

        canvas.Flush();
        return output.Copy();
    }

    private DenseTensor<float> BitmapToTensor(SKBitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        var tensor = new DenseTensor<float>(new[] { 1, 3, h, w });

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                tensor[0, 0, y, x] = pixel.Red / 255f;
                tensor[0, 1, y, x] = pixel.Green / 255f;
                tensor[0, 2, y, x] = pixel.Blue / 255f;
            }
        }

        return tensor;
    }

    private InferenceOutput RunInference(DenseTensor<float> input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        InferenceSession session;
        string inputName, outputName;
        lock (_sync)
        {
            session = _session ?? throw new InvalidOperationException(L("upscale.error.model_not_loaded"));
            inputName = _inputName;
            outputName = _outputName;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, input)
        };

        using var results = session.Run(inputs);
        var outputTensor = results.First(r => r.Name == outputName).AsTensor<float>();
        var dimensions = outputTensor.Dimensions;
        int outH = 0;
        int outW = 0;
        if (dimensions.Length >= 4)
        {
            outH = dimensions[^2];
            outW = dimensions[^1];
        }

        var data = outputTensor.AsEnumerable<float>().ToArray();
        if (outW <= 0 || outH <= 0)
        {
            int totalPixels = data.Length / 3;
            outW = (int)Math.Sqrt(totalPixels);
            while (outW > 1 && totalPixels % outW != 0)
                outW--;
            outH = Math.Max(1, totalPixels / Math.Max(1, outW));
        }

        return new InferenceOutput(data, outW, outH);
    }

    private SKBitmap TensorToBitmap(InferenceOutput output)
    {
        var data = output.Data;
        int outW = output.Width;
        int outH = output.Height;
        int planeSize = outW * outH;
        if (outW <= 0 || outH <= 0 || data.Length < planeSize * 3)
            throw new InvalidOperationException(L("upscale.error.decode_failed"));

        var bitmap = new SKBitmap(outW, outH, SKColorType.Rgba8888, SKAlphaType.Premul);

        for (int y = 0; y < outH; y++)
        {
            for (int x = 0; x < outW; x++)
            {
                int idx = y * outW + x;
                byte r = (byte)Math.Clamp(data[idx] * 255f, 0, 255);
                byte g = (byte)Math.Clamp(data[planeSize + idx] * 255f, 0, 255);
                byte b = (byte)Math.Clamp(data[2 * planeSize + idx] * 255f, 0, 255);
                bitmap.SetPixel(x, y, new SKColor(r, g, b, 255));
            }
        }

        return bitmap;
    }

    private static SKBitmap ResizeBitmap(SKBitmap source, int width, int height)
    {
        var resized = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(resized);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(source, new SKRect(0, 0, width, height));
        canvas.Flush();
        return resized;
    }

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    public void UnloadModel()
    {
        lock (_sync)
        {
            bool hadLoadedModel = _session != null || _loadedModelPath != null;
            _session?.Dispose();
            _session = null;
            _loadedModelPath = null;
            _inputName = "";
            _outputName = "";
            _executionProvider = "CPU";
            _loadedPreferCpu = false;
            _modelScale = 4;
            if (hadLoadedModel)
                System.Diagnostics.Debug.WriteLine("[Upscale] Model unloaded");
        }
    }

    private static (InferenceSession Session, string Provider) CreateSession(string modelPath, bool preferCpu)
    {
        if (!preferCpu)
        {
            try
            {
                var options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                };
                options.AppendExecutionProvider_DML(0);
                return (new InferenceSession(modelPath, options), "GPU (DirectML)");
            }
            catch
            {
                // GPU 不可用，回退到 CPU
            }
        }

        var cpuOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        return (new InferenceSession(modelPath, cpuOptions), "CPU");
    }

    public void Dispose()
    {
        UnloadModel();
    }
}
