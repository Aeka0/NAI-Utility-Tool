using SkiaSharp;

namespace NAITool.Services;

public partial class NovelAIService
{
    // The V5 web client selects the closest aspect ratio from these reference
    // sizes (ordered by aspect ratio) and center-crops the image to fill it.
    private static readonly (int Width, int Height)[] V5DirectorReferenceSizes =
    [
        (256, 4096), (256, 4032), (256, 3968), (256, 3904), (256, 3840), (256, 3776), (256, 3712),
        (256, 3648), (256, 3584), (256, 3520), (256, 3456), (256, 3392), (256, 3328),
        (320, 3264), (320, 3200), (320, 3136), (320, 3072), (320, 3008), (320, 2944), (320, 2880),
        (320, 2816), (320, 2752), (384, 2688), (384, 2624), (384, 2560), (384, 2496), (384, 2432),
        (384, 2368), (448, 2304), (448, 2240), (448, 2176), (448, 2112), (512, 2048), (512, 1984),
        (512, 1920), (512, 1856), (576, 1792), (576, 1728), (576, 1664), (640, 1600), (640, 1536),
        (704, 1472), (704, 1408), (768, 1344), (768, 1280), (832, 1216), (896, 1152), (960, 1088),
        (1024, 1024), (1088, 960), (1152, 896), (1216, 832), (1280, 768), (1344, 768), (1408, 704),
        (1472, 704), (1536, 640), (1600, 640), (1664, 576), (1728, 576), (1792, 576), (1856, 512),
        (1920, 512), (1984, 512), (2048, 512), (2112, 448), (2176, 448), (2240, 448), (2304, 448),
        (2368, 384), (2432, 384), (2496, 384), (2560, 384), (2624, 384), (2688, 384), (2752, 320),
        (2816, 320), (2880, 320), (2944, 320), (3008, 320), (3072, 320), (3136, 320), (3200, 320),
        (3264, 320), (3328, 256), (3392, 256), (3456, 256), (3520, 256), (3584, 256), (3648, 256),
        (3712, 256), (3776, 256), (3840, 256), (3904, 256), (3968, 256), (4032, 256), (4096, 256),
    ];

    private static string PrepareV5DirectorReferenceImage(string base64Image)
    {
        using var bitmap = SKBitmap.Decode(Convert.FromBase64String(base64Image));
        if (bitmap == null)
            return base64Image;

        double logAspect = Math.Log((double)bitmap.Width / bitmap.Height);
        var (width, height) = V5DirectorReferenceSizes.MinBy(size =>
            Math.Abs(logAspect - Math.Log((double)size.Width / size.Height)));

        if (bitmap.Width == width && bitmap.Height == height)
            return base64Image;

        // Draw the center crop straight into the target bitmap so an extreme
        // aspect ratio never requires a larger intermediate resized bitmap.
        double scale = Math.Max((double)width / bitmap.Width, (double)height / bitmap.Height);
        float resizedWidth = (float)Math.Floor(bitmap.Width * scale + 0.5);
        float resizedHeight = (float)Math.Floor(bitmap.Height * scale + 0.5);
        float left = -MathF.Floor((resizedWidth - width) / 2);
        float top = -MathF.Floor((resizedHeight - height) / 2);
        var destination = new SKRect(left, top, left + resizedWidth, top + resizedHeight);

        using var cropped = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(cropped);
        canvas.Clear(SKColors.Transparent);
        using var source = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(source, destination, new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var result = SKImage.FromBitmap(cropped);
        using var data = result.Encode(SKEncodedImageFormat.Png, 100);
        return Convert.ToBase64String(data.ToArray());
    }

    private static string PrepareDirectorReferenceImage(string base64Image)
    {
        const int TargetW = 1024;
        const int TargetH = 1536;

        byte[] bytes = Convert.FromBase64String(base64Image);
        using var bitmap = SKBitmap.Decode(bytes);
        if (bitmap == null)
            return base64Image;

        if (bitmap.Width == TargetW && bitmap.Height == TargetH
            && bitmap.ColorType == SKColorType.Rgb888x)
            return base64Image;

        float srcRatio = (float)bitmap.Width / bitmap.Height;
        float tgtRatio = (float)TargetW / TargetH;

        int newW, newH;
        if (srcRatio > tgtRatio)
        {
            newW = TargetW;
            newH = (int)(TargetW / srcRatio);
        }
        else
        {
            newH = TargetH;
            newW = (int)(TargetH * srcRatio);
        }

        using var resized = bitmap.Resize(new SKImageInfo(newW, newH, SKColorType.Rgb888x, SKAlphaType.Opaque), SKSamplingOptions.Default);
        if (resized == null)
            return base64Image;

        using var canvas = new SKBitmap(TargetW, TargetH, SKColorType.Rgb888x, SKAlphaType.Opaque);
        canvas.Erase(SKColors.Black);
        using var c = new SKCanvas(canvas);
        int offsetX = (TargetW - newW) / 2;
        int offsetY = (TargetH - newH) / 2;
        c.DrawBitmap(resized, offsetX, offsetY);

        using var image = SKImage.FromBitmap(canvas);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return Convert.ToBase64String(data.ToArray());
    }

    private static async Task ApplyPreciseReferenceParametersAsync(
        Dictionary<string, object?> parameters, string model, List<PreciseReferenceInfo> preciseReferences,
        CancellationToken ct)
    {
        bool isV5 = PreciseReferenceRules.SupportsV5Options(model);
        parameters["director_reference_images"] = await Task.Run(() =>
        {
            var images = new List<string>(preciseReferences.Count);
            foreach (var reference in preciseReferences)
            {
                ct.ThrowIfCancellationRequested();
                images.Add(isV5 ? PrepareV5DirectorReferenceImage(reference.ImageBase64)
                    : PrepareDirectorReferenceImage(reference.ImageBase64));
            }
            return images;
        }, ct);
        parameters["director_reference_strength_values"] = preciseReferences
            .Select(x => Math.Clamp(x.Strength, 0, PreciseReferenceRules.MaxStrength(model))).ToList();
        parameters["director_reference_secondary_strength_values"] = preciseReferences
            .Select(x => isV5 ? 0.0 : Math.Round(1.0 - Math.Clamp(x.Fidelity, 0, 1), 2)).ToList();
        parameters["director_reference_information_extracted"] = preciseReferences
            .Select(_ => 1.0).ToList();
        parameters["director_reference_descriptions"] = preciseReferences
            .Select(x => new Dictionary<string, object?>
            {
                ["caption"] = new Dictionary<string, object?>
                {
                    ["base_caption"] = PreciseReferenceRules.ToApiCaption(x, isV5),
                    ["char_captions"] = new List<object>(),
                },
                ["legacy_uc"] = false,
            }).ToList();
    }
}
