using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;

namespace NAITool.Services;

/// <summary>
/// 本地 vibe 编码缓存服务。
/// 基于 SHA256(图片字节) + informationExtracted 进行缓存查找与存储。
/// </summary>
public static class VibeCacheService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const string LookupFileName = "vibe_cache_lookup.json";

    private sealed class VibeCacheLookupEntry
    {
        public string ImageHash { get; set; } = "";
        public string ThumbnailHash { get; set; } = "";
        public string Model { get; set; } = "";
        public string InformationExtractedKey { get; set; } = "";
        public string VibeFileName { get; set; } = "";
    }

    private sealed class VibeCacheLookupData
    {
        public int Version { get; set; } = 1;
        public List<VibeCacheLookupEntry> Entries { get; set; } = new();
    }

    public static string GetCacheDir(string appRootDir) =>
        Path.Combine(appRootDir, "user", "vibe");

    public static string ComputeImageHash(byte[] imageBytes)
    {
        byte[] hash = SHA256.HashData(imageBytes);
        return Convert.ToHexStringLower(hash)[..16];
    }

    public static string ComputeThumbnailHash(byte[] thumbnailBytes) =>
        ComputeImageHash(thumbnailBytes);

    public static byte[] CreateCanonicalThumbnail(byte[] imageBytes)
    {
        const int targetSize = 256;
        try
        {
            using var bitmap = SKBitmap.Decode(imageBytes);
            if (bitmap == null)
                return imageBytes;

            float scale = Math.Min((float)targetSize / bitmap.Width, (float)targetSize / bitmap.Height);
            int newW = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
            int newH = Math.Max(1, (int)Math.Round(bitmap.Height * scale));

            using var resized = bitmap.Resize(new SKImageInfo(newW, newH, SKColorType.Rgba8888, SKAlphaType.Premul), SKSamplingOptions.Default);
            if (resized == null)
                return imageBytes;

            using var canvasBitmap = new SKBitmap(targetSize, targetSize, SKColorType.Rgba8888, SKAlphaType.Premul);
            canvasBitmap.Erase(SKColors.Transparent);
            using var canvas = new SKCanvas(canvasBitmap);
            int offsetX = (targetSize - newW) / 2;
            int offsetY = (targetSize - newH) / 2;
            canvas.DrawBitmap(resized, offsetX, offsetY);

            using var image = SKImage.FromBitmap(canvasBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray() ?? imageBytes;
        }
        catch
        {
            return imageBytes;
        }
    }

    private static string NormalizeInfoExtractedKey(double infoExtracted) =>
        Math.Round(infoExtracted, 2).ToString("0.00");

    private static string NormalizeModelKey(string model)
    {
        Span<char> buffer = stackalloc char[model.Length];
        int pos = 0;
        foreach (char ch in model)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[pos++] = char.ToLowerInvariant(ch);
            }
            else if (ch is '-' or '_')
            {
                buffer[pos++] = '-';
            }
        }
        return pos == 0 ? "model" : new string(buffer[..pos]);
    }

    private static string GetLookupPath(string cacheDir) =>
        Path.Combine(cacheDir, LookupFileName);

    private static VibeCacheLookupData LoadLookup(string cacheDir)
    {
        try
        {
            string path = GetLookupPath(cacheDir);
            if (!File.Exists(path))
                return new VibeCacheLookupData();

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<VibeCacheLookupData>(json) ?? new VibeCacheLookupData();
        }
        catch
        {
            return new VibeCacheLookupData();
        }
    }

    private static void SaveLookup(string cacheDir, VibeCacheLookupData data)
    {
        string json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(GetLookupPath(cacheDir), json);
    }

    /// <summary>
    /// 查找本地缓存的 vibe 编码（严格匹配：图片哈希 + 模型 + 信息提取强度）。
    /// </summary>
    public static string? TryGetCachedVibe(
        string cacheDir, byte[] imageBytes, double infoExtracted, string model)
    {
        if (!Directory.Exists(cacheDir))
            return null;

        string imageHash = ComputeImageHash(imageBytes);
        return TryGetCachedVibeByLookup(cacheDir, imageHash, infoExtracted, model);
    }

    /// <summary>
    /// 通过预计算参数严格匹配缓存编码。必须同时命中图片哈希、模型和信息提取强度。
    /// </summary>
    public static string? TryGetCachedVibeByLookup(
        string cacheDir, string imageHash, double infoExtracted, string model)
    {
        if (!Directory.Exists(cacheDir))
            return null;

        string ieKey = NormalizeInfoExtractedKey(infoExtracted);
        var lookup = LoadLookup(cacheDir);
        var entry = lookup.Entries.FirstOrDefault(x =>
            string.Equals(x.ImageHash, imageHash, StringComparison.Ordinal) &&
            string.Equals(x.Model, model, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.InformationExtractedKey, ieKey, StringComparison.Ordinal));

        if (entry == null || string.IsNullOrWhiteSpace(entry.VibeFileName))
            return null;

        string vibePath = Path.Combine(cacheDir, entry.VibeFileName);
        if (!File.Exists(vibePath))
        {
            try
            {
                lookup.Entries.Remove(entry);
                SaveLookup(cacheDir, lookup);
            }
            catch
            {
            }
            return null;
        }

        try
        {
            string json = File.ReadAllText(vibePath);
            string? fileModel = ExtractModelFromVibeJson(json);
            if (!string.IsNullOrWhiteSpace(fileModel) &&
                !string.Equals(fileModel, model, StringComparison.OrdinalIgnoreCase))
                return null;

            return ExtractEncodingFromVibeJson(json, infoExtracted);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 通过缩略图哈希反查缓存命中，并返回命中的编码和主图哈希。
    /// 用于“导入缩略图后定位到对应主图缓存”。
    /// </summary>
    public static (string? Encoding, string? ImageHash) TryGetCachedVibeByThumbnailHash(
        string cacheDir, string thumbnailHash, double infoExtracted, string model)
    {
        if (!Directory.Exists(cacheDir))
            return (null, null);

        string ieKey = NormalizeInfoExtractedKey(infoExtracted);
        var lookup = LoadLookup(cacheDir);
        var candidates = lookup.Entries.Where(x =>
            string.Equals(x.ThumbnailHash, thumbnailHash, StringComparison.Ordinal) &&
            string.Equals(x.Model, model, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.InformationExtractedKey, ieKey, StringComparison.Ordinal));

        foreach (var entry in candidates)
        {
            string vibePath = Path.Combine(cacheDir, entry.VibeFileName);
            if (!File.Exists(vibePath))
                continue;

            try
            {
                string json = File.ReadAllText(vibePath);
                string? encoding = ExtractEncodingFromVibeJson(json, infoExtracted);
                if (!string.IsNullOrWhiteSpace(encoding))
                    return (encoding, entry.ImageHash);
            }
            catch
            {
            }
        }

        return (null, null);
    }

    /// <summary>
    /// 从 .naiv4vibe JSON 中提取模型标识。
    /// </summary>
    public static string? ExtractModelFromVibeJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("importInfo", out var info) &&
                info.TryGetProperty("model", out var m))
                return m.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 保存 vibe 编码到本地缓存，并更新 lookup 索引。
    /// </summary>
    public static string SaveVibe(
        string cacheDir,
        byte[] imageBytes,
        byte[] thumbnailBytes,
        byte[] vibeData,
        double infoExtracted,
        string model)
    {
        Directory.CreateDirectory(cacheDir);

        byte[] canonicalThumbnailBytes = CreateCanonicalThumbnail(thumbnailBytes);
        string imageHash = ComputeImageHash(imageBytes);
        string thumbnailHash = ComputeThumbnailHash(canonicalThumbnailBytes);
        string ieKey = NormalizeInfoExtractedKey(infoExtracted);
        string modelKey = NormalizeModelKey(model);
        string vibeFileName = $"{imageHash}_th{thumbnailHash}_m{modelKey}_ie{ieKey}.naiv4vibe";
        string vibePath = Path.Combine(cacheDir, vibeFileName);

        string vibeBase64 = Convert.ToBase64String(vibeData);
        var vibeJson = new
        {
            importInfo = new { model },
            encodings = new
            {
                v4full = new
                {
                    _0 = new
                    {
                        encoding = vibeBase64,
                        @params = new { information_extracted = infoExtracted },
                    },
                },
            },
        };

        string json = JsonSerializer.Serialize(vibeJson, JsonOptions);
        // JsonSerializer 将 _0 序列化为 "_0"，需要手动修正为 "0"
        json = json.Replace("\"_0\"", "\"0\"");
        File.WriteAllText(vibePath, json);

        string thumbPath = Path.Combine(cacheDir, $"{imageHash}_{thumbnailHash}_thumb.png");
        if (!File.Exists(thumbPath))
        {
            try { File.WriteAllBytes(thumbPath, canonicalThumbnailBytes); }
            catch { }
        }

        try
        {
            var lookup = LoadLookup(cacheDir);
            lookup.Entries.RemoveAll(x =>
                string.Equals(x.ImageHash, imageHash, StringComparison.Ordinal) &&
                string.Equals(x.Model, model, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.InformationExtractedKey, ieKey, StringComparison.Ordinal));

            lookup.Entries.Add(new VibeCacheLookupEntry
            {
                ImageHash = imageHash,
                ThumbnailHash = thumbnailHash,
                Model = model,
                InformationExtractedKey = ieKey,
                VibeFileName = vibeFileName,
            });
            SaveLookup(cacheDir, lookup);
        }
        catch
        {
        }

        return vibePath;
    }

    /// <summary>
    /// 从 .naiv4vibe JSON 中提取 base64 编码数据。
    /// </summary>
    public static string? ExtractEncodingFromVibeJson(string json, double? targetIe = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("encodings", out var encodings))
                return null;

            foreach (var group in encodings.EnumerateObject())
            {
                foreach (var slot in group.Value.EnumerateObject())
                {
                    if (!slot.Value.TryGetProperty("encoding", out var enc))
                        continue;

                    if (targetIe.HasValue && slot.Value.TryGetProperty("params", out var p) &&
                        p.TryGetProperty("information_extracted", out var ie))
                    {
                        double storedIe = ie.GetDouble();
                        if (Math.Abs(storedIe - targetIe.Value) > 0.005)
                            continue;
                    }

                    string? encoding = enc.GetString();
                    if (!string.IsNullOrWhiteSpace(encoding))
                        return encoding;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    /// <summary>
    /// 获取指定哈希的缩略图文件路径。存在则返回完整路径，否则返回 null。
    /// </summary>
    public static string? GetThumbnailPath(string cacheDir, string imageHash, string thumbnailHash)
    {
        string thumbPath = Path.Combine(cacheDir, $"{imageHash}_{thumbnailHash}_thumb.png");
        return File.Exists(thumbPath) ? thumbPath : null;
    }

    /// <summary>
    /// 从 .naiv4vibe 文件的完整字节中提取 base64 编码。
    /// </summary>
    public static string? ExtractEncodingFromVibeFileBytes(byte[] fileBytes)
    {
        try
        {
            string json = System.Text.Encoding.UTF8.GetString(fileBytes);
            return ExtractEncodingFromVibeJson(json);
        }
        catch
        {
            return null;
        }
    }
}
