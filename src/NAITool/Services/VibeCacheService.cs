using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace NAITool.Services;

/// <summary>
/// 本地 vibe 编码缓存服务。
/// 基于 SHA256(图片字节) + informationExtracted 进行缓存查找与存储。
/// </summary>
public static class VibeCacheService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string GetCacheDir(string appRootDir) =>
        Path.Combine(appRootDir, "user", "vibe");

    public static string ComputeImageHash(byte[] imageBytes)
    {
        byte[] hash = SHA256.HashData(imageBytes);
        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>
    /// 查找本地缓存的 vibe 编码。返回 base64 编码字符串，未命中返回 null。
    /// 可选 requiredModel 参数用于验证缓存文件的模型是否匹配。
    /// </summary>
    public static string? TryGetCachedVibe(
        string cacheDir, byte[] imageBytes, double infoExtracted, string? requiredModel = null)
    {
        if (!Directory.Exists(cacheDir))
            return null;

        string prefix = ComputeImageHash(imageBytes);
        return TryGetCachedVibeByHash(cacheDir, prefix, infoExtracted, requiredModel);
    }

    /// <summary>
    /// 通过预计算的图片哈希查找缓存编码。返回 base64 编码字符串，未命中返回 null。
    /// </summary>
    public static string? TryGetCachedVibeByHash(
        string cacheDir, string imageHash, double infoExtracted, string? requiredModel = null)
    {
        if (!Directory.Exists(cacheDir))
            return null;

        string ieKey = infoExtracted.ToString("0.00");
        string pattern = $"{imageHash}_ie{ieKey}*.naiv4vibe";

        foreach (string file in Directory.EnumerateFiles(cacheDir, pattern))
        {
            try
            {
                string json = File.ReadAllText(file);
                if (requiredModel != null)
                {
                    string? fileModel = ExtractModelFromVibeJson(json);
                    if (fileModel != null &&
                        !string.Equals(fileModel, requiredModel, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                return ExtractEncodingFromVibeJson(json, infoExtracted);
            }
            catch
            {
            }
        }

        return null;
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
    /// 保存 vibe 编码到本地缓存。同时保存原图副本以供识别。
    /// </summary>
    public static string SaveVibe(
        string cacheDir,
        byte[] imageBytes,
        byte[] vibeData,
        double infoExtracted,
        string model)
    {
        Directory.CreateDirectory(cacheDir);

        string prefix = ComputeImageHash(imageBytes);
        string ieKey = infoExtracted.ToString("0.00");
        string vibeFileName = $"{prefix}_ie{ieKey}.naiv4vibe";
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

        string thumbPath = Path.Combine(cacheDir, $"{prefix}_thumb.png");
        if (!File.Exists(thumbPath))
        {
            try { File.WriteAllBytes(thumbPath, imageBytes); }
            catch { }
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
    public static string? GetThumbnailPath(string cacheDir, string imageHash)
    {
        string thumbPath = Path.Combine(cacheDir, $"{imageHash}_thumb.png");
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
