using System;
using System.Collections.Generic;
using System.Linq;

namespace NAITool.Models;

public sealed class AutomationSettings
{
    public string SelectedPresetName { get; set; } = "";
    public AutomationGenerationOptions Generation { get; set; } = new();
    public AutomationRandomizationOptions Randomization { get; set; } = new();
    public AutomationEffectsOptions Effects { get; set; } = new();

    public AutomationSettings Clone() => new()
    {
        SelectedPresetName = SelectedPresetName ?? "",
        Generation = Generation?.Clone() ?? new(),
        Randomization = Randomization?.Clone() ?? new(),
        Effects = Effects?.Clone() ?? new(),
    };

    public void Normalize()
    {
        SelectedPresetName ??= "";
        Generation ??= new();
        Generation.Normalize();
        Randomization ??= new();
        Randomization.Normalize();
        Effects ??= new();
        Effects.Normalize();
    }
}

public sealed class AutomationGenerationOptions
{
    public double MinDelaySeconds { get; set; } = 5.0;
    public double MaxDelaySeconds { get; set; } = 15.0;
    public int RequestLimit { get; set; }
    public int FailureRetryLimit { get; set; }

    public AutomationGenerationOptions Clone() => new()
    {
        MinDelaySeconds = MinDelaySeconds,
        MaxDelaySeconds = MaxDelaySeconds,
        RequestLimit = RequestLimit,
        FailureRetryLimit = FailureRetryLimit,
    };

    public void Normalize()
    {
        MinDelaySeconds = Math.Max(0.5, MinDelaySeconds);
        MaxDelaySeconds = Math.Max(MinDelaySeconds, MaxDelaySeconds);
        RequestLimit = Math.Max(0, RequestLimit);
        FailureRetryLimit = Math.Max(0, FailureRetryLimit);
    }
}

public sealed class AutomationRandomizationOptions
{
    public bool RandomizeSize { get; set; }
    public List<string> SizePresets { get; set; } = DefaultSizePresets();
    public bool RandomizeVibeFiles { get; set; }
    public bool RandomizeStyleTags { get; set; }
    public bool RandomizePrompt { get; set; }

    public AutomationRandomizationOptions Clone() => new()
    {
        RandomizeSize = RandomizeSize,
        SizePresets = (SizePresets ?? []).ToList(),
        RandomizeVibeFiles = RandomizeVibeFiles,
        RandomizeStyleTags = RandomizeStyleTags,
        RandomizePrompt = RandomizePrompt,
    };

    public void Normalize()
    {
        SizePresets ??= DefaultSizePresets();
        SizePresets = SizePresets
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<string> DefaultSizePresets() =>
    [
        "1024x1024",
        "1216x832",
        "832x1216",
    ];
}

public sealed class AutomationEffectsOptions
{
    public bool Enabled { get; set; }
    public bool UpscaleEnabled { get; set; }
    public string UpscaleModel { get; set; } = "";
    public int UpscaleScale { get; set; } = 2;
    public bool FxEnabled { get; set; }
    public string FxPresetName { get; set; } = "";

    public AutomationEffectsOptions Clone() => new()
    {
        Enabled = Enabled,
        UpscaleEnabled = UpscaleEnabled,
        UpscaleModel = UpscaleModel ?? "",
        UpscaleScale = UpscaleScale,
        FxEnabled = FxEnabled,
        FxPresetName = FxPresetName ?? "",
    };

    public void Normalize()
    {
        UpscaleModel ??= "";
        FxPresetName ??= "";
        UpscaleScale = UpscaleScale switch
        {
            2 or 3 or 4 => UpscaleScale,
            _ => 2,
        };
        if (Enabled && !FxEnabled && !string.IsNullOrWhiteSpace(FxPresetName))
            FxEnabled = true;
        Enabled = UpscaleEnabled || FxEnabled;
    }
}

public sealed class AutomationPresetFile
{
    public string Name { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public AutomationSettings Settings { get; set; } = new();
}

public sealed class AutomationPresetListItem
{
    public string Name { get; init; } = "";
    public DateTime SavedAt { get; init; }
    public string FilePath { get; init; } = "";
}
