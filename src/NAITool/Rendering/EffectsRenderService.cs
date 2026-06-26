using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NAITool.Models;
using SkiaSharp;
using static NAITool.Rendering.EffectsRenderingHelpers;

namespace NAITool.Rendering;

public sealed class EffectsRenderService
{
    private readonly object _gpuRendererLock = new();
    private Win2dEffectsRenderer? _gpuRenderer;
    private int _nextRequestId;

    public async Task<EffectsPreviewRenderResult> RenderPreviewAsync(
        SKBitmap? cachedSourceBitmap,
        byte[] sourceBytes,
        IReadOnlyList<EffectEntry> effects,
        string devicePreference,
        Action<string>? debugLog,
        CancellationToken cancellationToken)
    {
        string requestLabel = "Preview#" + Interlocked.Increment(ref _nextRequestId);
        bool cpuOnly = IsCpu(devicePreference);
        debugLog?.Invoke($"[Effects][{requestLabel}] Start | Preference={FormatDevicePreference(devicePreference)} | Route={(cpuOnly ? "CPU" : "GPU preferred")} | SourceBytes={sourceBytes.Length} | Effects={effects.Count} | Chain={DescribeEffectChain(effects)}");

        if (cpuOnly)
        {
            var cpuStopwatch = Stopwatch.StartNew();
            debugLog?.Invoke($"[Effects][{requestLabel}] CPU preview start | Reason=forced CPU");
            SKBitmap bitmap = await Task.Run(
                () => CpuEffectsRenderer.RenderEffectsPreview(
                    cachedSourceBitmap,
                    sourceBytes,
                    CopyEffects(effects)),
                cancellationToken);
            debugLog?.Invoke($"[Effects][{requestLabel}] CPU preview completed | Elapsed={FormatElapsed(cpuStopwatch)} | Output={bitmap.Width}x{bitmap.Height}");
            return new EffectsPreviewRenderResult
            {
                Bitmap = bitmap,
                PixelWidth = bitmap.Width,
                PixelHeight = bitmap.Height,
            };
        }

        try
        {
            var gpuStopwatch = Stopwatch.StartNew();
            debugLog?.Invoke($"[Effects][{requestLabel}] GPU preview start");
            EffectsPreviewRenderResult result = await GetGpuRenderer(debugLog, requestLabel)
                .RenderPreviewAsync(sourceBytes, effects, debugLog, requestLabel, cancellationToken);
            debugLog?.Invoke($"[Effects][{requestLabel}] GPU preview completed | Elapsed={FormatElapsed(gpuStopwatch)} | Output={result.PixelWidth}x{result.PixelHeight}");
            return result;
        }
        catch (OperationCanceledException)
        {
            debugLog?.Invoke($"[Effects][{requestLabel}] Preview cancelled");
            throw;
        }
        catch (Exception ex)
        {
            debugLog?.Invoke($"[Effects][{requestLabel}] GPU preview failed, falling back to CPU | Chain={DescribeEffectChain(effects)} | Exception={ex}");
            var cpuStopwatch = Stopwatch.StartNew();
            debugLog?.Invoke($"[Effects][{requestLabel}] CPU fallback preview start");
            SKBitmap bitmap = await Task.Run(
                () => CpuEffectsRenderer.RenderEffectsPreview(
                    cachedSourceBitmap,
                    sourceBytes,
                    CopyEffects(effects)),
                cancellationToken);
            debugLog?.Invoke($"[Effects][{requestLabel}] CPU fallback preview completed | Elapsed={FormatElapsed(cpuStopwatch)} | Output={bitmap.Width}x{bitmap.Height}");
            return new EffectsPreviewRenderResult
            {
                Bitmap = bitmap,
                PixelWidth = bitmap.Width,
                PixelHeight = bitmap.Height,
                UsedCpuFallback = true,
                FallbackReason = ex.Message,
            };
        }
    }

    public async Task<EffectsPngRenderResult> RenderPngAsync(
        byte[] sourceBytes,
        IReadOnlyList<EffectEntry> effects,
        string devicePreference,
        Action<string>? debugLog,
        CancellationToken cancellationToken)
    {
        string requestLabel = "Png#" + Interlocked.Increment(ref _nextRequestId);
        bool cpuOnly = IsCpu(devicePreference);
        debugLog?.Invoke($"[Effects][{requestLabel}] Start | Preference={FormatDevicePreference(devicePreference)} | Route={(cpuOnly ? "CPU" : "GPU preferred")} | SourceBytes={sourceBytes.Length} | Effects={effects.Count} | Chain={DescribeEffectChain(effects)}");

        if (cpuOnly)
        {
            var cpuStopwatch = Stopwatch.StartNew();
            debugLog?.Invoke($"[Effects][{requestLabel}] CPU PNG render start | Reason=forced CPU");
            byte[] bytes = await Task.Run(
                () => CpuEffectsRenderer.RenderEffects(sourceBytes, CopyEffects(effects)),
                cancellationToken);
            debugLog?.Invoke($"[Effects][{requestLabel}] CPU PNG render completed | Elapsed={FormatElapsed(cpuStopwatch)} | OutputBytes={bytes.Length}");
            return new EffectsPngRenderResult
            {
                Bytes = bytes,
            };
        }

        try
        {
            var gpuStopwatch = Stopwatch.StartNew();
            debugLog?.Invoke($"[Effects][{requestLabel}] GPU PNG render start");
            byte[] bytes = await GetGpuRenderer(debugLog, requestLabel)
                .RenderPngAsync(sourceBytes, effects, debugLog, requestLabel, cancellationToken);
            debugLog?.Invoke($"[Effects][{requestLabel}] GPU PNG render completed | Elapsed={FormatElapsed(gpuStopwatch)} | OutputBytes={bytes.Length}");
            return new EffectsPngRenderResult { Bytes = bytes };
        }
        catch (OperationCanceledException)
        {
            debugLog?.Invoke($"[Effects][{requestLabel}] PNG render cancelled");
            throw;
        }
        catch (Exception ex)
        {
            debugLog?.Invoke($"[Effects][{requestLabel}] GPU PNG render failed, falling back to CPU | Chain={DescribeEffectChain(effects)} | Exception={ex}");
            var cpuStopwatch = Stopwatch.StartNew();
            debugLog?.Invoke($"[Effects][{requestLabel}] CPU fallback PNG render start");
            byte[] bytes = await Task.Run(
                () => CpuEffectsRenderer.RenderEffects(sourceBytes, CopyEffects(effects)),
                cancellationToken);
            debugLog?.Invoke($"[Effects][{requestLabel}] CPU fallback PNG render completed | Elapsed={FormatElapsed(cpuStopwatch)} | OutputBytes={bytes.Length}");
            return new EffectsPngRenderResult
            {
                Bytes = bytes,
                UsedCpuFallback = true,
                FallbackReason = ex.Message,
            };
        }
    }

    private Win2dEffectsRenderer GetGpuRenderer(Action<string>? debugLog, string requestLabel)
    {
        lock (_gpuRendererLock)
        {
            if (_gpuRenderer != null)
            {
                debugLog?.Invoke($"[Effects][{requestLabel}] GPU renderer reused");
                return _gpuRenderer;
            }

            var stopwatch = Stopwatch.StartNew();
            debugLog?.Invoke($"[Effects][{requestLabel}] GPU renderer initialization start");
            try
            {
                _gpuRenderer = new Win2dEffectsRenderer();
                debugLog?.Invoke($"[Effects][{requestLabel}] GPU renderer initialization completed | Elapsed={FormatElapsed(stopwatch)}");
                return _gpuRenderer;
            }
            catch (Exception ex)
            {
                debugLog?.Invoke($"[Effects][{requestLabel}] GPU renderer initialization failed | Elapsed={FormatElapsed(stopwatch)} | Exception={ex}");
                throw;
            }
        }
    }

    private static bool IsCpu(string? devicePreference) =>
        string.Equals(devicePreference, "Cpu", StringComparison.OrdinalIgnoreCase);

    private static string FormatDevicePreference(string? devicePreference) =>
        string.IsNullOrWhiteSpace(devicePreference) ? "(empty)" : devicePreference;

    private static List<EffectEntry> CopyEffects(IReadOnlyList<EffectEntry> effects)
    {
        var copy = new List<EffectEntry>(effects.Count);
        foreach (var effect in effects)
        {
            copy.Add(new EffectEntry
            {
                Type = effect.Type,
                Value1 = effect.Value1,
                Value2 = effect.Value2,
                Value3 = effect.Value3,
                Value4 = effect.Value4,
                Value5 = effect.Value5,
                Value6 = effect.Value6,
                TextValue = effect.TextValue ?? "",
            });
        }

        return copy;
    }
}
