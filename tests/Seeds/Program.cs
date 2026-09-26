using System.Globalization;
using System.Text.Json;
using NAITool.Services;

int checks = 0;
void Check(bool condition, string scenario)
{
    checks++;
    if (!condition) throw new Exception(scenario);
}

foreach (var culture in new[] { "en-US", "zh-CN", "de-DE" })
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
    foreach (string seed in new[] { "2147483648", "4294967295", "9007199254740993", "18446744073709551616" })
    {
        string json = $$"""{"prompt":"test","seed":{{seed}},"sampler":"k_euler","steps":28}""";
        var metadata = ImageMetadataService.TryParseJson(json);
        Check(metadata?.Seed == seed && metadata.Sampler == "k_euler", "Numeric metadata must stay exact and parse fields after seed");
        Check(ImageMetadataService.TryParseSdFormat($"test\nSteps: 28, Seed: {seed}, Size: 832x1216")?.Seed == seed,
            "SD metadata must not truncate a large integer");
        var settings = JsonSerializer.Deserialize<NAIParameters>($$"""{"Seed":{{seed}},"Steps":31}""")!;
        Check(settings.Seed == seed && settings.Steps == 31, "Old numeric settings must load without resetting parameters");
        Check(JsonSerializer.Deserialize<NAIParameters>(JsonSerializer.Serialize(settings))?.Seed == seed, "Settings must round trip");
        Check(SeedValue.Resolve(seed) == seed, "Fixed numeric seeds must not randomize");
        Check(JsonSerializer.Serialize(new Dictionary<string, object> { ["seed"] = SeedValue.ToRequestValue(seed) }) == $$"""{"seed":{{seed}}}""",
            "API payload must contain an exact JSON number");
        Check(SeedValue.Adjust(SeedValue.Adjust(seed, 1), -1) == seed, "Increment/decrement must stay exact beyond double and int64");
    }
}

foreach (string seed in new[] { "hello", "中文种子🌱", "a, b", "\"seed\"\\value", "  text  ", "1.5", "1e6" })
{
    string json = JsonSerializer.Serialize(new { prompt = "test", seed, sampler = "k_euler" });
    var metadata = ImageMetadataService.TryParseJson(json);
    Check(metadata?.Seed == seed && metadata.Sampler == "k_euler", "Text metadata must parse without losing subsequent fields");
    Check(SeedValue.Resolve(seed) == seed, "Text seed must not be silently randomized");
    Check(SeedValue.Adjust(seed, 1) == "0" && SeedValue.Adjust(seed, -1) == "0", "First adjustment of text must become zero in either direction");
    var parameters = new NAIParameters { Seed = seed };
    Check(JsonSerializer.Deserialize<NAIParameters>(JsonSerializer.Serialize(parameters))?.Seed == seed, "Text settings must round trip exactly");
    string payload = JsonSerializer.Serialize(new { seed = SeedValue.ToRequestValue(seed) });
    using var document = JsonDocument.Parse(payload);
    Check(document.RootElement.GetProperty("seed").GetString() == seed, "API text seed must be sent verbatim");
}

Check(SeedValue.Adjust("2147483647", 1) == "2147483648", "Cross int32 boundary");
Check(SeedValue.Adjust("9007199254740992", 1) == "9007199254740993", "Cross double precision boundary");
Check(SeedValue.Adjust("18446744073709551615", 1) == "18446744073709551616", "Cross uint64 boundary");
Check(SeedValue.Adjust("0", -1) == "0", "Keep the existing zero lower bound");
Check(SeedValue.Adjust(SeedValue.Adjust("text", 1), 1) == "1", "Second adjustment continues from zero");
Check(SeedValue.Adjust("", 10) == "0", "Empty input safely resets on adjustment");
Check(SeedValue.Adjust("100", 10) == "110", "Large keyboard step");
Check(SeedValue.Adjust(new string('9', 1000), 1) == "1" + new string('0', 1000), "Long integer carries without overflow");
Check(ImageMetadataService.TryParseSdFormat("test\nSteps: 28, Seed: text-seed, Size: 832x1216")?.Seed == "text-seed", "SD text seed");
Check(ImageMetadataService.TryParseJson("{\"seed\":0}")?.Seed == "0", "Zero metadata is distinct from absent metadata");
Check(ImageMetadataService.TryParseJson("{\"prompt\":\"test\"}")?.Seed is null, "Missing seed is not zero");
Check(JsonSerializer.Deserialize<NAIParameters>("{\"Seed\":null}")?.Seed == "0", "Null setting is safe");
foreach (var seed in new[] { "", "0", "000", "+0", "-0", " 0 " })
    Check(SeedValue.IsRandom(seed) && int.Parse(SeedValue.Resolve(seed)) > 0, "Random sentinel produces a positive seed");
Check(!SeedValue.IsRandom("+") && !SeedValue.IsRandom("text"), "Non-numeric input remains fixed");
Check(SeedValue.Resolve("text", forceRandom: true) != "text", "Auto-generation can still force random seeds");
Check(SeedValue.ToWildcardSeed("123") == 123, "Existing wildcard expansion seed remains unchanged");
Check(SeedValue.ToWildcardSeed("004294967295") == SeedValue.ToWildcardSeed("4294967295"), "Equivalent numeric seeds produce the same wildcard choices");
Check(JsonSerializer.Serialize(SeedValue.ToRequestValue("004294967295")) == "4294967295", "Equivalent numeric seeds produce the same request and duplicate signature");
Check(SeedValue.ToWildcardSeed("中文种子") == SeedValue.ToWildcardSeed("中文种子"), "Text wildcard seed is repeatable");

// Real PNG text-chunk path, not just the JSON parser entry point.
byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9l8AAAAASUVORK5CYII=");
foreach (string seedJson in new[] { "4294967295", "\"中文种子\"" })
{
    string comment = $$"""{"prompt":"test","seed":{{seedJson}},"sampler":"k_euler"}""";
    byte[] image = ImageMetadataService.ReplacePngComment(png, comment);
    var metadata = ImageMetadataService.ReadFromBytes(image);
    using var document = JsonDocument.Parse(seedJson);
    Check(metadata?.Seed == SeedValue.ReadJson(document.RootElement), "PNG metadata import preserves seed");
}

Console.WriteLine($"Passed {checks} seed regression checks.");
ModelMetadataChecks.Run();
