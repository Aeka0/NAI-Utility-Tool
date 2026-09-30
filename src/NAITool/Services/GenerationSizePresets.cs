namespace NAITool.Services;

public sealed record GenerationSizePreset(string Category, string Orientation, int Width, int Height);

public static class GenerationSizePresets
{
    // NovelAI's V3/V4/V4.5/V5 resolution menu. Kept separate from the canvas
    // fitting presets, which are also used when importing an image into I2I.
    public static readonly GenerationSizePreset[] Defaults =
    [
        new("normal", "portrait", 832, 1216),
        new("normal", "landscape", 1216, 832),
        new("normal", "square", 1024, 1024),
        new("large", "portrait", 1024, 1536),
        new("large", "landscape", 1536, 1024),
        new("large", "square", 1472, 1472),
        new("wallpaper", "portrait", 1088, 1920),
        new("wallpaper", "landscape", 1920, 1088),
        new("small", "portrait", 512, 768),
        new("small", "landscape", 768, 512),
        new("small", "square", 640, 640),
    ];
}
