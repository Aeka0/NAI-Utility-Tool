using System.Net;
using System.Reflection;
using System.Text.Json;
using NAITool.Services;
using SkiaSharp;

internal static class EnhanceChecks
{
    private const string V5 = "nai-diffusion-5-full";
    private static int _checks;
    private static readonly Type Rules = typeof(NovelAIService).Assembly.GetType("NAITool.Services.EnhanceRules")!;
    private static T Rule<T>(string name, params object[] args) =>
        (T)Rules.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!;
    private static void Check(bool condition, string scenario)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(scenario);
    }

    public static async Task RunAsync()
    {
        CheckDimensionsAndSettings();
        CheckImagePreparation();
        CheckInpaintMaskEncoding();
        await CheckRequestsAsync();
        Console.WriteLine($"Passed {_checks} Enhance checks (in-memory HTTP; no account data or network requests).");
    }

    private static void CheckDimensionsAndSettings()
    {
        var settings = new AppSettings
        {
            EnhanceMagnitude = 99,
            EnhanceStrength = double.NaN,
            EnhanceNoise = double.PositiveInfinity,
            EnhanceUpscaleAmount = double.NaN,
        };
        settings.Normalize();
        Check(settings.EnhanceMagnitude == 5 && settings.EnhanceStrength == 0.5 &&
            settings.EnhanceNoise == 0 && settings.EnhanceUpscaleAmount == 1.5, "Invalid saved settings normalize.");
        Check(Rule<(double, double)>("GetMagnitudeValues", 1) == (0.2, 0), "Magnitude 1.");
        Check(Rule<(double, double)>("GetMagnitudeValues", 3) == (0.5, 0), "Magnitude 3.");
        Check(Rule<(double, double)>("GetMagnitudeValues", 5) == (0.7, 0.1), "Magnitude 5 adds noise.");
        foreach (var size in new[] { (832, 1216), (1216, 832), (1024, 1024) })
            Check(Rule<List<double>>("GetAvailableAmounts", size.Item1, size.Item2).SequenceEqual(new[] { 1d, 1.5 }), "Portrait/square supported amounts.");
        var small = Rule<List<double>>("GetAvailableAmounts", 512, 512);
        Check(small.SequenceEqual(new[] { 1d, 1.5, 2d }), "Small images permit 2x.");
        Check(Rule<List<double>>("GetAvailableAmounts", 1000, 1000).SequenceEqual(new[] { 1d }), "Unaligned scaling is omitted.");
        foreach (var size in new[] { (0, 1024), (-1, 512), (2048, 2048), (int.MaxValue, int.MaxValue) })
            Check(Rule<List<double>>("GetAvailableAmounts", size.Item1, size.Item2).Count == 0, "Invalid/oversized source rejected.");
        Check(Rule<double>("GetPreferredAmount", new List<double> { 1d }, 2d) == 1, "Remembered unsupported scale falls back.");
        Check(Rule<bool>("CanUseMax", V5, 1024, 1024), "V5 supports Max.");
        Check(!Rule<bool>("CanUseMax", "nai-diffusion-4-5-full", 1024, 1024), "V4.5 omits Max.");
        Check(!Rule<bool>("CanUseMax", V5, 2048, 1536), "Near-limit images omit Max.");
        Check(Rule<(int, int)>("GetOutputDimensions", 832, 1216, 1.5, false) == (1248, 1824), "Portrait 1.5x dimensions.");
        foreach (var size in new[] { (512, 512), (832, 1216), (1216, 832), (1024, 1024), (1024, 2048) })
        {
            var output = Rule<(int W, int H)>("GetOutputDimensions", size.Item1, size.Item2, 1d, true);
            Check((long)output.W * output.H <= 3L * 1024 * 1024 && output.W <= size.Item1 * 2 && output.H <= size.Item2 * 2,
                "Max stays within 3 MP and 2x.");
        }
    }

    private static void CheckImagePreparation()
    {
        using var bitmap = new SKBitmap(64, 64);
        bitmap.Erase(new SKColor(70, 90, 120, 128));
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var prepare = typeof(NovelAIService).GetMethod("PrepareEnhancedImageBase64", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (int side in new[] { 64, 96, 128 })
        {
            var result = (string)prepare.Invoke(null, [png.ToArray(), side, side])!;
            using var decoded = SKBitmap.Decode(Convert.FromBase64String(result));
            Check(decoded.Width == side && decoded.Height == side, "Prepared image matches request dimensions.");
            Check(decoded.GetPixel(0, 0).Alpha == 128, "Lossless preparation retains transparency.");
        }
        try
        {
            prepare.Invoke(null, [png.ToArray(), int.MaxValue, int.MaxValue]);
            throw new InvalidOperationException("Oversized allocation was allowed.");
        }
        catch (TargetInvocationException error) when (error.InnerException is ArgumentOutOfRangeException)
        { Check(true, "Reject oversized allocation before decoding."); }
    }

    private static string EncodeForRequest(SKBitmap bitmap, bool isMask) =>
        (string)typeof(NovelAIService).GetMethod("EncodeBitmapForRequest", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [bitmap, isMask])!;

    private static void CheckInpaintMaskEncoding()
    {
        foreach (var (width, height) in new[] { (1024, 1024), (832, 1216), (1216, 832), (3, 5), (1, 1), (1, 8), (8, 1) })
        {
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            bitmap.Erase(new SKColor(70, 90, 120, 128));
            var originalPixels = bitmap.Pixels;
            foreach (bool isMask in new[] { true, false, true })
            {
                byte[] bytes = Convert.FromBase64String(EncodeForRequest(bitmap, isMask));
                Check(System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" &&
                    System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP", "Request encoding remains WebP.");
                using var decoded = SKBitmap.Decode(bytes);
                Check(decoded.Width == width && decoded.Height == height,
                    "Mask and source image encoding retain the original generation dimensions.");
                if (isMask)
                    Check(decoded.Pixels.All(pixel => pixel == SKColors.Black),
                        "Alpha below the threshold becomes opaque black throughout the lossless mask.");
                else
                {
                    Check(decoded.GetPixel(0, 0).Alpha == 128, "Source image encoding preserves alpha.");
                    Check(decoded.Pixels.SequenceEqual(originalPixels), "Source image encoding remains lossless.");
                }
                Check(bitmap.Width == width && bitmap.Height == height && bitmap.Pixels.SequenceEqual(originalPixels),
                    "Encoding never mutates, binarizes, or repeatedly shrinks the original bitmap.");
            }
        }

        using var mask = new SKBitmap(new SKImageInfo(64, 48, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (int y = 0; y < mask.Height; y++)
        for (int x = 0; x < mask.Width; x++)
            mask.SetPixel(x, y, y < 24
                ? (x < 32 ? SKColors.White : SKColors.Transparent)
                : (x < 32 ? SKColors.Black : new SKColor(255, 255, 255, 155)));
        using var resized = SKBitmap.Decode(Convert.FromBase64String(EncodeForRequest(mask, true)));
        Check(resized.Width == 64 && resized.Height == 48, "Non-square mask returns to the exact original dimensions.");
        Check(resized.GetPixel(16, 12) == SKColors.White && resized.GetPixel(48, 12) == SKColors.Black &&
            resized.GetPixel(16, 36) == SKColors.White && resized.GetPixel(48, 36) == SKColors.Black,
            "Mask coverage keeps its relative position and comes from alpha, even for opaque black source pixels.");
        Check(resized.GetPixel(31, 12) == SKColors.White && resized.GetPixel(32, 12) == SKColors.Black,
            "Nearest-neighbor resizing and lossless encoding retain the hard binary edge.");
        Check(resized.Pixels.All(pixel => pixel == SKColors.White || pixel == SKColors.Black),
            "Every output mask pixel is fully opaque and strictly black or white.");

        // Each 8x8 source block maps to one coarse pixel and back. Cover every alpha value and varied RGB.
        using var ramp = new SKBitmap(new SKImageInfo(2048, 32, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (int y = 0; y < ramp.Height; y++)
        for (int x = 0; x < ramp.Width; x++)
        {
            byte alpha = (byte)(x / 8);
            var color = (y / 8) switch
            {
                0 => new SKColor(255, 255, 255, alpha),
                1 => new SKColor(0, 0, 0, alpha),
                2 => new SKColor(70, 90, 120, alpha),
                _ => new SKColor(255, 0, 0, alpha),
            };
            ramp.SetPixel(x, y, color);
        }
        var rampPixels = ramp.Pixels;
        using var binaryRamp = SKBitmap.Decode(Convert.FromBase64String(EncodeForRequest(ramp, true)));
        Check(binaryRamp.Width == 2048 && binaryRamp.Height == 32, "Alpha ramp is restored to full request dimensions.");
        for (int y = 0; y < binaryRamp.Height; y++)
        {
            Check(binaryRamp.GetPixel(155 * 8 + 7, y) == SKColors.Black, "Alpha 155 is opaque black regardless of RGB.");
            Check(binaryRamp.GetPixel(156 * 8, y) == SKColors.White, "Alpha 156 is opaque white regardless of RGB.");
            Check(Enumerable.Range(0, binaryRamp.Width).All(x => binaryRamp.GetPixel(x, y) == (x / 8 > 155 ? SKColors.White : SKColors.Black)),
                "All alpha values obey the strict >155 rule, with no intermediate colors or transparency.");
        }
        Check(ramp.Pixels.SequenceEqual(rampPixels), "Binarization leaves the original mask bitmap untouched.");
        CheckInpaintMaskGrid();
    }

    private static void CheckInpaintMaskGrid()
    {
        using var isolatedSamples = new SKBitmap(new SKImageInfo(32, 24, SKColorType.Bgra8888, SKAlphaType.Premul));
        isolatedSamples.Erase(SKColors.Transparent);
        for (int y = 0; y < 3; y++)
        for (int x = 0; x < 4; x++)
            if ((x + y) % 2 == 0)
                isolatedSamples.SetPixel(x * 8 + 3, y * 8 + 3, SKColors.White);
        // At exact center ties, Skia nearest-neighbor selects the upper/left pixel (3 in each 8-pixel cell).
        var original = isolatedSamples.Pixels;
        using var expanded = SKBitmap.Decode(Convert.FromBase64String(EncodeForRequest(isolatedSamples, true)));
        Check(expanded.Width == 32 && expanded.Height == 24, "Coarse-grid mask restores full dimensions.");
        Check(Enumerable.Range(0, 32 * 24).All(index => expanded.GetPixel(index % 32, index / 32) ==
            (((index % 32) / 8 + (index / 32) / 8) % 2 == 0 ? SKColors.White : SKColors.Black)),
            "Direct 1/8 nearest sampling selects original cell centers and expands to exact 8x8 binary blocks.");
        Check(isolatedSamples.Pixels.SequenceEqual(original), "Coarse sampling leaves the original fine mask untouched.");

        foreach (var (width, height) in new[] { (65, 49), (17, 25), (9, 9), (7, 3), (1, 8), (8, 1) })
        {
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                bitmap.SetPixel(x, y, new SKColor(70, 90, 120, (byte)((x * 37 + y * 53) % 256)));
            var pixels = bitmap.Pixels;
            using var decoded = SKBitmap.Decode(Convert.FromBase64String(EncodeForRequest(bitmap, true)));
            int gridWidth = Math.Max(1, width / 8), gridHeight = Math.Max(1, height / 8);
            bool matches = true;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int gridX = Math.Min(gridWidth - 1, (int)Math.Ceiling((x + 0.5) * gridWidth / width) - 1);
                int gridY = Math.Min(gridHeight - 1, (int)Math.Ceiling((y + 0.5) * gridHeight / height) - 1);
                int sourceX = Math.Min(width - 1, (int)Math.Ceiling((gridX + 0.5) * width / gridWidth) - 1);
                int sourceY = Math.Min(height - 1, (int)Math.Ceiling((gridY + 0.5) * height / gridHeight) - 1);
                var expected = pixels[sourceY * width + sourceX].Alpha > 155 ? SKColors.White : SKColors.Black;
                matches &= decoded.GetPixel(x, y) == expected;
            }
            Check(decoded.Width == width && decoded.Height == height, "Non-multiple and tiny masks retain exact output dimensions.");
            Check(matches, "Non-multiple masks use floor(W/8), floor(H/8), minimum one, and nearest-center sampling both ways.");
            Check(bitmap.Pixels.SequenceEqual(pixels), "Non-multiple mask encoding does not modify the input.");
        }
    }

    private static async Task CheckRequestsAsync()
    {
        var settings = new SettingsService(); // Never load or save real account settings.
        settings.SetApiTokens(["enhance-test-token"]);
        settings.Settings.UseProxy = false;
        using var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        using var service = new NovelAIService(settings);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(NovelAIService).GetField("_httpClient", flags)!.SetValue(service, client);
        typeof(NovelAIService).GetField("_httpClientProxyKey", flags)!.SetValue(service, "");
        var parameters = new NAIParameters
        {
            Model = V5, Seed = "4294967295", Steps = 28, QualityToggle = false,
            DenoiseStrength = 0.6, DenoiseNoise = 0.2,
        };
        foreach (var mode in new[] { (false, false), (true, false), (true, true) })
        {
            await service.ImageToImageAsync("AA==", 1024, 1024, "portrait, Text: test", "negative",
                parametersOverride: parameters, isEnhance: mode.Item1, upscaledEnhance: mode.Item2);
            var payload = handler.Payload;
            var sent = payload.GetProperty("parameters");
            Check(!sent.TryGetProperty("inpaintImg2ImgStrength", out _) && !sent.TryGetProperty("img2img", out _),
                "Plain img2img and Enhance omit masked inpainting controls.");
            Check(sent.GetProperty("width").GetInt32() == 1024 && sent.GetProperty("height").GetInt32() == 1024, "Request dimensions preserved.");
            Check(sent.GetProperty("strength").GetDouble() == 0.6 && sent.GetProperty("noise").GetDouble() == 0.2, "Strength/noise reach API.");
            Check(sent.GetProperty("params_version").GetInt32() == (mode.Item1 ? 4 : 3), "Protocol change is scoped to Enhance.");
            Check(sent.GetProperty("v4_negative_prompt").GetProperty("legacy_uc").GetBoolean() == !mode.Item1,
                "V5 Enhance uses modern negative prompts without changing plain img2img.");
            Check(sent.TryGetProperty("upscaled_enhance", out var max) == mode.Item2 && (!mode.Item2 || max.GetBoolean()), "Max uses the API upscale flag.");
            string prompt = payload.GetProperty("input").GetString()!;
            Check(prompt.Contains("-2::upscaled, blurry::") == (mode.Item1 && !mode.Item2), "Prompt hint only on standard Enhance.");
            if (mode.Item1)
            {
                Check(sent.GetProperty("extra_noise_seed").GetUInt64() == 4294967294, "Extra noise seed preserves large integers.");
                Check(!sent.GetProperty("sm").GetBoolean() && !sent.GetProperty("color_correct").GetBoolean() &&
                    sent.GetProperty("add_original_image").GetBoolean(), "Enhance request flags.");
            }
            else Check(!sent.TryGetProperty("extra_noise_seed", out _), "Plain img2img is unchanged.");
        }
        int count = handler.Count;
        parameters.Steps = 100;
        parameters.DenoiseStrength = 0.99;
        var blocked = await service.ImageToImageAsync("AA==", 1024, 1024, "test", "",
            parametersOverride: parameters, isEnhance: true, upscaledEnhance: true);
        Check(handler.Count == count && blocked.Error != null, "Max cost limit uses output dimensions before HTTP.");
        parameters.Model = "nai-diffusion-4-5-full";
        blocked = await service.ImageToImageAsync("AA==", 512, 512, "test", "",
            parametersOverride: parameters, isEnhance: true, upscaledEnhance: true);
        Check(handler.Count == count && blocked.Error != null, "Unsupported Max model rejected before HTTP.");
        await CheckInpaintRequestsAsync(settings, service, handler);
    }

    private static async Task CheckInpaintRequestsAsync(SettingsService settings, NovelAIService service, CaptureHandler handler)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(512, 512, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.White);
        string imageBase64 = EncodeForRequest(bitmap, false);
        string maskBase64 = EncodeForRequest(bitmap, true);
        using var encodedImage = SKBitmap.Decode(Convert.FromBase64String(imageBase64));
        using var encodedMask = SKBitmap.Decode(Convert.FromBase64String(maskBase64));
        Check(encodedImage.Width == 512 && encodedImage.Height == 512 && encodedMask.Width == 512 && encodedMask.Height == 512,
            "Inpainting fixture uses full-size image and mask after the 1/8-grid pass.");
        foreach (string model in new[] { "nai-diffusion-4-full-inpainting", "nai-diffusion-4-5-full-inpainting",
            "nai-diffusion-5-full-inpainting", "nai-diffusion-3-inpainting" })
        foreach (bool stream in new[] { false, true })
        foreach (double strength in new[] { 0d, 0.01, 0.5, 1d, -0.5, 1.5 })
        {
            var parameters = settings.Settings.InpaintParameters;
            parameters.Model = model;
            parameters.Seed = "12345";
            parameters.InpaintStrength = strength;
            parameters.InpaintNoise = 0.2;
            settings.Settings.StreamGeneration = stream;
            int count = handler.Count;
            await service.InpaintAsync(imageBase64, maskBase64, 512, 512, "test", "negative");
            Check(handler.Count == count + 1, "Inpainting request reaches HTTP capture.");
            var payload = handler.Payload;
            var sent = payload.GetProperty("parameters");
            bool supported = model != "nai-diffusion-3-inpainting";
            double expected = supported ? Math.Clamp(strength, 0, 1) : 1;
            Check(payload.GetProperty("action").GetString() == "infill" &&
                payload.GetProperty("model").GetString() == model &&
                sent.GetProperty("image").GetString() == imageBase64 && sent.GetProperty("mask").GetString() == maskBase64,
                "Masked inpainting retains its action, model and source images.");
            Check(sent.GetProperty("width").GetInt32() == 512 && sent.GetProperty("height").GetInt32() == 512,
                "Coarse-grid masks do not change the requested generation dimensions.");
            Check(sent.GetProperty("add_original_image").GetBoolean(), "Binary masks preserve add_original_image.");
            Check(sent.TryGetProperty("stream", out var streamValue) == stream && (!stream || streamValue.GetString() == "sse"),
                "Inpainting preserves streaming selection.");
            Check(sent.GetProperty("strength").GetDouble() == expected &&
                sent.GetProperty("noise").GetDouble() == (supported ? 0.2 : 0), "Legacy top-level image parameters stay compatible.");
            Check(sent.TryGetProperty("inpaintImg2ImgStrength", out var metadataStrength) == supported &&
                (!supported || metadataStrength.GetDouble() == expected), "Supported inpainting models retain the selected masked strength.");
            bool hasImg2Img = sent.TryGetProperty("img2img", out var img2img);
            Check(hasImg2Img == (supported && expected < 1), "Only partial-strength supported inpainting adds the img2img pass.");
            if (hasImg2Img)
                Check(img2img.GetProperty("strength").GetDouble() == expected && img2img.GetProperty("color_correct").GetBoolean(),
                    "Masked img2img preserves zero and low strengths with color correction.");
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public int Count { get; private set; }
        public JsonElement Payload { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"subscription":{"tier":3,"active":true,"trainingStepsLeft":1000,"usage":{"percent":100,"isNegative":false}}}"""),
                };
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Payload = json.RootElement.Clone();
            Count++;
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("test response") };
        }
    }
}
