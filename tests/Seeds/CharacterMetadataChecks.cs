using System.Text.Json;
using NAITool.Services;

internal static class CharacterMetadataChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new Exception(message);
        }

        foreach (string model in new[] { "nai-diffusion-5", "nai-diffusion-5-full", "nai-diffusion-5-curated",
                     "nai-diffusion-5-inpainting", "nai-diffusion-5-full-inpainting", "nai-diffusion-5-curated-inpainting" })
            Check(CharacterPromptRules.MaxForModel(model) == 22, "V5 generation and inpainting allow 22 characters");
        foreach (string model in new[] { "nai-diffusion-4-full", "nai-diffusion-4-curated-preview",
                     "nai-diffusion-4-5-full", "nai-diffusion-4-5-curated", "nai-diffusion-4-full-inpainting",
                     "nai-diffusion-4-curated-inpainting", "nai-diffusion-4-5-full-inpainting", "nai-diffusion-4-5-curated-inpainting" })
            Check(CharacterPromptRules.MaxForModel(model) == 6, "V4/V4.5 retain their six-character request limit");
        Check(CharacterPromptRules.MaxRetainedCount >= 22, "Imports and saved workspaces retain all V5 characters");

        foreach (var (json, expected) in new (string, bool?)[]
        {
            ("{}", null),
            ("{\"use_coords\":true}", true),
            ("{\"use_coords\":false}", false),
            ("{\"use_coords\":\"true\"}", null),
            ("{\"v4_prompt\":{\"use_coords\":true}}", true),
            ("{\"use_coords\":true,\"v4_prompt\":{\"use_coords\":false}}", false),
            ("{\"use_coords\":false,\"v4_prompt\":{\"use_coords\":true}}", true),
            ("{\"use_coords\":true,\"v4_prompt\":{\"use_coords\":null}}", true),
            ("{\"use_coords\":false,\"v4_prompt\":{\"use_coords\":1}}", false),
            ("{\"use_coords\":true,\"v4_prompt\":null}", true),
        })
        {
            var metadata = ImageMetadataService.TryParseJson(json);
            Check(metadata != null && metadata.UseCharacterCoordinates == expected,
                "Explicit caption flag overrides the root flag; missing or malformed flags preserve fallback semantics");
        }

        var positive = Enumerable.Range(0, 22).Select(i => new
        {
            char_caption = $"角色 {i + 1}", centers = new[] { new { x = i / 21.0, y = 1 - i / 21.0 } },
        }).ToArray();
        var negative = Enumerable.Range(0, 22).Select(i => new { char_caption = $"不要 {i + 1}" }).ToArray();
        string comment = JsonSerializer.Serialize(new
        {
            model = "nai-diffusion-5-full", prompt = "group", seed = 42,
            v4_prompt = new { use_coords = true, caption = new { char_captions = positive } },
            v4_negative_prompt = new { caption = new { char_captions = negative } },
        });
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9l8AAAAASUVORK5CYII=");
        var imported = ImageMetadataService.ReadFromBytes(ImageMetadataService.ReplacePngComment(png, comment));
        Check(imported != null && imported.CharacterPrompts.Count == 22 && imported.CharacterNegativePrompts.Count == 22 &&
              imported.CharacterCenters.Count == 22, "PNG import preserves all 22 character slots");
        Check(imported!.UseCharacterCoordinates == true && imported.Seed == "42" && imported.Model == "nai-diffusion-5-full",
            "PNG import restores coordinate usage without disturbing the seed or model");
        for (int i = 0; i < 22; i++)
        {
            Check(imported.CharacterPrompts[i] == positive[i].char_caption && imported.CharacterNegativePrompts[i] == negative[i].char_caption,
                "PNG import preserves Unicode and positive/negative character ordering");
            Check(imported.CharacterCenters[i] == (positive[i].centers[0].x, positive[i].centers[0].y),
                "PNG import associates each position with the correct character");
        }

        foreach (var (positions, expected) in new (string, (double, double))[]
        {
            ("[]", (0.5, 0.5)), ("null", (0.5, 0.5)), ("{}", (0.5, 0.5)), ("[null]", (0.5, 0.5)),
            ("[{\"x\":0.2}]", (0.2, 0.5)),
            ("[{\"x\":\"bad\",\"y\":null}]", (0.5, 0.5)),
            ("[{\"x\":-2,\"y\":2}]", (0, 1)),
            ("[{\"x\":1e999,\"y\":-1e999}]", (0.5, 0.5)),
            ("[{\"x\":0,\"y\":1},{\"x\":0.4,\"y\":0.6}]", (0, 1)),
        })
        {
            var metadata = ImageMetadataService.TryParseJson($$$$"""
                {"prompt":"keep","v4_prompt":{"caption":{"char_captions":[{"char_caption":"one","centers":{{{{positions}}}}}]}}}
                """);
            Check(metadata != null && metadata.PositivePrompt == "keep" && metadata.CharacterCenters.Single() == expected,
                "Malformed or out-of-range coordinates are normalized without losing the metadata");
            Check(metadata!.UseCharacterCoordinates == null, "Coordinates alone do not enable custom positioning");
        }

        var sparse = ImageMetadataService.TryParseJson("""
            {"v4_prompt":{"caption":{"char_captions":[
                {"centers":[{"x":0.1,"y":0.2}]},null,{"char_caption":"third","centers":[{"x":0.8,"y":0.9}]}]}},
             "v4_negative_prompt":{"caption":{"char_captions":[null,{}, {"char_caption":"third negative"}]}}}
            """);
        Check(sparse != null && sparse.CharacterPrompts.SequenceEqual(new[] { "", "", "third" }) &&
              sparse.CharacterNegativePrompts.SequenceEqual(new[] { "", "", "third negative" }),
            "Missing captions retain their slot rather than shifting later prompts");
        Check(sparse!.CharacterCenters.SequenceEqual(new[] { (0.1, 0.2), (0.5, 0.5), (0.8, 0.9) }),
            "Missing captions do not shift character coordinates");

        foreach (string malformed in new[] { "null", "[]", "{\"caption\":null}", "{\"caption\":{\"char_captions\":{}}}" })
        {
            var metadata = ImageMetadataService.TryParseJson($$$$"""{"prompt":"keep","v4_prompt":{{{{malformed}}}},"v4_negative_prompt":{{{{malformed}}}}} """);
            Check(metadata != null && metadata.PositivePrompt == "keep" && metadata.CharacterPrompts.Count == 0 &&
                  metadata.CharacterNegativePrompts.Count == 0, "Malformed optional character containers do not discard base metadata");
        }

        Console.WriteLine($"Passed {checks} character metadata checks.");
        return checks;
    }
}
