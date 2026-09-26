using System.Text.Json;
using NAITool.Services;

internal static class ModelMetadataChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new Exception(message);
        }

        string[] generation = ["nai-diffusion-5-full", "nai-diffusion-5-curated", "nai-diffusion-4-5-full",
            "nai-diffusion-4-5-curated", "nai-diffusion-4-full", "nai-diffusion-4-curated-preview", "nai-diffusion-3", "nai-diffusion-furry-3"];
        string[] inpaint = ["nai-diffusion-5-full-inpainting", "nai-diffusion-4-5-full-inpainting",
            "nai-diffusion-4-5-curated-inpainting", "nai-diffusion-4-full-inpainting",
            "nai-diffusion-4-curated-inpainting", "nai-diffusion-3-inpainting", "nai-diffusion-furry-3-inpainting"];

        foreach (var model in generation)
            Check(ImportedImageModel.ResolveForTarget(model, generation) == model, "Supported generation IDs stay exact");
        foreach (var model in inpaint)
            Check(ImportedImageModel.ResolveForTarget(model, inpaint) == model, "Supported inpainting IDs stay exact");

        foreach (var (name, expected) in new[]
        {
            ("NovelAI Diffusion V5", "nai-diffusion-5-full"),
            ("NovelAI Diffusion V4.5", "nai-diffusion-4-5-full"),
            ("NovelAI Diffusion V4.5 Curated", "nai-diffusion-4-5-curated"),
            ("NovelAI Diffusion V4 Full", "nai-diffusion-4-full"),
            ("NovelAI Diffusion V4 Curated", "nai-diffusion-4-curated-preview"),
            ("NovelAI Diffusion Anime V3", "nai-diffusion-3"),
            ("NovelAI Diffusion Furry V3", "nai-diffusion-furry-3"),
            ("nai-diffusion-4-5-full-inpainting", "nai-diffusion-4-5-full"),
            ("nai-diffusion-4-curated-inpainting", "nai-diffusion-4-curated-preview"),
        })
        {
            Check(ImportedImageModel.ResolveForTarget(name, generation) == expected, "Named models resolve for generation");
            string inpaintExpected = expected.Replace("-preview", "") + "-inpainting";
            Check(ImportedImageModel.ResolveForTarget(name, inpaint) == inpaintExpected, "Named models resolve for inpainting");
        }
        Check(ImportedImageModel.ResolveForTarget("NovelAI Diffusion V4.5 4BDE2A90", generation) == "nai-diffusion-4-5-full",
            "Source can contain a trailing hash");
        Check(ImportedImageModel.ResolveForTarget("nai-diffusion-5-curated", inpaint) == null,
            "Unavailable variants must not silently switch to a different model");

        foreach (var name in new[] { "", "Unknown", "my-NovelAI Diffusion V4.5", "nai-diffusion-4-5-full.safetensors",
                     "Stable Diffusion XL", "NovelAI Diffusion V40", "NovelAI Diffusion V99", "NovelAI Diffusion Furry V5", "其他模型" })
            Check(ImportedImageModel.ResolveForTarget(name, generation) == null, "External or unsupported names must preserve the selection");

        var sd = ImageMetadataService.TryParseSdFormat("prompt\nSteps: 28, Model hash: abc123, Model: 自定义模型_v2, Seed: 1, Size: 832x1216");
        Check(sd?.ModelDisplayName == "自定义模型_v2", "Read the SD model name, not the model hash");
        foreach (string field in new[] { "model", "model_name", "Model" })
        {
            var metadata = ImageMetadataService.TryParseJson(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [field] = "NovelAI Diffusion V4.5", ["seed"] = 1,
            }));
            Check(metadata?.ModelDisplayName == "NovelAI Diffusion V4.5", "Read model field aliases");
        }
        Check(ImageMetadataService.TryParseJson("{\"model\":null,\"model_name\":\"external model\"}")?.Model == "external model",
            "Invalid optional model field must not discard metadata");

        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9l8AAAAASUVORK5CYII=");
        byte[] image = ImageMetadataService.ReplacePngTextChunks(png, new Dictionary<string, string>
        {
            ["Source"] = "NovelAI Diffusion V4.5 4BDE2A90",
            ["Software"] = "NovelAI",
            ["Comment"] = "{\"prompt\":\"test\",\"seed\":1}",
        });
        var imported = ImageMetadataService.ReadFromBytes(image)!;
        Check(imported.ModelDisplayName == "NovelAI Diffusion V4.5 4BDE2A90", "PNG Source fallback stays visible");
        Check(ImportedImageModel.ResolveForTarget(imported.ModelDisplayName, generation) == "nai-diffusion-4-5-full", "PNG Source is importable");
        imported.Model = "my custom checkpoint";
        Check(imported.ModelDisplayName == "my custom checkpoint" && ImportedImageModel.ResolveForTarget(imported.ModelDisplayName, generation) == null,
            "Explicit non-NAI model takes precedence over an old NAI Source");
        Check(ImageMetadataService.TryParseJson("{}")?.ModelDisplayName == "", "Missing model stays unknown");
        Console.WriteLine($"Passed {checks} model metadata checks.");
        return checks;
    }
}
