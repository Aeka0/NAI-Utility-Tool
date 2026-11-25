namespace NAITool.Services;

internal static class PreciseReferenceRules
{
    internal const double V5StrengthMax = 5;

    internal static bool SupportsV5Options(string model) => NovelAiAnlasCalculator.IsV5Model(model);

    internal static bool Supports(string model, bool inpainting = false) =>
        NovelAiAnlasCalculator.IsV45Model(model) ||
        (SupportsV5Options(model) && !inpainting && !model.EndsWith("-inpainting", StringComparison.Ordinal));

    internal static bool SupportsType(string model, PreciseReferenceType type) =>
        type != PreciseReferenceType.Outfit || SupportsV5Options(model);

    internal static bool CanPreferSameOutfit(PreciseReferenceType type) =>
        type is PreciseReferenceType.Character or PreciseReferenceType.CharacterAndStyle;

    internal static bool PrefersSameOutfit(PreciseReferenceType type, bool? preference) =>
        CanPreferSameOutfit(type) && (preference ?? (type == PreciseReferenceType.CharacterAndStyle));

    internal static double MaxStrength(string model) => SupportsV5Options(model) ? V5StrengthMax : 1;

    internal static string ToApiCaption(PreciseReferenceInfo reference, bool isV5)
    {
        bool sameOutfit = isV5 && PrefersSameOutfit(reference.ReferenceType, reference.PreferSameOutfit);
        return reference.ReferenceType switch
        {
            PreciseReferenceType.Outfit => "costume",
            PreciseReferenceType.Style => "style",
            PreciseReferenceType.Character => sameOutfit ? "character_mainoutfit" : "character",
            _ => isV5 && !sameOutfit ? "character&style_anyoutfit" : "character&style",
        };
    }

    internal static (PreciseReferenceType Type, bool? PreferSameOutfit) ParseCaption(string caption) =>
        caption.Trim().ToLowerInvariant() switch
        {
            "character" => (PreciseReferenceType.Character, null),
            "character_mainoutfit" => (PreciseReferenceType.Character, true),
            "style" => (PreciseReferenceType.Style, null),
            "costume" or "outfit" => (PreciseReferenceType.Outfit, null),
            "character&style_anyoutfit" => (PreciseReferenceType.CharacterAndStyle, false),
            _ => (PreciseReferenceType.CharacterAndStyle, null),
        };

    internal static List<PreciseReferenceInfo>? Filter(string model, List<PreciseReferenceInfo>? references,
        bool inpainting = false) =>
        references is { Count: > 0 } && Supports(model, inpainting)
            ? references.Where(reference => SupportsType(model, reference.ReferenceType)).ToList()
            : null;
}
