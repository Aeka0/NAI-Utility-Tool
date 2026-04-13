using System;
using System.Collections.Generic;
using System.IO;
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
        var textChunks = ReadPngTextChunks(data);
        if (textChunks.TryGetValue("Comment", out var json))
        {
            var meta = ParseMetadataJson(json);
            if (meta != null)
            {
                meta.IsNaiParsed = true;
                meta.Software = textChunks.GetValueOrDefault("Software");
                meta.Source = textChunks.GetValueOrDefault("Source");
                return meta;
            }
        }
        if (textChunks.TryGetValue("parameters", out var sdText))
        {
            var sdMeta = TryParseSdFormat(sdText);
            if (sdMeta != null) return sdMeta;
        }
        if (json != null)
        {
            return new ImageMetadata
            {
                RawJson = json,
                IsNaiParsed = false,
                Software = textChunks.GetValueOrDefault("Software"),
                Source = textChunks.GetValueOrDefault("Source"),
            };
        }
        if (textChunks.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var kvp in textChunks)
                sb.AppendLine($"{kvp.Key}: {kvp.Value}");
            return new ImageMetadata { RawJson = sb.ToString().TrimEnd(), IsNaiParsed = false };
        }
        return null;
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
        ["Normal"] = "normal",
        ["Simple"] = "normal",
        ["Exponential"] = "exponential",
        ["SGM Uniform"] = "normal",
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
        using var output = new MemoryStream();
        output.Write(PngSignature, 0, 8);

        int offset = 8;
        bool wroteComment = false;

        while (offset + 12 <= pngData.Length)
        {
            int length = ReadInt32BE(pngData, offset);
            if (length < 0 || offset + 12 + length > pngData.Length) break;
            string type = Encoding.ASCII.GetString(pngData, offset + 4, 4);
            int chunkTotalLen = 12 + length;

            bool isOldComment = false;
            if ((type == "tEXt" || type == "iTXt") && length > 0)
            {
                int dataStart = offset + 8;
                int nullPos = Array.IndexOf(pngData, (byte)0, dataStart, length);
                if (nullPos >= 0)
                {
                    string key = Encoding.ASCII.GetString(pngData, dataStart, nullPos - dataStart);
                    if (key == "Comment") isOldComment = true;
                }
            }

            if (isOldComment)
            {
                if (!wroteComment && newComment != null)
                {
                    WriteTextChunk(output, "Comment", newComment);
                    wroteComment = true;
                }
                offset += chunkTotalLen;
                continue;
            }

            if (type == "IEND" && !wroteComment && newComment != null)
            {
                WriteTextChunk(output, "Comment", newComment);
                wroteComment = true;
            }

            output.Write(pngData, offset, chunkTotalLen);
            if (type == "IEND") break;
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
        byte[] keyBytes = Encoding.Latin1.GetBytes(keyword);
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
