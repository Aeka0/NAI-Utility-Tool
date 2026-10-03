namespace NAITool.Services;

/// <summary>Standalone upscaler contract and input-pixel pricing from the official client.</summary>
internal static class NovelAiUpscaleRules
{
    // Verified 2026-10-03 against the official API schema and production client:
    // https://image.novelai.net/docs/doc.json (image.UpscaleRequest)
    // /_next/static/chunks/1601-6ba10aad6d763f0c.js (module 50464, tY)
    // /_next/static/chunks/pages/_app-47190d048e3a43ce.js (module 44868)
    internal const string Model = "nai-diffusion-5-curated";
    internal const int Scale = 2;
    internal const int MaxInputPixels = 3_145_728;

    // Null means unsupported, never free. Standalone upscale has no Opus discount.
    internal static int? EstimateAnlas(int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        return ((long)width * height) switch
        {
            <= 1_048_576 => 1,
            <= 1_747_627 => 2,
            <= 2_446_678 => 3,
            <= MaxInputPixels => 4,
            _ => null,
        };
    }
}
