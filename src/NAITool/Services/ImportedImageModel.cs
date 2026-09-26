using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NAITool.Services;

public static class ImportedImageModel
{
    // Source may append a model hash. Only accept the complete NAI name, never a
    // substring of a third-party checkpoint name or a generic "Stable Diffusion" source.
    private static readonly Regex DisplayNamePattern = new(
        @"^(?:NovelAI|NAI) Diffusion (?:(Anime|Furry) )?V(3|4\.5|4|5)(?: (Full|Curated)(?: Preview)?)?(?: (Inpainting))?(?: [0-9a-f]{8,64})?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static string? ResolveForTarget(string? name, IReadOnlyList<string> availableModels)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string model = name.Trim().ToLowerInvariant();
        if (!model.StartsWith("nai-diffusion-", StringComparison.Ordinal))
        {
            var match = DisplayNamePattern.Match(name.Trim());
            if (!match.Success) return null;
            bool furry = match.Groups[1].Value.Equals("Furry", StringComparison.OrdinalIgnoreCase);
            string version = match.Groups[2].Value.Replace('.', '-');
            bool curated = match.Groups[3].Value.Equals("Curated", StringComparison.OrdinalIgnoreCase);
            if (furry && version != "3") return null;
            if (version == "3" && match.Groups[3].Success) return null;
            model = version == "3"
                ? furry ? "nai-diffusion-furry-3" : "nai-diffusion-3"
                : $"nai-diffusion-{version}-{(curated ? "curated" : "full")}";
        }

        string? exact = availableModels.FirstOrDefault(x => x.Equals(model, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        const string inpainting = "-inpainting";
        if (model.EndsWith(inpainting, StringComparison.Ordinal)) model = model[..^inpainting.Length];
        if (model == "nai-diffusion-4-curated-preview") model = "nai-diffusion-4-curated";
        // Generation and inpainting have different IDs. Never fall back to a
        // different version/edition when the requested variant is unavailable.
        return availableModels.FirstOrDefault(x =>
            x.Equals(model, StringComparison.OrdinalIgnoreCase) ||
            x.Equals(model + inpainting, StringComparison.OrdinalIgnoreCase) ||
            (model == "nai-diffusion-4-curated" && x == "nai-diffusion-4-curated-preview"));
    }
}
