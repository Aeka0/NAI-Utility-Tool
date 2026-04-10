using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using SkiaSharp;
using Windows.Storage.Streams;

namespace NAITool.Services;

public class CharacterPromptInfo
{
    public string PositivePrompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
    public double CenterX { get; set; } = 0.5;
    public double CenterY { get; set; } = 0.5;
    public bool UseCustomPosition { get; set; }
}

public class NovelAiAccountInfo
{
    public int? AnlasBalance { get; init; }
    public string TierName { get; init; } = "";
    public bool IsOpus { get; init; }
    public int? TierLevel { get; init; }
    public string? ExpiresAt { get; init; }
}

/// <summary>
/// NovelAI API 服务。
/// </summary>
public class NovelAIService : IDisposable
{
    private const string GenerateUrl = "https://image.novelai.net/ai/generate-image";
    private const string EncodeVibeUrl = "https://image.novelai.net/ai/encode-vibe";
    private const string UserInfoUrl = "https://api.novelai.net/user/information";
    private const string UserDataUrl = "https://api.novelai.net/user/data";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
    };
    private static readonly JsonSerializerOptions LogJsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true,
    };

    private HttpClient? _httpClient;
    private readonly SettingsService _settings;

    public NovelAIService(SettingsService settings) { _settings = settings; }

    private HttpClient GetOrCreateClient()
    {
        _httpClient?.Dispose();
        var handler = new HttpClientHandler();
        if (_settings.Settings.UseProxy && !string.IsNullOrEmpty(_settings.Settings.ProxyPort))
        {
            handler.Proxy = new WebProxy($"http://127.0.0.1:{_settings.Settings.ProxyPort}");
            handler.UseProxy = true;
        }
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(300) };
        if (!string.IsNullOrEmpty(_settings.Settings.ApiToken))
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.Settings.ApiToken);
        return _httpClient;
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(
        string token, CancellationToken ct = default)
    {
        try
        {
            var client = GetOrCreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var response = await client.GetAsync(UserInfoUrl, ct);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => (true, "连接成功！API Token 有效。"),
                HttpStatusCode.Unauthorized => (false, "认证失败 (401): API Token 无效。"),
                _ => (false, $"API 错误 ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}")
            };
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("proxy", StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"代理连接失败: {ex.Message}\n\n提示: 请检查代理客户端是否运行以及端口是否正确。");
        }
        catch (Exception ex)
        {
            return (false, $"连接 NovelAI 失败: {ex.Message}");
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetNestedElement(JsonElement element, out JsonElement result, params string[] path)
    {
        result = element;
        foreach (string segment in path)
        {
            if (!TryGetPropertyIgnoreCase(result, segment, out result))
                return false;
        }
        return true;
    }

    private static int? ReadNullableInt(JsonElement element)
    {
        try
        {
            return element.ValueKind switch
            {
                JsonValueKind.Number when element.TryGetInt32(out int intValue) => intValue,
                JsonValueKind.Number when element.TryGetDouble(out double doubleValue) => (int)Math.Round(doubleValue),
                JsonValueKind.String when int.TryParse(element.GetString(), out int stringValue) => stringValue,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 根据官方 API 规范 (https://api.novelai.net/docs) 解析账户信息。
    /// /user/data 返回 UserAccountDataResponse：
    ///   { subscription: { tier: int(0-3), active: bool, trainingStepsLeft: int|object, ... }, ... }
    /// tier: 0=Paper, 1=Tablet, 2=Scroll, 3=Opus
    /// trainingStepsLeft: Anlas 余额 (整数 或 { fixedTrainingStepsLeft, purchasedTrainingSteps })
    /// </summary>
    public async Task<NovelAiAccountInfo?> GetAccountInfoAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.Settings.ApiToken))
            return null;

        try
        {
            var client = GetOrCreateClient();

            var response = await client.GetAsync(UserDataUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[NAI] /user/data request failed: {(int)response.StatusCode}");
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(ct);
            Debug.WriteLine($"[NAI] /user/data response: {json[..Math.Min(json.Length, 2000)]}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!TryGetPropertyIgnoreCase(root, "subscription", out var sub))
            {
                Debug.WriteLine("[NAI] subscription field was not found in the response");
                return null;
            }

            // ── 订阅等级 ──
            // 官方字段: subscription.tier (int: 0=Paper, 1=Tablet, 2=Scroll, 3=Opus)
            int? tier = null;
            if (TryGetPropertyIgnoreCase(sub, "tier", out var tierEl))
                tier = ReadNullableInt(tierEl);

            Debug.WriteLine($"[NAI] subscription.tier = {tier}");

            bool isOpus = tier.HasValue && tier.Value >= 3;
            string tierName = tier switch
            {
                0 => "Paper",
                1 => "Tablet",
                2 => "Scroll",
                3 => "Opus",
                _ => tier.HasValue ? $"Tier{tier}" : "",
            };

            // ── Anlas 余额 ──
            // 官方字段: subscription.trainingStepsLeft
            // 实际可能是整数 (参考 ComfyUI-NAIDGenerator) 或对象 (参考 Swagger spec)
            int? anlas = null;
            if (TryGetPropertyIgnoreCase(sub, "trainingStepsLeft", out var stepsEl))
            {
                if (stepsEl.ValueKind == JsonValueKind.Number)
                {
                    anlas = ReadNullableInt(stepsEl);
                    Debug.WriteLine($"[NAI] trainingStepsLeft (number) = {anlas}");
                }
                else if (stepsEl.ValueKind == JsonValueKind.Object)
                {
                    int fixedSteps = 0, purchasedSteps = 0;
                    if (TryGetPropertyIgnoreCase(stepsEl, "fixedTrainingStepsLeft", out var fixedEl))
                        fixedSteps = ReadNullableInt(fixedEl) ?? 0;
                    if (TryGetPropertyIgnoreCase(stepsEl, "purchasedTrainingSteps", out var purchasedEl))
                        purchasedSteps = ReadNullableInt(purchasedEl) ?? 0;
                    anlas = fixedSteps + purchasedSteps;
                    Debug.WriteLine($"[NAI] trainingStepsLeft (object): fixed={fixedSteps}, purchased={purchasedSteps}, total={anlas}");
                }
                else
                {
                    Debug.WriteLine($"[NAI] trainingStepsLeft has an unknown value kind: {stepsEl.ValueKind}");
                }
            }
            else
            {
                Debug.WriteLine("[NAI] trainingStepsLeft field was not found in subscription");
            }

            string? expiresAt = null;
            if (TryGetPropertyIgnoreCase(sub, "expiresAt", out var expiresEl) && expiresEl.ValueKind == JsonValueKind.Number)
            {
                long ts = expiresEl.GetInt64();
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().ToString("yyyy-MM-dd");
            }

            Debug.WriteLine($"[NAI] Parsed account info: tier={tier}, tierName={tierName}, isOpus={isOpus}, anlas={anlas}, expires={expiresAt}");

            return new NovelAiAccountInfo
            {
                AnlasBalance = anlas,
                TierName = tierName,
                IsOpus = isOpus,
                TierLevel = tier,
                ExpiresAt = expiresAt,
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NAI] GetAccountInfoAsync exception: {ex}");
            return null;
        }
    }

    private static bool IsV4PlusModel(string model) =>
        model.Contains("-4", StringComparison.Ordinal);

    private static bool IsV45Model(string model) =>
        model.Contains("4-5", StringComparison.Ordinal);

    private static string NormalizeModelKey(string model) =>
        model.EndsWith("-inpainting", StringComparison.Ordinal)
            ? model[..^"-inpainting".Length]
            : model;

    private static string GetQualityTagSuffix(string model) => NormalizeModelKey(model) switch
    {
        "nai-diffusion-4-5-full" => "very aesthetic, masterpiece, no text",
        "nai-diffusion-4-5-curated" => "masterpiece, no text, -0.8::feet::, rating:general",
        "nai-diffusion-4-full" => "no text, best quality, very aesthetic, absurdres",
        "nai-diffusion-4-curated" => "rating:general, amazing quality, very aesthetic, absurdres",
        "nai-diffusion-3" => "best quality, amazing quality, very aesthetic, absurdres",
        _ => "",
    };

    private static string GetUcPresetText(string model, int ucPreset) => (NormalizeModelKey(model), ucPreset) switch
    {
        (_, 2) => "",
        ("nai-diffusion-4-5-full", 0) => "lowres, artistic error, film grain, scan artifacts, worst quality, bad quality, jpeg artifacts, very displeasing, chromatic aberration, dithering, halftone, screentone, multiple views, logo, too many watermarks, negative space, blank page",
        ("nai-diffusion-4-5-full", 1) => "lowres, artistic error, scan artifacts, worst quality, bad quality, jpeg artifacts, multiple views, very displeasing, too many watermarks, negative space, blank page",
        ("nai-diffusion-4-5-curated", 0) => "blurry, lowres, upscaled, artistic error, film grain, scan artifacts, worst quality, bad quality, jpeg artifacts, very displeasing, chromatic aberration, halftone, multiple views, logo, too many watermarks, negative space, blank page",
        ("nai-diffusion-4-5-curated", 1) => "blurry, lowres, upscaled, artistic error, scan artifacts, jpeg artifacts, logo, too many watermarks, negative space, blank page",
        ("nai-diffusion-4-full", 0) => "blurry, lowres, error, film grain, scan artifacts, worst quality, bad quality, jpeg artifacts, very displeasing, chromatic aberration, multiple views, logo, too many watermarks",
        ("nai-diffusion-4-full", 1) => "blurry, lowres, error, worst quality, bad quality, jpeg artifacts, very displeasing",
        ("nai-diffusion-4-curated", 0) => "blurry, lowres, error, film grain, scan artifacts, worst quality, bad quality, jpeg artifacts, very displeasing, chromatic aberration, logo, dated, signature, multiple views, gigantic breasts",
        ("nai-diffusion-4-curated", 1) => "blurry, lowres, error, worst quality, bad quality, jpeg artifacts, very displeasing, logo, dated, signature",
        ("nai-diffusion-3", 0) => "lowres, {bad}, error, fewer, extra, missing, worst quality, jpeg artifacts, bad quality, watermark, unfinished, displeasing, chromatic aberration, signature, extra digits, artistic error, username, scan, [abstract]",
        ("nai-diffusion-3", 1) => "lowres, jpeg artifacts, worst quality, watermark, blurry, very displeasing",
        _ => "",
    };

    private static string MergePromptSegments(params string[] segments)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();

        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment)) continue;
            foreach (var part in segment.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (seen.Add(part))
                    merged.Add(part);
            }
        }

        return string.Join(", ", merged);
    }

    private static string ApplyQualityTags(string prompt, string model, bool enabled)
    {
        if (!enabled) return prompt;
        string suffix = GetQualityTagSuffix(model);
        return string.IsNullOrWhiteSpace(suffix) ? prompt : MergePromptSegments(prompt, suffix);
    }

    private static string ApplyUcPreset(string negativePrompt, string model, int ucPreset)
    {
        string preset = GetUcPresetText(model, ucPreset);
        return string.IsNullOrWhiteSpace(preset) ? negativePrompt : MergePromptSegments(negativePrompt, preset);
    }

    private static string ToApiPreciseReferenceType(PreciseReferenceType type) => type switch
    {
        PreciseReferenceType.Character => "character",
        PreciseReferenceType.Style => "style",
        _ => "character&style",
    };

    private static void ApplyVibeTransferParameters(
        Dictionary<string, object?> parameters,
        List<VibeTransferInfo> vibeTransfers)
    {
        if (vibeTransfers.Count == 0)
            return;

        if (vibeTransfers.Count == 1)
        {
            parameters["reference_image"] = vibeTransfers[0].ImageBase64;
            parameters["reference_strength"] = vibeTransfers[0].Strength;
            parameters["reference_information_extracted"] = vibeTransfers[0].InformationExtracted;
            return;
        }

        parameters["reference_image_multiple"] = vibeTransfers.Select(x => x.ImageBase64).ToList();
        parameters["reference_strength_multiple"] = vibeTransfers.Select(x => x.Strength).ToList();
        parameters["reference_information_extracted_multiple"] = vibeTransfers.Select(x => x.InformationExtracted).ToList();
    }

    private static void ApplyPreciseReferenceParameters(
        Dictionary<string, object?> parameters,
        List<PreciseReferenceInfo> preciseReferences)
    {
        if (preciseReferences.Count == 0)
            return;

        if (preciseReferences.Count == 1)
        {
            parameters["reference_image"] = preciseReferences[0].ImageBase64;
            parameters["reference_strength"] = preciseReferences[0].Strength;
            parameters["reference_fidelity"] = preciseReferences[0].Fidelity;
            parameters["reference_type"] = ToApiPreciseReferenceType(preciseReferences[0].ReferenceType);
            return;
        }

        parameters["reference_image_multiple"] = preciseReferences.Select(x => x.ImageBase64).ToList();
        parameters["reference_strength_multiple"] = preciseReferences.Select(x => x.Strength).ToList();
        parameters["reference_fidelity_multiple"] = preciseReferences.Select(x => x.Fidelity).ToList();
        parameters["reference_type_multiple"] = preciseReferences
            .Select(x => ToApiPreciseReferenceType(x.ReferenceType))
            .ToList();
        parameters["precise_references"] = preciseReferences.Select(x => new Dictionary<string, object?>
        {
            ["imageBase64"] = x.ImageBase64,
            ["reference_type"] = ToApiPreciseReferenceType(x.ReferenceType),
            ["reference_strength"] = x.Strength,
            ["reference_fidelity"] = x.Fidelity,
            ["fileName"] = x.FileName,
        }).ToList();
    }

    private static object? SanitizeLogValue(object? value)
    {
        if (value == null)
            return null;

        if (value is string text)
        {
            if (text.Length > 200)
                return $"<long text, length {text.Length}>";
            return text;
        }

        if (value is IDictionary dictionary)
        {
            var result = new Dictionary<string, object?>();
            foreach (DictionaryEntry entry in dictionary)
            {
                string key = entry.Key?.ToString() ?? "";
                result[key] = SanitizeLogValue(entry.Value);
            }
            return result;
        }

        if (value is IEnumerable enumerable && value is not byte[])
        {
            var result = new List<object?>();
            foreach (var item in enumerable)
                result.Add(SanitizeLogValue(item));
            return result;
        }

        return value;
    }

    private void WriteRequestLog(
        string requestName,
        Dictionary<string, object> payload,
        long elapsedMs,
        HttpResponseMessage? response = null,
        string? responseText = null,
        byte[]? imageBytes = null,
        Exception? exception = null)
    {
        if (!_settings.Settings.DevLogEnabled)
            return;

        try
        {
            string dir = Path.Combine(AppPathResolver.AppRootDir, "logs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"api_{DateTime.Now:yyyy-MM-dd}.txt");

            var sanitizedPayload = SanitizeLogValue(payload);
            var builder = new StringBuilder();
            builder.AppendLine(new string('=', 72));
            builder.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            builder.AppendLine($"Request: {requestName}");
            builder.AppendLine($"Elapsed: {elapsedMs} ms");

            if (response != null)
            {
                builder.AppendLine($"Response status: {(int)response.StatusCode} {response.ReasonPhrase}");
                builder.AppendLine($"Response type: {response.Content.Headers.ContentType}");
                if (response.Content.Headers.ContentLength.HasValue)
                    builder.AppendLine($"Response length: {response.Content.Headers.ContentLength.Value} bytes");
            }

            if (exception != null)
            {
                builder.AppendLine($"Exception: {exception.Message}");
            }

            builder.AppendLine("Request payload:");
            builder.AppendLine(JsonSerializer.Serialize(sanitizedPayload, LogJsonOptions));

            if (!string.IsNullOrWhiteSpace(responseText))
            {
                builder.AppendLine("Response body:");
                builder.AppendLine(responseText);
            }

            if (imageBytes != null)
            {
                builder.AppendLine($"Response image: {imageBytes.Length} bytes");
                var meta = ImageMetadataService.ReadFromBytes(imageBytes);
                if (meta != null && !string.IsNullOrWhiteSpace(meta.RawJson))
                {
                    builder.AppendLine("Response image metadata:");
                    builder.AppendLine(ImageMetadataService.PrettyPrintJson(meta.RawJson));
                }
            }

            builder.AppendLine();
            File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
        }
        catch
        {
        }
    }

    public async Task<(byte[]? ImageBytes, string? Error)> InpaintAsync(
        string imageBase64, string maskBase64,
        int width, int height,
        string prompt, string negativePrompt,
        List<CharacterPromptInfo>? characters = null,
        List<VibeTransferInfo>? vibeTransfers = null,
        List<PreciseReferenceInfo>? preciseReferences = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_settings.Settings.ApiToken))
            return (null, "API Token 未设置。请在 设置 → 网络 中配置。");

        var naiParams = _settings.Settings.InpaintParameters;
        int seed = naiParams.Seed > 0 ? naiParams.Seed : Random.Shared.Next(1, int.MaxValue);
        bool isV4Plus = IsV4PlusModel(naiParams.Model);
        bool isV45 = IsV45Model(naiParams.Model);
        string effectivePrompt = ApplyQualityTags(prompt, naiParams.Model, naiParams.QualityToggle);
        string effectiveNegativePrompt = ApplyUcPreset(negativePrompt, naiParams.Model, naiParams.UcPreset);

        var parameters = new Dictionary<string, object?>
        {
            ["params_version"] = 3,
            ["width"] = width,
            ["height"] = height,
            ["scale"] = naiParams.Scale,
            ["sampler"] = naiParams.Sampler,
            ["steps"] = naiParams.Steps,
            ["n_samples"] = 1,
            ["seed"] = seed,
            ["image"] = imageBase64,
            ["mask"] = maskBase64,
            ["noise_schedule"] = naiParams.Schedule,
            ["uc"] = effectiveNegativePrompt,
            ["negative_prompt"] = effectiveNegativePrompt,
            ["ucPreset"] = naiParams.UcPreset,
            ["uc_preset"] = naiParams.UcPreset,
            ["add_original_image"] = true,
            ["cfg_rescale"] = naiParams.CfgRescale,
            ["legacy"] = false,
            ["legacy_v3_extend"] = false,
            ["dynamic_thresholding"] = naiParams.CfgRescale > 0,
            ["skip_cfg_above_sigma"] = null,
            ["strength"] = 1.0,
            ["noise"] = 0,
            ["qualityToggle"] = naiParams.QualityToggle,
            ["quality_toggle"] = naiParams.QualityToggle,
        };

        if (naiParams.Variety) parameters["variety"] = true;

        if (naiParams.Sampler == "k_euler_ancestral" && naiParams.Schedule != "native")
        {
            parameters["deliberate_euler_ancestral_bug"] = false;
            parameters["prefer_brownian"] = true;
        }

        if (isV4Plus)
        {
            var posCharCaptions = new List<object>();
            var negCharCaptions = new List<object>();
            bool useCoords = false;

            if (characters is { Count: > 0 })
            {
                foreach (var ch in characters)
                {
                    var center = new Dictionary<string, double> { ["x"] = ch.CenterX, ["y"] = ch.CenterY };
                    posCharCaptions.Add(new Dictionary<string, object>
                    {
                        ["char_caption"] = ch.PositivePrompt,
                        ["centers"] = new[] { center },
                    });
                    negCharCaptions.Add(new Dictionary<string, object>
                    {
                        ["char_caption"] = ch.NegativePrompt,
                        ["centers"] = new[] { center },
                    });
                    if (ch.UseCustomPosition)
                        useCoords = true;
                }
            }

            parameters["use_coords"] = useCoords;
            parameters["v4_prompt"] = new Dictionary<string, object>
            {
                ["caption"] = new Dictionary<string, object>
                {
                    ["base_caption"] = effectivePrompt,
                    ["char_captions"] = posCharCaptions,
                },
                ["use_coords"] = useCoords,
                ["use_order"] = true,
            };
            parameters["v4_negative_prompt"] = new Dictionary<string, object>
            {
                ["caption"] = new Dictionary<string, object>
                {
                    ["base_caption"] = effectiveNegativePrompt,
                    ["char_captions"] = negCharCaptions,
                },
                ["use_coords"] = useCoords,
                ["use_order"] = false,
                ["legacy_uc"] = !isV45,
            };
        }
        else
        {
            parameters["sm"] = naiParams.Sm;
            parameters["sm_dyn"] = false;
        }

        if (preciseReferences is { Count: > 0 })
            ApplyPreciseReferenceParameters(parameters, preciseReferences);
        else if (vibeTransfers is { Count: > 0 })
            ApplyVibeTransferParameters(parameters, vibeTransfers);

        var payload = new Dictionary<string, object>
        {
            ["input"] = effectivePrompt,
            ["model"] = naiParams.Model,
            ["action"] = "infill",
            ["parameters"] = parameters,
        };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var client = GetOrCreateClient();
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/zip"));

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(GenerateUrl, content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(ct);
                stopwatch.Stop();
                WriteRequestLog("重绘生成", payload, stopwatch.ElapsedMilliseconds, response, errorText);
                return (null, $"API 错误 ({(int)response.StatusCode}): {errorText}");
            }

            var zipBytes = await response.Content.ReadAsByteArrayAsync(ct);
            using var zipStream = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            if (archive.Entries.Count == 0)
                return (null, "API 返回了空的 zip 文件。");

            var entry = archive.Entries[0];
            using var entryStream = entry.Open();
            using var imageStream = new MemoryStream();
            await entryStream.CopyToAsync(imageStream, ct);
            byte[] imageBytes = imageStream.ToArray();
            stopwatch.Stop();
            WriteRequestLog("重绘生成", payload, stopwatch.ElapsedMilliseconds, response, imageBytes: imageBytes);
            return (imageBytes, null);
        }
        catch (TaskCanceledException ex)
        {
            stopwatch.Stop();
            WriteRequestLog("重绘生成", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            return (null, "请求已取消。");
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            WriteRequestLog("重绘生成", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            var msg = $"网络请求失败: {ex.Message}";
            if (ex.Message.Contains("proxy", StringComparison.OrdinalIgnoreCase))
                msg += "\n\n提示: 请检查代理客户端是否运行以及端口是否正确。";
            return (null, msg);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            WriteRequestLog("重绘生成", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            return (null, $"请求失败: {ex.Message}");
        }
    }

    public async Task<(byte[]? ImageBytes, string? Error)> GenerateAsync(
        int width, int height,
        string prompt, string negativePrompt,
        List<CharacterPromptInfo>? characters = null,
        List<VibeTransferInfo>? vibeTransfers = null,
        List<PreciseReferenceInfo>? preciseReferences = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_settings.Settings.ApiToken))
            return (null, "API Token 未设置。请在 设置 → 网络/API设置 中配置。");

        var naiParams = _settings.Settings.GenParameters;
        string model = naiParams.Model;
        int seed = naiParams.Seed > 0 ? naiParams.Seed : Random.Shared.Next(1, int.MaxValue);
        bool isV4Plus = IsV4PlusModel(model);
        bool isV45 = IsV45Model(model);
        string effectivePrompt = ApplyQualityTags(prompt, model, naiParams.QualityToggle);
        string effectiveNegativePrompt = ApplyUcPreset(negativePrompt, model, naiParams.UcPreset);

        var parameters = new Dictionary<string, object?>
        {
            ["params_version"] = 3,
            ["width"] = width,
            ["height"] = height,
            ["scale"] = naiParams.Scale,
            ["sampler"] = naiParams.Sampler,
            ["steps"] = naiParams.Steps,
            ["n_samples"] = 1,
            ["seed"] = seed,
            ["noise_schedule"] = naiParams.Schedule,
            ["uc"] = effectiveNegativePrompt,
            ["negative_prompt"] = effectiveNegativePrompt,
            ["ucPreset"] = naiParams.UcPreset,
            ["uc_preset"] = naiParams.UcPreset,
            ["cfg_rescale"] = naiParams.CfgRescale,
            ["legacy"] = false,
            ["legacy_v3_extend"] = false,
            ["dynamic_thresholding"] = naiParams.CfgRescale > 0,
            ["skip_cfg_above_sigma"] = null,
            ["qualityToggle"] = naiParams.QualityToggle,
            ["quality_toggle"] = naiParams.QualityToggle,
        };

        if (naiParams.Variety) parameters["variety"] = true;

        if (naiParams.Sampler == "k_euler_ancestral" && naiParams.Schedule != "native")
        {
            parameters["deliberate_euler_ancestral_bug"] = false;
            parameters["prefer_brownian"] = true;
        }

        if (isV4Plus)
        {
            var posCharCaptions = new List<object>();
            var negCharCaptions = new List<object>();
            bool useCoords = false;

            if (characters is { Count: > 0 })
            {
                foreach (var ch in characters)
                {
                    var center = new Dictionary<string, double> { ["x"] = ch.CenterX, ["y"] = ch.CenterY };
                    posCharCaptions.Add(new Dictionary<string, object>
                    {
                        ["char_caption"] = ch.PositivePrompt,
                        ["centers"] = new[] { center },
                    });
                    negCharCaptions.Add(new Dictionary<string, object>
                    {
                        ["char_caption"] = ch.NegativePrompt,
                        ["centers"] = new[] { center },
                    });
                    if (ch.UseCustomPosition)
                        useCoords = true;
                }
            }

            parameters["use_coords"] = useCoords;
            parameters["v4_prompt"] = new Dictionary<string, object>
            {
                ["caption"] = new Dictionary<string, object>
                {
                    ["base_caption"] = effectivePrompt,
                    ["char_captions"] = posCharCaptions,
                },
                ["use_coords"] = useCoords,
                ["use_order"] = true,
            };
            parameters["v4_negative_prompt"] = new Dictionary<string, object>
            {
                ["caption"] = new Dictionary<string, object>
                {
                    ["base_caption"] = effectiveNegativePrompt,
                    ["char_captions"] = negCharCaptions,
                },
                ["use_coords"] = useCoords,
                ["use_order"] = false,
                ["legacy_uc"] = !isV45,
            };
        }
        else
        {
            parameters["sm"] = naiParams.Sm;
            parameters["sm_dyn"] = false;
        }

        if (preciseReferences is { Count: > 0 })
            ApplyPreciseReferenceParameters(parameters, preciseReferences);
        else if (vibeTransfers is { Count: > 0 })
            ApplyVibeTransferParameters(parameters, vibeTransfers);

        var payload = new Dictionary<string, object>
        {
            ["input"] = effectivePrompt,
            ["model"] = model,
            ["action"] = "generate",
            ["parameters"] = parameters,
        };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var client = GetOrCreateClient();
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/zip"));

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(GenerateUrl, content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(ct);
                stopwatch.Stop();
                WriteRequestLog("生图生成", payload, stopwatch.ElapsedMilliseconds, response, errorText);
                return (null, $"API 错误 ({(int)response.StatusCode}): {errorText}");
            }

            var zipBytes = await response.Content.ReadAsByteArrayAsync(ct);
            using var zipStream = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            if (archive.Entries.Count == 0)
                return (null, "API 返回了空的 zip 文件。");

            var entry = archive.Entries[0];
            using var entryStream = entry.Open();
            using var imageStream = new MemoryStream();
            await entryStream.CopyToAsync(imageStream, ct);
            byte[] imageBytes = imageStream.ToArray();
            stopwatch.Stop();
            WriteRequestLog("生图生成", payload, stopwatch.ElapsedMilliseconds, response, imageBytes: imageBytes);
            return (imageBytes, null);
        }
        catch (TaskCanceledException ex)
        {
            stopwatch.Stop();
            WriteRequestLog("生图生成", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            return (null, "请求已取消。");
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            WriteRequestLog("生图生成", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            var msg = $"网络请求失败: {ex.Message}";
            if (ex.Message.Contains("proxy", StringComparison.OrdinalIgnoreCase))
                msg += "\n\n提示: 请检查代理客户端是否运行以及端口是否正确。";
            return (null, msg);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            WriteRequestLog("生图生成", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            return (null, $"请求失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 将 Win2D RenderTarget 编码为 base64 WEBP。
    /// - 原图：无损 WEBP（Lossless），保证反复迭代零质量损失
    /// - 遮罩：有损 WEBP quality=50，遮罩仅为区域标记，容忍度极高，最小化体积
    /// API 通信始终使用 WEBP 格式（PNG 导致 NovelAI API 灰色异常）。
    /// </summary>
    public static Task<string> EncodeRenderTargetAsync(CanvasRenderTarget target, bool isMask = false)
    {
        int w = (int)target.SizeInPixels.Width;
        int h = (int)target.SizeInPixels.Height;
        var pixels = target.GetPixelBytes();

        using var skBitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(pixels, 0, skBitmap.GetPixels(), pixels.Length);

        byte[] encoded;
        if (isMask)
        {
            using var image = SKImage.FromBitmap(skBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Webp, 50);
            encoded = data.ToArray();
        }
        else
        {
            using var pixmap = skBitmap.PeekPixels();
            using var wstream = new SKDynamicMemoryWStream();
            bool ok = pixmap.Encode(wstream, new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 75));
            if (ok)
            {
                using var data = wstream.DetachAsData();
                encoded = data.ToArray();
            }
            else
            {
                using var image = SKImage.FromBitmap(skBitmap);
                using var data = image.Encode(SKEncodedImageFormat.Webp, 90);
                encoded = data.ToArray();
            }
        }

        return Task.FromResult(Convert.ToBase64String(encoded));
    }

    /// <summary>
    /// 调用 NovelAI encode-vibe 端点，将图片编码为 vibe 数据。
    /// 每次调用消耗 2 Anlas，需要 Max 模式。
    /// </summary>
    public async Task<(byte[]? VibeData, string? Error)> EncodeVibeAsync(
        string imageBase64, string model, double informationExtracted,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_settings.Settings.ApiToken))
            return (null, "API Token 未设置。请在 设置 → 网络/API设置 中配置。");

        var payload = new Dictionary<string, object>
        {
            ["image"] = imageBase64,
            ["model"] = model,
            ["parameters"] = new Dictionary<string, object>
            {
                ["information_extracted"] = informationExtracted,
            },
        };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var client = GetOrCreateClient();
            client.DefaultRequestHeaders.Accept.Clear();

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(EncodeVibeUrl, content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(ct);
                stopwatch.Stop();
                WriteRequestLog("氛围编码", payload, stopwatch.ElapsedMilliseconds, response, errorText);
                return (null, $"API 错误 ({(int)response.StatusCode}): {errorText}");
            }

            byte[] vibeData = await response.Content.ReadAsByteArrayAsync(ct);
            stopwatch.Stop();
            WriteRequestLog("氛围编码", payload, stopwatch.ElapsedMilliseconds, response);
            return (vibeData, null);
        }
        catch (TaskCanceledException ex)
        {
            stopwatch.Stop();
            WriteRequestLog("氛围编码", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            return (null, "请求已取消。");
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            WriteRequestLog("氛围编码", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            var msg = $"网络请求失败: {ex.Message}";
            if (ex.Message.Contains("proxy", StringComparison.OrdinalIgnoreCase))
                msg += "\n\n提示: 请检查代理客户端是否运行以及端口是否正确。";
            return (null, msg);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            WriteRequestLog("氛围编码", payload, stopwatch.ElapsedMilliseconds, exception: ex);
            return (null, $"请求失败: {ex.Message}");
        }
    }

    public void Dispose() { _httpClient?.Dispose(); }
}
