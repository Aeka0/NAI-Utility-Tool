using System.Runtime.InteropServices.WindowsRuntime;
using NAITool.Services;
using Windows.Graphics.Imaging;

int checks = 0;
void Check(bool condition, string scenario)
{
    checks++;
    if (!condition) throw new Exception(scenario);
}

async Task WritePngAsync(string path, int width, int height)
{
    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
    using var stream = file.AsRandomAccessStream();
    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
    var pixels = new byte[width * height * 4];
    for (int i = 0; i < pixels.Length; i += 4)
    {
        pixels[i] = 32; pixels[i + 1] = 96; pixels[i + 2] = 192; pixels[i + 3] = 255;
    }
    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
        (uint)width, (uint)height, 96, 96, pixels);
    await encoder.FlushAsync();
}

var fixture = Directory.CreateTempSubdirectory("NAITool-thumbnail-tests-");
try
{
    string landscape = Path.Combine(fixture.FullName, "landscape.png");
    string portrait = Path.Combine(fixture.FullName, "portrait.png");
    string tiny = Path.Combine(fixture.FullName, "tiny.png");
    await WritePngAsync(landscape, 4096, 2048);
    await WritePngAsync(portrait, 2048, 4096);
    await WritePngAsync(tiny, 1, 1);
    var thumbnails = await Task.WhenAll(
        HistoryThumbnailService.DecodeAsync(landscape, 200, 140, CancellationToken.None),
        HistoryThumbnailService.DecodeAsync(portrait, 200, 140, CancellationToken.None));
    Check(thumbnails[0].Width == 200 && thumbnails[0].Height == 100, "Landscape fits available width");
    Check(thumbnails[1].Width == 70 && thumbnails[1].Height == 140, "Portrait fits available height");
    foreach (var thumbnail in thumbnails)
    {
        Check(thumbnail.Pixels.Length == thumbnail.Width * thumbnail.Height * 4, "Only thumbnail-sized BGRA buffer crosses to UI");
        Check(thumbnail.Pixels[3] == 255 && thumbnail.Pixels[2] > thumbnail.Pixels[0], "Alpha and BGRA channel order are retained");
    }
    var highDpi = await HistoryThumbnailService.DecodeAsync(landscape, 400, 280, CancellationToken.None);
    Check(highDpi.Width == 400 && highDpi.Height == 200, "200 percent DPI uses scaled physical pixels");
    var small = await HistoryThumbnailService.DecodeAsync(tiny, 200, 140, CancellationToken.None);
    Check(small.Width == 1 && small.Height == 1 && small.Pixels.Length == 4, "Tiny images are not needlessly upscaled");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try
    {
        await HistoryThumbnailService.DecodeAsync(landscape, 200, 140, cancellation.Token);
        throw new Exception("Canceled thumbnail unexpectedly completed");
    }
    catch (OperationCanceledException) { checks++; }
    string corrupt = Path.Combine(fixture.FullName, "corrupt.png");
    await File.WriteAllTextAsync(corrupt, "not a PNG");
    bool failed = false;
    try { await HistoryThumbnailService.DecodeAsync(corrupt, 200, 140, CancellationToken.None); }
    catch { failed = true; }
    Check(failed, "Corrupt image is reported, not silently treated as decoded");
    // A completed decode must not retain the source stream or block rename/delete.
    using var exclusive = new FileStream(landscape, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    Check(exclusive.Length > 0, "Decode closes file handles");
}
finally { fixture.Delete(recursive: true); }
Console.WriteLine($"Passed {checks} Windows thumbnail checks.");
