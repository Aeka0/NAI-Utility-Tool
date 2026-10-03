using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace NAITool.Services;

public partial class NovelAIService
{
    private const string OfficialUpscaleUrl = "https://image.novelai.net/ai/upscale";
    private static readonly TimeSpan OfficialUpscaleRequestTimeout = TimeSpan.FromSeconds(120);

    public async Task<(byte[]? ImageBytes, string? Error)> UpscaleImageAsync(
        byte[] imageBytes, CancellationToken ct = default)
    {
        if (!_settings.HasApiTokens)
            return (null, L("api.error.token_missing_network"));
        if (_settings.Settings.AccountAssetProtectionMode &&
            _settings.Settings.AccountAssetProtectionDisablePaidFeatures)
            return (null, L("upscale.official.protection_blocked"));

        var stopwatch = Stopwatch.StartNew();
        var payload = new Dictionary<string, object>();
        try
        {
            // Validate the actual image, not caller-supplied dimensions. Decode/encode off the UI thread.
            var prepared = await Task.Run(() => PrepareOfficialUpscaleImage(imageBytes), ct);
            payload["image"] = prepared.Base64;
            payload["model"] = NovelAiUpscaleRules.Model;
            payload["declared_blur_sigma"] = 0;

            using var request = new HttpRequestMessage(HttpMethod.Post, OfficialUpscaleUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
            };
            // Explicitly target the official service, independently of a custom generation URL.
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
            var account = await SelectAccountAsync(
                new ApiRequestCost(false, NovelAiUpscaleRules.EstimateAnlas(prepared.Width, prepared.Height)!.Value), ct);
            using var timeout = CreateRequestTimeoutTokenSource(OfficialUpscaleRequestTimeout, ct);
            using var response = await SendAccountRequestAsync(request, account,
                System.Net.Http.HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync(timeout.Token);
                WriteRequestLog("NovelAI upscale", payload, stopwatch.ElapsedMilliseconds, response, error);
                return (null, Lf("api.error.status", (int)response.StatusCode, error));
            }

            byte[]? result = await ReadGeneratedImageBytesAsync(response.Content, timeout.Token, findImageEntry: true);
            if (result == null)
                return (null, L("api.error.empty_zip"));
            using var codec = SKCodec.Create(new MemoryStream(result));
            if (codec == null || codec.Info.Width != prepared.Width * NovelAiUpscaleRules.Scale ||
                codec.Info.Height != prepared.Height * NovelAiUpscaleRules.Scale)
                return (null, L("upscale.official.invalid_result"));

            WriteRequestLog("NovelAI upscale", payload, stopwatch.ElapsedMilliseconds, response, imageBytes: result);
            return (result, null);
        }
        catch (OperationCanceledException ex)
        {
            WriteRequestLog("NovelAI upscale", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            return (null, ct.IsCancellationRequested ? L("api.error.request_cancelled") : L("upscale.official.timeout"));
        }
        catch (Exception ex)
        {
            WriteRequestLog("NovelAI upscale", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            return (null, Lf("api.error.request_failed", ex.Message));
        }
    }

    private static (string Base64, int Width, int Height) PrepareOfficialUpscaleImage(byte[] bytes)
    {
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        if (codec == null) throw new InvalidDataException(L("upscale.error.decode_failed"));
        int width = codec.Info.Width, height = codec.Info.Height;
        if (NovelAiUpscaleRules.EstimateAnlas(width, height) == null)
            throw new InvalidDataException(L("upscale.official.size_limit"));

        // PNG input stays intact, including metadata. Other workspace formats are converted losslessly.
        using var bitmap = SKBitmap.Decode(bytes)
            ?? throw new InvalidDataException(L("upscale.error.decode_failed"));
        if (codec.EncodedFormat == SKEncodedImageFormat.Png)
            return (Convert.ToBase64String(bytes), width, height);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException(L("upscale.error.decode_failed"));
        return (Convert.ToBase64String(encoded.ToArray()), width, height);
    }
}
