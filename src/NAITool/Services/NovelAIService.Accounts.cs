using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace NAITool.Services;

public partial class NovelAIService
{
    private readonly ApiAccountRouter _accountRouter = new();

    private static HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public async Task RefreshAccountsAsync(CancellationToken ct = default)
    {
        var accounts = ApiAccountRouter.UniqueAccounts(_settings.Accounts);
        await Parallel.ForEachAsync(accounts, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4, CancellationToken = ct,
        }, async (account, token) => await RefreshAccountAsync(account, token));
        _settings.Save();
    }

    public async Task<NovelAiAccountInfo?> RefreshAccountAsync(ApiAccount account, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        string baseUrl = _settings.Settings.ApiBaseUrl;
        await account.RefreshLock.WaitAsync(ct);
        try
        {
            // A refresh that completed while this caller waited already satisfies it.
            if (account.RefreshedAt >= started) return account.Info;
            var info = await GetAccountInfoAsync(account.Token, ct);
            ct.ThrowIfCancellationRequested();
            if (_settings.Accounts.Contains(account) && baseUrl == _settings.Settings.ApiBaseUrl)
            {
                if (info != null)
                {
                    account.Info = info;
                    account.IsTokenValid = info.IsAccountInfoAvailable ? true : null;
                }
                // Also throttle transient failures; a broken account must not delay every request.
                account.RefreshedAt = DateTimeOffset.UtcNow;
            }
            return info;
        }
        finally { account.RefreshLock.Release(); }
    }

    internal int EstimateAnlas(ApiRequestCost cost)
    {
        var selected = _accountRouter.Select(_settings.Accounts, _settings.Settings.ApiCallMode, cost,
            UsesCustomApiBaseUrl, advance: false);
        int estimate = cost.AnlasFor(selected?.Info);
        if (estimate > 0 && _settings.Settings.ApiCallMode == ApiCallMode.Random)
        {
            // Random selection has no fixed next account; only accounts that could be used affect the preview.
            return ApiAccountRouter.GetCandidates(_settings.Accounts, cost, UsesCustomApiBaseUrl)
                .Select(a => cost.AnlasFor(a.Info)).DefaultIfEmpty(cost.AnlasFor(null)).Max();
        }
        return estimate;
    }

    private async Task<ApiAccount> SelectAccountAsync(ApiRequestCost cost, CancellationToken ct)
    {
        string baseUrl = _settings.Settings.ApiBaseUrl;
        var configuredAccounts = _settings.Accounts;
        if (!UsesCustomApiBaseUrl)
        {
            var stale = ApiAccountRouter.UniqueAccounts(_settings.Accounts)
                .Where(a => DateTimeOffset.UtcNow - a.RefreshedAt > TimeSpan.FromSeconds(30)).ToArray();
            await Parallel.ForEachAsync(stale, new ParallelOptions
            {
                MaxDegreeOfParallelism = 4, CancellationToken = ct,
            }, async (account, token) => await RefreshAccountAsync(account, token));
        }
        ct.ThrowIfCancellationRequested();
        // Never combine a URL captured before an await with credentials edited during that await.
        if (baseUrl != _settings.Settings.ApiBaseUrl || !ReferenceEquals(configuredAccounts, _settings.Accounts))
            throw new InvalidOperationException(L("settings.api.configuration_changed"));
        return _accountRouter.Select(_settings.Accounts, _settings.Settings.ApiCallMode, cost, UsesCustomApiBaseUrl)
            ?? throw new InvalidOperationException(L("settings.api.no_eligible_account"));
    }

    private async Task<HttpResponseMessage> SendAccountRequestAsync(HttpRequestMessage request,
        ApiAccount account, HttpCompletionOption completion, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.Token);
        try
        {
            var response = await GetOrCreateClient().SendAsync(request, completion, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                account.IsTokenValid = false;
                account.Info = null;
            }
            return response;
        }
        finally
        {
            // Refresh before reusing this balance, including ambiguous timeout outcomes.
            account.RefreshedAt = DateTimeOffset.MinValue;
        }
    }

    private static int GenerationReferenceAnlas(string model,
        List<VibeTransferInfo>? vibes, List<PreciseReferenceInfo>? references)
    {
        // Match payload precedence: precise references replace vibes when both are supplied.
        if (SupportsPreciseReferenceModel(model) && references is { Count: > 0 })
            return NovelAiAnlasCalculator.ReferenceCost(0, 0, references.Count);
        return NovelAiAnlasCalculator.ReferenceCost(0,
            IsV4PlusModel(model) && SupportsVibeTransferModel(model) ? vibes?.Count ?? 0 : 0, 0);
    }
}
