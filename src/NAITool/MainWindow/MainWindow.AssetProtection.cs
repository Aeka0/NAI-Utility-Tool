namespace NAITool;

public sealed partial class MainWindow
{
    private const int AssetProtectionFreeVibeLimit = 4;

    private bool IsAssetProtectionModeEnabled() =>
        _settings.Settings.AccountAssetProtectionMode;

    private bool IsAssetProtectionSizeLimitEnabled() =>
        IsAssetProtectionModeEnabled() &&
        _settings.Settings.AccountAssetProtectionBlockOversizedDimensions;

    private bool IsAssetProtectionStepLimitEnabled() =>
        IsAssetProtectionModeEnabled() &&
        _settings.Settings.AccountAssetProtectionBlockOversizedSteps;

    private bool IsAssetProtectionPaidFeatureLimitEnabled() =>
        IsAssetProtectionModeEnabled() &&
        _settings.Settings.AccountAssetProtectionDisablePaidFeatures;

    private int GetMaxAllowedVibeTransfers() =>
        IsAssetProtectionPaidFeatureLimitEnabled()
            ? AssetProtectionFreeVibeLimit
            : MaxVibeTransfers;
}
