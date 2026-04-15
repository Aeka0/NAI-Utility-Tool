using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAITool.Services;

namespace NAITool;

public sealed partial class MainWindow
{
    private string? _lastGenerationRequestFingerprint;

    private enum DuplicateGenerationDecision
    {
        Proceed,
        ProceedWithRandomSeed,
        Cancel,
    }

    private sealed class GenerationRequestSignature
    {
        public string Fingerprint { get; init; } = "";
    }

    private static readonly JsonSerializerOptions DuplicateGenerationJsonOptions = new()
    {
        WriteIndented = false,
    };

    private static string ComputeStableHash(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string ComputeStableObjectHash<T>(T value)
    {
        string json = JsonSerializer.Serialize(value, DuplicateGenerationJsonOptions);
        return ComputeStableHash(json);
    }

    private static object[] CreateCharacterSignatureData(List<CharacterPromptInfo>? characters)
    {
        if (characters == null || characters.Count == 0)
            return [];

        return characters
            .Select(x => new
            {
                x.PositivePrompt,
                x.NegativePrompt,
                x.CenterX,
                x.CenterY,
                x.UseCustomPosition,
            })
            .Cast<object>()
            .ToArray();
    }

    private static object[] CreateVibeSignatureData(List<VibeTransferInfo>? vibes)
    {
        if (vibes == null || vibes.Count == 0)
            return [];

        return vibes
            .Select(x => new
            {
                x.FileName,
                x.Strength,
                x.InformationExtracted,
                x.IsEncoded,
                ImageHash = ComputeStableHash(x.ImageBase64),
            })
            .Cast<object>()
            .ToArray();
    }

    private static object[] CreatePreciseReferenceSignatureData(List<PreciseReferenceInfo>? preciseReferences)
    {
        if (preciseReferences == null || preciseReferences.Count == 0)
            return [];

        return preciseReferences
            .Select(x => new
            {
                x.FileName,
                x.ReferenceType,
                x.Strength,
                x.Fidelity,
                ImageHash = ComputeStableHash(x.ImageBase64),
            })
            .Cast<object>()
            .ToArray();
    }

    private GenerationRequestSignature BuildImageGenerationRequestSignature(
        NAIParameters parameters,
        int width,
        int height,
        int actualSeed,
        string prompt,
        string negativePrompt,
        List<CharacterPromptInfo>? characters,
        List<VibeTransferInfo>? vibes,
        List<PreciseReferenceInfo>? preciseReferences)
    {
        var payload = new
        {
            Kind = "text2img",
            Width = width,
            Height = height,
            parameters.Model,
            parameters.Sampler,
            parameters.Schedule,
            parameters.Scale,
            parameters.CfgRescale,
            parameters.Sm,
            parameters.Variety,
            parameters.QualityToggle,
            parameters.Steps,
            parameters.UcPreset,
            Seed = actualSeed,
            Prompt = prompt,
            NegativePrompt = negativePrompt,
            Characters = CreateCharacterSignatureData(characters),
            Vibes = CreateVibeSignatureData(vibes),
            PreciseReferences = CreatePreciseReferenceSignatureData(preciseReferences),
        };

        return new GenerationRequestSignature
        {
            Fingerprint = ComputeStableObjectHash(payload),
        };
    }

    private GenerationRequestSignature BuildI2IGenerationRequestSignature(
        string kind,
        NAIParameters parameters,
        int width,
        int height,
        int actualSeed,
        string prompt,
        string negativePrompt,
        List<CharacterPromptInfo>? characters,
        List<VibeTransferInfo>? vibes,
        List<PreciseReferenceInfo>? preciseReferences,
        string imageBase64,
        string? maskBase64,
        Vector2 imageOffset)
    {
        var payload = new
        {
            Kind = kind,
            Width = width,
            Height = height,
            parameters.Model,
            parameters.Sampler,
            parameters.Schedule,
            parameters.Scale,
            parameters.CfgRescale,
            parameters.Sm,
            parameters.Variety,
            parameters.QualityToggle,
            parameters.Steps,
            parameters.UcPreset,
            parameters.DenoiseStrength,
            parameters.DenoiseNoise,
            Seed = actualSeed,
            Prompt = prompt,
            NegativePrompt = negativePrompt,
            ImageHash = ComputeStableHash(imageBase64),
            MaskHash = ComputeStableHash(maskBase64),
            ImageOffsetX = imageOffset.X,
            ImageOffsetY = imageOffset.Y,
            Characters = CreateCharacterSignatureData(characters),
            Vibes = CreateVibeSignatureData(vibes),
            PreciseReferences = CreatePreciseReferenceSignatureData(preciseReferences),
        };

        return new GenerationRequestSignature
        {
            Fingerprint = ComputeStableObjectHash(payload),
        };
    }

    private void RememberLastGenerationRequest(GenerationRequestSignature signature)
    {
        _lastGenerationRequestFingerprint = signature.Fingerprint;
    }

    private void ApplyRequestedSeedToCurrentMode(int seed)
    {
        CurrentParams.Seed = seed;
        NbSeed.Value = seed;
        if (IsAdvancedWindowOpen)
            _advNbSeed.Value = seed;
        UpdateSeedRandomizeButtonStyle();
    }

    private async Task<DuplicateGenerationDecision> CheckDuplicateGenerationRequestAsync(
        GenerationRequestSignature signature,
        int requestedSeed)
    {
        if (_autoGenRunning || _continuousGenRunning)
            return DuplicateGenerationDecision.Proceed;

        if (requestedSeed <= 0)
            return DuplicateGenerationDecision.Proceed;

        if (!string.Equals(_lastGenerationRequestFingerprint, signature.Fingerprint, StringComparison.Ordinal))
            return DuplicateGenerationDecision.Proceed;

        var panel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = L("generate.duplicate_guard.message"),
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = L("generate.duplicate_guard.note"),
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.72,
                    FontSize = 12,
                },
            },
        };

        var dialog = new ContentDialog
        {
            Title = L("generate.duplicate_guard.title"),
            Content = panel,
            PrimaryButtonText = L("common.yes"),
            CloseButtonText = L("common.no"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (Content is FrameworkElement root)
            dialog.XamlRoot = root.XamlRoot;
        dialog.Resources["ContentDialogMaxWidth"] = 520.0;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            ApplyRequestedSeedToCurrentMode(0);
            _settings.Save();
            return DuplicateGenerationDecision.ProceedWithRandomSeed;
        }

        TxtStatus.Text = L("generate.duplicate_guard.cancelled");
        return DuplicateGenerationDecision.Cancel;
    }
}
