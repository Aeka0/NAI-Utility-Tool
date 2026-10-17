using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace NAITool.Services;

public static class VibeImportService
{
    public static string? SelectCompatibleModel(VibeFileService.Document document, string currentModel,
        IReadOnlyList<string> availableModels)
    {
        bool HasModel(string? model) => model != null && availableModels.Contains(model) &&
            document.Encodings.Any(e => e.Model == model);
        if (HasModel(currentModel)) return currentModel;
        if (HasModel(document.PreferredModel)) return document.PreferredModel;
        return document.Encodings.Select(e => e.Model).FirstOrDefault(HasModel);
    }

    public sealed record Prepared(string ImageBase64, bool IsEncoded, string? ImageHash,
        string? ThumbnailHash, string? OriginalBase64, string? OriginalPath,
        VibeFileService.Document Document, double InformationExtracted, double Strength);

    public static Prepared PrepareFile(byte[] bytes, string model, string cacheDir) =>
        PrepareDocument(VibeFileService.Parse(bytes), model, cacheDir);

    public static Prepared PrepareMetadata(VibeMetadataReference source, string sourceModel,
        string currentModel, string cacheDir)
    {
        byte[] bytes;
        if (source.Payload.ValueKind == JsonValueKind.Object)
            bytes = Encoding.UTF8.GetBytes(source.Payload.GetRawText());
        else if (source.Payload.ValueKind == JsonValueKind.String)
        {
            string text = source.Payload.GetString() ?? "";
            if (text.Length > VibeFileService.MaxFileBytes * 4L / 3)
                throw new InvalidDataException("Vibe metadata too large.");
            if (text.AsSpan().TrimStart().StartsWith("{")) bytes = Encoding.UTF8.GetBytes(text);
            else
            {
                if (text.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    int comma = text.IndexOf(',');
                    if (comma < 0 || !text[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Invalid image data URI.");
                    text = text[(comma + 1)..];
                }
                bytes = Convert.FromBase64String(text);
            }
        }
        else throw new InvalidDataException("Invalid vibe metadata payload.");

        if (bytes.Length == 0 || bytes.Length > VibeFileService.MaxFileBytes)
            throw new InvalidDataException("Invalid vibe metadata size.");

        VibeFileService.Document document;
        int first = 0;
        while (first < bytes.Length && bytes[first] is 9 or 10 or 13 or 32) first++;
        if (first < bytes.Length && bytes[first] == (byte)'{')
            document = VibeFileService.Parse(bytes);
        else
        {
            // V3 metadata can carry a source image; V4+ commonly carries only opaque encoding bytes.
            using var stream = new SKMemoryStream(bytes);
            using var codec = SKCodec.Create(stream);
            double ie = source.InformationExtracted ?? 1.0;
            double strength = source.Strength ?? 0.6;
            if (codec != null)
            {
                byte[] png = VibeFileService.NormalizeImage(bytes)
                    ?? throw new InvalidDataException("Unreadable vibe source image.");
                document = new(png, null, Array.Empty<VibeFileService.Encoding>(), ie, strength, bytes);
            }
            else
            {
                document = new(null, null,
                    new[] { new VibeFileService.Encoding(sourceModel, ie, Convert.ToBase64String(bytes)) }, ie, strength);
            }
        }
        document = document with
        {
            InformationExtracted = source.InformationExtracted ?? document.InformationExtracted,
            Strength = source.Strength ?? document.Strength,
        };
        return PrepareDocument(document, currentModel, cacheDir);
    }

    private static Prepared PrepareDocument(VibeFileService.Document document, string model, string cacheDir)
    {
        var encoding = document.FindEncoding(model, document.InformationExtracted);
        byte[]? original = document.OriginalPng;
        string? hash = original == null ? null : VibeCacheService.ComputeImageHash(original);
        byte[]? thumbnail = original == null ? document.ThumbnailPng : VibeCacheService.CreateCanonicalThumbnail(original);
        string? thumbHash = original == null ? null : VibeCacheService.ComputeThumbnailHash(thumbnail!);
        string? path = document.Encodings.Count == 0 ? null : VibeCacheService.ImportDocument(cacheDir, document);
        string? originalBase64 = original == null ? null : Convert.ToBase64String(original);
        return new(encoding?.Base64 ?? originalBase64 ?? "", encoding != null, hash, thumbHash,
            originalBase64, path,
            document with { OriginalPng = null, OriginalBytes = null, ThumbnailPng = thumbnail },
            document.InformationExtracted, document.Strength);
    }
}
