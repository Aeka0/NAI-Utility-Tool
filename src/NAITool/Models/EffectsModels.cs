using System;
using System.Collections.Generic;

namespace NAITool.Models;

public enum EffectType
{
    BrightnessContrast,
    SaturationVibrance,
    Temperature,
    Glow,
    RadialBlur,
    Vignette,
    ChromaticAberration,
    Noise,
    Gamma,
    Pixelate,
    SolidBlock,
    Scanline,
    JpegLoss,
}

public sealed class EffectEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public EffectType Type { get; init; }
    public double Value1 { get; set; }
    public double Value2 { get; set; }
    public double Value3 { get; set; }
    public double Value4 { get; set; }
    public double Value5 { get; set; }
    public double Value6 { get; set; }
    public string TextValue { get; set; } = "";
}

public sealed class EffectsWorkspaceState
{
    public byte[]? ImageBytes { get; init; }
    public string? ImagePath { get; init; }
    public Guid? SelectedEffectId { get; init; }
    public List<EffectEntry> Effects { get; init; } = new();
}

public sealed class EffectsPresetFile
{
    public string Name { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public List<EffectEntry> Effects { get; set; } = new();
}
