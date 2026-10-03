using NAITool.Services;
using SkiaSharp;
using System.Runtime.InteropServices;

internal static class LocalUpscaleChecks
{
    private static int _checks;
    private static void Check(bool value, string scenario)
    {
        _checks++;
        if (!value) throw new InvalidOperationException(scenario);
    }
    private static void Near(int expected, int actual, string scenario) => Check(Math.Abs(expected - actual) <= 1, $"{scenario}: {expected} vs {actual}");

    private static byte[] Image(int width, int height, Func<int, int, SKColor> pixel)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var row = new byte[width * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var color = pixel(x, y);
                row[x * 4] = color.Red;
                row[x * 4 + 1] = color.Green;
                row[x * 4 + 2] = color.Blue;
                row[x * 4 + 3] = color.Alpha;
            }
            Marshal.Copy(row, 0, bitmap.GetPixels() + y * bitmap.RowBytes, row.Length);
        }
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private static SKBitmap Decode(byte[] bytes)
    {
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success) throw new Exception("Invalid PNG output.");
        return bitmap;
    }

    public static async Task RunAsync(string? realModel)
    {
        string temp = Path.Combine(Path.GetTempPath(), "naitool-alpha-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string fixture = Path.Combine(temp, "fixture-x2.onnx");
        try
        {
            await File.WriteAllBytesAsync(fixture, TestModel.Create());
            using var service = new UpscaleService();
            service.LoadModel(fixture, preferCpu: true);
            foreach (int alpha in new[] { 255, 254, 253, 128, 1, 0 })
            {
                var progress = new CapturedProgress();
                using var output = Decode(await service.UpscaleAsync(Image(8, 8, (_, _) => new SKColor(160, 120, 80, (byte)alpha)), 2, progress));
                Check(output.Width == 16 && output.Height == 16, "Native output dimensions.");
                var pixel = output.GetPixel(4, 4);
                Near(160, pixel.Red, $"Straight RGB survives even at zero alpha (input alpha {alpha})");
                Near(60, pixel.Green, "Green actually passes through ONNX");
                Near(20, pixel.Blue, "Blue actually passes through ONNX");
                int expectedAlpha = alpha >= 254 ? 255 : Gray(alpha, alpha / 2, alpha / 4);
                Near(expectedAlpha, pixel.Alpha, "Alpha uses the model only below the stealth threshold");
                Check(progress.Values.Count(p => Math.Abs(p - 0.05) < 0.00001) == (alpha < 254 ? 1 : 0), "One or two inference stages selected by threshold.");
                Check(progress.Values.Last() == 1 && progress.Values.Zip(progress.Values.Skip(1)).All(p => p.First <= p.Second), "Progress stays monotonic across RGB and alpha.");
            }

            var stealth = Image(16, 16, (x, y) => new SKColor(91, 121, 201, (byte)(254 + (x + y) % 2)));
            using (var opaque = Decode(await service.UpscaleAsync(stealth, 2)))
                Check(opaque.Pixels.All(p => p.Alpha == 255), "Mixed 254/255 steganography does not create transparency.");

            using (var jpegBitmap = new SKBitmap(8, 8, SKColorType.Rgba8888, SKAlphaType.Opaque))
            {
                jpegBitmap.Erase(new SKColor(160, 120, 80));
                using var jpeg = jpegBitmap.Encode(SKEncodedImageFormat.Jpeg, 100);
                using var result = Decode(await service.UpscaleAsync(jpeg.ToArray(), 2));
                Check(result.Width == 16 && result.Pixels.All(p => p.Alpha == 255), "JPEG without an alpha channel follows the opaque path.");
            }

            // Sparse holes must not be discarded by an image-wide percentage threshold.
            var sparse = Image(32, 32, (x, y) => new SKColor(80, 160, 240, (byte)(x == 31 && y == 31 ? 0 : 255)));
            using (var result = Decode(await service.UpscaleAsync(sparse, 2)))
            {
                Check(result.GetPixel(63, 63).Alpha == 0, "A single transparent pixel at the last row/column survives.");
                Near(Gray(255, 127, 63), result.GetPixel(0, 0).Alpha, "Once classified, all alpha values follow the same model pipeline.");
            }

            foreach (double scale in new[] { 1.0, 1.5, 3.0, 4.0 })
            {
                using var result = Decode(await service.UpscaleAsync(Image(8, 8, (_, _) => new SKColor(160, 128, 64, 128)), scale));
                int passes = scale > 2 ? 2 : 1;
                Check(result.Width == (int)(8 * scale) && result.Height == (int)(8 * scale), "Fractional and repeated-pass dimensions.");
                var pixel = result.GetPixel(result.Width / 2, result.Height / 2);
                Near(Gray(128, passes == 1 ? 64 : 32, passes == 1 ? 32 : 8), pixel.Alpha, "Alpha receives every inference pass, not just interpolation.");
                Near(160, pixel.Red, "RGB remains independent through resize and merge");
            }

            // Wide, tall and >512-square cases cover tiling and the parallel channel-copy path.
            foreach (var (width, height) in new[] { (520, 4), (4, 520), (520, 516) })
            {
                var input = Image(width, height, (x, y) => new SKColor(160, 120, 80, (byte)((x + y) % 256)));
                using var result = Decode(await service.UpscaleAsync(input, 2));
                Check(result.Width == width * 2 && result.Height == height * 2, "Tiled dimensions.");
                foreach (var (x, y) in new[] { (0, 0), (width - 1, height - 1), (width / 2, height / 2) })
                {
                    int alpha = (x + y) % 256;
                    var pixel = result.GetPixel(x * 2, y * 2);
                    Near(Gray(alpha, alpha / 2, alpha / 4), pixel.Alpha, "Tiled alpha is aligned with RGB");
                    Near(160, pixel.Red, "Tiling does not composite transparent RGB onto black");
                }
            }

            using (var cts = new CancellationTokenSource())
            {
                bool cancelled = false;
                try
                {
                    await service.UpscaleAsync(Image(8, 8, (_, _) => new SKColor(90, 120, 180, 128)), 2,
                        new CapturedProgress(p => { if (p >= 0.5) cts.Cancel(); }), cts.Token);
                }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "Cancellation between RGB and alpha does not return a partial opaque result.");
            }

            if (realModel != null)
            {
                service.LoadModel(realModel, preferCpu: true);
                using var result = Decode(await service.UpscaleAsync(Image(16, 16,
                    (x, y) => new SKColor(180, 100, 60, (byte)(x < 8 ? 0 : 255))), 2));
                Check(result.Width == 32 && result.Height == 32, "Bundled model output dimensions.");
                Check(result.Pixels.Any(p => p.Alpha < 16) && result.Pixels.Any(p => p.Alpha > 240), "Bundled model retains transparent and opaque regions.");
            }
            Console.WriteLine($"Passed {_checks} local upscale checks; real model smoke test: {realModel != null}.");
        }
        finally
        {
            File.Delete(fixture);
            Directory.Delete(temp);
        }
    }

    private static int Gray(int r, int g, int b) => (299 * r + 587 * g + 114 * b + 500) / 1000;
    private sealed class CapturedProgress(Action<double>? action = null) : IProgress<double>
    {
        public List<double> Values { get; } = [];
        public void Report(double value) { Values.Add(value); action?.Invoke(value); }
    }
}
