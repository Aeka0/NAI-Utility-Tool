using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;

namespace NAITool.Services;

/// <summary>A vibe is a set of model/IE encodings with optional source and preview images.</summary>
public static class VibeFileService
{
    public const int MaxFileBytes = 128 * 1024 * 1024;
    private const long MaxImagePixels = 64 * 1024 * 1024;

    public sealed record Encoding(string Model, double InformationExtracted, string Base64);
    public sealed record Document(byte[]? OriginalPng, byte[]? ThumbnailPng,
        IReadOnlyList<Encoding> Encodings, double InformationExtracted, double Strength,
        byte[]? OriginalBytes = null, string? PreferredModel = null)
    {
        public Encoding? FindEncoding(string model, double ie) => Encodings.FirstOrDefault(e =>
            string.Equals(e.Model, model, StringComparison.OrdinalIgnoreCase) &&
            Math.Abs(e.InformationExtracted - ie) < 0.005);
    }

    public static string? GetModel(string group) => group switch
    {
        "v4full" => "nai-diffusion-4-full",
        "v4curated" => "nai-diffusion-4-curated-preview",
        "v4-5full" => "nai-diffusion-4-5-full",
        "v4-5curated" => "nai-diffusion-4-5-curated",
        _ => null,
    };

    public static string GetGroup(string model) => model switch
    {
        "nai-diffusion-4-full" => "v4full",
        "nai-diffusion-4-curated-preview" => "v4curated",
        "nai-diffusion-4-5-full" => "v4-5full",
        "nai-diffusion-4-5-curated" => "v4-5curated",
        _ => throw new InvalidDataException("Unsupported vibe model."),
    };

    private static double? ReadUnitValue(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) &&
        double.IsFinite(number) && number >= 0 && number <= 1 ? number : null;

    private static string? ReadString(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static byte[]? ReadImage(JsonElement root, string key, out byte[]? originalBytes)
    {
        originalBytes = null;
        string? encoded = ReadString(root, key);
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        if (encoded.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            int comma = encoded.IndexOf(',');
            if (comma < 0 || !encoded[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                return null;
            encoded = encoded[(comma + 1)..];
        }
        try
        {
            byte[] bytes = Convert.FromBase64String(encoded);
            byte[]? png = NormalizeImage(bytes);
            if (png != null) originalBytes = bytes;
            return png;
        }
        catch (FormatException) { return null; }
    }

    public static byte[]? NormalizeImage(byte[] bytes)
    {
        using var stream = new SKMemoryStream(bytes);
        using var codec = SKCodec.Create(stream);
        if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
            (long)codec.Info.Width * codec.Info.Height > MaxImagePixels) return null;
        using var bitmap = SKBitmap.Decode(codec);
        if (bitmap == null) return null;
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }

    public static Document Parse(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxFileBytes)
            throw new InvalidDataException("Invalid vibe file size.");
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("encodings", out var groups) || groups.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Missing vibe encodings.");

        root.TryGetProperty("importInfo", out var info);
        string? importModel = ReadString(info, "model");
        double? importIe = ReadUnitValue(info, "information_extracted");
        var encodings = new List<Encoding>();
        bool singleGroup = groups.EnumerateObject().Count() == 1;
        foreach (var group in groups.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Object) continue;
            // Early NAITool exports always used v4full; their explicit model is authoritative.
            string? model = singleGroup && group.Name == "v4full" &&
                importModel != null && new[] { "nai-diffusion-4-full", "nai-diffusion-4-curated-preview",
                    "nai-diffusion-4-5-full", "nai-diffusion-4-5-curated" }.Contains(importModel)
                ? importModel : GetModel(group.Name);
            if (model == null) continue;
            foreach (var slot in group.Value.EnumerateObject())
            {
                if (slot.Value.ValueKind != JsonValueKind.Object) continue;
                string? encoding = ReadString(slot.Value, "encoding");
                slot.Value.TryGetProperty("params", out var parameters);
                double? ie = ReadUnitValue(parameters, "information_extracted") ?? importIe;
                if (string.IsNullOrWhiteSpace(encoding) || ie == null) continue;
                try { if (Convert.FromBase64String(encoding).Length == 0) continue; }
                catch (FormatException) { continue; }
                encodings.Add(new Encoding(model, ie.Value, encoding));
            }
        }
        if (encodings.Count == 0) throw new InvalidDataException("No supported vibe encodings.");
        byte[]? original = ReadImage(root, "image", out var originalBytes);
        byte[]? thumbnail = ReadImage(root, "thumbnail", out _);
        return new Document(original, thumbnail, encodings,
            importIe ?? encodings[0].InformationExtracted, ReadUnitValue(info, "strength") ?? 0.6, originalBytes, importModel);
    }

    public static byte[] Serialize(Encoding encoding, byte[]? original, byte[]? thumbnail,
        double strength = 0.6)
    {
        var root = new Dictionary<string, object?>
        {
            ["identifier"] = "novelai-vibe-transfer",
            ["version"] = 1,
            ["type"] = original != null ? "image" : "encoding",
            ["encodings"] = new Dictionary<string, object>
            {
                [GetGroup(encoding.Model)] = new Dictionary<string, object>
                {
                    ["0"] = new { encoding = encoding.Base64,
                        @params = new { information_extracted = encoding.InformationExtracted } },
                },
            },
            ["importInfo"] = new { model = encoding.Model,
                information_extracted = encoding.InformationExtracted, strength },
        };
        if (original != null)
        {
            string id = Convert.ToHexStringLower(SHA256.HashData(original));
            root["image"] = Convert.ToBase64String(original);
            root["id"] = id;
            root["name"] = $"{id[..6]}-{id[^6..]}";
        }
        if (thumbnail != null) root["thumbnail"] = "data:image/png;base64," + Convert.ToBase64String(thumbnail);
        root["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }
}
