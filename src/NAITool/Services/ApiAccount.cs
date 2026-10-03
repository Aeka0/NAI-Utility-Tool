using System.Text.Json.Serialization;

namespace NAITool.Services;

public enum ApiCallMode { RoundRobin, MostQuota, LeastQuota, Random }

public sealed class ApiAccount
{
    [JsonIgnore] public string Token { get; set; } = "";
    public string? EncryptedApiToken { get; set; }
    public NovelAiAccountInfo? Info { get; set; }
    [JsonIgnore] public bool? IsTokenValid { get; set; }
    [JsonIgnore] public DateTimeOffset RefreshedAt { get; set; }
    [JsonIgnore] public SemaphoreSlim RefreshLock { get; } = new(1, 1);
}

public sealed class ApiConfig
{
    public List<ApiAccount> Accounts { get; set; } = [];
}

public class NovelAiAccountInfo
{
    public int? AnlasBalance { get; init; }
    public int? V5UsagePercent { get; init; }
    public bool? V5UsageIsNegative { get; init; }
    public int? V5UsageTimeUntilNextPercentSeconds { get; init; }
    public string TierName { get; init; } = "";
    public bool IsOpus { get; init; }
    public bool HasActiveSubscription { get; init; }
    public int? TierLevel { get; init; }
    public string? ExpiresAt { get; init; }
    public bool IsAccountInfoAvailable { get; init; } = true;
}

