using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using SkiaSharp;

namespace NAITool.Services;

public class ImageMetadata
{
    public string PositivePrompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
    public List<string> CharacterPrompts { get; set; } = new();
    public List<string> CharacterNegativePrompts { get; set; } = new();
    public List<(double X, double Y)> CharacterCenters { get; set; } = new();
    public List<VibeTransferInfo> VibeTransfers { get; set; } = new();
    public List<PreciseReferenceInfo> PreciseReferences { get; set; } = new();
    public int Width { get; set; }
    public int Height { get; set; }
    public int Steps { get; set; }
    public double Scale { get; set; }
    public double CfgRescale { get; set; }
    public long Seed { get; set; }
    public string Sampler { get; set; } = "";
    public string NoiseSchedule { get; set; } = "";
    public bool Sm { get; set; }
    public bool SmDyn { get; set; }
    public string? Software { get; set; }
    public string? Source { get; set; }
    public Dictionary<string, string> TextChunks { get; set; } = new(StringComparer.Ordinal);
    public bool? QualityToggle { get; set; }
    public int? UcPreset { get; set; }
    public string RawJson { get; set; } = "";
    public bool IsNaiParsed { get; set; }
    public bool IsSdFormat { get; set; }
    public bool IsModelInference { get; set; }
}

public static class ImageMetadataService
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private const string StealthMagicCompressed = "stealth_pngcomp";
    private const string StealthMagicPlain = "stealth_pnginfo";
    private const string UnofficialSignedHash = "Unofficial - Not Signed - Created using NAI Utility Tool";

    public static ImageMetadata? ReadFromFile(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            return ReadFromBytes(bytes);
        }
        catch { return null; }
    }

    public static ImageMetadata? ReadFromBytes(byte[] data)
    {
        var textChunks = ReadGenerationTextChunks(data);
        if (textChunks.TryGetValue("Comment", out var json))
        {
            var meta = ParseMetadataJson(json);
            if (meta != null)
            {
                meta.IsNaiParsed = true;
                meta.Software = textChunks.GetValueOrDefault("Software");
                meta.Source = textChunks.GetValueOrDefault("Source");
                meta.TextChunks = new Dictionary<string, string>(textChunks, StringComparer.Ordinal);
                return meta;
            }
        }
        if (textChunks.TryGetValue("parameters", out var sdText))
        {
            var sdMeta = TryParseSdFormat(sdText);
            if (sdMeta != null)
            {
                sdMeta.TextChunks = new Dictionary<string, string>(textChunks, StringComparer.Ordinal);
                return sdMeta;
            }
        }
        if (json != null)
        {
            return new ImageMetadata
            {
                RawJson = json,
                IsNaiParsed = false,
                Software = textChunks.GetValueOrDefault("Software"),
                Source = textChunks.GetValueOrDefault("Source"),
                TextChunks = new Dictionary<string, string>(textChunks, StringComparer.Ordinal),
            };
        }
        if (textChunks.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var kvp in textChunks)
                sb.AppendLine($"{kvp.Key}: {kvp.Value}");
            return new ImageMetadata
            {
                RawJson = sb.ToString().TrimEnd(),
                IsNaiParsed = false,
                TextChunks = new Dictionary<string, string>(textChunks, StringComparer.Ordinal),
            };
        }
        return null;
    }

    public static Dictionary<string, string> ReadGenerationTextChunks(byte[] data)
    {
        var textChunks = ReadPngTextChunks(data);
        if (textChunks.ContainsKey("Comment") || textChunks.ContainsKey("parameters"))
            return textChunks;

        var stealthChunks = TryReadStealthTextChunks(data);
        return stealthChunks.Count > 0 ? stealthChunks : textChunks;
    }

    public static Dictionary<string, string> ReadRoundTripTextChunks(byte[] data)
    {
        var textChunks = ReadPngTextChunks(data);
        return new Dictionary<string, string>(textChunks, StringComparer.Ordinal);
    }

    public static Dictionary<string, string> ReadPngTextChunks(byte[] data)
    {
        var result = new Dictionary<string, string>();
        if (data.Length < 8) return result;

        for (int i = 0; i < 8; i++)
            if (data[i] != PngSignature[i]) return result;

        int offset = 8;
        while (offset + 12 <= data.Length)
        {
            int length = ReadInt32BE(data, offset);
            if (length < 0 || offset + 12 + length > data.Length) break;

            string type = Encoding.ASCII.GetString(data, offset + 4, 4);
            int dataStart = offset + 8;

            if (type == "tEXt" && length > 0)
            {
                int nullPos = Array.IndexOf(data, (byte)0, dataStart, length);
                if (nullPos >= 0)
                {
                    string key = Encoding.Latin1.GetString(data, dataStart, nullPos - dataStart);
                    string value = Encoding.Latin1.GetString(data, nullPos + 1, length - (nullPos - dataStart) - 1);
                    result[key] = value;
                }
            }
            else if (type == "iTXt" && length > 0)
            {
                int nullPos = Array.IndexOf(data, (byte)0, dataStart, length);
                if (nullPos >= 0 && nullPos + 2 < dataStart + length)
                {
                    string key = Encoding.UTF8.GetString(data, dataStart, nullPos - dataStart);
                    byte compFlag = data[nullPos + 1];
                    int cur = nullPos + 3;
                    int langEnd = Array.IndexOf(data, (byte)0, cur, dataStart + length - cur);
                    if (langEnd >= 0)
                    {
                        cur = langEnd + 1;
                        int tkEnd = Array.IndexOf(data, (byte)0, cur, dataStart + length - cur);
                        if (tkEnd >= 0)
                        {
                            cur = tkEnd + 1;
                            int textLen = dataStart + length - cur;
                            if (textLen > 0 && compFlag == 0)
                                result[key] = Encoding.UTF8.GetString(data, cur, textLen);
                        }
                    }
                }
            }

            if (type == "IEND") break;
            offset += 12 + length;
        }

        return result;
    }

    private static int ReadInt32BE(byte[] data, int offset)
        => (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    // ─── SD WebUI format parsing ───

    private static readonly Dictionary<string, string> SdSamplerMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Euler"] = "k_euler",
        ["Euler a"] = "k_euler_ancestral",
        ["DPM++ 2M"] = "k_dpmpp_2m",
        ["DPM++ SDE"] = "k_dpmpp_sde",
        ["DPM++ 2M SDE"] = "k_dpmpp_2m_sde",
        ["DPM++ 2S a"] = "k_dpmpp_2s_ancestral",
        ["DDIM"] = "ddim_v3",
    };

    private static readonly Dictionary<string, string> SdScheduleMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Karras"] = "karras",
        ["Normal"] = "native",
        ["Simple"] = "native",
        ["Exponential"] = "exponential",
        ["SGM Uniform"] = "native",
    };

    public static ImageMetadata? TryParseSdFormat(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var meta = new ImageMetadata { IsSdFormat = true, IsNaiParsed = false, RawJson = text };

        int negIdx = text.IndexOf("\nNegative prompt:", StringComparison.Ordinal);
        if (negIdx < 0) negIdx = text.IndexOf("Negative prompt:", StringComparison.Ordinal);

        int paramsLineIdx = text.LastIndexOf("\nSteps:", StringComparison.Ordinal);
        if (paramsLineIdx < 0) paramsLineIdx = text.LastIndexOf("Steps:", StringComparison.Ordinal);
        if (paramsLineIdx < 0) return null;

        if (negIdx >= 0 && negIdx < paramsLineIdx)
        {
            meta.PositivePrompt = text[..negIdx].Trim();
            int negStart = text.IndexOf(':', negIdx) + 1;
            meta.NegativePrompt = text[negStart..paramsLineIdx].Trim();
        }
        else
        {
            meta.PositivePrompt = text[..paramsLineIdx].Trim();
            meta.NegativePrompt = "";
        }

        string paramsLine = text[paramsLineIdx..].Trim();
        meta.Steps = ExtractSdInt(paramsLine, @"Steps:\s*(\d+)");
        meta.Seed = ExtractSdLong(paramsLine, @"Seed:\s*(\d+)");
        meta.Scale = ExtractSdDouble(paramsLine, @"CFG scale:\s*([\d.]+)");

        var sizeMatch = System.Text.RegularExpressions.Regex.Match(paramsLine, @"Size:\s*(\d+)x(\d+)");
        if (sizeMatch.Success)
        {
            meta.Width = int.Parse(sizeMatch.Groups[1].Value);
            meta.Height = int.Parse(sizeMatch.Groups[2].Value);
        }

        string sdSampler = ExtractSdString(paramsLine, @"Sampler:\s*([^,]+)");
        string sdSchedule = ExtractSdString(paramsLine, @"Schedule type:\s*([^,]+)");

        if (string.IsNullOrEmpty(sdSchedule))
        {
            foreach (var suffix in new[] { " Karras", " Exponential" })
            {
                if (sdSampler.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    sdSchedule = suffix.Trim();
                    sdSampler = sdSampler[..^suffix.Length];
                    break;
                }
            }
        }

        meta.Sampler = SdSamplerMap.GetValueOrDefault(sdSampler, sdSampler);
        meta.NoiseSchedule = SdScheduleMap.GetValueOrDefault(sdSchedule, sdSchedule);

        return meta;
    }

    private static int ExtractSdInt(string s, string pattern)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s, pattern);
        return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : 0;
    }

    private static long ExtractSdLong(string s, string pattern)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s, pattern);
        return m.Success && long.TryParse(m.Groups[1].Value, out long v) ? v : 0;
    }

    private static double ExtractSdDouble(string s, string pattern)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s, pattern);
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;
    }

    private static string ExtractSdString(string s, string pattern)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s, pattern);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    /// <summary>
    /// Converts an SD-format prompt for use in NAI:
    /// - Strips lora tags: &lt;lora:name:weight&gt;
    /// - Converts SD weight (prompt:weight) to NAI v4 format weight::prompt::
    /// </summary>
    public static string ConvertSdPromptToNai(string prompt)
    {
        string result = System.Text.RegularExpressions.Regex.Replace(prompt, @"<lora:[^>]+>", "");
        result = System.Text.RegularExpressions.Regex.Replace(result, @",\s*,", ",");
        result = result.Trim(' ', ',');

        result = System.Text.RegularExpressions.Regex.Replace(result, @"\(([^()]+):([\d.]+)\)", m =>
        {
            string inner = m.Groups[1].Value;
            string weight = m.Groups[2].Value;
            return $"{weight}::{inner}::";
        });

        return result.Trim();
    }

    public static ImageMetadata? TryParseJson(string json) => ParseMetadataJson(json);

    public static string PrettyPrintJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                WriteSorted(writer, doc.RootElement);
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch { return json; }
    }

    public static string CompactJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                doc.RootElement.WriteTo(writer);
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch { return json; }
    }

    private static void WriteSorted(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(prop.Name);
                    WriteSorted(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteSorted(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    public static byte[] ReplacePngComment(byte[] pngData, string? newComment)
    {
        var textChunks = ReadPngTextChunks(pngData);
        if (newComment == null)
            textChunks.Remove("Comment");
        else
            textChunks["Comment"] = newComment;
        return ReplacePngTextChunks(pngData, textChunks);
    }

    public static byte[] ReplacePngTextChunks(byte[] pngData, IReadOnlyDictionary<string, string>? textChunks)
    {
        using var output = new MemoryStream();
        output.Write(PngSignature, 0, 8);

        int offset = 8;
        bool wroteText = false;
        while (offset + 12 <= pngData.Length)
        {
            int length = ReadInt32BE(pngData, offset);
            if (length < 0 || offset + 12 + length > pngData.Length) break;

            string type = Encoding.ASCII.GetString(pngData, offset + 4, 4);
            int chunkTotalLen = 12 + length;
            bool isTextChunk = type is "tEXt" or "iTXt" or "zTXt" or "eXIf";

            if (!isTextChunk)
            {
                if (type == "IEND" && !wroteText && textChunks != null && textChunks.Count > 0)
                {
                    foreach (var kvp in textChunks)
                        WriteTextChunk(output, kvp.Key, kvp.Value);
                    wroteText = true;
                }

                output.Write(pngData, offset, chunkTotalLen);
                if (type == "IEND") break;
            }

            offset += chunkTotalLen;
        }

        return output.ToArray();
    }

    public static byte[] StripPngMetadata(byte[] pngData)
    {
        using var output = new MemoryStream();
        output.Write(PngSignature, 0, 8);

        int offset = 8;
        while (offset + 12 <= pngData.Length)
        {
            int length = ReadInt32BE(pngData, offset);
            if (length < 0 || offset + 12 + length > pngData.Length) break;

            string type = Encoding.ASCII.GetString(pngData, offset + 4, 4);
            int chunkTotalLen = 12 + length;

            // Strip common metadata-bearing ancillary chunks.
            bool shouldStrip = type is "tEXt" or "iTXt" or "zTXt" or "eXIf";
            if (!shouldStrip)
                output.Write(pngData, offset, chunkTotalLen);

            if (type == "IEND") break;
            offset += chunkTotalLen;
        }

        byte[] stripped = output.ToArray();
        return RemoveNovelAiStealthMetadata(stripped);
    }

    public static byte[] ReapplyNovelAiMetadata(byte[] pngData, byte[]? metadataSourceBytes, int imageWidth, int imageHeight)
    {
        if (pngData.Length == 0 || metadataSourceBytes == null || metadataSourceBytes.Length == 0)
            return pngData;

        var sourceChunks = ReadGenerationTextChunks(metadataSourceBytes);
        if (sourceChunks.Count == 0)
            return pngData;

        var normalizedChunks = NormalizeTextChunksForSavedImage(sourceChunks, imageWidth, imageHeight);
        if (normalizedChunks.Count == 0)
            return pngData;

        byte[] withStealth = InjectNovelAiStealthMetadata(pngData, normalizedChunks);
        return ReplacePngTextChunks(withStealth, normalizedChunks);
    }

    public static byte[] ReapplyNovelAiMetadata(byte[] pngData, IReadOnlyDictionary<string, string>? textChunks)
    {
        if (pngData.Length == 0 || textChunks == null || textChunks.Count == 0)
            return pngData;

        try
        {
            using var bitmap = SKBitmap.Decode(pngData);
            if (bitmap == null)
                return pngData;

            return ReapplyNovelAiMetadata(pngData, textChunks, bitmap.Width, bitmap.Height);
        }
        catch
        {
            return pngData;
        }
    }

    public static byte[] ReapplyNovelAiMetadata(
        byte[] pngData,
        IReadOnlyDictionary<string, string>? textChunks,
        int imageWidth,
        int imageHeight)
    {
        if (pngData.Length == 0 || textChunks == null || textChunks.Count == 0)
            return pngData;

        var normalizedChunks = NormalizeTextChunksForSavedImage(textChunks, imageWidth, imageHeight);
        if (normalizedChunks.Count == 0)
            return pngData;

        byte[] withStealth = InjectNovelAiStealthMetadata(pngData, normalizedChunks);
        return ReplacePngTextChunks(withStealth, normalizedChunks);
    }

    private static byte[] RemoveNovelAiStealthMetadata(byte[] pngData)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(pngData);
            if (bitmap == null) return pngData;

            bool changed = false;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    var color = bitmap.GetPixel(x, y);
                    if (color.Alpha >= 254 && color.Alpha != 255)
                    {
                        bitmap.SetPixel(x, y, new SKColor(color.Red, color.Green, color.Blue, 255));
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                // Even if all alpha values are already 255 in the decoded bitmap,
                // re-encoding removes any hidden data carried through the original PNG structure.
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray() ?? pngData;
        }
        catch
        {
            return pngData;
        }
    }

    private static void WriteTextChunk(Stream output, string keyword, string text)
    {
        byte[] keyBytes = Encoding.ASCII.GetBytes(keyword);
        if (CanEncodeLatin1(text))
        {
            byte[] textBytes = Encoding.Latin1.GetBytes(text);
            int dataLen = keyBytes.Length + 1 + textBytes.Length;

            WriteInt32BE(output, dataLen);
            byte[] typeBytes = Encoding.ASCII.GetBytes("tEXt");
            output.Write(typeBytes, 0, 4);
            output.Write(keyBytes, 0, keyBytes.Length);
            output.WriteByte(0);
            output.Write(textBytes, 0, textBytes.Length);

            byte[] chunkData = new byte[4 + dataLen];
            Array.Copy(typeBytes, 0, chunkData, 0, 4);
            Array.Copy(keyBytes, 0, chunkData, 4, keyBytes.Length);
            chunkData[4 + keyBytes.Length] = 0;
            Array.Copy(textBytes, 0, chunkData, 5 + keyBytes.Length, textBytes.Length);
            uint crc = Crc32(chunkData);
            WriteInt32BE(output, (int)crc);
            return;
        }

        byte[] utf8Bytes = Encoding.UTF8.GetBytes(text);
        using var payload = new MemoryStream();
        payload.Write(keyBytes, 0, keyBytes.Length);
        payload.WriteByte(0);
        payload.WriteByte(0);
        payload.WriteByte(0);
        payload.WriteByte(0);
        payload.WriteByte(0);
        payload.Write(utf8Bytes, 0, utf8Bytes.Length);

        byte[] payloadBytes = payload.ToArray();
        WriteInt32BE(output, payloadBytes.Length);
        byte[] type = Encoding.ASCII.GetBytes("iTXt");
        output.Write(type, 0, 4);
        output.Write(payloadBytes, 0, payloadBytes.Length);

        byte[] crcInput = new byte[4 + payloadBytes.Length];
        Array.Copy(type, 0, crcInput, 0, 4);
        Array.Copy(payloadBytes, 0, crcInput, 4, payloadBytes.Length);
        WriteInt32BE(output, (int)Crc32(crcInput));
    }

    private static void WriteInt32BE(Stream s, int value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    private static readonly uint[] Crc32Table = InitCrc32Table();
    private static uint[] InitCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int j = 0; j < 8; j++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static ImageMetadata? ParseMetadataJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var meta = new ImageMetadata { RawJson = json };

            if (root.TryGetProperty("prompt", out var prompt))
                meta.PositivePrompt = prompt.GetString() ?? "";
            if (root.TryGetProperty("uc", out var uc))
                meta.NegativePrompt = uc.GetString() ?? "";
            if (root.TryGetProperty("width", out var w))
                meta.Width = w.GetInt32();
            if (root.TryGetProperty("height", out var h))
                meta.Height = h.GetInt32();
            if (root.TryGetProperty("steps", out var steps))
                meta.Steps = steps.GetInt32();
            if (root.TryGetProperty("scale", out var scale))
                meta.Scale = scale.GetDouble();
            if (root.TryGetProperty("cfg_rescale", out var cfgRescale))
                meta.CfgRescale = cfgRescale.GetDouble();
            if (root.TryGetProperty("seed", out var seed))
                meta.Seed = seed.GetInt64();
            if (root.TryGetProperty("sampler", out var sampler))
                meta.Sampler = sampler.GetString() ?? "";
            if (root.TryGetProperty("noise_schedule", out var schedule))
                meta.NoiseSchedule = schedule.GetString() ?? "";
            if (root.TryGetProperty("sm", out var sm))
                meta.Sm = sm.GetBoolean();
            if (root.TryGetProperty("sm_dyn", out var smDyn))
                meta.SmDyn = smDyn.GetBoolean();
            if (root.TryGetProperty("quality_toggle", out var qt))
                meta.QualityToggle = qt.GetBoolean();
            if (root.TryGetProperty("ucPreset", out var ucPreset))
                meta.UcPreset = ucPreset.GetInt32();

            if (root.TryGetProperty("v4_prompt", out var v4p) &&
                v4p.TryGetProperty("caption", out var v4Caption) &&
                v4Caption.TryGetProperty("char_captions", out var charCaptions))
            {
                foreach (var cc in charCaptions.EnumerateArray())
                {
                    if (cc.TryGetProperty("char_caption", out var cap))
                        meta.CharacterPrompts.Add(cap.GetString() ?? "");
                    double cx = 0.5, cy = 0.5;
                    if (cc.TryGetProperty("centers", out var centers))
                    {
                        foreach (var c in centers.EnumerateArray())
                        {
                            if (c.TryGetProperty("x", out var xp)) cx = xp.GetDouble();
                            if (c.TryGetProperty("y", out var yp)) cy = yp.GetDouble();
                            break;
                        }
                    }
                    meta.CharacterCenters.Add((cx, cy));
                }
            }

            if (root.TryGetProperty("v4_negative_prompt", out var v4np) &&
                v4np.TryGetProperty("caption", out var v4NegCaption) &&
                v4NegCaption.TryGetProperty("char_captions", out var charNegCaptions))
            {
                foreach (var cc in charNegCaptions.EnumerateArray())
                    if (cc.TryGetProperty("char_caption", out var cap))
                        meta.CharacterNegativePrompts.Add(cap.GetString() ?? "");
            }

            ParseVibeTransfers(root, meta);
            ParsePreciseReferences(root, meta);

            return meta;
        }
        catch { return null; }
    }

    private static bool CanEncodeLatin1(string text)
    {
        foreach (char ch in text)
        {
            if (ch > 0xFF)
                return false;
        }
        return true;
    }

    private static Dictionary<string, string> TryReadStealthTextChunks(byte[] pngData)
    {
        string? jsonText = TryExtractStealthJsonText(pngData);
        if (string.IsNullOrWhiteSpace(jsonText))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        result[prop.Name] = prop.Value.GetString() ?? "";
                }

                if (result.ContainsKey("Comment") || result.ContainsKey("parameters") ||
                    result.ContainsKey("Description") || result.ContainsKey("Software") || result.ContainsKey("Source"))
                {
                    return result;
                }
            }
        }
        catch
        {
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Comment"] = jsonText,
        };
    }

    private static string? TryExtractStealthJsonText(byte[] pngData)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(pngData);
            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
                return null;

            int byteCount = (bitmap.Width * bitmap.Height) / 8;
            if (byteCount <= 0)
                return null;

            byte[] packedBytes = new byte[byteCount];
            int bitCount = 0;
            int byteIndex = 0;
            byte current = 0;

            for (int x = 0; x < bitmap.Width; x++)
            {
                for (int y = 0; y < bitmap.Height; y++)
                {
                    byte bit = (byte)(bitmap.GetPixel(x, y).Alpha & 1);
                    current = (byte)((current << 1) | bit);
                    bitCount++;
                    if ((bitCount & 7) == 0)
                    {
                        packedBytes[byteIndex++] = current;
                        current = 0;
                    }
                }
            }

            ReadOnlySpan<byte> probe = packedBytes;
            byte[] magicCompressed = Encoding.UTF8.GetBytes(StealthMagicCompressed);
            byte[] magicPlain = Encoding.UTF8.GetBytes(StealthMagicPlain);
            int payloadOffset;
            bool compressed;

            if (probe.Length >= magicCompressed.Length &&
                probe[..magicCompressed.Length].SequenceEqual(magicCompressed))
            {
                payloadOffset = magicCompressed.Length;
                compressed = true;
            }
            else if (probe.Length >= magicPlain.Length &&
                     probe[..magicPlain.Length].SequenceEqual(magicPlain))
            {
                payloadOffset = magicPlain.Length;
                compressed = false;
            }
            else
            {
                return null;
            }

            if (payloadOffset + 4 > packedBytes.Length)
                return null;

            int bitLength = ReadInt32BE(packedBytes, payloadOffset);
            if (bitLength <= 0 || (bitLength & 7) != 0)
                return null;

            int payloadLength = bitLength / 8;
            int payloadStart = payloadOffset + 4;
            if (payloadStart + payloadLength > packedBytes.Length)
                return null;

            byte[] payload = new byte[payloadLength];
            Array.Copy(packedBytes, payloadStart, payload, 0, payloadLength);
            byte[] rawBytes = compressed ? DecompressGzip(payload) : payload;
            return Encoding.UTF8.GetString(rawBytes);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] DecompressGzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] CompressGzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static Dictionary<string, string> NormalizeTextChunksForSavedImage(
        IReadOnlyDictionary<string, string> sourceChunks,
        int imageWidth,
        int imageHeight)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kvp in sourceChunks)
        {
            string value = kvp.Key == "Comment"
                ? NormalizeCommentJsonForSavedImage(kvp.Value, imageWidth, imageHeight)
                : kvp.Key == "signed_hash"
                    ? UnofficialSignedHash
                    : kvp.Value;
            normalized[kvp.Key] = value;
        }

        if (normalized.TryGetValue("Comment", out var comment) &&
            string.IsNullOrWhiteSpace(comment))
        {
            normalized.Remove("Comment");
        }

        return normalized;
    }

    private static string NormalizeCommentJsonForSavedImage(string json, int imageWidth, int imageHeight)
    {
        if (string.IsNullOrWhiteSpace(json))
            return json;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return json;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = false,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                writer.WriteStartObject();
                bool wroteWidth = false;
                bool wroteHeight = false;
                bool wroteSignedHash = false;

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("width"))
                    {
                        writer.WriteNumber("width", imageWidth);
                        wroteWidth = true;
                        continue;
                    }

                    if (prop.NameEquals("height"))
                    {
                        writer.WriteNumber("height", imageHeight);
                        wroteHeight = true;
                        continue;
                    }

                    if (prop.NameEquals("signed_hash"))
                    {
                        writer.WriteString("signed_hash", UnofficialSignedHash);
                        wroteSignedHash = true;
                        continue;
                    }

                    prop.WriteTo(writer);
                }

                if (!wroteWidth)
                    writer.WriteNumber("width", imageWidth);
                if (!wroteHeight)
                    writer.WriteNumber("height", imageHeight);
                if (!wroteSignedHash)
                    writer.WriteString("signed_hash", UnofficialSignedHash);

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return json;
        }
    }

    private static byte[] InjectNovelAiStealthMetadata(byte[] pngData, IReadOnlyDictionary<string, string> textChunks)
    {
        try
        {
            byte[] cleaned = SetAllAlphaToOpaque(pngData);
            using var bitmap = SKBitmap.Decode(cleaned);
            if (bitmap == null)
                return pngData;

            string jsonText = SerializeTextChunks(textChunks);
            byte[] hiddenStream = BuildStealthPayload(jsonText, compressed: true);
            byte[] bitStream = ExpandToBits(hiddenStream);

            int capacity = bitmap.Width * bitmap.Height;
            if (bitStream.Length > capacity)
                return cleaned;

            int bitIndex = 0;
            for (int x = 0; x < bitmap.Width; x++)
            {
                for (int y = 0; y < bitmap.Height; y++)
                {
                    byte alpha = 0xFF;
                    if (bitIndex < bitStream.Length)
                        alpha = (byte)(0xFE | bitStream[bitIndex++]);

                    var color = bitmap.GetPixel(x, y);
                    bitmap.SetPixel(x, y, new SKColor(color.Red, color.Green, color.Blue, alpha));
                }
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray() ?? cleaned;
        }
        catch
        {
            return pngData;
        }
    }

    private static byte[] SetAllAlphaToOpaque(byte[] pngData)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(pngData);
            if (bitmap == null)
                return pngData;

            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    var color = bitmap.GetPixel(x, y);
                    if (color.Alpha != 255)
                        bitmap.SetPixel(x, y, new SKColor(color.Red, color.Green, color.Blue, 255));
                }
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray() ?? pngData;
        }
        catch
        {
            return pngData;
        }
    }

    private static string SerializeTextChunks(IReadOnlyDictionary<string, string> textChunks)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            foreach (var kvp in textChunks)
                writer.WriteString(kvp.Key, kvp.Value);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static byte[] BuildStealthPayload(string jsonText, bool compressed)
    {
        byte[] raw = Encoding.UTF8.GetBytes(jsonText);
        byte[] payload = compressed ? CompressGzip(raw) : raw;
        string magic = compressed ? StealthMagicCompressed : StealthMagicPlain;
        byte[] magicBytes = Encoding.UTF8.GetBytes(magic);
        byte[] result = new byte[magicBytes.Length + 4 + payload.Length];
        Array.Copy(magicBytes, result, magicBytes.Length);
        WriteInt32BE(result, magicBytes.Length, payload.Length * 8);
        Array.Copy(payload, 0, result, magicBytes.Length + 4, payload.Length);
        return result;
    }

    private static byte[] ExpandToBits(byte[] data)
    {
        var bits = new byte[data.Length * 8];
        int index = 0;
        foreach (byte value in data)
        {
            for (int shift = 7; shift >= 0; shift--)
                bits[index++] = (byte)((value >> shift) & 1);
        }

        return bits;
    }

    private static void WriteInt32BE(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void ParseVibeTransfers(JsonElement root, ImageMetadata meta)
    {
        if (root.TryGetProperty("reference_image_multiple", out var imageArray) &&
            root.TryGetProperty("reference_information_extracted_multiple", out var infoArray))
        {
            var strengthValues = TryReadDoubleArray(root, "reference_strength_multiple");
            int index = 0;
            foreach (var item in imageArray.EnumerateArray())
            {
                string imageBase64 = item.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(imageBase64))
                {
                    index++;
                    continue;
                }

                double info = TryReadDoubleAt(infoArray, index, 1.0);
                double strength = index < strengthValues.Count ? strengthValues[index] : 0.6;
                meta.VibeTransfers.Add(new VibeTransferInfo
                {
                    ImageBase64 = imageBase64,
                    Strength = strength,
                    InformationExtracted = info,
                    FileName = LocalizationService.Instance.Format("references.imported.vibe_numbered", index + 1),
                });
                index++;
            }

            return;
        }

        if (root.TryGetProperty("reference_image", out var singleImage))
        {
            string imageBase64 = singleImage.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(imageBase64))
            {
                meta.VibeTransfers.Add(new VibeTransferInfo
                {
                    ImageBase64 = imageBase64,
                    Strength = TryReadDouble(root, "reference_strength", 0.6),
                    InformationExtracted = TryReadDouble(root, "reference_information_extracted", 1.0),
                    FileName = LocalizationService.Instance.Format("references.imported.vibe_numbered", 1),
                });
            }
        }
    }

    private static void ParsePreciseReferences(JsonElement root, ImageMetadata meta)
    {
        if (root.TryGetProperty("director_reference_images", out var dirImageArray) &&
            dirImageArray.ValueKind == JsonValueKind.Array)
        {
            var strengthValues = TryReadDoubleArray(root, "director_reference_strength_values");
            var secondaryValues = TryReadDoubleArray(root, "director_reference_secondary_strength_values");
            bool hasDescriptions = root.TryGetProperty("director_reference_descriptions", out var descArray)
                && descArray.ValueKind == JsonValueKind.Array;

            int index = 0;
            foreach (var item in dirImageArray.EnumerateArray())
            {
                string imageBase64 = item.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(imageBase64))
                {
                    index++;
                    continue;
                }

                string typeValue = "";
                if (hasDescriptions)
                {
                    var descElements = descArray.EnumerateArray().ToList();
                    if (index < descElements.Count &&
                        descElements[index].TryGetProperty("caption", out var caption) &&
                        caption.TryGetProperty("base_caption", out var baseCap))
                    {
                        typeValue = baseCap.GetString() ?? "";
                    }
                }

                double secondaryStrength = index < secondaryValues.Count ? secondaryValues[index] : 0.0;
                meta.PreciseReferences.Add(new PreciseReferenceInfo
                {
                    ImageBase64 = imageBase64,
                    FileName = LocalizationService.Instance.Format("references.imported.precise_numbered", index + 1),
                    ReferenceType = ParsePreciseReferenceType(typeValue),
                    Strength = index < strengthValues.Count ? strengthValues[index] : 1.0,
                    Fidelity = Math.Round(1.0 - secondaryStrength, 2),
                });
                index++;
            }
        }

        if (meta.PreciseReferences.Count > 0)
            return;

        if (root.TryGetProperty("precise_references", out var objectArray) &&
            objectArray.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (var item in objectArray.EnumerateArray())
            {
                string imageBase64 = TryReadString(item, "imageBase64", "image", "reference_image");
                if (string.IsNullOrWhiteSpace(imageBase64))
                {
                    index++;
                    continue;
                }

                meta.PreciseReferences.Add(new PreciseReferenceInfo
                {
                    ImageBase64 = imageBase64,
                    FileName = TryReadString(item, "fileName", "filename", "name", "label"),
                    ReferenceType = ParsePreciseReferenceType(TryReadString(item, "referenceType", "reference_type", "type")),
                    Strength = TryReadDouble(item, "strength", 1.0, "reference_strength"),
                    Fidelity = TryReadDouble(item, "fidelity", 0.0, "reference_fidelity"),
                });
                index++;
            }
        }

        if (meta.PreciseReferences.Count > 0)
            return;

        bool hasTypeArray = root.TryGetProperty("reference_type_multiple", out var typeArray);
        bool hasFidelityArray = root.TryGetProperty("reference_fidelity_multiple", out _);
        if (!hasTypeArray && !hasFidelityArray)
        {
            return;
        }

        if (!root.TryGetProperty("reference_image_multiple", out var imageArray) ||
            imageArray.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var legacyStrengthValues = TryReadDoubleArray(root, "reference_strength_multiple");
        var fidelityValues = TryReadDoubleArray(root, "reference_fidelity_multiple");
        int currentIndex = 0;
        foreach (var item in imageArray.EnumerateArray())
        {
            string imageBase64 = item.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(imageBase64))
            {
                currentIndex++;
                continue;
            }

            string fileName = LocalizationService.Instance.Format("references.imported.precise_numbered", currentIndex + 1);
            string typeValue = hasTypeArray && typeArray.ValueKind == JsonValueKind.Array
                ? TryReadStringAt(typeArray, currentIndex)
                : "";
            meta.PreciseReferences.Add(new PreciseReferenceInfo
            {
                ImageBase64 = imageBase64,
                FileName = fileName,
                ReferenceType = ParsePreciseReferenceType(typeValue),
                Strength = currentIndex < legacyStrengthValues.Count ? legacyStrengthValues[currentIndex] : 1.0,
                Fidelity = currentIndex < fidelityValues.Count ? fidelityValues[currentIndex] : 0.0,
            });
            currentIndex++;
        }
    }

    private static double TryReadDouble(JsonElement element, string propertyName, double fallback, params string[] aliases)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number)
            return value.GetDouble();

        foreach (var alias in aliases)
        {
            if (element.TryGetProperty(alias, out value) && value.ValueKind == JsonValueKind.Number)
                return value.GetDouble();
        }

        return fallback;
    }

    private static string TryReadString(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";
        }

        return "";
    }

    private static List<double> TryReadDoubleArray(JsonElement element, string propertyName)
    {
        var result = new List<double>();
        if (!element.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number)
                result.Add(item.GetDouble());
        }

        return result;
    }

    private static double TryReadDoubleAt(JsonElement array, int index, double fallback)
    {
        if (array.ValueKind != JsonValueKind.Array)
            return fallback;

        int current = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (current == index)
                return item.ValueKind == JsonValueKind.Number ? item.GetDouble() : fallback;
            current++;
        }

        return fallback;
    }

    private static string TryReadStringAt(JsonElement array, int index)
    {
        if (array.ValueKind != JsonValueKind.Array)
            return "";

        int current = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (current == index)
                return item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
            current++;
        }

        return "";
    }

    private static PreciseReferenceType ParsePreciseReferenceType(string rawType) =>
        rawType.Trim().ToLowerInvariant() switch
        {
            "character" => PreciseReferenceType.Character,
            "style" => PreciseReferenceType.Style,
            "character_style" => PreciseReferenceType.CharacterAndStyle,
            "character&style" => PreciseReferenceType.CharacterAndStyle,
            "character+style" => PreciseReferenceType.CharacterAndStyle,
            _ => PreciseReferenceType.CharacterAndStyle,
        };
}
