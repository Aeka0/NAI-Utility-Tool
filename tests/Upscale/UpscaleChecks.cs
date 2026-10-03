using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text.Json;
using NAITool.Services;
using SkiaSharp;

internal static class UpscaleChecks
{
    private static int _checks;
    private static void Check(bool value, string scenario)
    {
        _checks++;
        if (!value) throw new InvalidOperationException(scenario);
    }

    public static async Task RunAsync()
    {
        // Fixtures from the official production client, 2026-10-03. Check both sides
        // of every tier and non-square inputs: the price is based on total input pixels.
        var rules = typeof(NovelAIService).Assembly.GetType("NAITool.Services.NovelAiUpscaleRules")!;
        var estimate = rules.GetMethod("EstimateAnlas", BindingFlags.Static | BindingFlags.NonPublic)!;
        int? Cost(int width, int height) => (int?)estimate.Invoke(null, [width, height]);
        foreach (var (width, height, cost) in new (int, int, int?)[]
        {
            (1, 1, 1), (832, 1216, 1), (1024, 1024, 1), (1024, 1536, 2),
            (1472, 1472, 3), (1536, 2048, 4), (2048, 1536, 4), (2048, 2048, null),
            (0, 1024, null), (-1, -1, null), (int.MaxValue, int.MaxValue, null),
            (1, 1048576, 1), (1, 1048577, 2), (1, 1747627, 2), (1, 1747628, 3),
            (1, 2446678, 3), (1, 2446679, 4), (1, 3145728, 4), (1, 3145729, null),
        }) Check(Cost(width, height) == cost, $"Pricing {width} × {height}: {cost}");

        await CheckTransportAsync();
        Console.WriteLine($"Passed {_checks} official upscale checks (in-memory HTTP; no account data or network requests).");
    }

    private static byte[] Image(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(new SKColor(80, 120, 160, 128));
        using var data = bitmap.Encode(format, 100);
        return data.ToArray();
    }

    private static byte[] Zip(byte[]? image)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("metadata.json").Open())) writer.Write("{}");
            if (image != null)
            {
                using var entry = archive.CreateEntry("image.png").Open();
                entry.Write(image);
            }
        }
        return stream.ToArray();
    }

    private static async Task CheckTransportAsync()
    {
        var settings = new SettingsService(); // Do not load or save the user's settings.
        settings.SetApiTokens(["upscale-test-token"]);
        settings.Settings.ApiBaseUrl = "https://unused.invalid/custom-generation";
        settings.Settings.UseProxy = false;
        using var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        using var service = new NovelAIService(settings);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(NovelAIService).GetField("_httpClient", flags)!.SetValue(service, client);
        typeof(NovelAIService).GetField("_httpClientProxyKey", flags)!.SetValue(service, "");
        byte[] input = Image(8, 12), output = Image(16, 24);
        handler.ResponseBytes = Zip(output);
        var result = await service.UpscaleImageAsync(input);
        Check(result.Error == null && result.ImageBytes!.SequenceEqual(output), "ZIP metadata entry is skipped; original result bytes survive.");
        Check(handler.Uri == "https://image.novelai.net/ai/upscale", "Official route ignores custom generation URL.");
        Check(handler.Authorization == "Bearer upscale-test-token" && handler.Accept == "application/zip", "Explicit token and ZIP negotiation.");
        Check(handler.Payload.EnumerateObject().Count() == 3, "Only current protocol fields are sent, no legacy width/height/scale.");
        Check(handler.Payload.GetProperty("model").GetString() == "nai-diffusion-5-curated", "Official upscaler model.");
        Check(handler.Payload.GetProperty("declared_blur_sigma").GetInt32() == 0, "Official blur default.");
        Check(Convert.FromBase64String(handler.Payload.GetProperty("image").GetString()!).SequenceEqual(input), "PNG input and metadata are passed through unchanged.");

        handler.ResponseBytes = output;
        result = await service.UpscaleImageAsync(Image(8, 12, SKEncodedImageFormat.Webp));
        using (var codec = SKCodec.Create(new MemoryStream(Convert.FromBase64String(handler.Payload.GetProperty("image").GetString()!))))
            Check(codec.EncodedFormat == SKEncodedImageFormat.Png && codec.Info.Width == 8 && codec.Info.Height == 12,
                "Other formats are converted to PNG without resizing.");
        Check(result.Error == null && result.ImageBytes!.SequenceEqual(output), "Raw PNG response accepted.");

        handler.ResponseBytes = Zip(Image(2048, 3072));
        result = await service.UpscaleImageAsync(Image(1024, 1536));
        Check(result.Error == null && result.ImageBytes != null, "Portrait above the old 1024-per-side limit is accepted.");
        handler.ResponseBytes = output;

        int before = handler.Count;
        result = await service.UpscaleImageAsync(Image(2048, 2048));
        Check(result.Error != null && handler.Count == before, "Oversized input rejected before HTTP.");
        result = await service.UpscaleImageAsync([1, 2, 3]);
        Check(result.Error != null && handler.Count == before, "Undecodable image rejected before HTTP.");
        settings.SetApiTokens([" "]);
        result = await service.UpscaleImageAsync(input);
        Check(result.Error != null && handler.Count == before, "Blank token rejected before HTTP.");
        settings.SetApiTokens(["upscale-test-token"]);
        settings.Settings.AccountAssetProtectionMode = true;
        settings.Settings.AccountAssetProtectionDisablePaidFeatures = true;
        result = await service.UpscaleImageAsync(input);
        Check(result.Error != null && handler.Count == before, "Paid-feature protection enforced at service boundary.");
        settings.Settings.AccountAssetProtectionDisablePaidFeatures = false;
        result = await service.UpscaleImageAsync(input);
        Check(result.Error == null && handler.Count == before + 1, "Asset protection with paid features allowed does not block upscale.");
        before = handler.Count;
        settings.Settings.AccountAssetProtectionMode = false;
        result = await service.UpscaleImageAsync(input, new CancellationToken(true));
        Check(result.Error != null && handler.Count == before, "Cancelled request never reaches HTTP.");

        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.PaymentRequired, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
        {
            handler.Status = status;
            before = handler.Count;
            result = await service.UpscaleImageAsync(input);
            Check(result.ImageBytes == null && result.Error!.Contains(((int)status).ToString()) && handler.Count == before + 1,
                $"HTTP {(int)status} is surfaced without an automatic paid retry.");
        }
        handler.Status = HttpStatusCode.OK;
        handler.ResponseBytes = Zip(null);
        result = await service.UpscaleImageAsync(input);
        Check(result.ImageBytes == null && result.Error != null, "ZIP without image fails safely.");
        handler.ResponseBytes = Zip(input);
        result = await service.UpscaleImageAsync(input);
        Check(result.ImageBytes == null && result.Error != null, "Wrong output dimensions fail before replacing workspace image.");
        handler.ResponseBytes = [1, 2, 3];
        result = await service.UpscaleImageAsync(input);
        Check(result.ImageBytes == null && result.Error != null, "Malformed response fails safely.");
        handler.ThrowTimeout = true;
        before = handler.Count;
        result = await service.UpscaleImageAsync(input);
        Check(result.ImageBytes == null && result.Error != null && handler.Count == before + 1, "Timeout is reported without retry.");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public int Count { get; private set; }
        public string? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Accept { get; private set; }
        public JsonElement Payload { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public byte[] ResponseBytes { get; set; } = [];
        public bool ThrowTimeout { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Check(request.Method == HttpMethod.Post, "POST upscale request.");
            Uri = request.RequestUri!.AbsoluteUri;
            Authorization = request.Headers.Authorization!.ToString();
            Accept = request.Headers.Accept.ToString();
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Payload = json.RootElement.Clone();
            Count++;
            if (ThrowTimeout) throw new TaskCanceledException("Simulated timeout");
            return new HttpResponseMessage(Status)
            {
                Content = Status == HttpStatusCode.OK ? new ByteArrayContent(ResponseBytes) : new StringContent("test response"),
            };
        }
    }
}
