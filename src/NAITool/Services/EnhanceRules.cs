using System;
using System.Collections.Generic;
using System.Linq;

namespace NAITool.Services;

internal static class EnhanceRules
{
    internal const long MaxPixels = 3L * 1024 * 1024;

    internal static (double Strength, double Noise) GetMagnitudeValues(int magnitude) =>
        Math.Clamp(magnitude, 1, 5) switch
        {
            1 => (0.2, 0.0),
            2 => (0.4, 0.0),
            3 => (0.5, 0.0),
            4 => (0.6, 0.0),
            _ => (0.7, 0.1),
        };

    internal static bool CanUseMax(string model, int width, int height) =>
        NovelAiAnlasCalculator.IsV5Model(model) && width > 0 && height > 0 &&
        (long)width * height < MaxPixels * 0.8;

    internal static (int Width, int Height) GetOutputDimensions(
        int width, int height, double amount, bool useMax = false)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            throw new ArgumentOutOfRangeException(nameof(width), "Enhance dimensions exceed the supported pixel range.");
        if (!double.IsFinite(amount) || amount is not (1.0 or 1.5 or 2.0))
            throw new ArgumentOutOfRangeException(nameof(amount));

        double scale = useMax
            ? Math.Min(2, Math.Sqrt((double)MaxPixels / ((long)width * height)))
            : amount;
        return ((int)Math.Floor(width * scale), (int)Math.Floor(height * scale));
    }

    internal static List<double> GetAvailableAmounts(int width, int height)
    {
        var amounts = new List<double>();
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            return amounts;

        amounts.Add(1.0);
        bool standardPortrait = width == 832 && height == 1216 || width == 1216 && height == 832;
        foreach (double amount in standardPortrait ? new[] { 1.5 } : new[] { 1.5, 2.0 })
        {
            var dims = GetOutputDimensions(width, height, amount);
            if ((long)dims.Width * dims.Height <= MaxPixels &&
                (standardPortrait || dims.Width % 64 == 0 && dims.Height % 64 == 0))
                amounts.Add(amount);
        }
        return amounts;
    }

    internal static double GetPreferredAmount(IReadOnlyList<double> available, double requested) =>
        available.OrderBy(value => Math.Abs(value - (double.IsFinite(requested) ? requested : 1.5))).First();
}
