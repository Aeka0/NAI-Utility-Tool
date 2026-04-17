using System;
using System.Collections.Generic;
using System.Linq;

namespace NAITool.Models;

public sealed class AutomationSettings
{
    public string SelectedPresetName { get; set; } = "";
    public AutomationGenerationOptions Generation { get; set; } = new();
    public AutomationErrorHandlingOptions ErrorHandling { get; set; } = new();
    public AutomationRandomizationOptions Randomization { get; set; } = new();
    public AutomationEffectsOptions Effects { get; set; } = new();

    public AutomationSettings Clone() => new()
    {
        SelectedPresetName = SelectedPresetName ?? "",
        Generation = Generation?.Clone() ?? new(),
        ErrorHandling = ErrorHandling?.Clone() ?? new(),
        Randomization = Randomization?.Clone() ?? new(),
        Effects = Effects?.Clone() ?? new(),
    };

    public void Normalize()
    {
        SelectedPresetName ??= "";
        Generation ??= new();
        Generation.Normalize();
        ErrorHandling ??= new();
        ErrorHandling.Normalize();
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
    public int RequestLimit { get; set; } = 100;

    public AutomationGenerationOptions Clone() => new()
    {
        MinDelaySeconds = MinDelaySeconds,
        MaxDelaySeconds = MaxDelaySeconds,
        RequestLimit = RequestLimit,
    };

    public void Normalize()
    {
        MinDelaySeconds = Math.Max(0.5, MinDelaySeconds);
        MaxDelaySeconds = Math.Max(MinDelaySeconds, MaxDelaySeconds);
        RequestLimit = Math.Max(0, RequestLimit);
    }
}

public sealed class AutomationErrorHandlingOptions
{
    public static IReadOnlyList<int> SupportedStatusCodes { get; } =
    [
        400,
        401,
        402,
        403,
        429,
        500,
        502,
        503,
    ];

    public Dictionary<int, int> MaxConsecutiveRetriesByStatusCode { get; set; } = DefaultRetryLimits();

    public AutomationErrorHandlingOptions Clone() => new()
    {
        MaxConsecutiveRetriesByStatusCode = new Dictionary<int, int>(
            MaxConsecutiveRetriesByStatusCode ?? DefaultRetryLimits()),
    };

    public void Normalize()
    {
        var normalized = new Dictionary<int, int>();
        var source = MaxConsecutiveRetriesByStatusCode ?? DefaultRetryLimits();
        foreach (int statusCode in SupportedStatusCodes)
        {
            int retryLimit = source.TryGetValue(statusCode, out int configured)
                ? configured
                : GetDefaultRetryLimit(statusCode);
            normalized[statusCode] = Math.Max(-1, retryLimit);
        }

        MaxConsecutiveRetriesByStatusCode = normalized;
    }

    public int GetRetryLimit(int statusCode)
    {
        Normalize();
        return MaxConsecutiveRetriesByStatusCode.TryGetValue(statusCode, out int retryLimit)
            ? retryLimit
            : 0;
    }

    public int GetRetryLimit(int? statusCode) =>
        statusCode.HasValue ? GetRetryLimit(statusCode.Value) : 0;

    public void SetRetryLimit(int statusCode, int retryLimit)
    {
        MaxConsecutiveRetriesByStatusCode ??= DefaultRetryLimits();
        MaxConsecutiveRetriesByStatusCode[statusCode] = Math.Max(-1, retryLimit);
    }

    public static Dictionary<int, int> DefaultRetryLimits() =>
        SupportedStatusCodes.ToDictionary(x => x, GetDefaultRetryLimit);

    private static int GetDefaultRetryLimit(int statusCode) => statusCode switch
    {
        400 => 1,
        401 => 1,
        402 => 0,
        403 => 1,
        429 => -1,
        500 => 3,
        502 => 3,
        503 => 3,
        _ => 0,
    };
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
