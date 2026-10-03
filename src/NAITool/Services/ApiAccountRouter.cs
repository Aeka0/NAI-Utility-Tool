namespace NAITool.Services;

/// <summary>The cost of one HTTP operation; separately encoded vibes have their own operation.</summary>
internal readonly record struct ApiRequestCost(
    bool UsesV5, int PaidBaseAnlas, int ReferenceAnlas = 0, bool OpusFreeEligible = false,
    bool RequiresSubscription = false, bool RequiresOpus = false)
{
    public int AnlasFor(NovelAiAccountInfo? info)
    {
        bool free = OpusFreeEligible && info is { IsOpus: true, HasActiveSubscription: true } &&
                    !(UsesV5 && info.V5UsageIsNegative == true);
        return (int)Math.Min(int.MaxValue, (long)(free ? 0 : PaidBaseAnlas) + ReferenceAnlas);
    }

    public static ApiRequestCost Image(string model, int width, int height, int steps,
        bool sm = false, double strength = 1, int referenceAnlas = 0) => new(
        NovelAiAnlasCalculator.IsV5Model(model),
        NovelAiAnlasCalculator.PaidBaseCost(model, width, height, steps, sm, strength),
        referenceAnlas,
        width > 0 && height > 0 && steps > 0 && steps <= 28 && (long)width * height <= 1048576);
}

/// <summary>One cursor shared by every operation. Selection never changes global credentials.</summary>
internal sealed class ApiAccountRouter
{
    private readonly object _gate = new();
    private string? _lastToken;

    internal static ApiAccount[] UniqueAccounts(IEnumerable<ApiAccount> accounts) => accounts
        .Where(a => !string.IsNullOrWhiteSpace(a.Token))
        .DistinctBy(a => a.Token, StringComparer.Ordinal).ToArray();

    /// <summary>Shared request rules for every selection mode and the cost preview, in input order.</summary>
    internal static ApiAccount[] GetCandidates(IEnumerable<ApiAccount> accounts, ApiRequestCost cost,
        bool customEndpoint = false)
    {
        var candidates = UniqueAccounts(accounts)
            .Where(a => customEndpoint || (a.IsTokenValid != false && Eligible(a.Info, cost))).ToArray();

        // A request that can be free should not accidentally spend another account's Anlas.
        if (!customEndpoint)
        {
            var free = candidates.Where(a => cost.AnlasFor(a.Info) == 0).ToArray();
            if (free.Length > 0) return free;
        }
        return candidates;
    }

    public ApiAccount? Select(IEnumerable<ApiAccount> accounts, ApiCallMode mode, ApiRequestCost cost,
        bool customEndpoint = false, bool advance = true)
    {
        lock (_gate)
        {
            var all = UniqueAccounts(accounts);
            int start = Array.FindIndex(all, a => a.Token == _lastToken) + 1;
            // Modes only choose among eligible accounts; none may bypass the shared rules.
            var candidates = GetCandidates(all.Skip(start).Concat(all.Take(start)), cost, customEndpoint);
            if (candidates.Length == 0) return null;

            bool consumesNothing = !cost.UsesV5 && candidates.All(a => cost.AnlasFor(a.Info) == 0);
            ApiAccount selected = candidates[0];
            if (!consumesNothing && mode == ApiCallMode.Random)
                selected = advance ? candidates[Random.Shared.Next(candidates.Length)] : candidates[0];
            else if (!consumesNothing && !customEndpoint && mode is ApiCallMode.MostQuota or ApiCallMode.LeastQuota)
            {
                // Null means unknown, not zero: prefer known balances in either ordering.
                int? Quota(ApiAccount a) => cost.AnlasFor(a.Info) > 0
                    ? a.Info?.AnlasBalance : a.Info?.V5UsagePercent;
                var known = candidates.Where(a => Quota(a).HasValue);
                selected = (mode == ApiCallMode.MostQuota
                    ? known.OrderByDescending(Quota) : known.OrderBy(Quota)).FirstOrDefault() ?? selected;
            }
            if (advance) _lastToken = selected.Token;
            return selected;
        }
    }

    private static bool Eligible(NovelAiAccountInfo? info, ApiRequestCost cost)
    {
        if (info == null || !info.IsAccountInfoAvailable) return true;
        if (cost.RequiresSubscription && !info.HasActiveSubscription) return false;
        if (cost.RequiresOpus && !info.IsOpus) return false;
        if (cost.UsesV5 && (info.V5UsagePercent <= 0 || info.V5UsageIsNegative == true)) return false;
        int anlas = cost.AnlasFor(info);
        return anlas == 0 || !info.AnlasBalance.HasValue || info.AnlasBalance >= anlas;
    }
}

internal readonly record struct ApiQuotaTotals(long? Anlas, long? V5Percent, int AccountCount, int KnownCount)
{
    public static ApiQuotaTotals From(IEnumerable<ApiAccount> accounts)
    {
        var all = ApiAccountRouter.UniqueAccounts(accounts);
        var infos = all.Select(a => a.Info).Where(i => i?.IsAccountInfoAvailable == true).ToArray();
        long? Sum(Func<NovelAiAccountInfo, int?> value) => infos.Any(i => value(i!).HasValue)
            ? infos.Sum(i => (long)Math.Max(0, value(i!) ?? 0)) : null;
        return new(Sum(i => i.AnlasBalance), Sum(i => i.V5UsageIsNegative == true ? 0 : i.V5UsagePercent),
            all.Length, infos.Count(i => i!.AnlasBalance.HasValue && i.V5UsagePercent.HasValue));
    }
}
