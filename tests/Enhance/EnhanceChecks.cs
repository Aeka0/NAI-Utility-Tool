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
