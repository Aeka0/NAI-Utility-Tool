using System.Collections.Concurrent;
using System.Text.Json;
using NAITool.Services;

int checks = 0;
void Check(bool condition, string scenario)
{
    checks++;
    if (!condition) throw new Exception(scenario);
}
ApiAccount Account(string token, int? anlas, int? v5, bool opus = true, bool active = true, bool negative = false) => new()
{
    Token = token,
    Info = new NovelAiAccountInfo
    {
        AnlasBalance = anlas, V5UsagePercent = v5, V5UsageIsNegative = negative,
        IsOpus = opus, HasActiveSubscription = active,
    },
};
var rich = Account("synthetic-rich", 1000, 10);
var full = Account("synthetic-full", 100, 90);
var emptyV5 = Account("synthetic-empty", 10000, 0);
var v5 = ApiRequestCost.Image("nai-diffusion-5-full", 1024, 1024, 28);
var paidV5 = ApiRequestCost.Image("nai-diffusion-5-full", 1024, 1536, 28);
var free = ApiRequestCost.Image("nai-diffusion-4-5-full", 1024, 1024, 28);
var encode = new ApiRequestCost(false, 2);
ApiAccount[] accounts = [rich, full, emptyV5];
foreach (var mode in Enum.GetValues<ApiCallMode>())
{
    var router = new ApiAccountRouter();
    for (int i = 0; i < 9; i++)
        Check(router.Select(accounts, mode, free) == accounts[i % 3], $"free requests always rotate: {mode}");
}
Check(new ApiAccountRouter().Select(accounts, ApiCallMode.MostQuota, v5) == full, "V5 compares V5 allowance");
Check(new ApiAccountRouter().Select(accounts, ApiCallMode.LeastQuota, v5) == rich, "least V5 skips exhausted accounts");
Check(new ApiAccountRouter().Select(accounts, ApiCallMode.MostQuota, paidV5) == rich, "paid V5 compares Anlas but excludes zero V5");
Check(new ApiAccountRouter().Select(accounts, ApiCallMode.LeastQuota, paidV5) == full, "least Anlas on paid V5");
Check(new ApiAccountRouter().Select(accounts, ApiCallMode.MostQuota, encode) == emptyV5, "Anlas-only operation can use zero V5");
Check(new ApiAccountRouter().Select([emptyV5], ApiCallMode.RoundRobin, v5) == null, "no eligible V5 account does not fall back to exhausted account");
Check(new ApiAccountRouter().Select([], ApiCallMode.RoundRobin, encode) == null, "empty list");
var insufficient = Account("synthetic-poor", 1, 90);
Check(new ApiAccountRouter().Select([insufficient, rich], ApiCallMode.LeastQuota, encode) == rich, "least quota skips insufficient Anlas");
var belowCost = Account("synthetic-below-cost", 44, 90);
var exactCost = Account("synthetic-exact-cost", 45, 90);
var aboveCost = Account("synthetic-above-cost", 60, 90);
ApiAccount[] paidCandidates = [insufficient, aboveCost, belowCost, rich, exactCost, emptyV5];
var leastRouter = new ApiAccountRouter();
Check(leastRouter.Select(paidCandidates, ApiCallMode.LeastQuota, paidV5) == exactCost,
    "least quota skips multiple insufficient accounts and selects the smallest sufficient balance, including exact cost");
Check(leastRouter.Select(paidCandidates, ApiCallMode.LeastQuota, paidV5 with { ReferenceAnlas = 1 }) == aboveCost,
    "least quota includes reference charges when checking affordability");
exactCost.Info = Account("unused", 40, 90).Info;
Check(leastRouter.Select(paidCandidates, ApiCallMode.LeastQuota, paidV5) == aboveCost,
    "least quota reevaluates changed Anlas balances before each selection");
Check(leastRouter.Select([insufficient, belowCost, exactCost, emptyV5], ApiCallMode.LeastQuota, paidV5) == null,
    "no account is selected when all Anlas balances are insufficient or V5 allowance is exhausted");
var negativeV5 = Account("synthetic-negative-v5", 1000, 90, negative: true);
var unauthorized = Account("synthetic-unauthorized", 1000, 90);
unauthorized.IsTokenValid = false;
foreach (var mode in Enum.GetValues<ApiCallMode>())
{
    var router = new ApiAccountRouter();
    var affordable = Account("synthetic-affordable", 45, 90);
    ApiAccount[] candidates = [belowCost, emptyV5, negativeV5, unauthorized, affordable, affordable];
    Check(router.Select(candidates, mode, paidV5, advance: false) == affordable,
        $"preview applies the same quota and validity rules: {mode}");
    for (int i = 0; i < 4; i++)
        Check(router.Select(candidates, mode, paidV5) == affordable,
            $"every mode accepts exact Anlas cost and skips insufficient, exhausted, negative and invalid accounts: {mode}");
    Check(router.Select(candidates, mode, paidV5 with { ReferenceAnlas = 1 }) == null,
        $"reference charges can make every account ineligible: {mode}");
    Check(router.Select([.. candidates, aboveCost], mode, paidV5 with { ReferenceAnlas = 1 }) == aboveCost,
        $"reference charges select an account that can pay the entire request: {mode}");
    affordable.Info = Account("unused", 44, 90).Info;
    Check(router.Select(candidates, mode, paidV5) == null,
        $"updated insufficient balances never fall back to an exhausted account: {mode}");
    affordable.Info = Account("unused", 0, 90).Info;
    Check(router.Select(candidates, mode, v5) != null,
        $"zero Anlas does not block an Opus request that only consumes V5: {mode}");
    Check(router.Select([insufficient, emptyV5], mode, encode) == emptyV5,
        $"Anlas-only requests require sufficient Anlas but do not require V5: {mode}");
}
var rotating = new ApiAccountRouter();
var rotatingMinimum = Account("synthetic-rotating-minimum", 45, 90);
for (int i = 0; i < 6; i++)
    Check(rotating.Select([belowCost, aboveCost, emptyV5, rotatingMinimum], ApiCallMode.RoundRobin, paidV5) ==
          (i % 2 == 0 ? aboveCost : rotatingMinimum), "round robin keeps order while skipping ineligible accounts");
rotatingMinimum.Info = Account("unused", 44, 90).Info;
Check(rotating.Select([belowCost, aboveCost, emptyV5, rotatingMinimum], ApiCallMode.RoundRobin, paidV5) == aboveCost,
    "round robin safely advances when its previous account becomes ineligible");
var inactive = Account("synthetic-inactive", 100, 70, active: false);
Check(new ApiAccountRouter().Select([inactive, rich], ApiCallMode.RoundRobin, free) == rich, "free eligible account avoids unnecessary Anlas");
Check(paidV5.AnlasFor(rich.Info) == 45 && v5.AnlasFor(rich.Info) == 0, "per-account request cost");
Check(new ApiAccountRouter().Select([inactive], ApiCallMode.Random, new(false, 0, RequiresSubscription: true)) == null, "text subscription requirement");
var tablet = Account("synthetic-tablet", 100, 30, opus: false);
Check(new ApiAccountRouter().Select([tablet, rich], ApiCallMode.RoundRobin, new(false, 0, RequiresSubscription: true, RequiresOpus: true)) == rich, "Opus-only text model");

var duplicate = Account(rich.Token, int.MaxValue, int.MaxValue);
var duplicateRouter = new ApiAccountRouter();
for (int i = 0; i < 6; i++)
    Check(duplicateRouter.Select([rich, duplicate, full, new()], ApiCallMode.RoundRobin, free) == (i % 2 == 0 ? rich : full), "duplicates and blanks cannot gain routing weight");
var totals = ApiQuotaTotals.From([rich, duplicate, full, new()]);
Check(totals == new ApiQuotaTotals(1100, 100, 2, 2), "duplicates cannot inflate totals");
var large1 = Account("large1", int.MaxValue, int.MaxValue);
var large2 = Account("large2", int.MaxValue, int.MaxValue);
Check(ApiQuotaTotals.From([large1, large2]).Anlas == 2L * int.MaxValue, "Anlas sum cannot overflow Int32");
Check(ApiQuotaTotals.From([large1, large2]).V5Percent == 2L * int.MaxValue, "V5 sum keeps overage and cannot overflow Int32");
Check(ApiQuotaTotals.From([Account("negative", 2, 50, negative: true)]).V5Percent == 0, "negative V5 is zero");
var unknown = new ApiAccount { Token = "synthetic-unknown" };
Check(ApiQuotaTotals.From([unknown]).Anlas == null, "unknown is not zero");
Check(ApiQuotaTotals.From([unknown, rich]).KnownCount == 1, "partial totals report coverage");
Check(new ApiAccountRouter().Select([unknown, rich], ApiCallMode.LeastQuota, paidV5) == rich, "unknown quota follows known eligible quota");
Check(new ApiAccountRouter().Select([unknown], ApiCallMode.MostQuota, v5) == unknown, "missing quota can still be routed");
unknown.IsTokenValid = false;
Check(new ApiAccountRouter().Select([unknown], ApiCallMode.Random, encode) == null, "unauthorized token skipped");
Check(new ApiAccountRouter().Select([rich, full], ApiCallMode.MostQuota, paidV5, customEndpoint: true) == rich, "custom endpoint with unavailable quota rotates");

var preview = new ApiAccountRouter();
Check(preview.Select([rich, full], ApiCallMode.RoundRobin, v5, advance: false) == rich, "preview returns next");
Check(preview.Select([rich, full], ApiCallMode.RoundRobin, v5) == rich, "preview does not consume turn");
Check(preview.Select([full], ApiCallMode.RoundRobin, v5) == full, "removing last account is safe");
var random = new ApiAccountRouter();
for (int i = 0; i < 100; i++)
    Check(random.Select([belowCost, rich, emptyV5, negativeV5, unauthorized, full], ApiCallMode.Random, paidV5) is { } selected &&
          (selected == rich || selected == full), "random uses only accounts satisfying both Anlas and V5 requirements");
var concurrentRouter = new ApiAccountRouter();
var selections = new ConcurrentBag<string>();
Parallel.For(0, 600, _ => selections.Add(concurrentRouter.Select([rich, full], ApiCallMode.RoundRobin, v5)!.Token));
Check(selections.Count(t => t == rich.Token) == 300 && selections.Count(t => t == full.Token) == 300, "parallel round robin is balanced");

rich.EncryptedApiToken = "synthetic-ciphertext";
string json = JsonSerializer.Serialize(new ApiConfig { Accounts = [rich, full] });
Check(!json.Contains(rich.Token) && !json.Contains(full.Token), "plaintext tokens cannot be serialized");
Check(!json.Contains("RefreshLock") && !json.Contains("RefreshedAt") && !json.Contains("IsTokenValid"), "runtime state stays out of config");
var roundTrip = JsonSerializer.Deserialize<ApiConfig>(json)!;
Check(roundTrip.Accounts[0].EncryptedApiToken == rich.EncryptedApiToken && roundTrip.Accounts[0].Info!.V5UsagePercent == 10, "encrypted credential and per-account quota round trip");
Console.WriteLine($"Passed {checks} multi-API checks.");
