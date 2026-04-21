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

    private static string L(string key) => LocalizationService.Instance.GetString(key);

    public record UpscaleModelInfo(string DisplayName, string FilePath, int Scale);

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
            int scale;
            lock (_sync) scale = _modelScale;

            var outW = srcW * scale;
            var outH = srcH * scale;

            if (srcW <= DefaultTileSize && srcH <= DefaultTileSize)
            {
                progress?.Report(0.1);
                var result = RunSingleTile(sourceBitmap, ct);
                progress?.Report(1.0);
                return EncodePng(result);
            }

            return RunTiled(sourceBitmap, scale, progress, ct);
        }, ct);
    }

    private SKBitmap RunSingleTile(SKBitmap bitmap, CancellationToken ct)
    {
        var tensor = BitmapToTensor(bitmap);
        var output = RunInference(tensor, bitmap.Width, bitmap.Height, ct);
        return TensorToBitmap(output);
    }

    private byte[] RunTiled(SKBitmap source, int scale,
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
                var tileOut = RunInference(tensor, sw, sh, ct);
                using var outTileBitmap = TensorToBitmap(tileOut);

                int ox = sx * scale;
                int oy = sy * scale;
                canvas.DrawBitmap(outTileBitmap, ox, oy);

                doneTiles++;
                progress?.Report((double)doneTiles / totalTiles);
            }
        }

        return EncodePng(output);
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

    private float[] RunInference(DenseTensor<float> input, int tileW, int tileH, CancellationToken ct)
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
        var outputTensor = results.First(r => r.Name == outputName);
        return outputTensor.AsEnumerable<float>().ToArray();
    }

    private SKBitmap TensorToBitmap(float[] data)
    {
        int scale;
        lock (_sync) scale = _modelScale;

        int totalPixels = data.Length / 3;
        int outW = (int)Math.Sqrt(totalPixels * 1.0);
        int outH = totalPixels / outW;

        if (outW * outH != totalPixels)
        {
            for (int w = (int)Math.Sqrt(totalPixels); w >= 1; w--)
            {
                if (totalPixels % w == 0)
                {
                    outW = w;
                    outH = totalPixels / w;
                    break;
                }
            }
        }

        var bitmap = new SKBitmap(outW, outH, SKColorType.Rgba8888, SKAlphaType.Premul);
        int planeSize = outW * outH;

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
