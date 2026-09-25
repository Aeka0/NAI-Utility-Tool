using System.Text.Json.Serialization;

namespace NAITool.Services;

public class NAIParameters
{
    public const string DefaultGenerationModel = "nai-diffusion-5-full";
    public const string DefaultInpaintModel = "nai-diffusion-4-5-full-inpainting";
    public const string DefaultI2IDenoiseModel = "nai-diffusion-4-5-full";

    public string Model { get; set; } = DefaultGenerationModel;
    public string Sampler { get; set; } = "k_euler_ancestral";
    public string Schedule { get; set; } = "karras";
    public double Scale { get; set; } = 3.0;
    public double CfgRescale { get; set; } = 0;
    public bool Sm { get; set; }
    public bool Variety { get; set; } = false;
    public bool QualityToggle { get; set; } = true;
    public bool TagHintTransparentBackground { get; set; } = true;
    public bool StraightAlpha { get; set; } = false;
    public int Steps { get; set; } = 28;
    [JsonConverter(typeof(SeedTextJsonConverter))]
    public string Seed { get; set; } = "0";
    public int UcPreset { get; set; } = 0;
    public double InpaintStrength { get; set; } = 1.0;
    public double InpaintNoise { get; set; }
    public double DenoiseStrength { get; set; } = 0.7;
    public double DenoiseNoise { get; set; } = 0;
}
