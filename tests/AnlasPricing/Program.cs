using NAITool.Services;

const string V5 = "nai-diffusion-5-full";
int checks = 0;
void Equal(int expected, int actual, string scenario)
{
    checks++;
    if (expected != actual)
        throw new Exception($"{scenario}: expected {expected}, got {actual}");
}

// Fixtures from the official production calculator, September 2026.
foreach (string model in new[] { V5, "nai-diffusion-5-curated", "nai-diffusion-5-full-inpainting" })
{
    Equal(30, NovelAiAnlasCalculator.PaidBaseCost(model, 1024, 1024, 28), model);
    Equal(30, NovelAiAnlasCalculator.PaidBaseCost(model, 832, 1216, 28), "portrait");
    Equal(33, NovelAiAnlasCalculator.PaidBaseCost(model, 1024, 1024, 30), "30 steps");
    Equal(45, NovelAiAnlasCalculator.PaidBaseCost(model, 1024, 1536, 28), "large portrait");
    Equal(68, NovelAiAnlasCalculator.PaidBaseCost(model, 1536, 1536, 28), "round after multiplier");
    Equal(51, NovelAiAnlasCalculator.PaidBaseCost(model, 1024, 1024, 50), "50 steps");
    Equal(30, NovelAiAnlasCalculator.PaidBaseCost(model, 1024, 1024, 28, sm: true), "stale SMEA ignored");
}

Equal(20, NovelAiAnlasCalculator.PaidBaseCost("nai-diffusion-4-5-full", 1024, 1024, 28), "V4.5 unchanged");
Equal(24, NovelAiAnlasCalculator.PaidBaseCost("nai-diffusion-3", 1024, 1024, 28, sm: true), "V3 SMEA");
Equal(0, NovelAiAnlasCalculator.BaseCost(V5, 1024, 1024, 28, true, false), "Opus available, including displayed 0%");
Equal(0, NovelAiAnlasCalculator.BaseCost(V5, 1024, 1024, 28, true, null), "missing usage follows official default");
Equal(30, NovelAiAnlasCalculator.BaseCost(V5, 1024, 1024, 28, true, true), "Opus exhausted");
Equal(30, NovelAiAnlasCalculator.BaseCost(V5, 1024, 1024, 28, false, false), "inactive or non-Opus");
Equal(0, NovelAiAnlasCalculator.BaseCost("nai-diffusion-4-5-full", 1024, 1024, 28, true, true), "V5 exhaustion does not affect V4.5");
Equal(33, NovelAiAnlasCalculator.BaseCost(V5, 1024, 1024, 30, true, false), "Opus step limit");
Equal(45, NovelAiAnlasCalculator.BaseCost(V5, 1024, 1536, 28, true, false), "Opus size limit");
Equal(15, NovelAiAnlasCalculator.PaidBaseCost(V5, 1024, 1024, 28, strength: 0.5), "enhance strength");
Equal(23, NovelAiAnlasCalculator.PaidBaseCost(V5, 1024, 1536, 28, strength: 0.5), "strength then round");
Equal(2, NovelAiAnlasCalculator.PaidBaseCost(V5, 1024, 1024, 28, strength: 0), "minimum price");
Equal(30, NovelAiAnlasCalculator.PaidBaseCost(V5, 1024, 1024, 28, strength: double.NaN), "invalid strength cannot erase price");
Equal(0, NovelAiAnlasCalculator.BaseCost(V5, 1024, 1024, 28, true, false, strength: 0.5), "free img2img");
Equal(15, NovelAiAnlasCalculator.BaseCost(V5, 1024, 1024, 28, true, true, strength: 0.5), "paid img2img");
Equal(150, NovelAiAnlasCalculator.PaidBaseCost(V5, 2048, 2048, 36), "over-limit is not clamped to 140");
Equal(int.MaxValue, NovelAiAnlasCalculator.PaidBaseCost(V5, int.MaxValue, int.MaxValue, int.MaxValue), "large inputs cannot overflow");
Equal(int.MaxValue, NovelAiAnlasCalculator.BaseCost(V5, 0, 1024, 28, true, false), "invalid dimensions cannot be free");
Equal(0, NovelAiAnlasCalculator.ReferenceCost(0, 4, 0), "four encoded vibes");
Equal(8, NovelAiAnlasCalculator.ReferenceCost(2, 6, 0), "encoding plus extra slots");
Equal(10, NovelAiAnlasCalculator.ReferenceCost(0, 0, 2), "precise references");
Console.WriteLine($"Passed {checks} Anlas pricing checks.");
