using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using NAITool.Services;

internal static class ApiTransportChecks
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        Interlocked.Increment(ref _checks);
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync()
    {
        // This executable's own output directory is the isolated config root; never load app user data.
        Check(AppPathResolver.AppRootDir.StartsWith(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase), "Configuration must stay in the test output directory.");
        var settings = new SettingsService();
        settings.SetApiTokens([" test-a ", "test-b", "test-a", "", "test-zero"]);
        Check(settings.Accounts[0].Token == "test-a" && ReferenceEquals(settings.Accounts[0], settings.Accounts[2]),
            "Trimmed duplicates share one account state.");
        using var handler = new AccountHandler();
        using var client = new HttpClient(handler);
        using var service = new NovelAIService(settings);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(NovelAIService).GetField("_httpClient", flags)!.SetValue(service, client);
        typeof(NovelAIService).GetField("_httpClientProxyKey", flags)!.SetValue(service, "");

        await service.RefreshAccountsAsync();
        Check(handler.GetCount == 3, "All-account refresh issues only one GET per unique nonblank token.");
        Check(settings.Accounts[0].Info?.AnlasBalance == 1000 && settings.Accounts[1].Info?.AnlasBalance == 100,
            "Concurrent refresh associates balances with the right credential.");
        Check(client.DefaultRequestHeaders.Authorization == null && !client.DefaultRequestHeaders.Accept.Any(),
            "Shared HTTP client has no mutable authorization or negotiation headers.");
        var allTests = await Task.WhenAll(Enumerable.Range(0, 24).Select(i =>
            service.TestConnectionAsync(i % 2 == 0 ? "test-a" : "test-b")));
        Check(allTests.All(t => t.Success), "Concurrent API tests preserve every request token.");

        settings.Settings.GenParameters.Model = "nai-diffusion-5-full";
        settings.Settings.GenParameters.Steps = 28;
        settings.Settings.GenParameters.QualityToggle = false;
        settings.Settings.ApiCallMode = ApiCallMode.MostQuota;
        await service.GenerateAsync(1024, 1024, "test", "");
        Check(handler.LastPostToken == "test-b", "V5 generation chooses maximum V5 balance.");
        await service.GenerateAsync(1024, 1536, "test", "");
        Check(handler.LastPostToken == "test-a", "Paid V5 generation prioritizes Anlas and skips zero V5.");
        settings.Settings.ApiCallMode = ApiCallMode.LeastQuota;
        await service.GenerateAsync(1024, 1536, "test", "");
        Check(handler.LastPostToken == "test-b", "Paid V5 generation chooses minimum sufficient Anlas.");
        await service.EncodeVibeAsync("AA==", "nai-diffusion-4-5-full", 1);
        Check(handler.LastPostToken == "test-b", "Vibe encoding uses Anlas routing.");
        settings.Settings.ApiCallMode = ApiCallMode.MostQuota;
        await service.EncodeVibeAsync("AA==", "nai-diffusion-4-5-full", 1);
        Check(handler.LastPostToken == "test-zero", "Standalone Anlas operation can use account with zero V5.");

        settings.SetApiTokens(["test-a", "test-b", "test-a"]);
        settings.Settings.GenParameters.Model = "nai-diffusion-4-5-full";
        settings.Settings.ApiCallMode = ApiCallMode.Random;
        var sequence = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            await service.GenerateAsync(1024, 1024, "test", "");
            sequence.Add(handler.LastPostToken!);
        }
        Check(sequence.SequenceEqual(new[] { "test-a", "test-b", "test-a", "test-b" }),
            "Free generation rotates even in random mode, without duplicate weighting.");
        Check(handler.PostCount == 9, "Each operation has exactly one POST, no automatic retries.");

        foreach (var account in settings.Accounts) account.RefreshedAt = DateTimeOffset.MinValue;
        int postsBeforeEdit = handler.PostCount;
        var pendingGeneration = service.GenerateAsync(1024, 1024, "test", "");
        settings.Settings.ApiBaseUrl = "https://changed.invalid";
        var changedResult = await pendingGeneration;
        Check(changedResult.Error != null && handler.PostCount == postsBeforeEdit,
            "Editing the endpoint during account refresh cancels dispatch instead of mixing URL and credentials.");
        settings.Settings.ApiBaseUrl = "";
        await service.RefreshAccountsAsync();

        var retained = settings.Accounts[1];
        var refresh = service.RefreshAccountAsync(settings.Accounts[0]);
        settings.SetApiTokens(["test-b"]);
        await refresh;
        Check(settings.Accounts.Count == 1 && ReferenceEquals(settings.Accounts[0], retained) &&
            settings.Accounts[0].Info?.AnlasBalance == 100, "Removing an account during refresh cannot replace another account's state.");
        settings.SetApiTokens(["test-b", "test-invalid"]);
        await service.RefreshAccountsAsync();
        Check(settings.Accounts[1].IsTokenValid == false && settings.Accounts[1].Info == null,
            "Unauthorized account is retained for editing but excluded from routing.");
        await service.GenerateAsync(1024, 1024, "test", "");
        Check(handler.LastPostToken == "test-b", "Invalid API does not block valid API.");

        settings.SetApiTokens(["test-b", "test-transient"]);
        var transientTest = await service.TestConnectionAsync("test-transient");
        Check(!transientTest.Success && settings.Accounts[1].Token == "test-transient" &&
            settings.Accounts[1].IsTokenValid != false, "Transient failure does not delete credentials or mark them unauthorized.");
        settings.SetApiTokens(["test-a", "test-b"]);
        await service.RefreshAccountsAsync();
        Check(settings.Save(), "Encrypted account configuration saves.");
        string config = File.ReadAllText(Path.Combine(AppPathResolver.AppRootDir, "user", "config", "apiconfig.json"));
        Check(!config.Contains("test-a") && !config.Contains("test-b"), "Saved config contains no plaintext tokens.");
        var loaded = new SettingsService();
        loaded.Load();
        Check(loaded.Accounts.Select(a => a.Token).SequenceEqual(new[] { "test-a", "test-b" }) &&
            loaded.Accounts[1].Info?.AnlasBalance == 100, "DPAPI token and per-account quota survive restart.");
        Check(loaded.Settings.ApiCallMode == ApiCallMode.Random, "Call mode survives restart.");

        string configPath = Path.Combine(AppPathResolver.AppRootDir, "user", "config", "apiconfig.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(new
        {
            EncryptedApiToken = loaded.Accounts[0].EncryptedApiToken,
            CachedAnlas = 1234, CachedV5UsagePercent = 120, SubscriptionTierLevel = 3, SubscriptionActive = true,
        }));
        var migrated = new SettingsService();
        migrated.Load();
        Check(migrated.Accounts.Count == 1 && migrated.Accounts[0].Token == "test-a" &&
            migrated.Accounts[0].Info?.V5UsagePercent == 120, "Existing single-account encrypted configuration imports without losing overage.");
        File.WriteAllText(configPath, JsonSerializer.Serialize(new ApiConfig
        {
            Accounts = [loaded.Accounts[0], loaded.Accounts[0], new ApiAccount { EncryptedApiToken = "invalid-ciphertext" }],
        }));
        var partiallyDecrypted = new SettingsService();
        partiallyDecrypted.Load();
        Check(partiallyDecrypted.ApiTokenDecryptFailed && partiallyDecrypted.HasApiTokens &&
            partiallyDecrypted.Accounts[0].Token == "test-a", "One undecryptable token does not erase other accounts.");
        Check(ReferenceEquals(partiallyDecrypted.Accounts[0], partiallyDecrypted.Accounts[1]),
            "Duplicate credentials loaded from disk share quota and validation state.");
        await CheckQuotaRoutingAsync(settings, service, handler);
        Console.WriteLine($"Passed {_checks} API transport and persistence checks (in-memory HTTP; synthetic tokens only).");
    }

    private static async Task CheckQuotaRoutingAsync(SettingsService settings, NovelAIService service, AccountHandler handler)
    {
        settings.SetApiTokens(["test-too-small", "test-exact", "test-zero", "test-exact"]);
        settings.Settings.GenParameters.Model = "nai-diffusion-5-full";
        handler.Balances["test-too-small"] = (44, 90);
        foreach (var mode in Enum.GetValues<ApiCallMode>())
        {
            settings.Settings.ApiCallMode = mode;
            handler.Balances["test-exact"] = (45, 90);
            await service.RefreshAccountsAsync();
            for (int i = 0; i < 3; i++)
            {
                int before = handler.PostCount;
                await service.GenerateAsync(1024, 1536, "test", "");
                Check(handler.PostCount == before + 1 && handler.LastPostToken == "test-exact",
                    $"Only the account with sufficient Anlas and available V5 receives the generation POST: {mode}.");
            }

            // The last POST invalidates its balance; the next selection must refresh and exclude it.
            handler.Balances["test-exact"] = (44, 90);
            int postsBeforeBlockedRequest = handler.PostCount;
            var blocked = await service.GenerateAsync(1024, 1536, "test", "");
            Check(blocked.Error != null && handler.PostCount == postsBeforeBlockedRequest,
                $"No generation POST is sent when every account is insufficient or has zero V5: {mode}.");
        }

        settings.SetApiTokens(["test-preview-opus", "test-preview-tablet"]);
        settings.Accounts[0].Info = new NovelAiAccountInfo
        {
            AnlasBalance = 100, V5UsagePercent = 90, IsOpus = true, HasActiveSubscription = true,
        };
        settings.Accounts[1].Info = new NovelAiAccountInfo
        {
            AnlasBalance = 0, V5UsagePercent = 90, HasActiveSubscription = true,
        };
        settings.Settings.ApiCallMode = ApiCallMode.Random;
        var costType = typeof(NovelAIService).Assembly.GetType("NAITool.Services.ApiRequestCost")!;
        var cost = costType.GetMethod("Image", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, ["nai-diffusion-5-full", 1024, 1024, 28, false, 1d, 1]);
        var estimate = typeof(NovelAIService).GetMethod("EstimateAnlas", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [cost]);
        Check(Equals(estimate, 1), "Random cost preview excludes the higher cost of an account that cannot afford the request.");
    }

    private sealed class AccountHandler : HttpMessageHandler
    {
        private int _getCount, _postCount;
        public int GetCount => _getCount;
        public int PostCount => _postCount;
        public string? LastPostToken { get; private set; }
        public ConcurrentDictionary<string, (int Anlas, int V5)> Balances { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string? token = request.Headers.Authorization?.Parameter;
            await Task.Delay(15, ct);
            Check(token != null && request.Headers.Authorization?.Parameter == token, "Authorization is request-local under concurrency.");
            if (token == "test-invalid") return new(HttpStatusCode.Unauthorized) { Content = new StringContent("unauthorized") };
            if (token == "test-transient") return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("unavailable") };
            if (request.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref _getCount);
                int balance = token == "test-a" ? 1000 : token == "test-b" ? 100 : 5000;
                int v5 = token == "test-a" ? 10 : token == "test-b" ? 90 : 0;
                if (Balances.TryGetValue(token!, out var quota)) (balance, v5) = quota;
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        subscription = new
                        {
                            tier = 3, active = true, trainingStepsLeft = balance,
                            usage = new { percent = v5, isNegative = false, timeUntilNextPercent = 60 },
                        },
                    })),
                };
            }
            Interlocked.Increment(ref _postCount);
            LastPostToken = token;
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("synthetic response; no generation") };
        }
    }
}
