using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAITool.Controls;
using NAITool.Models;
using NAITool.Services;

namespace NAITool;

public sealed partial class MainWindow
{
    private AutomationRunContext CreateAutomationRunContext(AutomationSettings settingsSnapshot)
    {
        return new AutomationRunContext
        {
            SettingsSnapshot = settingsSnapshot.Clone(),
            PromptPool = _promptShortcuts
                .Where(x => !string.IsNullOrWhiteSpace(x.Prompt))
                .Select(x => new PromptShortcutEntry
                {
                    Shortcut = x.Shortcut,
                    Prompt = x.Prompt,
                })
                .ToList(),
            VibePool = _genVibeTransfers
                .Where(x => !x.IsDisabled)
                .Select(CloneVibeTransferEntry)
                .ToList(),
        };
    }

    private void PrepareAutomationIteration(AutomationRunContext context)
    {
        context.CurrentPromptOverride = null;
        context.CurrentSizeOverride = null;
        context.CurrentVibeOverride = null;

        var notes = new List<string>();
        var randomization = context.SettingsSnapshot.Randomization;

        if (randomization.RandomizeSize)
        {
            var sizeCandidates = randomization.SizePresets
                .Select(x => TryParseAutomationSizePreset(x, out int w, out int h) ? (Valid: true, W: w, H: h) : default)
                .Where(x => x.Valid)
                .ToList();
            if (sizeCandidates.Count > 0)
            {
                var selected = sizeCandidates[Random.Shared.Next(sizeCandidates.Count)];
                context.CurrentSizeOverride = (selected.W, selected.H);
                notes.Add(Lf("automation.note.size", selected.W, selected.H));
            }
        }

        if (randomization.RandomizePrompt)
        {
            if (context.PromptPool.Count > 0)
            {
                var selected = context.PromptPool[Random.Shared.Next(context.PromptPool.Count)];
                context.CurrentPromptOverride = selected.Prompt;
                string label = string.IsNullOrWhiteSpace(selected.Shortcut)
                    ? L("automation.prompt_shortcut_fallback")
                    : selected.Shortcut;
                notes.Add(Lf("automation.note.prompt", label));
            }
            else if (!context.MissingPromptPoolNotified)
            {
                TxtStatus.Text = L("automation.status.random_prompt_missing");
                context.MissingPromptPoolNotified = true;
            }
        }

        if (randomization.RandomizeVibeFiles)
        {
            if (context.VibePool.Count > 0)
            {
                var selected = context.VibePool[Random.Shared.Next(context.VibePool.Count)];
                context.CurrentVibeOverride =
                [
                    new VibeTransferInfo
                    {
                        FileName = selected.FileName,
                        ImageBase64 = selected.ImageBase64,
                        Strength = Math.Clamp(selected.Strength, 0, 1),
                        InformationExtracted = Math.Clamp(selected.InformationExtracted, 0, 1),
                    }
                ];
                notes.Add(Lf("automation.note.vibe", selected.FileName));
            }
            else if (!context.MissingVibePoolNotified)
            {
                TxtStatus.Text = L("automation.status.random_vibe_missing");
                context.MissingVibePoolNotified = true;
            }
        }

        context.LastSummary = notes.Count > 0
            ? Lf("automation.status.iteration_summary", string.Join(" | ", notes))
            : L("automation.status.preparing");
        TxtStatus.Text = context.LastSummary;
    }

    private static VibeTransferEntry CloneVibeTransferEntry(VibeTransferEntry x) => new()
    {
        FileName = x.FileName,
        ImageBase64 = x.ImageBase64,
        Strength = x.Strength,
        InformationExtracted = x.InformationExtracted,
        IsEncodedFile = x.IsEncodedFile,
        IsCollapsed = x.IsCollapsed,
        IsDisabled = x.IsDisabled,
        OriginalImageHash = x.OriginalImageHash,
        OriginalThumbnailHash = x.OriginalThumbnailHash,
        OriginalImageBase64 = x.OriginalImageBase64,
        IsCachedEncoding = x.IsCachedEncoding,
    };

    private void PopulateAutomationEffectsPresetCombo(ComboBox combo)
    {
        combo.Items.Clear();
        foreach (string preset in GetAvailableEffectsPresetNames())
            combo.Items.Add(CreateTextComboBoxItem(preset));
        if (combo.SelectedIndex < 0 && combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private IReadOnlyList<string> GetAvailableEffectsPresetNames()
    {
        EnsureDefaultFxPresets();
        if (!Directory.Exists(FxPresetsDir))
            return [];

        var names = new List<string>();
        foreach (string file in Directory.EnumerateFiles(FxPresetsDir, "*.json")
                     .OrderByDescending(File.GetLastWriteTime))
        {
            string label = Path.GetFileNameWithoutExtension(file);
            try
            {
                var parsed = JsonSerializer.Deserialize<EffectsPresetFile>(File.ReadAllText(file));
                if (!string.IsNullOrWhiteSpace(parsed?.Name))
                    label = parsed.Name;
            }
            catch
            {
                // 忽略损坏的预设文件，沿用文件名。
            }

            if (!names.Contains(label, StringComparer.OrdinalIgnoreCase))
                names.Add(label);
        }

        return names;
    }

    private string? TryResolveEffectsPresetPath(string? presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName) || !Directory.Exists(FxPresetsDir))
            return null;

        foreach (string file in Directory.EnumerateFiles(FxPresetsDir, "*.json"))
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(file), presetName, StringComparison.OrdinalIgnoreCase))
                return file;

            try
            {
                var parsed = JsonSerializer.Deserialize<EffectsPresetFile>(File.ReadAllText(file));
                if (string.Equals(parsed?.Name, presetName, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
            catch
            {
                // 忽略损坏的预设文件。
            }
        }

        return null;
    }

    private async Task<List<EffectEntry>> LoadEffectsFromPresetByNameAsync(string presetName)
    {
        string? path = TryResolveEffectsPresetPath(presetName);
        if (path == null)
            return [];

        var parsed = JsonSerializer.Deserialize<EffectsPresetFile>(await File.ReadAllTextAsync(path));
        if (parsed?.Effects == null)
            return [];

        return parsed.Effects
            .Select(RehydratePresetEffect)
            .ToList();
    }

    private async Task<AutomationEffectsProcessResult> RunAutomationEffectsProcessAsync(
        byte[] sourceBytes,
        AutomationEffectsOptions options,
        CancellationToken ct)
    {
        options.Normalize();
        byte[] currentBytes = sourceBytes;
        var notes = new List<string>();

        if (options.UpscaleEnabled && !string.IsNullOrWhiteSpace(options.UpscaleModel))
        {
            try
            {
                currentBytes = await RunAutomationUpscaleAsync(currentBytes, options, ct);
                notes.Add(Lf("automation.note.upscale", options.UpscaleModel, options.UpscaleScale));
            }
            catch (InvalidOperationException)
            {
                notes.Add(Lf("automation.note.upscale_skipped", options.UpscaleModel));
            }
        }

        if (options.FxEnabled && !string.IsNullOrWhiteSpace(options.FxPresetName))
        {
            var effects = await LoadEffectsFromPresetByNameAsync(options.FxPresetName);
            if (effects.Count > 0)
            {
                currentBytes = await Task.Run(() => RenderEffects(currentBytes, effects), ct);
                notes.Add(Lf("automation.note.filter", options.FxPresetName));
            }
        }

        return new AutomationEffectsProcessResult
        {
            Bytes = currentBytes,
            Summary = notes.Count > 0 ? string.Join(" -> ", notes) : "",
        };
    }

    private async Task<byte[]> RunAutomationUpscaleAsync(
        byte[] sourceBytes,
        AutomationEffectsOptions options,
        CancellationToken ct)
    {
        var modelInfo = _upscaleModelInfos
            .FirstOrDefault(x => string.Equals(x.DisplayName, options.UpscaleModel, StringComparison.OrdinalIgnoreCase));
        if (modelInfo == null)
        {
            _upscaleModelInfos = UpscaleService.ScanModels(Path.Combine(ModelsDir, "upscaler"));
            modelInfo = _upscaleModelInfos
                .FirstOrDefault(x => string.Equals(x.DisplayName, options.UpscaleModel, StringComparison.OrdinalIgnoreCase));
        }
        if (modelInfo == null)
            throw new InvalidOperationException(Lf("automation.error.upscale_model_not_found", options.UpscaleModel));

        _upscaleService ??= new UpscaleService();
        bool preferCpu = PreferCpuForOnnxInference;

        try
        {
            await Task.Run(() => _upscaleService.LoadModel(modelInfo.FilePath, preferCpu), ct);

            int modelScale = Math.Max(2, modelInfo.Scale);
            int targetScale = options.UpscaleScale;
            int passCount = 1;
            int cumulativeScale = modelScale;
            while (cumulativeScale < targetScale && passCount < 3)
            {
                passCount++;
                cumulativeScale *= modelScale;
            }

            byte[] currentBytes = sourceBytes;
            for (int i = 0; i < passCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                currentBytes = await _upscaleService.UpscaleAsync(currentBytes, null, ct);
            }

            return currentBytes;
        }
        finally
        {
            if (ShouldUnloadOnnxModelsAfterInference)
                _upscaleService?.UnloadModel();
        }
    }
}
