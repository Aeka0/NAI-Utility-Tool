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
        : _settings.Settings.InpaintParameters;

    private void SyncUIToParams()
    {
        var p = CurrentParams;
        p.Seed = (int)NbSeed.Value;
        p.Variety = ChkVariety.IsChecked == true;
        p.Model = GetSelectedComboText(CboModel) ?? p.Model;
        p.Sampler = NormalizeSamplerForModel(p.Sampler, p.Model);
    }

    private static NAIParameters CreateDefaultGenerationParameters() => new()
    {
        Model = "nai-diffusion-4-5-full",
    };

    private static NAIParameters CreateDefaultInpaintParameters() => new()
    {
        Model = "nai-diffusion-4-5-full-inpainting",
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
            _customWidth = 832;
            _customHeight = 1216;
            _genPositivePrompt = "";
            _genNegativePrompt = "";
            _genStylePrompt = "";
            _inpaintPositivePrompt = "";
            _inpaintNegativePrompt = "";
            _inpaintStylePrompt = "";
            _isSplitPrompt = false;
            return;
        }

        var remembered = _settings.Settings.RememberedPrompts ?? new RememberedPromptState();
        _genPositivePrompt = remembered.GenPositivePrompt ?? "";
        _genNegativePrompt = remembered.GenNegativePrompt ?? "";
        _genStylePrompt = remembered.GenStylePrompt ?? "";
        _inpaintPositivePrompt = remembered.InpaintPositivePrompt ?? "";
        _inpaintNegativePrompt = remembered.InpaintNegativePrompt ?? "";
        _inpaintStylePrompt = remembered.InpaintStylePrompt ?? "";
        _isSplitPrompt = remembered.IsSplitPrompt;
        _customWidth = SnapToMultipleOf64(_settings.Settings.RememberedCustomWidth);
        _customHeight = SnapToMultipleOf64(_settings.Settings.RememberedCustomHeight);
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

        _settings.Settings.RememberedPrompts = new RememberedPromptState
        {
            GenPositivePrompt = _genPositivePrompt,
            GenNegativePrompt = _genNegativePrompt,
            GenStylePrompt = _genStylePrompt,
            InpaintPositivePrompt = _inpaintPositivePrompt,
            InpaintNegativePrompt = _inpaintNegativePrompt,
            InpaintStylePrompt = _inpaintStylePrompt,
            IsSplitPrompt = _isSplitPrompt,
        };
        _settings.Settings.RememberedCustomWidth = _customWidth;
        _settings.Settings.RememberedCustomHeight = _customHeight;
    }
}
