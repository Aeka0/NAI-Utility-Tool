using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NAITool.Services;
using SkiaSharp;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace NAITool;

public sealed partial class MainWindow
{
    private const int MaxVibeTransfers = 16;
    private const int MaxPreciseReferences = 16;

    private readonly List<VibeTransferEntry> _genVibeTransfers = new();
    private readonly List<PreciseReferenceEntry> _genPreciseReferences = new();

    private sealed class VibeTransferEntry
    {
        public string FileName { get; set; } = "";
        public string ImageBase64 { get; set; } = "";
        public double Strength { get; set; } = 0.6;
        public double InformationExtracted { get; set; } = 1.0;
        public bool IsEncodedFile { get; set; }
        public bool IsCollapsed { get; set; }
        public bool IsDisabled { get; set; }
        /// <summary>原始图片 SHA256 前缀（仅原始图片条目）</summary>
        public string? OriginalImageHash { get; set; }
        /// <summary>缩略图 SHA256 前缀（仅原始图片条目）</summary>
        public string? OriginalThumbnailHash { get; set; }
        /// <summary>原始图片 Base64（缓存失效时回退）</summary>
        public string? OriginalImageBase64 { get; set; }
        /// <summary>当前使用的是本地缓存编码数据</summary>
        public bool IsCachedEncoding { get; set; }
    }

    private sealed class PreciseReferenceEntry
    {
        public string FileName { get; set; } = "";
        public string ImageBase64 { get; set; } = "";
        public PreciseReferenceType ReferenceType { get; set; } = PreciseReferenceType.CharacterAndStyle;
        public double Strength { get; set; } = 1.0;
        public double Fidelity { get; set; }
        public bool IsCollapsed { get; set; }
        public bool IsDisabled { get; set; }
    }

    private int ActiveVibeTransferCount() =>
        _genVibeTransfers.Count(x => !x.IsDisabled);

    private int ActivePreciseReferenceCount() =>
        _genPreciseReferences.Count(x => !x.IsDisabled);

    private string GetCurrentModelKey() => GetSelectedComboText(CboModel) ?? CurrentParams.Model;

    private static bool IsV3ModelKey(string model) =>
        model == "nai-diffusion-3" || model == "nai-diffusion-3-inpainting";

    private static bool IsV4PlusModelKey(string model) =>
        model.Contains("-4", StringComparison.Ordinal);

    private static bool IsV45ModelKey(string model) =>
        model.Contains("4-5", StringComparison.Ordinal);

    private bool SupportsCharacterFeature()
    {
        if (!IsPromptMode(_currentMode)) return false;
        return IsV4PlusModelKey(GetCurrentModelKey());
    }

    private bool SupportsVibeTransferFeature()
    {
        if (!IsPromptMode(_currentMode)) return false;
        return true;
    }

    private bool RequiresEncodedVibeFileOnly() =>
        IsV4PlusModelKey(GetCurrentModelKey()) && IsAssetProtectionPaidFeatureLimitEnabled();

    private bool IsPreciseReferenceBlockedByAssetProtection() =>
        IsPromptMode(_currentMode) && IsV45ModelKey(GetCurrentModelKey()) && IsAssetProtectionPaidFeatureLimitEnabled();

    private bool SupportsPreciseReferenceFeature()
    {
        if (!IsPromptMode(_currentMode)) return false;
        return IsV45ModelKey(GetCurrentModelKey()) && !IsAssetProtectionPaidFeatureLimitEnabled();
    }

    private bool CanEditVibeTransferFeature() =>
        SupportsVibeTransferFeature() &&
        ActivePreciseReferenceCount() == 0;

    private bool CanEditPreciseReferenceFeature() =>
        SupportsPreciseReferenceFeature() &&
        ActiveVibeTransferCount() == 0;

    private bool ShouldShowVibeTransferPanel() =>
        SupportsVibeTransferFeature() && _genVibeTransfers.Count > 0;

    private bool ShouldShowPreciseReferencePanel() =>
        SupportsPreciseReferenceFeature() && _genPreciseReferences.Count > 0;

    private int EstimateCurrentRequestAnlasCost()
    {
        if (!IsPromptMode(_currentMode) || string.IsNullOrWhiteSpace(_settings.Settings.ApiToken))
            return 0;

        int steps = IsAdvancedWindowOpen ? (int)_advNbSteps.Value : CurrentParams.Steps;
        int width;
        int height;
        if (_currentMode == AppMode.ImageGeneration)
        {
            (width, height) = GetSelectedSize();
        }
        else
        {
            width = MaskCanvas.CanvasW > 0 ? MaskCanvas.CanvasW : _customWidth;
            height = MaskCanvas.CanvasH > 0 ? MaskCanvas.CanvasH : _customHeight;
        }

        long pixelCount = (long)width * height;
        bool isSmEnabled = CurrentParams.Sm;
        bool isSmDynamic = false;

        long dimension = Math.Max(pixelCount, 65536);
        int accountTier = _isOpusSubscriber ? 3 : 1;

        // ── 基础生图费用 ──
        // Opus 免费条件: <=28步, 总像素 <= 1024x1024
        int baseCost = 0;
        bool opusFree = steps <= 28 && dimension <= 1024L * 1024L && accountTier >= 3;
        if (!opusFree)
        {
            double factor = isSmDynamic ? 1.4 : isSmEnabled ? 1.2 : 1.0;
            double raw = Math.Ceiling(2951823174884865e-21 * pixelCount +
                                      5.753298233447344e-7 * pixelCount * steps) * factor;
            baseCost = Math.Clamp((int)Math.Ceiling(raw), 2, 140);
        }

        // ── 氛围迁移 / 精确参考额外费用 ──
        int refCost = 0;
        bool isV4Plus = IsV4PlusModelKey(GetCurrentModelKey());

        var activeVibes = _genVibeTransfers.Where(v => !v.IsDisabled).ToList();
        if (isV4Plus && !IsAssetProtectionPaidFeatureLimitEnabled() && activeVibes.Count > 0)
        {
            int encodingCost = activeVibes.Count(v => !v.IsEncodedFile) * 2;
            int slotCost = Math.Max(activeVibes.Count - 4, 0) * 2;
            refCost += encodingCost + slotCost;
        }

        int activePreciseCount = ActivePreciseReferenceCount();
        if (SupportsPreciseReferenceFeature() && activePreciseCount > 0)
        {
            refCost += activePreciseCount * 5;
        }

        return baseCost + refCost;
    }

    private bool CurrentRequestUsesAnlas() => EstimateCurrentRequestAnlasCost() > 0;

    private bool TryValidateReferenceRequest(out string error)
    {
        error = "";

        if (!IsPromptMode(_currentMode))
            return true;

        int activeVibeCount = ActiveVibeTransferCount();
        int activePreciseCount = ActivePreciseReferenceCount();

        if (activeVibeCount > 0 &&
            activePreciseCount > 0 &&
            !IsAssetProtectionPaidFeatureLimitEnabled() &&
            IsV45ModelKey(GetCurrentModelKey()))
        {
            error = L("references.validation.mixed_reference_types");
            return false;
        }

        if (RequiresEncodedVibeFileOnly() &&
            _genVibeTransfers.Any(x => !x.IsDisabled && !x.IsEncodedFile))
        {
            error = L("references.error.asset_protection_requires_encoded_vibe");
            return false;
        }

        if (IsAssetProtectionPaidFeatureLimitEnabled() &&
            activeVibeCount > AssetProtectionFreeVibeLimit)
        {
            error = Lf("references.error.asset_protection_vibe_count_limit", AssetProtectionFreeVibeLimit);
            return false;
        }

        return true;
    }

    private void ClearReferenceFeatures()
    {
        _genVibeTransfers.Clear();
        _genPreciseReferences.Clear();
    }

    private void ApplyReferenceDataFromMetadata(ImageMetadata? meta)
    {
        _genVibeTransfers.Clear();
        _genPreciseReferences.Clear();

        if (meta == null)
            return;

        foreach (var vibe in meta.VibeTransfers.Take(GetMaxAllowedVibeTransfers()))
        {
            if (string.IsNullOrWhiteSpace(vibe.ImageBase64))
                continue;

            _genVibeTransfers.Add(new VibeTransferEntry
            {
                FileName = string.IsNullOrWhiteSpace(vibe.FileName) ? L("references.imported.vibe_label") : vibe.FileName,
                ImageBase64 = vibe.ImageBase64,
                Strength = Math.Clamp(vibe.Strength, 0, 1),
                InformationExtracted = Math.Clamp(vibe.InformationExtracted, 0, 1),
                IsEncodedFile = true,
            });
        }

        foreach (var reference in meta.PreciseReferences.Take(MaxPreciseReferences))
        {
            if (string.IsNullOrWhiteSpace(reference.ImageBase64))
                continue;

            _genPreciseReferences.Add(new PreciseReferenceEntry
            {
                FileName = string.IsNullOrWhiteSpace(reference.FileName) ? L("references.imported.precise_label") : reference.FileName,
                ImageBase64 = reference.ImageBase64,
                ReferenceType = reference.ReferenceType,
                Strength = Math.Clamp(reference.Strength, -1, 1),
                Fidelity = Math.Clamp(reference.Fidelity, -1, 1),
            });
        }
    }

    private void AppendReferenceImportNotes(ImageMetadata? meta, List<string> notes)
    {
        if (meta == null) return;
        if (meta.VibeTransfers.Count > 0)
            notes.Add(Lf("references.imported.vibe_count", meta.VibeTransfers.Count));
        if (meta.PreciseReferences.Count > 0)
            notes.Add(Lf("references.imported.precise_count", meta.PreciseReferences.Count));
    }

    private async Task<string?> EnsureVibesEncodedAsync(string model, CancellationToken ct)
    {
        if (!NovelAIService.IsV4PlusModel(model))
            return null;

        string cacheDir = VibeCacheService.GetCacheDir(AppRootDir);
        bool anyEncoded = false;

        foreach (var entry in _genVibeTransfers.Where(x => !x.IsDisabled))
        {
            if (entry.IsEncodedFile || string.IsNullOrWhiteSpace(entry.ImageBase64))
                continue;

            string rawBase64 = entry.OriginalImageBase64 ?? entry.ImageBase64;
            var (vibeData, error) = await _naiService.EncodeVibeAsync(
                rawBase64, model, entry.InformationExtracted, ct);

            if (vibeData == null)
                return Lf("references.error.vibe_encode_failed", entry.FileName, error ?? "");

            string encodedBase64 = Convert.ToBase64String(vibeData);
            entry.ImageBase64 = encodedBase64;
            entry.IsEncodedFile = true;
            entry.IsCachedEncoding = true;

            if (entry.OriginalImageHash != null)
            {
                byte[] originalBytes = Convert.FromBase64String(rawBase64);
                entry.OriginalThumbnailHash ??= VibeCacheService.ComputeThumbnailHash(
                    VibeCacheService.CreateCanonicalThumbnail(originalBytes));
                VibeCacheService.SaveVibe(
                    cacheDir, originalBytes, originalBytes, vibeData, entry.InformationExtracted, model);
            }

            DebugLog($"[Generate] Vibe 自动编码并缓存: {entry.FileName}");
            anyEncoded = true;
        }

        if (anyEncoded)
        {
            RefreshVibeTransferPanel();
            UpdateGenerateButtonWarning();
        }

        return null;
    }

    private List<VibeTransferInfo>? GetVibeTransferData()
    {
        if (!SupportsVibeTransferFeature()) return null;
        if (ActivePreciseReferenceCount() > 0) return null;

        var result = _genVibeTransfers
            .Where(x => !x.IsDisabled && !string.IsNullOrWhiteSpace(x.ImageBase64))
            .Select(x => new VibeTransferInfo
            {
                FileName = x.FileName,
                ImageBase64 = x.ImageBase64,
                Strength = Math.Clamp(x.Strength, 0, 1),
                InformationExtracted = Math.Clamp(x.InformationExtracted, 0, 1),
                IsEncoded = x.IsEncodedFile,
            })
            .ToList();

        return result.Count > 0 ? result : null;
    }

    private List<PreciseReferenceInfo>? GetPreciseReferenceData()
    {
        if (!SupportsPreciseReferenceFeature()) return null;
        if (ActiveVibeTransferCount() > 0) return null;

        var result = _genPreciseReferences
            .Where(x => !x.IsDisabled && !string.IsNullOrWhiteSpace(x.ImageBase64))
            .Select(x => new PreciseReferenceInfo
            {
                FileName = x.FileName,
                ImageBase64 = x.ImageBase64,
                ReferenceType = x.ReferenceType,
                Strength = Math.Clamp(x.Strength, -1, 1),
                Fidelity = Math.Clamp(x.Fidelity, -1, 1),
            })
            .ToList();

        return result.Count > 0 ? result : null;
    }
}
