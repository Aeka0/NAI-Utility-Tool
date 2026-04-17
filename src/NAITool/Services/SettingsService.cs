using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAITool.Models;

namespace NAITool.Services;

/// <summary>
/// 应用设置持久化服务。
/// 通用设置与 API 凭证分别存储在 user/config/ 下的独立文件中。
/// </summary>
public class SettingsService
{
    private static string AppRootDir => AppPathResolver.AppRootDir;

    private static readonly string ConfigDir = Path.Combine(AppRootDir, "user", "config");
    private static readonly string SettingsFilePath = Path.Combine(ConfigDir, "settings.json");
    private static readonly string ApiConfigFilePath = Path.Combine(ConfigDir, "apiconfig.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AppSettings Settings { get; private set; } = new();
    public ApiConfig CachedApiConfig { get; private set; } = new();

    /// <summary>从磁盘加载设置（通用 + API 凭证 + 缓存账户信息）</summary>
    public void Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new();
                Settings.Normalize();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Load failed: {ex.Message}");
            Settings = new();
        }

        try
        {
            if (File.Exists(ApiConfigFilePath))
            {
                var json = File.ReadAllText(ApiConfigFilePath);
                var apiCfg = JsonSerializer.Deserialize<ApiConfig>(json, JsonOptions);
                if (apiCfg != null)
                {
                    Settings.ApiToken = apiCfg.ApiToken;
                    CachedApiConfig = apiCfg;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApiConfig] Load failed: {ex.Message}");
        }
    }

    /// <summary>保存设置到磁盘（通用设置与 API 凭证分别写入）</summary>
    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            Settings.Normalize();

            var token = Settings.ApiToken;
            Settings.ApiToken = null;
            var settingsJson = JsonSerializer.Serialize(Settings, JsonOptions);
            Settings.ApiToken = token;
            File.WriteAllText(SettingsFilePath, settingsJson);

            CachedApiConfig.ApiToken = token;
            var apiJson = JsonSerializer.Serialize(CachedApiConfig, JsonOptions);
            File.WriteAllText(ApiConfigFilePath, apiJson);

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Save failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>更新缓存的账户信息并写入 apiconfig.json</summary>
    public void UpdateCachedAccountInfo(int? anlas, string? tier, int? tierLevel, bool? active, string? expiresAt)
    {
        CachedApiConfig.CachedAnlas = anlas;
        CachedApiConfig.SubscriptionTier = tier;
        CachedApiConfig.SubscriptionTierLevel = tierLevel;
        CachedApiConfig.SubscriptionActive = active;
        CachedApiConfig.SubscriptionExpiresAt = expiresAt;
        Save();
    }
}

/// <summary>API 凭证与账户缓存信息</summary>
public class ApiConfig
{
    // Changing the numbers here won't do anything that affects the your account, nice try though.
    public string? ApiToken { get; set; }
    public int? CachedAnlas { get; set; }
    public string? SubscriptionTier { get; set; }
    public int? SubscriptionTierLevel { get; set; }
    public bool? SubscriptionActive { get; set; }
    public string? SubscriptionExpiresAt { get; set; }
}

public class AppSettings
{
    [JsonIgnore]
    public string? ApiToken { get; set; }
    public bool WeightHighlight { get; set; } = true;
    public bool AutoComplete { get; set; } = true;
    public bool RememberPromptAndParameters { get; set; } = true;
    public bool SuperDropEnabled { get; set; } = true;
    public bool ShowGenerationResultBar { get; set; } = true;
    public bool WildcardsEnabled { get; set; } = true;
    public bool WildcardsRequireExplicitSyntax { get; set; } = true;
    public int RandomStyleTagCount { get; set; } = 3;
    public int RandomStyleMinCount { get; set; } = 80;
    public bool RandomStyleUseWeight { get; set; }
    public bool AutoGenRandomStylePrefix { get; set; }
    public bool AccountAssetProtectionMode { get; set; } = true;
    public bool AccountAssetProtectionBlockOversizedDimensions { get; set; } = true;
    public bool AccountAssetProtectionBlockOversizedSteps { get; set; } = true;
    public bool AccountAssetProtectionDisablePaidFeatures { get; set; } = true;
    public bool UseProxy { get; set; }
    public string ProxyPort { get; set; } = "10808";
    public bool UseWebp { get; set; }
    public string ThemeMode { get; set; } = "System";
    public string AppearanceTransparency { get; set; } = "Standard";
    public string LanguageCode { get; set; } = "";
    public bool DevLogEnabled { get; set; }
    public bool StreamGeneration { get; set; }
    public ReverseTaggerSettings ReverseTagger { get; set; } = new();
    public NAIParameters GenParameters { get; set; } = new() { Model = "nai-diffusion-4-5-full" };
    public NAIParameters InpaintParameters { get; set; } = new() { Model = "nai-diffusion-4-5-full-inpainting" };
    public NAIParameters I2IDenoiseParameters { get; set; } = new() { Model = "nai-diffusion-4-5-full", DenoiseStrength = 0.7, DenoiseNoise = 0 };
    public RememberedPromptState RememberedPrompts { get; set; } = new();
    public int RememberedCustomWidth { get; set; } = 832;
    public int RememberedCustomHeight { get; set; } = 1216;
    public AutomationSettings Automation { get; set; } = new();

    public void Normalize()
    {
        if (!string.IsNullOrWhiteSpace(LanguageCode))
            LanguageCode = LocalizationService.NormalizeLanguageCode(LanguageCode);
        AppearanceTransparency = AppearanceTransparency switch
        {
            "Standard" or "Lesser" or "Opaque" => AppearanceTransparency,
            _ => "Standard",
        };
        ReverseTagger ??= new();
        GenParameters ??= new() { Model = "nai-diffusion-4-5-full" };
        InpaintParameters ??= new() { Model = "nai-diffusion-4-5-full-inpainting" };
        I2IDenoiseParameters ??= new() { Model = "nai-diffusion-4-5-full", DenoiseStrength = 0.7, DenoiseNoise = 0 };
        RememberedPrompts ??= new();
        Automation ??= new();
        Automation.Normalize();
        AutoGenRandomStylePrefix = Automation.Randomization.RandomizeStyleTags;
    }
}

public class RememberedPromptState
{
    public string GenPositivePrompt { get; set; } = "";
    public string GenNegativePrompt { get; set; } = "";
    public string GenStylePrompt { get; set; } = "";
    public string I2IPositivePrompt { get; set; } = "";
    public string I2INegativePrompt { get; set; } = "";
    public string I2IStylePrompt { get; set; } = "";
    public bool IsSplitPrompt { get; set; }
    public List<RememberedCharacterState> GenCharacters { get; set; } = new();
}

public class RememberedCharacterState
{
    public string PositivePrompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
    public double CenterX { get; set; } = 0.5;
    public double CenterY { get; set; } = 0.5;
    public bool IsPositiveTab { get; set; } = true;
    public bool IsCollapsed { get; set; }
    public bool IsDisabled { get; set; }
    public bool UseCustomPosition { get; set; }
}

public class ReverseTaggerSettings
{
    public string ModelPath { get; set; } = "";
    public bool AddCharacterTags { get; set; } = true;
    public bool AddCopyrightTags { get; set; }
    public bool ReplaceUnderscoresWithSpaces { get; set; } = true;
    public double GeneralThreshold { get; set; } = 0.7;
    public double CharacterThreshold { get; set; } = 0.9;
    public bool UnloadModelAfterInference { get; set; } = true;
}

public class NAIParameters
{
    public string Model { get; set; } = "nai-diffusion-4-5-curated";
    public string Sampler { get; set; } = "k_euler_ancestral";
    public string Schedule { get; set; } = "karras";
    public double Scale { get; set; } = 3.0;
    public double CfgRescale { get; set; } = 0;
    public bool Sm { get; set; }
    public bool Variety { get; set; } = false;
    public bool QualityToggle { get; set; } = true;
    public int Steps { get; set; } = 28;
    public int Seed { get; set; } = 0;
    public int UcPreset { get; set; } = 0;
    public double DenoiseStrength { get; set; } = 0.7;
    public double DenoiseNoise { get; set; } = 0;
}
