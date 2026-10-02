using System;

namespace NAITool.Services;

internal static class CharacterPromptRules
{
    // Retain V5 entries even while a model with a lower request limit is selected.
    internal const int MaxRetainedCount = 22;

    internal static int MaxForModel(string model) =>
        NovelAiAnlasCalculator.IsV5Model(model) ? MaxRetainedCount : 6;

    internal static double NormalizeCoordinate(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.5;
}
