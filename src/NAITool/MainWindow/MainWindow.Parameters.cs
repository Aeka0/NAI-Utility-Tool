using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using NAITool.Controls;
using NAITool.Models;
using NAITool.Services;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using System.Runtime.InteropServices.WindowsRuntime;

namespace NAITool;

public sealed partial class MainWindow
{
    private void PopulateLeftSidebarControls()
    {
        foreach (var p in MaskCanvasControl.CanvasPresets)
            CboSize.Items.Add(CreateTextComboBoxItem(p.Label));
        ApplyMenuTypography(CboSize);
        SetSizeInputsSilently(_customWidth, _customHeight);
        SuppressNumberBoxClearButton(NbMaxWidth);
        SuppressNumberBoxClearButton(NbMaxHeight);
        SuppressNumberBoxClearButton(NbSeed);
        UpdateSizeControlMode();
        UpdateSizeWarningVisuals();
    }

    private void SyncParamsToUI()
    {
        var p = CurrentParams;
        NbSeed.Value = p.Seed;
        ChkVariety.IsChecked = p.Variety;
        SetSizeInputsSilently(_customWidth, _customHeight);
        UpdateModelDependentUI();
        UpdateSeedRandomizeButtonStyle();
    }

    private void SetSizeInputsSilently(int width, int height)
    {
        _isUpdatingMaxSize = true;
        try
        {
            if (NbMaxWidth != null) NbMaxWidth.Value = width;
            if (NbMaxHeight != null) NbMaxHeight.Value = height;
            if (IsAdvancedWindowOpen)
            {
                if (_advNbMaxWidth != null) _advNbMaxWidth.Value = width;
                if (_advNbMaxHeight != null) _advNbMaxHeight.Value = height;
            }
        }
        finally
        {
            _isUpdatingMaxSize = false;
        }
    }

    private bool IsCurrentModelV3()
    {
        return IsV3ModelKey(GetCurrentModelKey());
    }

    private static string[] GetAvailableSamplersForModel(string? model)
    {
        bool isV3 = IsV3ModelKey(model ?? "");
        return AvailableSamplers
            .Where(x => isV3
                ? !string.Equals(x, "ddim", StringComparison.Ordinal)
                : !string.Equals(x, "ddim_v3", StringComparison.Ordinal))
            .ToArray();
    }

    private static string NormalizeSamplerForModel(string? sampler, string? model)
    {
        var available = GetAvailableSamplersForModel(model);
        if (!string.IsNullOrWhiteSpace(sampler) && available.Contains(sampler, StringComparer.Ordinal))
            return sampler;
        return available.FirstOrDefault() ?? "k_euler_ancestral";
    }

    private static string NormalizeScheduleForModel(string? schedule, string? model, string? fallback = null)
    {
        string normalized = (schedule ?? "").Trim();
        if (AvailableSchedules.Contains(normalized, StringComparer.Ordinal))
            return normalized;

        // Imports from Stable Diffusion or other tools may carry schedule names
        // that NovelAI does not accept directly. Map them to the closest safe value.
        if (normalized.Equals("normal", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("simple", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("sgm uniform", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("sgm_uniform", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("automatic", StringComparison.OrdinalIgnoreCase))
            return "native";

        string fallbackNormalized = (fallback ?? "").Trim();
        if (AvailableSchedules.Contains(fallbackNormalized, StringComparer.Ordinal))
            return fallbackNormalized;

        return AvailableSchedules.FirstOrDefault() ?? "karras";
    }

    private sealed class ImportedPromptPresetMatch
    {
        public string PositivePrompt { get; init; } = "";
        public string NegativePrompt { get; init; } = "";
        public bool QualityMatched { get; init; }
        public int? UcPresetMatched { get; init; }
    }

    private sealed class PromptPresetCandidateMatch
    {
        public string Model { get; init; } = "";
        public string StrippedPrompt { get; init; } = "";
        public int PresetIndex { get; init; } = -1;
        public int TagCount { get; init; }
    }

    private ImportedPromptPresetMatch ExtractImportedPromptPresetMatch(string positivePrompt, string negativePrompt, string model)
    {
        var qualityMatch = FindBestQualityPresetMatch(positivePrompt, model);
        var ucMatch = FindBestUcPresetMatch(negativePrompt, model);

        return new ImportedPromptPresetMatch
        {
            PositivePrompt = qualityMatch?.StrippedPrompt ?? positivePrompt,
            NegativePrompt = ucMatch?.StrippedPrompt ?? negativePrompt,
            QualityMatched = qualityMatch != null,
            UcPresetMatched = ucMatch?.PresetIndex,
        };
    }

    private PromptPresetCandidateMatch? FindBestQualityPresetMatch(string prompt, string currentModel)
    {
        PromptPresetCandidateMatch? bestMatch = null;
        foreach (string candidateModel in GetPromptPresetRecognitionModelCandidates(currentModel))
        {
            string presetText = NovelAIService.GetQualityTagSuffix(candidateModel);
            int tagCount = SplitPromptPresetTags(presetText).Count;
            if (tagCount == 0)
                continue;
            if (!TryStripPromptPreset(prompt, presetText, out string strippedPrompt))
                continue;

            var match = new PromptPresetCandidateMatch
            {
                Model = candidateModel,
                StrippedPrompt = strippedPrompt,
                TagCount = tagCount,
            };
            if (IsBetterPromptPresetMatch(match, bestMatch))
                bestMatch = match;
        }

        return bestMatch;
    }

    private PromptPresetCandidateMatch? FindBestUcPresetMatch(string prompt, string currentModel)
    {
        PromptPresetCandidateMatch? bestMatch = null;
        foreach (string candidateModel in GetPromptPresetRecognitionModelCandidates(currentModel))
        {
            foreach (int presetIndex in new[] { 0, 1 })
            {
                string presetText = NovelAIService.GetUcPresetText(candidateModel, presetIndex);
                int tagCount = SplitPromptPresetTags(presetText).Count;
                if (tagCount == 0)
                    continue;
                if (!TryStripPromptPreset(prompt, presetText, out string strippedPrompt))
                    continue;

                var match = new PromptPresetCandidateMatch
                {
                    Model = candidateModel,
                    PresetIndex = presetIndex,
                    StrippedPrompt = strippedPrompt,
                    TagCount = tagCount,
                };
                if (IsBetterPromptPresetMatch(match, bestMatch))
                    bestMatch = match;
            }
        }

        return bestMatch;
    }

    private IEnumerable<string> GetPromptPresetRecognitionModelCandidates(string currentModel)
    {
        string normalizedCurrentModel = NormalizePromptPresetModelKey(currentModel);
        if (!string.IsNullOrWhiteSpace(normalizedCurrentModel))
            yield return normalizedCurrentModel;

        foreach (string model in GenerationModels.Concat(I2IModels))
        {
            string normalizedModel = NormalizePromptPresetModelKey(model);
            if (string.IsNullOrWhiteSpace(normalizedModel) ||
                string.Equals(normalizedModel, normalizedCurrentModel, StringComparison.Ordinal))
            {
                continue;
            }

            yield return normalizedModel;
        }
    }

    private static string NormalizePromptPresetModelKey(string model) =>
        model.EndsWith("-inpainting", StringComparison.Ordinal)
            ? model[..^"-inpainting".Length]
            : model;

    private static bool IsBetterPromptPresetMatch(PromptPresetCandidateMatch candidate, PromptPresetCandidateMatch? currentBest)
    {
        if (currentBest == null)
            return true;
        if (candidate.TagCount != currentBest.TagCount)
            return candidate.TagCount > currentBest.TagCount;

        // Keep the earlier candidate on ties so the current model still wins.
        return false;
    }

    private static bool TryStripPromptPreset(string prompt, string presetText, out string strippedPrompt)
    {
        strippedPrompt = prompt;

        var promptTags = SplitPromptPresetTags(prompt);
        var presetTags = SplitPromptPresetTags(presetText);
        if (promptTags.Count == 0 || presetTags.Count == 0)
            return false;

        var consumed = new bool[promptTags.Count];
        foreach (var presetTag in presetTags)
        {
            string normalizedPresetTag = NormalizePromptTagForComparison(presetTag);
            int matchIndex = -1;
            for (int i = 0; i < promptTags.Count; i++)
            {
                if (consumed[i])
                    continue;
                if (!string.Equals(NormalizePromptTagForComparison(promptTags[i]), normalizedPresetTag, StringComparison.Ordinal))
                    continue;

                matchIndex = i;
                break;
            }

            if (matchIndex < 0)
                return false;

            consumed[matchIndex] = true;
        }

        strippedPrompt = string.Join(", ", promptTags.Where((_, index) => !consumed[index]));
        return true;
    }

    private static List<string> SplitPromptPresetTags(string prompt) =>
        (prompt ?? "")
            .Replace('，', ',')
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => tag.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .ToList();

    private static string NormalizePromptTagForComparison(string tag)
    {
        string normalized = (tag ?? "").Replace('，', ',').Trim();
        normalized = Regex.Replace(normalized, @"\s+", " ");
        return normalized.ToLowerInvariant();
    }

    private string GetUcPresetDisplayName(int ucPreset) => ucPreset switch
    {
        0 => L("dialog.advanced.uc_preset.full"),
        1 => L("dialog.advanced.uc_preset.light"),
        _ => L("dialog.advanced.uc_preset.none"),
    };

    private void RefreshAdvancedSamplerOptions()
    {
        if (_advCboSampler == null)
            return;

        string model = GetCurrentModelKey();
        string selected = NormalizeSamplerForModel(CurrentParams.Sampler, model);
        var samplers = GetAvailableSamplersForModel(model);

        _advCboSampler.Items.Clear();
        foreach (var s in samplers)
            _advCboSampler.Items.Add(CreateTextComboBoxItem(s));

        _advCboSampler.SelectedIndex = Array.IndexOf(samplers, selected);
        if (_advCboSampler.SelectedIndex < 0)
            _advCboSampler.SelectedIndex = 0;
    }

    private void UpdateModelDependentUI()
    {
        if (ChkVariety == null || CboModel == null) return;
        bool isV3 = IsCurrentModelV3();
        CurrentParams.Sampler = NormalizeSamplerForModel(CurrentParams.Sampler, GetCurrentModelKey());
        CurrentParams.Schedule = NormalizeScheduleForModel(CurrentParams.Schedule, GetCurrentModelKey());
        RecheckVibeTransferCacheState();
        UpdateReferenceButtonAndPanelState();
        ChkVariety.Visibility = Visibility.Visible;
        if (IsAdvancedWindowOpen)
        {
            if (_advChkVariety != null)
                _advChkVariety.Visibility = Visibility.Visible;
            if (_advChkSmea != null)
                _advChkSmea.Visibility = (_currentMode == AppMode.ImageGeneration && isV3)
                    ? Visibility.Visible : Visibility.Collapsed;
            RefreshAdvancedSamplerOptions();
        }

        UpdateAnlasBalanceText();
        UpdateBtnGenerateForApiKey();
        UpdateGenerateButtonWarning();
    }

    private NAIParameters CurrentParams => _currentMode == AppMode.ImageGeneration
        ? _settings.Settings.GenParameters
        : _i2iEditMode == I2IEditMode.Denoise
            ? _settings.Settings.I2IDenoiseParameters
            : _settings.Settings.InpaintParameters;

    private void SyncUIToParams()
    {
        var p = CurrentParams;
        p.Seed = (int)NbSeed.Value;
        p.Variety = ChkVariety.IsChecked == true;
        p.Model = GetSelectedComboText(CboModel) ?? p.Model;
        p.Sampler = NormalizeSamplerForModel(p.Sampler, p.Model);
        p.Schedule = NormalizeScheduleForModel(p.Schedule, p.Model);
    }

    private static NAIParameters CreateDefaultGenerationParameters() => new()
    {
        Model = "nai-diffusion-4-5-full",
    };

    private static NAIParameters CreateDefaultInpaintParameters() => new()
    {
        Model = "nai-diffusion-4-5-full-inpainting",
    };

    private static NAIParameters CreateDefaultI2IDenoiseParameters() => new()
    {
        Model = "nai-diffusion-4-5-full",
        DenoiseStrength = 0.7,
        DenoiseNoise = 0,
    };

    private string GetWildcardsRootDir() => DefaultWildcardsDir;

    private static void EnsureDefaultWildcards()
    {
        try
        {
            if (!Directory.Exists(BundledWildcardsDir)) return;
            Directory.CreateDirectory(DefaultWildcardsDir);

            foreach (string sourceFile in Directory.GetFiles(BundledWildcardsDir, "*.txt", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(BundledWildcardsDir, sourceFile);
                string targetPath = Path.Combine(DefaultWildcardsDir, relative);
                if (!File.Exists(targetPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    File.Copy(sourceFile, targetPath, overwrite: false);
                }
            }
        }
        catch { }
    }

    private void LoadWildcards()
    {
        try
        {
            EnsureDefaultWildcards();
            _wildcardService.Reload(GetWildcardsRootDir());
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("wildcards.load_failed", ex.Message);
        }
    }

    private WildcardWeightFormat GetWildcardWeightFormatForModel(string model) =>
        IsV3ModelKey(model)
            ? WildcardWeightFormat.NaiClassic
            : WildcardWeightFormat.NaiNumeric;

    private WildcardExpandContext CreateWildcardContext(int seed, string model) =>
        new(seed, GetWildcardWeightFormatForModel(model));

    private string ExpandPromptFeatures(string text, WildcardExpandContext context, bool isNegativeText = false)
    {
        string expanded = ExpandPromptShortcuts(text);
        if (!_settings.Settings.WildcardsEnabled)
            return expanded;

        return _wildcardService
            .ExpandText(expanded, context, _settings.Settings.WildcardsRequireExplicitSyntax, true)
            .Text;
    }

    private void ApplyRememberedPromptAndParameterPreference()
    {
        if (!_settings.Settings.RememberPromptAndParameters)
        {
            _settings.Settings.GenParameters = CreateDefaultGenerationParameters();
            _settings.Settings.InpaintParameters = CreateDefaultInpaintParameters();
            _settings.Settings.I2IDenoiseParameters = CreateDefaultI2IDenoiseParameters();
            _customWidth = 832;
            _customHeight = 1216;
            _genPositivePrompt = "";
            _genNegativePrompt = "";
            _genStylePrompt = "";
            _i2iPositivePrompt = "";
            _i2iNegativePrompt = "";
            _i2iStylePrompt = "";
            _isSplitPrompt = false;
            _genCharacters.Clear();
            _i2iCharacters.Clear();
            return;
        }

        var remembered = _settings.Settings.RememberedPrompts ?? new RememberedPromptState();
        _genPositivePrompt = remembered.GenPositivePrompt ?? "";
        _genNegativePrompt = remembered.GenNegativePrompt ?? "";
        _genStylePrompt = remembered.GenStylePrompt ?? "";
        _i2iPositivePrompt = remembered.I2IPositivePrompt ?? "";
        _i2iNegativePrompt = remembered.I2INegativePrompt ?? "";
        _i2iStylePrompt = remembered.I2IStylePrompt ?? "";
        _isSplitPrompt = remembered.IsSplitPrompt;
        _customWidth = SnapToMultipleOf64(_settings.Settings.RememberedCustomWidth);
        _customHeight = SnapToMultipleOf64(_settings.Settings.RememberedCustomHeight);
        LoadRememberedCharacters(_genCharacters, remembered.GenCharacters);
        LoadRememberedCharacters(_i2iCharacters, remembered.I2ICharacters);
    }

    private static void LoadRememberedCharacters(List<CharacterEntry> target, List<RememberedCharacterState>? rememberedCharacters)
    {
        target.Clear();
        foreach (var item in (rememberedCharacters ?? new List<RememberedCharacterState>()).Take(MaxCharacters))
        {
            target.Add(new CharacterEntry
            {
                PositivePrompt = item.PositivePrompt ?? "",
                NegativePrompt = item.NegativePrompt ?? "",
                CenterX = Math.Clamp(item.CenterX, 0, 1),
                CenterY = Math.Clamp(item.CenterY, 0, 1),
                IsPositiveTab = item.IsPositiveTab,
                IsCollapsed = item.IsCollapsed,
                IsDisabled = item.IsDisabled,
                UseCustomPosition = item.UseCustomPosition,
            });
        }
    }

    private void ClearRememberedPromptState()
    {
        _settings.Settings.RememberedPrompts = new RememberedPromptState();
        _settings.Settings.RememberedCustomWidth = 832;
        _settings.Settings.RememberedCustomHeight = 1216;
    }

    private void SyncRememberedPromptAndParameterState()
    {
        if (!_settings.Settings.RememberPromptAndParameters)
            return;

        SaveAllCharacterPrompts();
        _settings.Settings.RememberedPrompts = new RememberedPromptState
        {
            GenPositivePrompt = _genPositivePrompt,
            GenNegativePrompt = _genNegativePrompt,
            GenStylePrompt = _genStylePrompt,
            I2IPositivePrompt = _i2iPositivePrompt,
            I2INegativePrompt = _i2iNegativePrompt,
            I2IStylePrompt = _i2iStylePrompt,
            IsSplitPrompt = _isSplitPrompt,
            GenCharacters = _genCharacters.Select(CreateRememberedCharacterState).ToList(),
            I2ICharacters = _i2iCharacters.Select(CreateRememberedCharacterState).ToList(),
        };
        _settings.Settings.RememberedCustomWidth = _customWidth;
        _settings.Settings.RememberedCustomHeight = _customHeight;
    }

    private static RememberedCharacterState CreateRememberedCharacterState(CharacterEntry entry) => new()
    {
        PositivePrompt = entry.PositivePrompt,
        NegativePrompt = entry.NegativePrompt,
        CenterX = entry.CenterX,
        CenterY = entry.CenterY,
        IsPositiveTab = entry.IsPositiveTab,
        IsCollapsed = entry.IsCollapsed,
        IsDisabled = entry.IsDisabled,
        UseCustomPosition = entry.UseCustomPosition,
    };
}
