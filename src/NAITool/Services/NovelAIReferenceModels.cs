namespace NAITool.Services;

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
}

public sealed class PreciseReferenceInfo
{
    public string ImageBase64 { get; set; } = "";
    public PreciseReferenceType ReferenceType { get; set; } = PreciseReferenceType.CharacterAndStyle;
    public double Strength { get; set; } = 1.0;
    public double Fidelity { get; set; }
    public string FileName { get; set; } = "";
}
