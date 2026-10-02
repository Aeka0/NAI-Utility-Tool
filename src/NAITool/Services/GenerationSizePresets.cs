namespace NAITool.Services;

public sealed record GenerationSizePreset(string Category, int Width, int Height);

public static class GenerationSizePresets
{
    // Generation presets are separate from canvas fitting presets used by image imports.
    public static readonly GenerationSizePreset[] Defaults =
    [
        new("normal", 832, 1216),
        new("normal", 1216, 832),
        new("normal", 1024, 1024),
        new("large", 1024, 1536),
        new("large", 1536, 1024),
        new("large", 1472, 1472),
        new("wallpaper", 1088, 1920),
        new("wallpaper", 1920, 1088),
        new("small", 512, 768),
        new("small", 768, 512),
        new("small", 640, 640),
    ];
}
