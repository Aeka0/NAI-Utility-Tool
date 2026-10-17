namespace NAITool.Services;

/// <summary>Deferred metadata payload: inspecting a PNG must not decode all reference images.</summary>
public sealed record VibeMetadataReference(System.Text.Json.JsonElement Payload,
    double? Strength, double? InformationExtracted);

public enum PreciseReferenceType
{
    CharacterAndStyle,
    Character,
    Style,
}

public sealed class VibeTransferInfo
{
    public string ImageBase64 { get; set; } = "";
    public double Strength { get; set; } = 0.6;
    public double InformationExtracted { get; set; } = 1.0;
    public string FileName { get; set; } = "";
    public bool IsEncoded { get; set; }
    /// <summary>Import-only metadata. API requests use only the resolved ImageBase64 payload.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public VibeMetadataReference? MetadataSource { get; set; }
}

public sealed class PreciseReferenceInfo
{
    public string ImageBase64 { get; set; } = "";
    public PreciseReferenceType ReferenceType { get; set; } = PreciseReferenceType.CharacterAndStyle;
    public double Strength { get; set; } = 1.0;
    public double Fidelity { get; set; }
    public string FileName { get; set; } = "";
}
